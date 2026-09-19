using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CodexOptionsSchema = "reign-codex-options-v1";
        private const string CodexRuntimeDiagnosticsSchema = "reign-codex-runtime-diagnostics-v1";
        private const string CodexModelVerificationCandidate = "gpt-5.4";
        private static readonly object CodexRuntimeStateLock = new object();
        private static CodexCatalogState CodexRuntimeCatalog = new CodexCatalogState();
        private static CodexProtocolState CodexRuntimeProtocol = new CodexProtocolState();
        private static Dictionary<string, object> CodexLastCapabilityStatus = new Dictionary<string, object>();
        private static readonly CodexThreadReusePool CodexRuntimeThreads = new CodexThreadReusePool();
        private static readonly CodexThreadCleanupQueue CodexRuntimeCleanup = new CodexThreadCleanupQueue();
        private static readonly Dictionary<string, CodexThreadLease> CodexPendingThreadLeases =
            new Dictionary<string, CodexThreadLease>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A request-local snapshot. The snapshot is deliberately independent of the mutable settings
        /// dictionary so a provider switch cannot move an in-flight request to another provider.
        /// </summary>
        private sealed class CodexRuntimeOptions
        {
            public string Schema = CodexOptionsSchema;
            public string ReasoningMode = "selective";
            public string ReasoningEffort = "medium";
            public bool FastMode;
            public bool StructuredOutputs;
            public bool CompactMetadata;
            public bool StablePromptMapping;
            public bool ParallelContextPreparation;
            public bool ReuseThreads;
            public bool AsyncThreadCleanup;

            public static CodexRuntimeOptions Capture(Dictionary<string, object> settings, Dictionary<string, object> codexContext)
            {
                Dictionary<string, object> source = ReadDictionary(codexContext, "options")
                    ?? ReadDictionary(settings, "codexOptions")
                    ?? new Dictionary<string, object>();
                CodexRuntimeOptions result = new CodexRuntimeOptions
                {
                    Schema = ReadString(source, "schema", CodexOptionsSchema),
                    ReasoningMode = ReadString(source, "reasoningMode", ReadString(settings, "reasoningMode", "selective")),
                    ReasoningEffort = ReadString(source, "reasoningEffort", ReadString(settings, "reasoningEffort", "medium")),
                    FastMode = ReadBool(source, "fastMode", false),
                    StructuredOutputs = ReadBool(source, "structuredOutputs", false),
                    CompactMetadata = ReadBool(source, "compactMetadata", false),
                    StablePromptMapping = ReadBool(source, "stablePromptMapping", false),
                    ParallelContextPreparation = ReadBool(source, "parallelContextPreparation", false),
                    ReuseThreads = ReadBool(source, "reuseThreads", false),
                    AsyncThreadCleanup = ReadBool(source, "asyncThreadCleanup", false)
                };
                if (!string.Equals(result.Schema, CodexOptionsSchema, StringComparison.Ordinal)) result.Schema = CodexOptionsSchema;
                result.ReasoningMode = NormalizeCodexReasoningMode(result.ReasoningMode);
                result.ReasoningEffort = NormalizeCodexEffort(result.ReasoningEffort);
                return result;
            }

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["schema"] = Schema,
                    ["reasoningMode"] = ReasoningMode,
                    ["reasoningEffort"] = ReasoningEffort,
                    ["fastMode"] = FastMode,
                    ["structuredOutputs"] = StructuredOutputs,
                    ["compactMetadata"] = CompactMetadata,
                    ["stablePromptMapping"] = StablePromptMapping,
                    ["parallelContextPreparation"] = ParallelContextPreparation,
                    ["reuseThreads"] = ReuseThreads,
                    ["asyncThreadCleanup"] = AsyncThreadCleanup
                };
            }
        }

        private sealed class CodexRuntimeRequestContext
        {
            public string RequestType = "dialogue";
            public string Model = "";
            public string CorrelationId = "";
            public string AttemptId = "";
            public string ParentCorrelationId = "";
            public string RequestedReasoningEffort = "medium";
            public string ResolvedReasoningEffort = "medium";
            public bool DisabledReasoningRequested;
            public string CampaignId = "";
            public string TimelineId = "";
            public string LoadGenerationId = "";
            public string SessionId = "";
            public string SpeakerId = "";
            public string ConversationMode = "";
            public string VisibilityScope = "";
            public string AuthorityHash = "";
            public string SchemaId = "";
            public Dictionary<string, object> OutputSchema;
            public bool SchemaRequested;
            public bool SchemaApplied;
            public string SchemaState = "not_requested";
            public bool StablePromptMappingRequested;
            public bool StablePromptMappingApplied;
            public string StablePromptMappingState = "off";
            public string StablePromptMappingReason = "Stable prompt mapping is disabled.";
            public string StableBaseInstructions = "";
            public List<Dictionary<string, object>> StableTurnInput = new List<Dictionary<string, object>>();
            public List<Dictionary<string, object>> PromptSegments = new List<Dictionary<string, object>>();
            public string CanonicalHistorySignature = "";
            public string AcceptedHistorySignature = "";
            public string CurrentTurnText = "";
            // The complete prompt is retained only in request-local runtime state. It is used to
            // prove append-only thread reuse and is intentionally omitted from diagnostics.
            public string PromptText = "";
            public string PromptHash = "";
            public string PromptContractHash = "";
            public CodexRuntimeOptions Options = new CodexRuntimeOptions();
            public CodexFastResolution Fast = CodexFastResolution.Unknown("Fast mode was not requested.");
            public bool FinalizationApproved;
            public bool Repaired;
            public bool DeferFinalization;
            public bool HasExplicitThreadMetadata;
            public bool ReasoningCapabilityKnown;
            public string ReasoningResolution = "";

            public string ThreadIdentityKey
            {
                get
                {
                    return CodexThreadIdentity.TryCreate(this, out CodexThreadIdentity identity) ? identity.Key : "";
                }
            }

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["requestType"] = RequestType,
                    ["model"] = Model,
                    ["correlationId"] = CorrelationId,
                    ["attemptId"] = AttemptId,
                    ["parentCorrelationId"] = ParentCorrelationId,
                    ["requestedReasoningEffort"] = RequestedReasoningEffort,
                    ["resolvedReasoningEffort"] = ResolvedReasoningEffort,
                    ["disabledReasoningRequested"] = DisabledReasoningRequested,
                    ["campaignId"] = CampaignId,
                    ["timelineId"] = TimelineId,
                    ["loadGenerationId"] = LoadGenerationId,
                    ["sessionId"] = SessionId,
                    ["speakerId"] = SpeakerId,
                    ["conversationMode"] = ConversationMode,
                    ["visibilityScope"] = VisibilityScope,
                    ["authorityHash"] = AuthorityHash,
                    ["schemaId"] = SchemaId,
                    ["schemaRequested"] = SchemaRequested,
                    ["schemaApplied"] = SchemaApplied,
                    ["schemaState"] = SchemaState,
                    ["stablePromptMapping"] = new Dictionary<string, object>
                    {
                        ["requested"] = StablePromptMappingRequested,
                        ["applied"] = StablePromptMappingApplied,
                        ["state"] = StablePromptMappingState,
                        ["reason"] = StablePromptMappingReason,
                        ["baseInstructionsPresent"] = !string.IsNullOrWhiteSpace(StableBaseInstructions),
                        ["turnInputCount"] = StableTurnInput == null ? 0 : StableTurnInput.Count
                    },
                    ["canonicalHistorySignature"] = CanonicalHistorySignature,
                    ["acceptedHistorySignature"] = AcceptedHistorySignature,
                    ["currentTurnTextPresent"] = !string.IsNullOrWhiteSpace(CurrentTurnText),
                    ["threadIdentity"] = ThreadIdentityKey,
                    ["promptContractHash"] = PromptContractHash,
                    ["reasoningCapabilityKnown"] = ReasoningCapabilityKnown,
                    ["reasoningResolution"] = ReasoningResolution,
                    ["finalizationApproved"] = FinalizationApproved,
                    ["repaired"] = Repaired,
                    ["deferFinalization"] = DeferFinalization,
                    ["options"] = Options.ToDictionary(),
                    ["fastMode"] = new Dictionary<string, object>
                    {
                        ["requested"] = Fast.Requested,
                        ["state"] = Fast.State,
                        ["applied"] = Fast.Applied,
                        ["field"] = Fast.Field,
                        ["reason"] = Fast.Reason
                    }
                };
            }
        }

        private static CodexRuntimeRequestContext CaptureCodexRequestContext(
            Dictionary<string, object> settings,
            Dictionary<string, object> payload,
            string requestType,
            string model,
            string correlationId,
            Dictionary<string, object> codexContext = null)
        {
            Dictionary<string, object> source = codexContext ?? new Dictionary<string, object>();
            CodexRuntimeOptions options = CodexRuntimeOptions.Capture(settings, source);
            string requestedReasoningEffort = NormalizeCodexEffort(ReadString(source, "requestedReasoningEffort", options.ReasoningEffort));
            bool disabledReasoning = ReadBool(source, "disabledReasoningRequested", ReadBool(source, "disableReasoning", false))
                || requestedReasoningEffort.Equals("none", StringComparison.OrdinalIgnoreCase);
            bool capabilityKnown;
            string reasoningResolution;
            string resolvedReasoningEffort = ResolveRequestedReasoningEffort(model, requestedReasoningEffort, disabledReasoning, out capabilityKnown, out reasoningResolution);
            CodexRuntimeRequestContext result = new CodexRuntimeRequestContext
            {
                RequestType = ReadString(source, "requestType", requestType ?? "dialogue"),
                Model = (model ?? ReadString(source, "model", "")).Trim(),
                CorrelationId = FirstNonEmpty(ReadString(source, "correlationId", ""), correlationId, Guid.NewGuid().ToString("N")),
                AttemptId = FirstNonEmpty(ReadString(source, "attemptId", ""), Guid.NewGuid().ToString("N")),
                ParentCorrelationId = ReadString(source, "parentCorrelationId", ""),
                RequestedReasoningEffort = requestedReasoningEffort,
                ResolvedReasoningEffort = resolvedReasoningEffort,
                DisabledReasoningRequested = disabledReasoning,
                CampaignId = ReadCodexContextString(source, "campaignId", ""),
                TimelineId = ReadCodexContextString(source, "timelineId", ""),
                LoadGenerationId = ReadCodexContextString(source, "loadGenerationId", ""),
                SessionId = ReadCodexContextString(source, "sessionId", ""),
                SpeakerId = ReadCodexContextString(source, "speakerId", ""),
                ConversationMode = ReadCodexContextString(source, "conversationMode", ""),
                VisibilityScope = FirstNonEmpty(ReadCodexContextString(source, "visibilityScope", ""), ReadCodexContextString(source, "visibility", "")),
                AuthorityHash = ReadCodexContextString(source, "authorityHash", ""),
                SchemaId = ReadCodexContextString(source, "schemaId", ""),
                CanonicalHistorySignature = FirstNonEmpty(ReadCodexContextString(source, "canonicalHistorySignature", ""), ReadCodexContextString(source, "historySignature", "")),
                AcceptedHistorySignature = ReadCodexContextString(source, "acceptedHistorySignature", ""),
                CurrentTurnText = ReadCodexContextString(source, "currentTurnText", ""),
                OutputSchema = ReadDictionary(source, "outputSchema"),
                PromptSegments = ReadDictionaryList(source, "promptSegments"),
                Options = options,
                FinalizationApproved = ReadBool(source, "finalizationApproved", false),
                Repaired = ReadBool(source, "repaired", false),
                DeferFinalization = ReadBool(source, "deferFinalization", false),
                HasExplicitThreadMetadata = HasThreadMetadata(source),
                PromptContractHash = FirstNonEmpty(ReadCodexContextString(source, "promptContractHash", ""), ReadCodexContextString(source, "promptVersion", "")),
                ReasoningCapabilityKnown = capabilityKnown,
                ReasoningResolution = reasoningResolution
            };
            result.PromptSegments = ReadDictionaryList(source, "promptSegments");
            result.PromptText = BuildPromptTextForRuntime(payload);
            result.PromptHash = HashCodexText(result.PromptText);
            if (string.IsNullOrWhiteSpace(result.PromptContractHash))
                result.PromptContractHash = HashCodexText(Json.Serialize(result.PromptSegments));
            result.SchemaRequested = options.StructuredOutputs && result.OutputSchema != null;
            if (!result.SchemaRequested)
            {
                result.SchemaState = "not_requested";
                result.SchemaApplied = false;
            }
            else if (CodexRuntimeProtocol.SupportsOutputSchema == true)
            {
                result.SchemaState = "applied";
                result.SchemaApplied = true;
            }
            else if (CodexRuntimeProtocol.SupportsOutputSchema == false)
            {
                result.SchemaState = "unsupported_legacy_path";
                result.SchemaApplied = false;
            }
            else
            {
                result.SchemaState = "unknown_legacy_path";
                result.SchemaApplied = false;
            }
            result.Fast = CodexFastMode.Resolve(options.FastMode, CodexRuntimeProtocol);
            return result;
        }

        private sealed class CodexPromptMapping
        {
            public bool Requested;
            public bool Applied;
            public string State = "off";
            public string Reason = "Stable prompt mapping is disabled.";
            public string BaseInstructions = "";
            public List<Dictionary<string, object>> TurnInput = new List<Dictionary<string, object>>();
        }

        private static CodexPromptMapping BuildCodexPromptMapping(CodexRuntimeRequestContext context, List<Dictionary<string, object>> messages)
        {
            CodexPromptMapping result = new CodexPromptMapping
            {
                Requested = context != null && context.Options.StablePromptMapping
            };
            if (context == null || !result.Requested)
            {
                result.TurnInput.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = BuildCodexPrompt(messages) });
                return result;
            }
            if (CodexRuntimeProtocol.SupportsBaseInstructions == false)
            {
                result.State = "unsupported_legacy_path";
                result.Reason = "The bundled app-server schema does not expose ThreadStartParams.baseInstructions; the legacy prompt envelope is retained.";
                result.TurnInput.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = BuildCodexPrompt(messages) });
                return result;
            }
            if (CodexRuntimeProtocol.SupportsBaseInstructions != true || string.IsNullOrWhiteSpace(CodexRuntimeProtocol.BaseInstructionsField))
            {
                result.State = "unknown_legacy_path";
                result.Reason = "The bundled app-server schema has not confirmed baseInstructions; the legacy prompt envelope is retained.";
                result.TurnInput.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = BuildCodexPrompt(messages) });
                return result;
            }
            List<Dictionary<string, object>> segments = context.PromptSegments ?? new List<Dictionary<string, object>>();
            int sharedCount = 0;
            StringBuilder shared = new StringBuilder();
            while (sharedCount < segments.Count)
            {
                Dictionary<string, object> segment = segments[sharedCount];
                if (!ReadString(segment, "name", "").Equals("sharedRules", StringComparison.OrdinalIgnoreCase)
                    || !ReadString(segment, "role", "").Equals("system", StringComparison.OrdinalIgnoreCase)) break;
                string content = ReadString(segment, "content", "");
                if (string.IsNullOrWhiteSpace(content)) break;
                if (shared.Length > 0) shared.Append("\n\n");
                shared.Append(content.Trim());
                sharedCount++;
            }
            if (sharedCount == 0 || messages == null || messages.Count < sharedCount)
            {
                result.State = "not_applicable";
                result.Reason = "Stable mapping requires a leading system sharedRules segment; the legacy prompt envelope is retained.";
                result.TurnInput.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = BuildCodexPrompt(messages) });
                return result;
            }
            result.Applied = true;
            result.State = "applied";
            result.Reason = "Leading system sharedRules are carried by baseInstructions; remaining segments retain their original order and role labels.";
            result.BaseInstructions = "You are the text-generation provider inside Bannerlord Reign. Do not use tools, shell commands, files, or network access. Return only the response requested by the conversation below.\n\n" + shared.ToString();
            for (int index = sharedCount; index < messages.Count; index++)
            {
                Dictionary<string, object> message = messages[index] ?? new Dictionary<string, object>();
                string role = ReadString(message, "role", "user").Trim().ToUpperInvariant();
                result.TurnInput.Add(new Dictionary<string, object>
                {
                    ["type"] = "text",
                    ["text"] = "--- " + role + " ---\n" + ReadString(message, "content", "")
                });
            }
            if (result.TurnInput.Count == 0)
                result.TurnInput.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = "Return only the requested response." });
            return result;
        }

        private static void ApplyCodexPromptMapping(CodexRuntimeRequestContext context, Dictionary<string, object> parameters)
        {
            if (context == null || parameters == null || !context.StablePromptMappingApplied
                || string.IsNullOrWhiteSpace(context.StableBaseInstructions)
                || string.IsNullOrWhiteSpace(CodexRuntimeProtocol.BaseInstructionsField)) return;
            parameters[CodexRuntimeProtocol.BaseInstructionsField] = context.StableBaseInstructions;
        }

        private static void AugmentCodexPromptContractHash(CodexRuntimeRequestContext context)
        {
            if (context == null) return;
            context.PromptContractHash = HashCodexText((context.PromptContractHash ?? "")
                + "|promptMapping=" + (context.StablePromptMappingState ?? "")
                + "|schemaState=" + (context.SchemaState ?? "")
                + "|schemaId=" + (context.SchemaId ?? ""));
        }

        private static bool HasThreadMetadata(Dictionary<string, object> source)
        {
            if (source == null) return false;
            return !string.IsNullOrWhiteSpace(ReadCodexContextString(source, "campaignId", ""))
                && !string.IsNullOrWhiteSpace(ReadCodexContextString(source, "timelineId", ""))
                && !string.IsNullOrWhiteSpace(ReadCodexContextString(source, "sessionId", ""))
                && !string.IsNullOrWhiteSpace(ReadCodexContextString(source, "speakerId", ""))
                && !string.IsNullOrWhiteSpace(ReadCodexContextString(source, "conversationMode", ""))
                && !string.IsNullOrWhiteSpace(FirstNonEmpty(ReadCodexContextString(source, "visibilityScope", ""), ReadCodexContextString(source, "visibility", "")))
                && !string.IsNullOrWhiteSpace(FirstNonEmpty(ReadCodexContextString(source, "canonicalHistorySignature", ""), ReadCodexContextString(source, "historySignature", "")))
                && !string.IsNullOrWhiteSpace(ReadCodexContextString(source, "currentTurnText", ""));
        }

        private static string ReadCodexContextString(Dictionary<string, object> source, string key, string fallback)
        {
            if (source == null) return fallback;
            string direct = ReadString(source, key, "");
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
            Dictionary<string, object> identity = ReadDictionary(source, "threadIdentity");
            return ReadString(identity, key, fallback);
        }

        private sealed class CodexFastResolution
        {
            public bool Requested;
            public string State = "off";
            public string Applied = "standard";
            public string Field = "";
            public string Reason = "";
            public bool CanRetryStandard;

            public static CodexFastResolution Unknown(string reason)
            {
                return new CodexFastResolution { State = "unknown", Applied = "unknown", Reason = reason ?? "" };
            }
        }

        private static class CodexFastMode
        {
            public static CodexFastResolution Resolve(bool requested, CodexProtocolState protocol)
            {
                CodexFastResolution result = new CodexFastResolution { Requested = requested };
                if (protocol != null && protocol.SupportsFastMode.HasValue && !string.IsNullOrWhiteSpace(protocol.FastModeField))
                    result.Field = protocol.FastModeField;
                if (!requested)
                {
                    result.State = "off";
                    result.Applied = "standard";
                    result.Reason = "Fast mode is disabled.";
                    return result;
                }
                if (protocol == null || !protocol.SupportsFastMode.HasValue)
                {
                    result.State = "unknown";
                    result.Applied = "unknown";
                    result.Reason = "The bundled app-server protocol has not reported a Fast service tier.";
                    return result;
                }
                if (!protocol.SupportsFastMode.Value)
                {
                    result.State = "unsupported";
                    result.Applied = "standard";
                    result.Reason = "This runtime does not report a Fast service tier; Standard is used on the same model.";
                    return result;
                }
                result.State = "requested";
                result.Applied = "fast";
                if (string.IsNullOrWhiteSpace(result.Field))
                {
                    result.State = "unknown";
                    result.Applied = "unknown";
                    result.Reason = "The runtime schema did not identify a per-turn Fast service-tier field.";
                    result.CanRetryStandard = false;
                    return result;
                }
                result.Reason = "Fast service tier will be requested for this turn.";
                result.CanRetryStandard = true;
                return result;
            }

            public static void Apply(Dictionary<string, object> parameters, CodexFastResolution resolution)
            {
                if (parameters == null || resolution == null) return;
                if (resolution.Requested && resolution.State == "requested" && !string.IsNullOrWhiteSpace(resolution.Field))
                {
                    parameters[resolution.Field] = "fast";
                    return;
                }
                // Standard must be explicit whenever the generated protocol identified a
                // per-turn field. This prevents a global Fast setting from leaking into Reign.
                if (!string.IsNullOrWhiteSpace(resolution.Field) && resolution.State != "unknown")
                    parameters[resolution.Field] = "default";
            }

            public static void ApplyThreadScope(Dictionary<string, object> parameters, CodexFastResolution resolution, CodexProtocolState protocol)
            {
                if (parameters == null || protocol == null || string.IsNullOrWhiteSpace(protocol.ThreadServiceTierField)) return;
                string value = resolution != null && resolution.Requested && resolution.State == "requested" ? "fast" : "default";
                parameters[protocol.ThreadServiceTierField] = value;
            }

            public static void ApplyStandard(Dictionary<string, object> parameters, CodexFastResolution resolution)
            {
                if (parameters == null || resolution == null || string.IsNullOrWhiteSpace(resolution.Field)) return;
                parameters[resolution.Field] = "default";
            }

            public static bool IsExplicitPreStartRejection(CodexRpcException error, CodexFastResolution resolution)
            {
                if (error == null || error.Method != "turn/start") return false;
                string text = (error.Message ?? "").ToLowerInvariant();
                string field = resolution == null ? "" : (resolution.Field ?? "").ToLowerInvariant();
                return IsExplicitCapabilityRejection(error.Code, text, field);
            }

            public static bool IsExplicitThreadScopeRejection(CodexRpcException error, CodexFastResolution resolution)
            {
                if (error == null || (error.Method != "thread/start" && error.Method != "thread/resume")) return false;
                string text = (error.Message ?? "").ToLowerInvariant();
                return IsExplicitCapabilityRejection(error.Code, text, resolution == null ? "" : resolution.Field.ToLowerInvariant());
            }

            private static bool IsExplicitCapabilityRejection(int code, string text, string field)
            {
                if (code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504) return false;
                bool namesTier = text.Contains("service tier") || text.Contains("service_tier") || text.Contains("fast mode")
                    || text.Contains("fast tier") || (!string.IsNullOrWhiteSpace(field) && text.Contains(field));
                bool explicitRejection = text.Contains("unsupported") || text.Contains("not supported")
                    || text.Contains("unrecognized") || text.Contains("unknown field") || text.Contains("invalid field")
                    || text.Contains("invalid value") || text.Contains("not available");
                bool validationCode = code == 0 || code == -32602 || code == 400 || code == 422;
                return namesTier && explicitRejection && validationCode;
            }
        }

        private sealed class CodexRpcException : InvalidOperationException
        {
            public readonly string Method;
            public readonly int Code;

            public CodexRpcException(string method, int code, string message)
                : base(message ?? "Codex app-server request failed.")
            {
                Method = method ?? "";
                Code = code;
            }
        }

        private sealed class CodexProtocolState
        {
            public string RuntimeVersion = "";
            public string ProtocolSchema = "codex-app-server-runtime-unknown";
            public string SchemaFingerprint = "";
            public DateTime InspectedUtc;
            public bool? SupportsOutputSchema;
            public bool? SupportsFastMode;
            public string FastModeField = "";
            public string ServiceNameField = "";
            public bool? SupportsBaseInstructions;
            public string BaseInstructionsField = "";
            public string ThreadServiceTierField = "";
            public string StandardServiceTierValue = "default";
            public List<string> ServiceTierValues = new List<string>();
            public bool SupportsThreadResume;
            public string InspectionState = "unknown";
            public string InspectionError = "";
            public string SchemaPath = "";
            public string SchemaSource = "";
            public int SchemaFileCount;

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["schema"] = ProtocolSchema,
                    ["runtimeVersion"] = RuntimeVersion,
                    ["schemaFingerprint"] = SchemaFingerprint,
                    ["inspectedUtc"] = InspectedUtc == default(DateTime) ? "" : InspectedUtc.ToString("o"),
                    ["inspectionState"] = InspectionState,
                    ["inspectionError"] = InspectionError,
                    ["supportsOutputSchema"] = SupportsOutputSchema.HasValue ? (object)SupportsOutputSchema.Value : null,
                    ["supportsFastMode"] = SupportsFastMode.HasValue ? (object)SupportsFastMode.Value : null,
                    ["fastModeField"] = FastModeField,
                    ["serviceNameField"] = ServiceNameField,
                    ["supportsBaseInstructions"] = SupportsBaseInstructions.HasValue ? (object)SupportsBaseInstructions.Value : null,
                    ["baseInstructionsField"] = BaseInstructionsField,
                    ["threadServiceTierField"] = ThreadServiceTierField,
                    ["standardServiceTierValue"] = StandardServiceTierValue,
                    ["serviceTierValues"] = ServiceTierValues.ToList(),
                    ["serviceTierValuesKnown"] = ServiceTierValues.Count > 0,
                    ["supportsThreadResume"] = SupportsThreadResume,
                    ["schemaPath"] = SchemaPath,
                    ["schemaSource"] = SchemaSource,
                    ["schemaFileCount"] = SchemaFileCount,
                    ["generatedSchemaCommand"] = "codex app-server generate-json-schema --out .codex-build/codex-runtime-schema"
                };
            }
        }

        private sealed class CodexCatalogState
        {
            public string RuntimeVersion = "";
            public DateTime RefreshedUtc;
            public int PageCount;
            public string Freshness = "unknown";
            public string DiscoveryState = "unknown";
            public string DiscoveryError = "";
            public readonly HashSet<string> ListedModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Verification = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> VerificationReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, object> ToDictionary(List<Dictionary<string, object>> models)
            {
                List<Dictionary<string, object>> verification = Verification
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new Dictionary<string, object>
                    {
                        ["model"] = pair.Key,
                        ["status"] = pair.Value,
                        ["reason"] = VerificationReasons.ContainsKey(pair.Key) ? VerificationReasons[pair.Key] : ""
                    }).ToList();
                return new Dictionary<string, object>
                {
                    ["runtimeVersion"] = RuntimeVersion,
                    ["refreshedUtc"] = RefreshedUtc == default(DateTime) ? "" : RefreshedUtc.ToString("o"),
                    ["pageCount"] = PageCount,
                    ["freshness"] = Freshness,
                    ["discoveryState"] = DiscoveryState,
                    ["discoveryError"] = DiscoveryError,
                    ["listedModels"] = (models ?? new List<Dictionary<string, object>>()).Count,
                    ["listedModelIds"] = ListedModelIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                    ["verification"] = verification
                };
            }
        }

        private static void RecordCodexProtocolInspection(Dictionary<string, object> initializeResult)
        {
            lock (CodexRuntimeStateLock)
            {
                Dictionary<string, object> result = initializeResult ?? new Dictionary<string, object>();
                Dictionary<string, object> capabilities = ReadDictionary(result, "capabilities") ?? result;
                CodexRuntimeProtocol.RuntimeVersion = FirstNonEmpty(
                    ReadString(result, "version", ""), ReadString(result, "runtimeVersion", ""), ReadString(result, "serverVersion", ""));
                CodexRuntimeProtocol.SchemaFingerprint = FirstNonEmpty(
                    ReadString(result, "schemaFingerprint", ""), ReadString(capabilities, "schemaFingerprint", ""));
                CodexRuntimeProtocol.ProtocolSchema = FirstNonEmpty(
                    ReadString(result, "protocolSchema", ""), ReadString(capabilities, "protocolSchema", ""), "codex-app-server-runtime-unknown");
                // Initialize is intentionally treated as advisory. The runtime's generated
                // schema below is the authority for request fields; old app-server builds do
                // not advertise these capabilities in initialize.
                CodexRuntimeProtocol.SupportsThreadResume = ReadBool(capabilities, "supportsThreadResume", false);
                CodexRuntimeProtocol.SupportsOutputSchema = ReadNullableBool(capabilities, "supportsOutputSchema", ReadNullableBool(result, "supportsOutputSchema", null));
                CodexRuntimeProtocol.SupportsFastMode = ReadNullableBool(capabilities, "supportsFastMode", ReadNullableBool(result, "supportsFastMode", null));
                CodexRuntimeProtocol.FastModeField = FirstNonEmpty(
                    ReadString(capabilities, "fastModeField", ""), ReadString(result, "fastModeField", ""));
                CodexRuntimeProtocol.ServiceNameField = ReadString(capabilities, "serviceNameField", ReadString(result, "serviceNameField", ""));
                CodexRuntimeProtocol.SupportsBaseInstructions = ReadNullableBool(capabilities, "supportsBaseInstructions", ReadNullableBool(result, "supportsBaseInstructions", null));
                CodexRuntimeProtocol.BaseInstructionsField = ReadString(capabilities, "baseInstructionsField", ReadString(result, "baseInstructionsField", ""));
                CodexRuntimeProtocol.ServiceTierValues = ReadStringList(capabilities, "serviceTierValues");
                List<string> tiers = ReadStringList(capabilities, "supportedServiceTiers");
                if (!CodexRuntimeProtocol.SupportsFastMode.HasValue && tiers.Count > 0)
                    CodexRuntimeProtocol.SupportsFastMode = tiers.Any(x => x.Equals("fast", StringComparison.OrdinalIgnoreCase));
                if (!CodexRuntimeProtocol.SupportsFastMode.HasValue && !string.IsNullOrWhiteSpace(CodexRuntimeProtocol.FastModeField))
                    CodexRuntimeProtocol.SupportsFastMode = true;
                CodexRuntimeProtocol.InspectedUtc = DateTime.UtcNow;
                CodexRuntimeProtocol.InspectionState = "initialize_inspected";
                CodexRuntimeProtocol.InspectionError = "";
            }
        }

        /// <summary>
        /// Inspect the exact bundled app-server protocol without starting a provider turn. The
        /// command only writes generated JSON schemas and exits; it never authenticates or calls a
        /// model. This is deliberately separate from model discovery and model verification.
        /// </summary>
        private static void InspectCodexProtocolFromBundle(string executable, Dictionary<string, object> settings,
            Dictionary<string, object> initializeResult)
        {
            RecordCodexProtocolInspection(initializeResult);
            string schemaDirectory = Path.Combine(CodexWorkingDirectory(), ".codex-build", "codex-runtime-schema");
            string schemaError = "";
            bool generated = false;
            try
            {
                Directory.CreateDirectory(schemaDirectory);
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = string.IsNullOrWhiteSpace(executable) ? "codex" : executable,
                    Arguments = "app-server generate-json-schema --out " + QuoteCodexProcessArgument(schemaDirectory),
                    WorkingDirectory = CodexWorkingDirectory(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                string codexHome = ReadString(settings, "codexHome", "").Trim();
                if (!string.IsNullOrWhiteSpace(codexHome)) start.EnvironmentVariables["CODEX_HOME"] = codexHome;
                using (Process process = new Process { StartInfo = start })
                {
                    if (!process.Start()) throw new InvalidOperationException("The Codex schema generator did not start.");
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("The Codex schema generator timed out after 10000 ms.");
                    }
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("Codex schema generation failed with exit code " + process.ExitCode + ": " + LimitText(error, 600));
                    generated = File.Exists(Path.Combine(schemaDirectory, "v2", "TurnStartParams.json"));
                    if (!generated) throw new InvalidOperationException("Codex schema generation completed without TurnStartParams.json.");
                }
            }
            catch (Exception ex)
            {
                schemaError = LimitText(ex.Message, 1200);
                // A previously generated schema is still useful as protocol evidence, but the
                // status remains explicit so the UI can distinguish it from a fresh inspection.
                generated = File.Exists(Path.Combine(schemaDirectory, "v2", "TurnStartParams.json"));
            }

            string version = RunCodexVersionProbe(executable, CodexWorkingDirectory(), settings);
            lock (CodexRuntimeStateLock)
            {
                CodexRuntimeProtocol.RuntimeVersion = FirstNonEmpty(version, CodexRuntimeProtocol.RuntimeVersion);
                CodexRuntimeProtocol.SchemaPath = schemaDirectory;
                CodexRuntimeProtocol.SchemaSource = generated ? (string.IsNullOrWhiteSpace(schemaError) ? "bundled_generated" : "bundled_cached") : "unavailable";
                CodexRuntimeProtocol.SchemaFileCount = generated ? Directory.GetFiles(schemaDirectory, "*.json", SearchOption.AllDirectories).Length : 0;
                if (generated)
                {
                    string turnPath = Path.Combine(schemaDirectory, "v2", "TurnStartParams.json");
                    string threadPath = Path.Combine(schemaDirectory, "v2", "ThreadStartParams.json");
                    string resumePath = Path.Combine(schemaDirectory, "v2", "ThreadResumeParams.json");
                    Dictionary<string, object> turnSchema = ReadGeneratedSchema(turnPath);
                    Dictionary<string, object> threadSchema = ReadGeneratedSchema(threadPath);
                    Dictionary<string, object> resumeSchema = ReadGeneratedSchema(resumePath);
                    Dictionary<string, object> turnProperties = ReadDictionary(turnSchema, "properties");
                    Dictionary<string, object> threadProperties = ReadDictionary(threadSchema, "properties");
                    Dictionary<string, object> resumeProperties = ReadDictionary(resumeSchema, "properties");
                    bool hasOutputSchema = turnProperties != null && turnProperties.ContainsKey("outputSchema");
                    bool hasTurnFast = turnProperties != null && turnProperties.ContainsKey("serviceTierForTurn");
                    bool hasTurnServiceTier = turnProperties != null && turnProperties.ContainsKey("serviceTier");
                    bool hasThreadServiceTier = threadProperties != null && threadProperties.ContainsKey("serviceTier");
                    if (!hasThreadServiceTier && resumeProperties != null) hasThreadServiceTier = resumeProperties.ContainsKey("serviceTier");
                    bool hasThreadServiceName = threadProperties != null && threadProperties.ContainsKey("serviceName");
                    bool hasThreadBaseInstructions = threadProperties != null && threadProperties.ContainsKey("baseInstructions");
                    if (!hasThreadBaseInstructions && resumeProperties != null) hasThreadBaseInstructions = resumeProperties.ContainsKey("baseInstructions");
                    CodexRuntimeProtocol.SupportsOutputSchema = hasOutputSchema;
                    CodexRuntimeProtocol.FastModeField = hasTurnFast ? "serviceTierForTurn" : (hasTurnServiceTier ? "serviceTier" : "");
                    CodexRuntimeProtocol.SupportsFastMode = hasTurnFast || hasTurnServiceTier;
                    CodexRuntimeProtocol.ServiceNameField = hasThreadServiceName ? "serviceName" : "";
                    CodexRuntimeProtocol.SupportsBaseInstructions = hasThreadBaseInstructions;
                    CodexRuntimeProtocol.BaseInstructionsField = hasThreadBaseInstructions ? "baseInstructions" : "";
                    CodexRuntimeProtocol.ThreadServiceTierField = hasThreadServiceTier ? "serviceTier" : "";
                    CodexRuntimeProtocol.ServiceTierValues = ReadSchemaStringEnums(
                        turnProperties != null && turnProperties.ContainsKey("serviceTier") ? ReadDictionary(turnProperties, "serviceTier") : null);
                    if (CodexRuntimeProtocol.ServiceTierValues.Count == 0)
                        CodexRuntimeProtocol.ServiceTierValues = ReadSchemaStringEnums(
                            threadProperties != null && threadProperties.ContainsKey("serviceTier") ? ReadDictionary(threadProperties, "serviceTier") : null);
                    CodexRuntimeProtocol.SupportsThreadResume = File.Exists(resumePath);
                    CodexRuntimeProtocol.SchemaFingerprint = HashCodexSchemaDirectory(schemaDirectory);
                    CodexRuntimeProtocol.ProtocolSchema = "codex-app-server-generated-json-schema-v1";
                    CodexRuntimeProtocol.InspectionState = string.IsNullOrWhiteSpace(schemaError) ? "schema_inspected" : "schema_cached";
                    CodexRuntimeProtocol.InspectionError = schemaError;
                }
                else
                {
                    CodexRuntimeProtocol.InspectionState = "schema_unavailable";
                    CodexRuntimeProtocol.InspectionError = schemaError;
                }
                CodexRuntimeProtocol.InspectedUtc = DateTime.UtcNow;
            }
        }

        private static Dictionary<string, object> ReadGeneratedSchema(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new Dictionary<string, object>();
            try { return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8)) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        private static List<string> ReadSchemaStringEnums(Dictionary<string, object> schema)
        {
            List<string> result = new List<string>();
            if (schema == null) return result;
            Action<Dictionary<string, object>> collect = value =>
            {
                if (value == null || !value.ContainsKey("enum") || value["enum"] == null) return;
                IEnumerable values = value["enum"] as IEnumerable;
                if (values == null) return;
                foreach (object item in values)
                {
                    string text = Convert.ToString(item);
                    if (!string.IsNullOrWhiteSpace(text) && !result.Contains(text, StringComparer.OrdinalIgnoreCase)) result.Add(text);
                }
            };
            collect(schema);
            IEnumerable alternatives = schema.ContainsKey("anyOf") ? schema["anyOf"] as IEnumerable : null;
            if (alternatives != null)
                foreach (object item in alternatives) collect(item as Dictionary<string, object>);
            return result;
        }

        private static string HashCodexSchemaDirectory(string directory)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    StringBuilder material = new StringBuilder();
                    foreach (string path in Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    {
                        material.Append(Path.GetFileName(path)).Append('\n').Append(File.ReadAllText(path, Encoding.UTF8)).Append('\n');
                    }
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(material.ToString()))).Replace("-", "").ToLowerInvariant();
                }
            }
            catch { return ""; }
        }

        private static string RunCodexVersionProbe(string executable, string workingDirectory, Dictionary<string, object> settings)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = string.IsNullOrWhiteSpace(executable) ? "codex" : executable,
                    Arguments = "--version",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                string codexHome = ReadString(settings, "codexHome", "").Trim();
                if (!string.IsNullOrWhiteSpace(codexHome)) start.EnvironmentVariables["CODEX_HOME"] = codexHome;
                using (Process process = new Process { StartInfo = start })
                {
                    if (!process.Start()) return "";
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } return ""; }
                    return LimitText(output, 120).Trim();
                }
            }
            catch { return ""; }
        }

        private static string QuoteCodexProcessArgument(string value)
        {
            // ProcessStartInfo uses the Windows command-line parser. Backslashes are path
            // characters here and must not be doubled unless they precede an escaped quote.
            string escaped = (value ?? "").Replace("\"", "\\\"");
            return "\"" + escaped + "\"";
        }

        private static void RecordCodexCatalogDiscovery(List<Dictionary<string, object>> models, string runtimeVersion, int pageCount)
        {
            lock (CodexRuntimeStateLock)
            {
                CodexRuntimeCatalog.RuntimeVersion = FirstNonEmpty(runtimeVersion, CodexRuntimeProtocol.RuntimeVersion);
                CodexRuntimeCatalog.RefreshedUtc = DateTime.UtcNow;
                CodexRuntimeCatalog.PageCount = Math.Max(0, pageCount);
                CodexRuntimeCatalog.Freshness = "fresh";
                CodexRuntimeCatalog.DiscoveryState = "success";
                CodexRuntimeCatalog.DiscoveryError = "";
                CodexRuntimeCatalog.ListedModelIds.Clear();
                foreach (Dictionary<string, object> model in models ?? new List<Dictionary<string, object>>())
                {
                    string id = CodexModelId(model);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    CodexRuntimeCatalog.ListedModelIds.Add(id);
                    if (!CodexRuntimeCatalog.Verification.ContainsKey(id) || CodexRuntimeCatalog.Verification[id] == "listed")
                    {
                        CodexRuntimeCatalog.Verification[id] = "listed";
                        CodexRuntimeCatalog.VerificationReasons[id] = "Returned by model/list; no bounded verification has run.";
                    }
                }
            }
        }

        private static void RecordCodexCatalogFailure(string error, bool timeout)
        {
            lock (CodexRuntimeStateLock)
            {
                CodexRuntimeCatalog.DiscoveryState = timeout ? "timeout" : "error";
                CodexRuntimeCatalog.DiscoveryError = LimitText(error, 1200);
                CodexRuntimeCatalog.Freshness = CodexRuntimeCatalog.RefreshedUtc == default(DateTime) ? "unknown" : "stale";
            }
        }

        private static CodexCatalogState CloneCodexCatalogState(CodexCatalogState source)
        {
            CodexCatalogState result = new CodexCatalogState();
            if (source == null) return result;
            result.RuntimeVersion = source.RuntimeVersion;
            result.RefreshedUtc = source.RefreshedUtc;
            result.PageCount = source.PageCount;
            result.Freshness = source.Freshness;
            result.DiscoveryState = source.DiscoveryState;
            result.DiscoveryError = source.DiscoveryError;
            foreach (string id in source.ListedModelIds) result.ListedModelIds.Add(id);
            foreach (KeyValuePair<string, string> pair in source.Verification) result.Verification[pair.Key] = pair.Value;
            foreach (KeyValuePair<string, string> pair in source.VerificationReasons) result.VerificationReasons[pair.Key] = pair.Value;
            return result;
        }

        private static Dictionary<string, object> CodexCatalogDiagnostics(List<Dictionary<string, object>> models)
        {
            lock (CodexRuntimeStateLock)
            {
                if (CodexRuntimeCatalog.RefreshedUtc != default(DateTime)
                    && DateTime.UtcNow.Subtract(CodexRuntimeCatalog.RefreshedUtc).TotalHours > 24d)
                    CodexRuntimeCatalog.Freshness = "stale";
                return CodexRuntimeCatalog.ToDictionary(models);
            }
        }

        private static Dictionary<string, object> GetCodexModelVerificationStatus(string candidate = CodexModelVerificationCandidate)
        {
            candidate = (candidate ?? "").Trim();
            if (string.IsNullOrWhiteSpace(candidate)) candidate = CodexModelVerificationCandidate;
            lock (CodexRuntimeStateLock)
            {
                string status;
                if (!CodexRuntimeCatalog.Verification.TryGetValue(candidate, out status)) status = "unverified";
                return new Dictionary<string, object>
                {
                    ["model"] = candidate,
                    ["listed"] = CodexRuntimeCatalog.ListedModelIds.Contains(candidate),
                    ["status"] = status,
                    ["reason"] = CodexRuntimeCatalog.VerificationReasons.ContainsKey(candidate) ? CodexRuntimeCatalog.VerificationReasons[candidate] : "The model is not listed in the latest catalog; access is unverified.",
                    ["providerCalls"] = 0,
                    ["liveTestRun"] = false
                };
            }
        }

        private static Dictionary<string, object> BuildCodexModelVerificationPlan(string candidate, Dictionary<string, object> payload)
        {
            candidate = string.IsNullOrWhiteSpace(candidate) ? CodexModelVerificationCandidate : candidate.Trim();
            bool explicitlyAuthorized = ReadBool(payload, "authorizeProviderUsage", false)
                || ReadBool(payload, "explicitAuthorization", false)
                || string.Equals(ReadString(payload, "confirmation", ""), "verify Codex model availability", StringComparison.OrdinalIgnoreCase);
            Dictionary<string, object> status = GetCodexModelVerificationStatus(candidate);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["model"] = candidate,
                ["listed"] = ReadBool(status, "listed", false),
                ["status"] = ReadString(status, "status", "unverified"),
                ["requiresExplicitAuthorization"] = !explicitlyAuthorized,
                ["authorized"] = explicitlyAuthorized,
                ["providerCallCap"] = 1,
                ["providerCalls"] = 0,
                ["execute"] = false,
                ["message"] = explicitlyAuthorized
                    ? "A bounded verification may run only through the separately gated provider-test path; this plan does not execute it."
                    : "Model availability verification consumes Codex usage and requires an explicit bounded test authorization.",
                ["verification"] = status
            };
        }

        private static Dictionary<string, object> EvaluateCodexModelVerification(
            string requestedModel,
            string authoritativeModel,
            Dictionary<string, object> error,
            bool timedOut,
            int providerCalls = 1)
        {
            requestedModel = (requestedModel ?? "").Trim();
            authoritativeModel = (authoritativeModel ?? "").Trim();
            string status;
            string reason;
            if (timedOut)
            {
                status = "inconclusive";
                reason = "The bounded verification timed out; no access conclusion is safe.";
            }
            else if (error != null)
            {
                string message = ReadString(error, "message", Json.Serialize(error));
                string lowered = message.ToLowerInvariant();
                if (lowered.Contains("unknown model") || lowered.Contains("unsupported model") || lowered.Contains("model not found") || lowered.Contains("not available"))
                {
                    status = "unavailable";
                    reason = "The runtime explicitly rejected the requested model.";
                }
                else
                {
                    status = "inconclusive";
                    reason = "The verification failed without an explicit unsupported-model response.";
                }
            }
            else if (string.IsNullOrWhiteSpace(authoritativeModel))
            {
                status = "inconclusive";
                reason = "The runtime did not report an authoritative model identity; request echoes are insufficient proof.";
            }
            else if (!authoritativeModel.Equals(requestedModel, StringComparison.OrdinalIgnoreCase))
            {
                status = "unavailable";
                reason = "The runtime reported a different model, so substitution was detected.";
            }
            else
            {
                status = "verified";
                reason = "The runtime reported the requested model identity for the bounded verification.";
            }
            lock (CodexRuntimeStateLock)
            {
                if (!string.IsNullOrWhiteSpace(requestedModel))
                {
                    CodexRuntimeCatalog.Verification[requestedModel] = status;
                    CodexRuntimeCatalog.VerificationReasons[requestedModel] = reason;
                }
            }
            return new Dictionary<string, object>
            {
                ["model"] = requestedModel,
                ["reportedModel"] = authoritativeModel,
                ["status"] = status,
                ["reason"] = reason,
                ["providerCalls"] = Math.Max(0, providerCalls),
                ["liveTestRun"] = providerCalls > 0
            };
        }

        /// <summary>
        /// Explicitly gated model verification. Discovery never calls this method. The caller must
        /// provide the exact confirmation, bounded=true, and maxRequests=1. Once admitted, this
        /// method sends one fresh short turn with no Fast fallback, retry, repair, or replay.
        /// </summary>
        private static Dictionary<string, object> CodexVerifyModelApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string candidate = ReadString(payload, "model", CodexModelVerificationCandidate).Trim();
            int maxRequests = ReadInt(payload, "maxRequests", 0);
            bool bounded = ReadBool(payload, "bounded", false);
            bool authorized = string.Equals(ReadString(payload, "confirmation", ""), "verify one Codex model request", StringComparison.Ordinal);
            Dictionary<string, object> plan = BuildCodexModelVerificationPlan(candidate, payload);
            plan["requiresExplicitAuthorization"] = !authorized;
            plan["authorized"] = authorized;
            plan["execute"] = false;
            if (!UsesCodexSubscription(LoadSettings()))
            {
                plan["ok"] = false;
                plan["error"] = "Select Codex before checking a Codex model.";
                return plan;
            }
            if (!authorized || !bounded || maxRequests != 1)
            {
                plan["ok"] = false;
                plan["error"] = "Model verification requires confirmation 'verify one Codex model request', bounded=true, and maxRequests=1.";
                return plan;
            }

            Dictionary<string, object> settings = LoadSettings();
            string correlationId = FirstNonEmpty(ReadString(payload, "correlationId", ""), Guid.NewGuid().ToString("N"));
            CodexRuntimeRequestContext context = CaptureCodexRequestContext(settings, payload, "model_verification", candidate, correlationId,
                new Dictionary<string, object>
                {
                    ["requestType"] = "model_verification", ["model"] = candidate, ["correlationId"] = correlationId,
                    ["requestedReasoningEffort"] = "none", ["options"] = new Dictionary<string, object>
                    {
                        ["schema"] = CodexOptionsSchema, ["reasoningMode"] = "direct", ["reasoningEffort"] = "none",
                        ["fastMode"] = false
                    }
                });
            context.Fast = CodexFastMode.Resolve(false, CodexRuntimeProtocol);
            CodexRequestTimings timings = new CodexRequestTimings();
            CodexTurnAssembly assembly = null;
            CodexTurnWaiter waiter = null;
            string threadId = "";
            string turnId = "";
            Dictionary<string, object> errorResult = null;
            bool timedOut = false;
            bool providerCallReserved = false;
            bool turnStarted = false;
            bool cancelled = false;
            bool cleanupFallback = false;
            try
            {
                EnsureCodexAppServer(settings);
                timings.ThreadStartUtc = DateTime.UtcNow;
                Dictionary<string, object> threadParameters = new Dictionary<string, object>
                {
                    ["model"] = candidate,
                    ["cwd"] = CodexWorkingDirectory(),
                    ["approvalPolicy"] = "never",
                    ["sandbox"] = "read-only"
                };
                CodexFastMode.ApplyThreadScope(threadParameters, context.Fast, CodexRuntimeProtocol);
                Dictionary<string, object> thread = CodexRpc("thread/start", threadParameters, Math.Min(CodexRpcTimeout(settings), 30000));
                threadId = ReadNestedId(thread, "thread");
                if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidOperationException("Codex verification did not receive a fresh thread id.");
                timings.ThreadReadyUtc = DateTime.UtcNow;
                Dictionary<string, object> turnParameters = new Dictionary<string, object>
                {
                    ["threadId"] = threadId,
                    ["input"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = "Reply with exactly CODEX_MODEL_VERIFY." }
                    },
                    ["model"] = candidate,
                    ["effort"] = context.ResolvedReasoningEffort,
                    ["approvalPolicy"] = "never",
                    ["sandboxPolicy"] = new Dictionary<string, object> { ["type"] = "readOnly" }
                };
                CodexFastMode.Apply(turnParameters, context.Fast);
                // This is the only provider-consuming dispatch in the verification operation.
                BeginCodexTurnAttempt(threadId);
                ReserveCodexPerformanceCall(context.ToDictionary());
                providerCallReserved = true;
                timings.TurnStartUtc = DateTime.UtcNow;
                Dictionary<string, object> turn = CodexRpc("turn/start", turnParameters, Math.Min(CodexRpcTimeout(settings), 30000));
                turnId = ReadNestedId(turn, "turn");
                if (string.IsNullOrWhiteSpace(turnId)) throw new InvalidOperationException("Codex verification did not receive a turn id.");
                turnStarted = true;
                RegisterCodexTurn(turnId, threadId);
                waiter = GetOrCreateCodexTurn(turnId, threadId);
                if (waiter == null) throw new InvalidOperationException("Codex verification turn was already retired.");
                WaitForCodexTurn(waiter, Math.Min(CodexRpcTimeout(settings), 30000), true);
                assembly = waiter.Assembly.Snapshot();
                timings.FirstTextUtc = assembly.FirstTextUtc;
                timings.TurnCompletedUtc = assembly.CompletedUtc == default(DateTime) ? DateTime.UtcNow : assembly.CompletedUtc;
                if (!string.IsNullOrWhiteSpace(assembly.Error))
                    errorResult = new Dictionary<string, object> { ["message"] = assembly.Error };
            }
            catch (CodexRpcException ex)
            {
                errorResult = new Dictionary<string, object> { ["message"] = ex.Message, ["code"] = ex.Code };
            }
            catch (TimeoutException ex)
            {
                timedOut = true;
                errorResult = new Dictionary<string, object> { ["message"] = ex.Message };
            }
            catch (OperationCanceledException ex)
            {
                cancelled = true;
                errorResult = new Dictionary<string, object> { ["message"] = ex.Message, ["cancelled"] = true };
            }
            catch (Exception ex)
            {
                errorResult = new Dictionary<string, object> { ["message"] = ex.Message };
            }
            finally
            {
                if (waiter != null)
                {
                    assembly = waiter.Assembly.Snapshot();
                    if (timings.FirstTextUtc == default(DateTime)) timings.FirstTextUtc = assembly.FirstTextUtc;
                    if (timings.TurnCompletedUtc == default(DateTime)) timings.TurnCompletedUtc = assembly.CompletedUtc;
                }
                if (turnStarted && (cancelled || timedOut || errorResult != null))
                    InterruptCodexTurn(threadId, turnId, settings);
                RetireCodexTurn(turnId);
                if (string.IsNullOrWhiteSpace(turnId)) EndCodexTurnAttempt(threadId);
                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    timings.CleanupQueuedUtc = DateTime.UtcNow;
                    QueueCodexThreadCleanup(threadId, settings, ref cleanupFallback);
                    timings.CleanupCompletedUtc = DateTime.UtcNow;
                }
            }
            Dictionary<string, object> verification = EvaluateCodexModelVerification(candidate,
                assembly == null ? "" : assembly.ServedModel, errorResult, timedOut,
                providerCallReserved ? 1 : 0);
            verification["ok"] = ReadString(verification, "status", "") == "verified";
            verification["maxRequests"] = 1;
            verification["bounded"] = true;
            verification["confirmation"] = "verified explicit bounded request";
            verification["diagnostics"] = CodexRuntimeDiagnostics(context, timings, assembly, cleanupFallback);
            // Do not return the short probe text; only its authoritative model identity and
            // diagnostics are relevant to availability verification.
            return verification;
        }

        private sealed class CodexTurnAssembly
        {
            public string Text = "";
            public string Status = "running";
            public string Error = "";
            public string AuthoritativeModel = "";
            public string ReportedModel = "";
            public string ServedModel = "";
            public string ModelEvidence = "";
            public DateTime FirstTextUtc;
            public DateTime CompletedUtc;
            public Dictionary<string, object> Usage;
            public bool Completed;
        }

        private sealed class CodexItemBuffer
        {
            public string ItemId;
            public string Phase = "";
            public string Text = "";
            public bool Completed;
            public DateTime CompletedUtc;
        }

        /// <summary>
        /// Event assembly is scoped by turn and item. Deltas are useful for timing only; the final completed
        /// agent message is authoritative, so incomplete or rejected drafts are never returned to Reign.
        /// </summary>
        private sealed class CodexTurnEventAssembler
        {
            private readonly object Sync = new object();
            private readonly string TurnId;
            private string ThreadId;
            private readonly Dictionary<string, CodexItemBuffer> Items = new Dictionary<string, CodexItemBuffer>(StringComparer.OrdinalIgnoreCase);
            private string LastCompletedAgentItemId = "";
            private string FinalAgentItemId = "";
            private string AccumulatedText = "";
            private string FinalText = "";
            private string Status = "running";
            private string Error = "";
            private string ReportedModel = "";
            private string ServedModel = "";
            private string ModelEvidence = "";
            private DateTime FirstTextUtc;
            private DateTime CompletedUtc;
            private Dictionary<string, object> Usage;
            private bool UsageFromThreadTokenUpdate;
            private bool Completed;

            public CodexTurnEventAssembler(string turnId, string threadId = "")
            {
                TurnId = turnId ?? "";
                ThreadId = threadId ?? "";
            }

            public void SetThreadId(string threadId)
            {
                if (string.IsNullOrWhiteSpace(threadId)) return;
                lock (Sync)
                {
                    if (string.IsNullOrWhiteSpace(ThreadId)) ThreadId = threadId;
                }
            }

            public void Accept(Dictionary<string, object> message)
            {
                if (message == null) return;
                string method = ReadString(message, "method", "");
                Dictionary<string, object> parameters = ReadDictionary(message, "params") ?? new Dictionary<string, object>();
                Dictionary<string, object> turn = ReadDictionary(parameters, "turn");
                string eventTurnId = FirstNonEmpty(ReadString(parameters, "turnId", ""), ReadString(turn, "id", ""));
                if (!string.IsNullOrWhiteSpace(eventTurnId) && !string.IsNullOrWhiteSpace(TurnId)
                    && !eventTurnId.Equals(TurnId, StringComparison.OrdinalIgnoreCase)) return;
                string eventThreadId = FirstNonEmpty(ReadString(parameters, "threadId", ""), ReadString(turn, "threadId", ""));
                lock (Sync)
                {
                    if (!string.IsNullOrWhiteSpace(eventThreadId) && !string.IsNullOrWhiteSpace(ThreadId)
                        && !eventThreadId.Equals(ThreadId, StringComparison.OrdinalIgnoreCase)) return;

                    // The app-server reports usage on its own thread/tokenUsage/updated
                    // notification.  It can arrive before or after turn/completed, so keep
                    // this branch ahead of the Completed guard and retain the per-turn
                    // "last" breakdown while preserving the cumulative "total" breakdown
                    // for diagnostics.
                    if (method == "thread/tokenUsage/updated" || method == "thread/token_usage/updated")
                    {
                        Dictionary<string, object> updatedUsage = ReadUsage(parameters);
                        if (updatedUsage != null)
                        {
                            Usage = updatedUsage;
                            UsageFromThreadTokenUpdate = true;
                        }
                        return;
                    }
                    if (Completed) return;
                    if (method == "turn/started")
                    {
                        ReportedModel = FirstNonEmpty(ReadString(turn, "model", ""), ReadString(parameters, "model", ""), ReportedModel);
                        return;
                    }
                    if (method == "model/rerouted")
                    {
                        string fromModel = ReadString(parameters, "fromModel", "");
                        string toModel = ReadString(parameters, "toModel", "");
                        if (!string.IsNullOrWhiteSpace(toModel))
                        {
                            ServedModel = toModel;
                            ModelEvidence = "model/rerouted";
                        }
                        if (!string.IsNullOrWhiteSpace(fromModel)) ReportedModel = FirstNonEmpty(ReportedModel, fromModel);
                        return;
                    }
                    if (method == "item/agentMessage/delta")
                    {
                        string itemId = FirstNonEmpty(ReadString(parameters, "itemId", ""), ReadString(ReadDictionary(parameters, "item"), "id", ""), "agent-message");
                        CodexItemBuffer item = GetItem(itemId);
                        string delta = ReadString(parameters, "delta", "");
                        if (delta.Length > 0 && FirstTextUtc == default(DateTime)) FirstTextUtc = DateTime.UtcNow;
                        item.Text += delta;
                        AccumulatedText += delta;
                        return;
                    }
                    if (method == "item/completed")
                    {
                        Dictionary<string, object> itemObject = ReadDictionary(parameters, "item") ?? parameters;
                        string type = ReadString(itemObject, "type", "");
                        if (!type.Equals("agentMessage", StringComparison.OrdinalIgnoreCase)) return;
                        string itemId = FirstNonEmpty(ReadString(itemObject, "id", ""), ReadString(parameters, "itemId", ""), "agent-message");
                        CodexItemBuffer item = GetItem(itemId);
                        string completedText = ExtractCodexItemText(itemObject);
                        // Some runtimes emit a compact item/completed record without the
                        // already-streamed text. Preserve the accumulated delta in that case;
                        // a non-empty completed payload remains authoritative.
                        if (!string.IsNullOrWhiteSpace(completedText) || string.IsNullOrWhiteSpace(item.Text))
                            item.Text = completedText;
                        if (!string.IsNullOrWhiteSpace(item.Text) && FirstTextUtc == default(DateTime))
                            FirstTextUtc = DateTime.UtcNow;
                        item.Phase = ReadString(itemObject, "phase", "");
                        item.Completed = true;
                        item.CompletedUtc = DateTime.UtcNow;
                        LastCompletedAgentItemId = itemId;
                        ReportedModel = FirstNonEmpty(ReadString(itemObject, "model", ""), ReportedModel);
                        Dictionary<string, object> itemUsage = ReadUsage(itemObject);
                        if (itemUsage != null && !UsageFromThreadTokenUpdate) Usage = itemUsage;
                        if (item.Phase.Equals("final_answer", StringComparison.OrdinalIgnoreCase))
                        {
                            FinalAgentItemId = itemId;
                            FinalText = item.Text;
                        }
                        else if (string.IsNullOrWhiteSpace(FinalAgentItemId))
                        {
                            AccumulatedText = completedText;
                        }
                        return;
                    }
                    if (method == "turn/completed")
                    {
                        Status = turn == null ? ReadString(parameters, "status", "completed") : ReadString(turn, "status", "completed");
                        Dictionary<string, object> error = turn == null ? ReadDictionary(parameters, "error") : ReadDictionary(turn, "error");
                        Error = error == null ? "" : ReadString(error, "message", Json.Serialize(error));
                        Dictionary<string, object> completedUsage = turn == null ? ReadUsage(parameters) : ReadUsage(turn);
                        if (completedUsage != null && !UsageFromThreadTokenUpdate) Usage = completedUsage;
                        ReportedModel = FirstNonEmpty(turn == null ? "" : ReadString(turn, "model", ""), ReadString(parameters, "model", ""), ReportedModel);
                        if (!Status.Equals("completed", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Error))
                            Error = "Codex turn ended with status '" + Status + "'.";
                        Completed = true;
                        CompletedUtc = DateTime.UtcNow;
                    }
                }
            }

            public CodexTurnAssembly Snapshot()
            {
                lock (Sync)
                {
                    string final = !string.IsNullOrWhiteSpace(FinalAgentItemId) ? FinalText
                        : !string.IsNullOrWhiteSpace(LastCompletedAgentItemId) ? Items[LastCompletedAgentItemId].Text
                        : AccumulatedText;
                    return new CodexTurnAssembly
                    {
                        Text = final ?? "",
                        Status = Status,
                        Error = Error,
                        AuthoritativeModel = ServedModel,
                        ReportedModel = ReportedModel,
                        ServedModel = ServedModel,
                        ModelEvidence = ModelEvidence,
                        FirstTextUtc = FirstTextUtc,
                        CompletedUtc = CompletedUtc,
                        Usage = Usage == null ? null : new Dictionary<string, object>(Usage),
                        Completed = Completed
                    };
                }
            }

            private CodexItemBuffer GetItem(string itemId)
            {
                CodexItemBuffer item;
                if (!Items.TryGetValue(itemId, out item))
                {
                    item = new CodexItemBuffer { ItemId = itemId };
                    Items[itemId] = item;
                }
                return item;
            }
        }

        private static Dictionary<string, object> ReadUsage(Dictionary<string, object> source)
        {
            if (source == null) return null;
            Dictionary<string, object> usage = ReadDictionary(source, "usage") ?? ReadDictionary(source, "tokenUsage") ?? ReadDictionary(source, "token_usage");
            if (usage == null) return NormalizeKnownUsage(source);

            // ThreadTokenUsageUpdatedNotification contains both a per-turn `last`
            // breakdown and a cumulative `total` breakdown.  Reign's standard usage
            // contract must describe this turn, so expose `last` as the standard fields
            // and retain the cumulative values under an explicitly namespaced diagnostic
            // field.  A plain usage object (turn/completed or item/completed) continues
            // to normalize directly.
            Dictionary<string, object> last = ReadDictionary(usage, "last");
            Dictionary<string, object> total = ReadDictionary(usage, "total");
            Dictionary<string, object> result = NormalizeKnownUsage(last ?? usage);
            if (last != null && total != null)
            {
                Dictionary<string, object> normalizedTotal = NormalizeKnownUsage(total);
                if (normalizedTotal != null)
                {
                    if (result == null) result = new Dictionary<string, object>();
                    result["codex_thread_total"] = normalizedTotal;
                    object totalTokens;
                    if (normalizedTotal.TryGetValue("total_tokens", out totalTokens) && totalTokens != null)
                        result["codex_thread_total_tokens"] = totalTokens;
                }
            }
            return result;
        }

        private static Dictionary<string, object> NormalizeKnownUsage(Dictionary<string, object> usage)
        {
            if (usage == null) return null;
            Dictionary<string, object> result = new Dictionary<string, object>();
            CopyUsageNumber(usage, result, "prompt_tokens", "prompt_tokens", "input_tokens", "inputTokens", "promptTokens");
            CopyUsageNumber(usage, result, "completion_tokens", "completion_tokens", "output_tokens", "outputTokens", "completionTokens");
            CopyUsageNumber(usage, result, "total_tokens", "total_tokens", "totalTokens");
            CopyUsageNumber(usage, result, "reasoning_tokens", "reasoning_tokens", "reasoningTokens", "reasoningOutputTokens", "reasoning_output_tokens");

            Dictionary<string, object> details = new Dictionary<string, object>();
            CopyUsageNumber(usage, details, "cached_tokens", "cached_tokens", "cachedInputTokens", "cached_input_tokens");
            CopyUsageNumber(usage, details, "cache_write_tokens", "cache_write_tokens", "cacheWriteInputTokens", "cache_write_input_tokens");
            if (details.Count > 0) result["prompt_tokens_details"] = details;
            return result.Count == 0 ? null : result;
        }

        private static bool ShouldCancelCodexWait(bool verification)
        {
            if (ShutdownRequested) return true;
            return verification && ActiveCodexPerformanceBudget.Value != null && ShouldCancelVerification();
        }

        private static void WaitForCodexTurn(CodexTurnWaiter waiter, int timeoutMs, bool verification)
        {
            if (waiter == null) throw new InvalidOperationException("Codex turn waiter was not created.");
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (!waiter.Completed.Wait(250))
            {
                if (ShouldCancelCodexWait(verification))
                    throw new OperationCanceledException(verification ? "Codex verification was cancelled." : "Codex request was cancelled.");
                if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                    throw new TimeoutException("Codex app-server turn timed out after " + timeoutMs + " ms.");
            }
        }

        private static void InterruptCodexTurn(string threadId, string turnId, Dictionary<string, object> settings)
        {
            if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId)) return;
            try
            {
                CodexRpc("turn/interrupt", new Dictionary<string, object>
                {
                    ["threadId"] = threadId,
                    ["turnId"] = turnId
                }, Math.Min(CodexRpcTimeout(settings), 10000));
            }
            catch { }
        }

        private static void CopyUsageNumber(Dictionary<string, object> source, Dictionary<string, object> target, string targetKey, params string[] keys)
        {
            foreach (string key in keys ?? new string[0])
            {
                if (source != null && source.ContainsKey(key) && source[key] != null)
                {
                    target[targetKey] = source[key];
                    return;
                }
            }
        }

        private sealed class CodexRequestTimings
        {
            public readonly DateTime CreatedUtc = DateTime.UtcNow;
            public DateTime ThreadStartUtc;
            public DateTime ThreadReadyUtc;
            public DateTime TurnStartUtc;
            public DateTime FirstTextUtc;
            public DateTime TurnCompletedUtc;
            public DateTime CleanupQueuedUtc;
            public DateTime CleanupCompletedUtc;

            public Dictionary<string, object> ToDictionary()
            {
                object queue = ElapsedMilliseconds(CreatedUtc, ThreadStartUtc);
                object threadStartup = ElapsedMilliseconds(ThreadStartUtc, ThreadReadyUtc);
                object firstText = ElapsedMilliseconds(CreatedUtc, FirstTextUtc);
                object generation = ElapsedMilliseconds(TurnStartUtc, TurnCompletedUtc);
                object cleanup = ElapsedMilliseconds(CleanupQueuedUtc, CleanupCompletedUtc);
                return new Dictionary<string, object>
                {
                    ["createdUtc"] = CreatedUtc.ToString("o"),
                    ["queueMs"] = queue,
                    ["threadStartupMs"] = threadStartup,
                    ["threadStartMs"] = threadStartup,
                    ["turnStartMs"] = ElapsedMilliseconds(CreatedUtc, TurnStartUtc),
                    ["firstTextMs"] = firstText,
                    ["generationMs"] = generation,
                    ["cleanupQueueMs"] = ElapsedMilliseconds(TurnCompletedUtc, CleanupQueuedUtc),
                    ["cleanupMs"] = cleanup,
                    ["stages"] = new Dictionary<string, object>
                    {
                        ["queue"] = queue,
                        ["threadStartup"] = threadStartup,
                        ["firstText"] = firstText,
                        ["generation"] = generation,
                        ["cleanup"] = cleanup
                    }
                };
            }

            private static object ElapsedMilliseconds(DateTime start, DateTime end)
            {
                return start == default(DateTime) || end == default(DateTime) ? null : Math.Max(0d, (end - start).TotalMilliseconds);
            }
        }

        private sealed class CodexThreadIdentity
        {
            public string CampaignId;
            public string TimelineId;
            public string LoadGenerationId;
            public string SessionId;
            public string SpeakerId;
            public string ConversationMode;
            public string VisibilityScope;
            public string Model;
            public string ReasoningEffort;
            public bool FastMode;
            public string SchemaId;
            public string SchemaState;
            public string AuthorityHash;
            public string PromptContractHash;
            public string Key;

            public static bool TryCreate(CodexRuntimeRequestContext context, out CodexThreadIdentity identity)
            {
                identity = null;
                if (context == null || !context.Options.ReuseThreads || !context.HasExplicitThreadMetadata) return false;
                string timeline = FirstNonEmpty(context.TimelineId, context.LoadGenerationId);
                if (string.IsNullOrWhiteSpace(timeline) || string.IsNullOrWhiteSpace(context.AuthorityHash)) return false;
                identity = new CodexThreadIdentity
                {
                    CampaignId = context.CampaignId,
                    TimelineId = context.TimelineId,
                    LoadGenerationId = context.LoadGenerationId,
                    SessionId = context.SessionId,
                    SpeakerId = context.SpeakerId,
                    ConversationMode = context.ConversationMode,
                    VisibilityScope = context.VisibilityScope,
                    Model = context.Model,
                    ReasoningEffort = context.ResolvedReasoningEffort,
                    FastMode = context.Options.FastMode,
                    SchemaId = context.SchemaId,
                    SchemaState = context.SchemaState,
                    AuthorityHash = context.AuthorityHash,
                    PromptContractHash = context.PromptContractHash
                };
                identity.Key = string.Join("\u001f", new[]
                {
                    identity.CampaignId, timeline, identity.SessionId, identity.ConversationMode,
                    identity.SpeakerId, identity.VisibilityScope, identity.Model, identity.ReasoningEffort,
                    identity.FastMode ? "fast" : "standard", identity.SchemaId, identity.SchemaState, identity.AuthorityHash, identity.PromptContractHash
                });
                return true;
            }
        }

        private sealed class CodexThreadLease
        {
            public string ThreadId;
            public string CorrelationId;
            public string IdentityKey;
            public bool NewThread;
            public DateTime AcquiredUtc;
            public string PreviousPromptText = "";
            public string PreviousAcceptedHistorySignature = "";
        }

        private sealed class CodexThreadReusePool
        {
            private sealed class Entry
            {
                public CodexThreadIdentity Identity;
                public string ThreadId;
                public int Turns;
                public long InputTokens;
                public DateTime LastUsedUtc;
                public bool Leased;
                public bool Finalized;
                public bool Invalidated;
                public string LastPromptText = "";
                public string PendingPromptText = "";
                public string LastAcceptedHistorySignature = "";
                public string PendingCanonicalHistorySignature = "";
            }

            private readonly object Sync = new object();
            private readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            private readonly List<string> EvictedThreadIds = new List<string>();
            private const int MaxEntries = 64;
            private const int MaxTurns = 8;
            private const long MaxInputTokens = 32000;
            private static readonly TimeSpan IdleExpiry = TimeSpan.FromMinutes(10);

            public CodexThreadLease Acquire(CodexRuntimeRequestContext context)
            {
                if (!CodexThreadIdentity.TryCreate(context, out CodexThreadIdentity identity)) return null;
                lock (Sync)
                {
                    EvictExpiredLocked();
                    EvictInvalidatedLocked();
                    Entry entry;
                    if (Entries.TryGetValue(identity.Key, out entry) && !entry.Leased && !entry.Invalidated && entry.Finalized
                        && entry.Identity.Model.Equals(identity.Model, StringComparison.OrdinalIgnoreCase)
                        && entry.Identity.ReasoningEffort.Equals(identity.ReasoningEffort, StringComparison.OrdinalIgnoreCase)
                        && entry.Identity.FastMode == identity.FastMode
                        && entry.Identity.SchemaId.Equals(identity.SchemaId, StringComparison.OrdinalIgnoreCase)
                        && entry.Identity.AuthorityHash.Equals(identity.AuthorityHash, StringComparison.Ordinal)
                        && entry.Identity.PromptContractHash.Equals(identity.PromptContractHash, StringComparison.Ordinal))
                    {
                        entry.Leased = true;
                        entry.LastUsedUtc = DateTime.UtcNow;
                        return new CodexThreadLease
                        {
                            ThreadId = entry.ThreadId,
                            CorrelationId = context.CorrelationId,
                            IdentityKey = identity.Key,
                            AcquiredUtc = DateTime.UtcNow,
                            PreviousPromptText = entry.LastPromptText ?? "",
                            PreviousAcceptedHistorySignature = entry.LastAcceptedHistorySignature ?? ""
                        };
                    }
                    return null;
                }
            }

            public CodexThreadLease RegisterNew(CodexRuntimeRequestContext context, string threadId)
            {
                if (!CodexThreadIdentity.TryCreate(context, out CodexThreadIdentity identity) || string.IsNullOrWhiteSpace(threadId)) return null;
                lock (Sync)
                {
                    Entry prior;
                    if (Entries.TryGetValue(identity.Key, out prior))
                    {
                        // A leased entry belongs to an in-flight request. Never replace it with
                        // a second thread under the same identity; the caller will retain the
                        // fresh thread only for this request and clean it up on completion.
                        if (prior.Leased) return null;
                        if (!string.IsNullOrWhiteSpace(prior.ThreadId)) EvictedThreadIds.Add(prior.ThreadId);
                        Entries.Remove(identity.Key);
                    }
                    EvictToBoundLocked();
                    if (Entries.Count >= MaxEntries) return null;
                    Entry entry = new Entry
                    {
                        Identity = identity,
                        ThreadId = threadId,
                        LastUsedUtc = DateTime.UtcNow,
                        Leased = true,
                        Finalized = false,
                        PendingPromptText = context.PromptText ?? "",
                        PendingCanonicalHistorySignature = context.CanonicalHistorySignature ?? ""
                    };
                    Entries[identity.Key] = entry;
                    return new CodexThreadLease { ThreadId = threadId, CorrelationId = context.CorrelationId, IdentityKey = identity.Key, NewThread = true, AcquiredUtc = DateTime.UtcNow };
                }
            }

            public bool TryGetAppendOnlyPrompt(CodexThreadLease lease, CodexRuntimeRequestContext context, out string delta)
            {
                delta = "";
                if (lease == null) return false;
                if (context == null || string.IsNullOrWhiteSpace(lease.PreviousAcceptedHistorySignature)
                    || string.IsNullOrWhiteSpace(context.CanonicalHistorySignature)
                    || !lease.PreviousAcceptedHistorySignature.Equals(context.CanonicalHistorySignature, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(context.CurrentTurnText)) return false;
                delta = BuildCodexIncrementalTurnPrompt(context.CurrentTurnText);
                return true;
            }

            public void RecordPrompt(CodexThreadLease lease, CodexRuntimeRequestContext context)
            {
                if (lease == null) return;
                lock (Sync)
                {
                    Entry entry;
                    if (Entries.TryGetValue(lease.IdentityKey, out entry) && entry.Leased
                        && entry.ThreadId.Equals(lease.ThreadId, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.PendingPromptText = context == null ? "" : context.PromptText ?? "";
                        entry.PendingCanonicalHistorySignature = context == null ? "" : context.CanonicalHistorySignature ?? "";
                    }
                }
            }

            public void RecordAcceptedHistorySignature(CodexThreadLease lease, string signature)
            {
                if (lease == null) return;
                lock (Sync)
                {
                    Entry entry;
                    if (Entries.TryGetValue(lease.IdentityKey, out entry) && entry.Leased
                        && entry.ThreadId.Equals(lease.ThreadId, StringComparison.OrdinalIgnoreCase))
                        entry.PendingCanonicalHistorySignature = signature ?? "";
                }
            }

            public string Abandon(CodexThreadLease lease)
            {
                if (lease == null) return "";
                lock (Sync)
                {
                    Entry entry;
                    if (!Entries.TryGetValue(lease.IdentityKey, out entry)) return lease.ThreadId;
                    if (entry.ThreadId.Equals(lease.ThreadId, StringComparison.OrdinalIgnoreCase))
                    {
                        Entries.Remove(lease.IdentityKey);
                        return entry.ThreadId;
                    }
                    return lease.ThreadId;
                }
            }

            public string Finalize(CodexThreadLease lease, bool accepted, bool substantiveChanged, long inputTokens)
            {
                return Finalize(lease, accepted, substantiveChanged, inputTokens, inputTokens > 0);
            }

            public string Finalize(CodexThreadLease lease, bool accepted, bool substantiveChanged, long inputTokens, bool usageKnown)
            {
                if (lease == null) return "";
                lock (Sync)
                {
                    Entry entry;
                    if (!Entries.TryGetValue(lease.IdentityKey, out entry)) return lease.ThreadId;
                    if (!entry.ThreadId.Equals(lease.ThreadId, StringComparison.OrdinalIgnoreCase)) return lease.ThreadId;
                    entry.Leased = false;
                    entry.LastUsedUtc = DateTime.UtcNow;
                    if (entry.Invalidated || !accepted || substantiveChanged || !usageKnown || entry.Turns + 1 >= MaxTurns || entry.InputTokens + Math.Max(0, inputTokens) >= MaxInputTokens)
                    {
                        Entries.Remove(lease.IdentityKey);
                        return entry.ThreadId;
                    }
                    entry.Turns++;
                    entry.InputTokens += Math.Max(0, inputTokens);
                    if (!string.IsNullOrWhiteSpace(entry.PendingPromptText)) entry.LastPromptText = entry.PendingPromptText;
                    if (!string.IsNullOrWhiteSpace(entry.PendingCanonicalHistorySignature)) entry.LastAcceptedHistorySignature = entry.PendingCanonicalHistorySignature;
                    entry.PendingPromptText = "";
                    entry.PendingCanonicalHistorySignature = "";
                    entry.Finalized = true;
                    return "";
                }
            }

            public List<string> InvalidateAll()
            {
                lock (Sync)
                {
                    List<string> result = Entries.Values.Where(x => !x.Leased).Select(x => x.ThreadId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (Entry entry in Entries.Values.ToList())
                    {
                        if (entry.Leased) entry.Invalidated = true;
                        else Entries.Remove(entry.Identity.Key);
                    }
                    return result;
                }
            }

            public List<string> TakeEvictedThreadIds()
            {
                lock (Sync)
                {
                    List<string> result = EvictedThreadIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    EvictedThreadIds.Clear();
                    return result;
                }
            }

            public Dictionary<string, object> Diagnostics()
            {
                lock (Sync)
                {
                    EvictExpiredLocked();
                    return new Dictionary<string, object>
                    {
                        ["count"] = Entries.Count,
                        ["max"] = MaxEntries,
                        ["maxTurns"] = MaxTurns,
                        ["idleSeconds"] = (int)IdleExpiry.TotalSeconds,
                        ["maxInputTokens"] = MaxInputTokens,
                        ["leased"] = Entries.Values.Count(x => x.Leased)
                    };
                }
            }

            private void EvictExpiredLocked()
            {
                DateTime cutoff = DateTime.UtcNow.Subtract(IdleExpiry);
                foreach (string key in Entries.Where(pair => !pair.Value.Leased && pair.Value.LastUsedUtc < cutoff).Select(pair => pair.Key).ToList())
                {
                    EvictedThreadIds.Add(Entries[key].ThreadId);
                    Entries.Remove(key);
                }
            }

            private void EvictInvalidatedLocked()
            {
                foreach (string key in Entries.Where(pair => !pair.Value.Leased && pair.Value.Invalidated).Select(pair => pair.Key).ToList())
                {
                    if (!string.IsNullOrWhiteSpace(Entries[key].ThreadId)) EvictedThreadIds.Add(Entries[key].ThreadId);
                    Entries.Remove(key);
                }
            }

            private void EvictToBoundLocked()
            {
                EvictExpiredLocked();
                EvictInvalidatedLocked();
                while (Entries.Count >= MaxEntries)
                {
                    Entry oldest = Entries.Values.Where(x => !x.Leased).OrderBy(x => x.LastUsedUtc).FirstOrDefault();
                    if (oldest == null) return;
                    EvictedThreadIds.Add(oldest.ThreadId);
                    Entries.Remove(oldest.Identity.Key);
                }
            }
        }

        private sealed class CodexThreadCleanupQueue
        {
            private sealed class Item
            {
                public string ThreadId;
                public Func<string, bool> Delete;
            }

            private readonly object Sync = new object();
            private readonly Queue<Item> Queue = new Queue<Item>();
            private readonly HashSet<string> Queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private Thread Worker;
            private bool Stopping;
            private const int Capacity = 256;
            private const int MaxAttempts = 2;
            private const int TimeoutMs = 10000;

            public bool Enqueue(string threadId, Func<string, bool> delete)
            {
                if (string.IsNullOrWhiteSpace(threadId) || delete == null) return true;
                lock (Sync)
                {
                    if (Stopping) return false;
                    if (Queued.Contains(threadId)) return true;
                    if (Queue.Count >= Capacity) return false;
                    Queued.Add(threadId);
                    Queue.Enqueue(new Item { ThreadId = threadId, Delete = delete });
                    if (Worker == null || !Worker.IsAlive)
                    {
                        Worker = new Thread(Drain) { IsBackground = true, Name = "ReignCodexThreadCleanup" };
                        Worker.Start();
                    }
                    Monitor.PulseAll(Sync);
                    return true;
                }
            }

            public Dictionary<string, object> Diagnostics()
            {
                lock (Sync) return new Dictionary<string, object> { ["queued"] = Queue.Count, ["capacity"] = Capacity, ["timeoutMs"] = TimeoutMs, ["maxAttempts"] = MaxAttempts, ["stopping"] = Stopping };
            }

            public void Stop()
            {
                Thread worker;
                lock (Sync)
                {
                    Stopping = true;
                    Queue.Clear();
                    Queued.Clear();
                    worker = Worker;
                    Monitor.PulseAll(Sync);
                }
                if (worker != null && worker != Thread.CurrentThread)
                {
                    // Deletion is performed directly by the single worker and each RPC is
                    // bounded to TimeoutMs. Waiting for both bounded attempts prevents a
                    // detached Task from surviving shutdown.
                    try { worker.Join((TimeoutMs * MaxAttempts) + 1000); } catch { }
                }
                lock (Sync)
                {
                    if (Worker == null || !Worker.IsAlive)
                    {
                        Worker = null;
                        Stopping = false;
                    }
                }
            }

            private void Drain()
            {
                while (true)
                {
                    Item item;
                    lock (Sync)
                    {
                        while (!Stopping && Queue.Count == 0) Monitor.Wait(Sync, 1000);
                        if (Stopping && Queue.Count == 0) return;
                        if (Queue.Count == 0) continue;
                        item = Queue.Dequeue();
                    }
                    bool completed = false;
                    for (int attempt = 0; attempt < MaxAttempts && !completed; attempt++)
                    {
                        try
                        {
                            // QueueCodexThreadCleanup supplies a bounded RPC. Calling it on
                            // this one owned worker avoids creating an unbounded orphan task
                            // when a deletion times out.
                            completed = item.Delete(item.ThreadId);
                        }
                        catch { completed = false; }
                    }
                    lock (Sync) Queued.Remove(item.ThreadId);
                }
            }
        }

        private static void QueueCodexThreadCleanup(string threadId, Dictionary<string, object> settings, ref bool synchronousFallback)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            Func<string, bool> delete = value =>
            {
                try
                {
                    CodexRpc("thread/delete", new Dictionary<string, object> { ["threadId"] = value }, Math.Min(CodexRpcTimeout(settings), 10000));
                    return true;
                }
                catch { return false; }
            };
            if (ReadBool(settings, "codexOptions.asyncThreadCleanup", false) || ReadBool(ReadDictionary(settings, "codexOptions"), "asyncThreadCleanup", false))
            {
                if (!CodexRuntimeCleanup.Enqueue(threadId, delete))
                {
                    synchronousFallback = true;
                    delete(threadId);
                }
            }
            else
            {
                synchronousFallback = true;
                delete(threadId);
            }
        }

        private static void DrainCodexThreadEvictions(Dictionary<string, object> settings, ref bool synchronousFallback)
        {
            if (settings == null) settings = LoadSettings();
            foreach (string threadId in CodexRuntimeThreads.TakeEvictedThreadIds())
                QueueCodexThreadCleanup(threadId, settings, ref synchronousFallback);
        }

        private static void FinalizeCodexThreadRequest(Dictionary<string, object> result, Dictionary<string, object> context)
        {
            CodexRuntimeRequestContext request = CaptureCodexRequestContext(LoadSettings(), result, ReadString(context, "requestType", "dialogue"), ReadString(context, "model", ""), ReadString(context, "correlationId", ""), context);
            CodexThreadLease lease;
            lock (CodexRuntimeStateLock)
            {
                if (!CodexPendingThreadLeases.TryGetValue(request.CorrelationId, out lease)) return;
                CodexPendingThreadLeases.Remove(request.CorrelationId);
            }
            bool accepted = ReadBool(result, "accepted", ReadBool(context, "accepted", ReadBool(ReadDictionary(result, "codexFinalization"), "accepted", request.FinalizationApproved)));
            Dictionary<string, object> finalization = ReadDictionary(result, "codexFinalization");
            bool substantiveChanged = request.Repaired;
            if (finalization != null && finalization.ContainsKey("substantiveChanged")) substantiveChanged = ReadBool(finalization, "substantiveChanged", substantiveChanged);
            if (finalization != null && finalization.ContainsKey("substantivelyChanged")) substantiveChanged = ReadBool(finalization, "substantivelyChanged", substantiveChanged);
            if (context != null && context.ContainsKey("substantiveChanged")) substantiveChanged = ReadBool(context, "substantiveChanged", substantiveChanged);
            if (context != null && context.ContainsKey("substantivelyChanged")) substantiveChanged = ReadBool(context, "substantivelyChanged", substantiveChanged);
            if (result != null && result.ContainsKey("substantiveChanged")) substantiveChanged = ReadBool(result, "substantiveChanged", substantiveChanged);
            if (result != null && result.ContainsKey("substantivelyChanged")) substantiveChanged = ReadBool(result, "substantivelyChanged", substantiveChanged);
            Dictionary<string, object> usage = ReadDictionary(result, "usage");
            bool usageKnown = usage != null && (usage.ContainsKey("prompt_tokens") || usage.ContainsKey("input_tokens") || usage.ContainsKey("inputTokens"));
            long inputTokens = usage == null ? 0 : ReadLong(usage, "prompt_tokens", ReadLong(usage, "input_tokens", ReadLong(usage, "inputTokens", 0)));
            string acceptedHistorySignature = FirstNonEmpty(ReadString(result, "acceptedHistorySignature", ""), ReadString(context, "acceptedHistorySignature", ""), request.CanonicalHistorySignature);
            if (accepted && !substantiveChanged && !string.IsNullOrWhiteSpace(acceptedHistorySignature))
                CodexRuntimeThreads.RecordAcceptedHistorySignature(lease, acceptedHistorySignature);
            string cleanupId = CodexRuntimeThreads.Finalize(lease, accepted, substantiveChanged, inputTokens, usageKnown);
            bool fallback = false;
            QueueCodexThreadCleanup(cleanupId, LoadSettings(), ref fallback);
        }

        private static void ConfirmCodexThreadTurn(CodexRuntimeRequestContext context, bool accepted, bool substantiveChanged, long inputTokens)
        {
            if (context == null) return;
            FinalizeCodexThreadRequest(new Dictionary<string, object>
            {
                ["accepted"] = accepted,
                ["substantiveChanged"] = substantiveChanged,
                ["usage"] = new Dictionary<string, object> { ["input_tokens"] = inputTokens }
            }, context.ToDictionary());
        }

        private static void InvalidateCodexThreads(string reason)
        {
            List<string> threadIds = CodexRuntimeThreads.InvalidateAll();
            Dictionary<string, object> settings = LoadSettings();
            foreach (string threadId in threadIds)
            {
                bool fallback = false;
                QueueCodexThreadCleanup(threadId, settings, ref fallback);
            }
            bool evictionFallback = false;
            DrainCodexThreadEvictions(settings, ref evictionFallback);
        }

        private static void StopCodexRuntime()
        {
            InvalidateCodexThreads("runtime stopped");
            CodexRuntimeCleanup.Stop();
        }

        private static Dictionary<string, object> CodexCapabilityStatusSnapshot()
        {
            lock (CodexRuntimeStateLock)
                return new Dictionary<string, object>(CodexLastCapabilityStatus);
        }

        private static void RecordCodexCapabilityStatus(CodexRuntimeRequestContext context)
        {
            if (context == null) return;
            Dictionary<string, object> status = new Dictionary<string, object>
            {
                ["model"] = context.Model,
                ["requestedReasoningEffort"] = context.RequestedReasoningEffort,
                ["resolvedReasoningEffort"] = context.ResolvedReasoningEffort,
                ["disabledReasoningRequested"] = context.DisabledReasoningRequested,
                ["reasoningCapabilityKnown"] = context.ReasoningCapabilityKnown,
                ["reasoningResolution"] = context.ReasoningResolution,
                ["fastMode"] = new Dictionary<string, object>
                {
                    ["requested"] = context.Fast.Requested,
                    ["state"] = context.Fast.State,
                    ["applied"] = context.Fast.Applied,
                    ["field"] = context.Fast.Field,
                    ["reason"] = context.Fast.Reason
                },
                ["structuredOutput"] = new Dictionary<string, object>
                {
                    ["requested"] = context.SchemaRequested,
                    ["applied"] = context.SchemaApplied,
                    ["state"] = context.SchemaState,
                    ["schemaId"] = context.SchemaId
                },
                ["requestType"] = context.RequestType,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };
            lock (CodexRuntimeStateLock) CodexLastCapabilityStatus = status;
        }

        private static Dictionary<string, object> CodexRuntimeDiagnostics(CodexRuntimeRequestContext context, CodexRequestTimings timings, CodexTurnAssembly assembly, bool cleanupFallback)
        {
            RecordCodexCapabilityStatus(context);
            return new Dictionary<string, object>
            {
                ["schema"] = CodexRuntimeDiagnosticsSchema,
                ["provider"] = CodexSubscriptionProvider,
                ["request"] = context == null ? new Dictionary<string, object>() : context.ToDictionary(),
                ["protocol"] = CodexRuntimeProtocol.ToDictionary(),
                ["timings"] = timings == null ? new Dictionary<string, object>() : timings.ToDictionary(),
                ["authoritativeModel"] = assembly == null ? "" : assembly.AuthoritativeModel,
                ["reportedModel"] = assembly == null ? "" : assembly.ReportedModel,
                ["servedModel"] = assembly == null ? "" : assembly.ServedModel,
                ["modelEvidence"] = assembly == null ? "" : assembly.ModelEvidence,
                ["effectiveModelKnown"] = assembly != null && !string.IsNullOrWhiteSpace(assembly.ServedModel),
                ["firstTextObserved"] = assembly != null && assembly.FirstTextUtc != default(DateTime),
                ["usageKnown"] = assembly != null && assembly.Usage != null,
                ["cleanupFallbackSynchronous"] = cleanupFallback,
                ["threadPool"] = CodexRuntimeThreads.Diagnostics(),
                ["cleanup"] = CodexRuntimeCleanup.Diagnostics()
            };
        }

        private static Dictionary<string, object> CodexRuntimeSafeModel(Dictionary<string, object> model)
        {
            string id = CodexModelId(model);
            List<string> modalities = ReadStringList(model, "inputModalities");
            if (modalities.Count == 0) modalities = new List<string> { "text", "image" };
            Dictionary<string, object> safe = new Dictionary<string, object>
            {
                ["id"] = id,
                ["displayName"] = ReadString(model, "displayName", ReadString(model, "name", id)),
                ["description"] = ReadString(model, "description", ""),
                ["hidden"] = ReadBool(model, "hidden", false),
                ["defaultReasoningEffort"] = ReadString(model, "defaultReasoningEffort", ""),
                ["supportedReasoningEfforts"] = ReadReasoningEfforts(model),
                ["inputModalities"] = modalities,
                ["supportsPersonality"] = ReadBool(model, "supportsPersonality", false),
                ["isDefault"] = ReadBool(model, "isDefault", false),
                ["upgrade"] = ReadString(model, "upgrade", ""),
                ["capabilityMetadataPreserved"] = true
            };
            foreach (string key in new[] { "model", "defaultServiceTier", "serviceTiers", "additionalSpeedTiers", "modelSpecialty", "multiAgentVersion", "upgradeInfo", "availabilityNux", "supportsFastMode", "supportedServiceTiers", "supportsParallelToolCalls", "supports_parallel_tool_calls", "modelProvider" })
                if (model != null && model.ContainsKey(key)) safe[key] = model[key];
            // Keep capability fields introduced by newer runtimes available to the UI and
            // diagnostics without copying credential-like metadata from an untrusted payload.
            if (model != null)
            {
                foreach (KeyValuePair<string, object> pair in model)
                {
                    string lower = (pair.Key ?? "").ToLowerInvariant();
                    if (safe.ContainsKey(pair.Key) || lower.Contains("token") || lower.Contains("secret")
                        || lower.Contains("password") || lower.Contains("credential") || lower.Contains("api_key") || lower.Contains("apikey"))
                        continue;
                    safe[pair.Key] = pair.Value;
                }
            }
            return safe;
        }

        private static List<Dictionary<string, object>> ReadReasoningEfforts(Dictionary<string, object> model)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            if (model == null || !model.ContainsKey("supportedReasoningEfforts") || model["supportedReasoningEfforts"] == null) return result;
            IEnumerable values = model["supportedReasoningEfforts"] as IEnumerable;
            if (values == null) return result;
            foreach (object value in values)
            {
                Dictionary<string, object> entry = value as Dictionary<string, object>;
                if (entry != null) result.Add(new Dictionary<string, object>
                {
                    ["reasoningEffort"] = ReadString(entry, "reasoningEffort", ReadString(entry, "effort", "")),
                    ["description"] = ReadString(entry, "description", "")
                });
                else if (!string.IsNullOrWhiteSpace(Convert.ToString(value))) result.Add(new Dictionary<string, object> { ["reasoningEffort"] = Convert.ToString(value), ["description"] = "" });
            }
            return result;
        }

        private static bool? ReadNullableBool(Dictionary<string, object> source, string key, bool? fallback)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null) return fallback;
            bool value;
            return bool.TryParse(Convert.ToString(source[key]), out value) ? (bool?)value : fallback;
        }

        private static string NormalizeCodexReasoningMode(string value)
        {
            string mode = (value ?? "").Trim().ToLowerInvariant();
            return mode == "direct" || mode == "off" || mode == "none" ? "direct" : "selective";
        }

        private static string NormalizeCodexEffort(string value)
        {
            string effort = (value ?? "").Trim().ToLowerInvariant();
            return effort == "none" || effort == "minimal" || effort == "low" || effort == "medium" || effort == "high" || effort == "xhigh" || effort == "max" || effort == "ultra" ? effort : "medium";
        }

        private static string BuildPromptTextForRuntime(Dictionary<string, object> payload)
        {
            if (payload == null) return "";
            List<Dictionary<string, object>> messages = ReadDictionaryList(payload, "messages");
            if (messages == null || messages.Count == 0) return "";
            try { return BuildCodexPrompt(messages); }
            catch { return ""; }
        }

        private static string BuildCodexIncrementalTurnPrompt(string currentTurnText)
        {
            return "Continue the established Reign conversation using the authoritative prior thread context. Process only this new canonical turn; preserve the existing identity, secrecy, relationships, and world state. Return only the requested response.\n\n--- CURRENT CANONICAL TURN ---\n" + (currentTurnText ?? "").Trim();
        }

        private static string HashCodexText(string value)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }

        private static List<string> SupportedCodexReasoningEfforts(string model, out bool known)
        {
            List<string> supported = new List<string>();
            known = false;
            lock (CodexStateLock)
            {
                Dictionary<string, object> entry = CodexModels.FirstOrDefault(x => CodexModelId(x).Equals(model ?? "", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return supported;
                List<Dictionary<string, object>> advertised = ReadReasoningEfforts(entry);
                known = advertised.Count > 0;
                foreach (Dictionary<string, object> effort in advertised)
                {
                    string value = NormalizeCodexEffort(ReadString(effort, "reasoningEffort", ""));
                    if (!string.IsNullOrWhiteSpace(value) && !supported.Contains(value, StringComparer.OrdinalIgnoreCase)) supported.Add(value);
                }
            }
            return supported;
        }

        private static string ResolveRequestedReasoningEffort(string model, string requested, bool disabled,
            out bool capabilityKnown, out string resolution)
        {
            requested = NormalizeCodexEffort(requested);
            List<string> supported = SupportedCodexReasoningEfforts(model, out capabilityKnown);
            if (supported.Count == 0)
            {
                resolution = "The selected model did not expose supported reasoning efforts; the requested value was preserved.";
                capabilityKnown = false;
                return requested;
            }
            if (disabled)
            {
                if (supported.Any(value => value.Equals("none", StringComparison.OrdinalIgnoreCase)))
                {
                    resolution = "Disabled reasoning is supported by the selected model.";
                    return "none";
                }
                string lowestDisabled = LowestCodexReasoningEffort(supported);
                resolution = "The selected model cannot disable reasoning; its lowest advertised effort was applied.";
                return lowestDisabled;
            }
            if (supported.Any(value => value.Equals(requested, StringComparison.OrdinalIgnoreCase)))
            {
                resolution = "The requested reasoning effort is advertised by the selected model.";
                return requested;
            }
            string nearest = ClosestCodexReasoningEffort(supported, requested);
            resolution = "The requested reasoning effort is not advertised by the selected model; the closest advertised effort was applied.";
            return nearest;
        }

        private static string LowestCodexReasoningEffort(List<string> supported)
        {
            string[] order = { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" };
            return order.FirstOrDefault(value => supported.Contains(value, StringComparer.OrdinalIgnoreCase)) ?? supported[0];
        }

        private static string ClosestCodexReasoningEffort(List<string> supported, string requested)
        {
            string[] order = { "minimal", "low", "medium", "high", "xhigh", "max", "ultra" };
            int requestedIndex = Array.IndexOf(order, requested);
            if (requestedIndex < 0) return LowestCodexReasoningEffort(supported);
            string lower = order.Take(requestedIndex + 1).Reverse().FirstOrDefault(value => supported.Contains(value, StringComparer.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(lower) ? LowestCodexReasoningEffort(supported) : lower;
        }

        private static string ResolveDisabledReasoningEffort(string model, string requested)
        {
            bool known;
            string reason;
            return ResolveRequestedReasoningEffort(model, requested, true, out known, out reason);
        }

        private static CodexRuntimeRequestContext BuildCodexRuntimeSelfTestContext(string sessionId, string historySignature, string currentTurn)
        {
            Dictionary<string, object> options = new Dictionary<string, object>
            {
                ["schema"] = CodexOptionsSchema,
                ["reasoningMode"] = "selective",
                ["reasoningEffort"] = "high",
                ["reuseThreads"] = true,
                ["fastMode"] = false,
                ["structuredOutputs"] = false,
                ["compactMetadata"] = false,
                ["stablePromptMapping"] = false,
                ["parallelContextPreparation"] = false,
                ["asyncThreadCleanup"] = false
            };
            Dictionary<string, object> source = new Dictionary<string, object>
            {
                ["options"] = options,
                ["campaignId"] = "selftest-campaign",
                ["timelineId"] = "selftest-load",
                ["sessionId"] = sessionId,
                ["speakerId"] = "selftest-speaker",
                ["conversationMode"] = "individual",
                ["visibilityScope"] = "private",
                ["authorityHash"] = "selftest-authority",
                ["schemaId"] = "selftest-schema",
                ["promptContractHash"] = "selftest-prompt-contract",
                ["canonicalHistorySignature"] = historySignature,
                ["currentTurnText"] = currentTurn,
                ["finalizationApproved"] = true
            };
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = currentTurn }
                }
            };
            return CaptureCodexRequestContext(new Dictionary<string, object>(), payload, "dialogue", "gpt-selftest", "selftest-" + sessionId, source);
        }

        private static List<Dictionary<string, object>> RunCodexRuntimeSelfTests()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => tests.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = passed, ["summary"] = summary });
            Dictionary<string, object> settings = new Dictionary<string, object> { ["reasoningMode"] = "selective", ["reasoningEffort"] = "high" };
            Dictionary<string, object> options = new Dictionary<string, object>
            {
                ["schema"] = CodexOptionsSchema, ["reasoningMode"] = "selective", ["reasoningEffort"] = "high", ["fastMode"] = false,
                ["structuredOutputs"] = false, ["compactMetadata"] = false, ["stablePromptMapping"] = false,
                ["parallelContextPreparation"] = false, ["reuseThreads"] = false, ["asyncThreadCleanup"] = false
            };
            settings["codexOptions"] = options;
            CodexRuntimeRequestContext context = CaptureCodexRequestContext(settings, new Dictionary<string, object>(), "dialogue", "gpt-test", "corr-test", new Dictionary<string, object> { ["options"] = options });
            add("runtime_options_defaults", context.Options.Schema == CodexOptionsSchema && context.Options.ReasoningEffort == "high" && !context.Options.FastMode && !context.Options.StructuredOutputs,
                "Codex options are captured once with all optional experiments disabled by default.");

            CodexProtocolState supported = new CodexProtocolState { SupportsFastMode = true, FastModeField = "serviceTier" };
            CodexFastResolution fast = CodexFastMode.Resolve(true, supported);
            Dictionary<string, object> turn = new Dictionary<string, object> { ["model"] = "gpt-test", ["effort"] = "high" };
            CodexFastMode.Apply(turn, fast);
            add("fast_same_model_effort", fast.State == "requested" && ReadString(turn, "model", "") == "gpt-test" && ReadString(turn, "effort", "") == "high" && ReadString(turn, "serviceTier", "") == "fast",
                "Fast mode adds only the runtime tier field and preserves model and reasoning effort.");

            CodexProtocolState unknown = new CodexProtocolState();
            CodexFastResolution unknownFast = CodexFastMode.Resolve(true, unknown);
            add("fast_unknown_is_explicit", unknownFast.State == "unknown" && unknownFast.Applied == "unknown",
                "Fast mode remains explicitly unknown until the runtime reports support.");

            CodexTurnEventAssembler assembler = new CodexTurnEventAssembler("turn-test");
            assembler.Accept(new Dictionary<string, object> { ["method"] = "item/agentMessage/delta", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["itemId"] = "item-1", ["delta"] = "bad" } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "item/agentMessage/delta", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["itemId"] = "item-1", ["delta"] = " draft" } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "item/completed", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["item"] = new Dictionary<string, object> { ["id"] = "item-1", ["type"] = "agentMessage", ["phase"] = "commentary", ["text"] = "rejected" } } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "item/completed", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["item"] = new Dictionary<string, object> { ["id"] = "item-2", ["type"] = "agentMessage", ["phase"] = "final_answer", ["text"] = "authoritative" } } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "model/rerouted", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["fromModel"] = "gpt-test", ["toModel"] = "gpt-test", ["reason"] = "fixture" } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "thread/tokenUsage/updated", ["params"] = new Dictionary<string, object>
            {
                ["threadId"] = "thread-test", ["turnId"] = "turn-test",
                ["tokenUsage"] = new Dictionary<string, object>
                {
                    ["last"] = new Dictionary<string, object>
                    {
                        ["cachedInputTokens"] = 5, ["inputTokens"] = 12, ["outputTokens"] = 4,
                        ["reasoningOutputTokens"] = 2, ["totalTokens"] = 18, ["cacheWriteInputTokens"] = 1
                    },
                    ["total"] = new Dictionary<string, object>
                    {
                        ["cachedInputTokens"] = 7, ["inputTokens"] = 100, ["outputTokens"] = 20,
                        ["reasoningOutputTokens"] = 5, ["totalTokens"] = 125, ["cacheWriteInputTokens"] = 3
                    }
                }
            } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "turn/completed", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["turn"] = new Dictionary<string, object> { ["id"] = "turn-test", ["status"] = "completed", ["model"] = "gpt-test" } } });
            CodexTurnAssembly assembled = assembler.Snapshot();
            Dictionary<string, object> assembledDetails = ReadDictionary(assembled.Usage, "prompt_tokens_details");
            Dictionary<string, object> assembledTotal = ReadDictionary(assembled.Usage, "codex_thread_total");
            add("event_final_item_authority", assembled.Text == "authoritative" && assembled.AuthoritativeModel == "gpt-test" && assembled.ReportedModel == "gpt-test" && assembled.ModelEvidence == "model/rerouted" && assembled.Usage != null && ReadLong(assembled.Usage, "prompt_tokens", 0) == 12 && ReadLong(assembled.Usage, "completion_tokens", 0) == 4 && ReadLong(assembled.Usage, "total_tokens", 0) == 18 && ReadLong(assembledDetails, "cached_tokens", 0) == 5 && ReadLong(assembledTotal, "total_tokens", 0) == 125 && assembled.Completed,
                "Fragmented deltas and multiple items are reconciled to the final completed agent item; exact thread usage keeps per-turn last fields, cached input, and cumulative totals.");

            CodexTurnEventAssembler lateUsageAssembler = new CodexTurnEventAssembler("usage-turn");
            lateUsageAssembler.Accept(new Dictionary<string, object> { ["method"] = "turn/completed", ["params"] = new Dictionary<string, object> { ["turnId"] = "usage-turn", ["turn"] = new Dictionary<string, object> { ["id"] = "usage-turn", ["status"] = "completed" } } });
            lateUsageAssembler.Accept(new Dictionary<string, object> { ["method"] = "thread/tokenUsage/updated", ["params"] = new Dictionary<string, object>
            {
                ["threadId"] = "thread-test", ["turnId"] = "usage-turn",
                ["tokenUsage"] = new Dictionary<string, object>
                {
                    ["last"] = new Dictionary<string, object> { ["cached_input_tokens"] = 2, ["input_tokens"] = 9, ["output_tokens"] = 3, ["reasoning_output_tokens"] = 1, ["total_tokens"] = 13 },
                    ["total"] = new Dictionary<string, object> { ["cached_input_tokens"] = 2, ["input_tokens"] = 9, ["output_tokens"] = 3, ["reasoning_output_tokens"] = 1, ["total_tokens"] = 13 }
                }
            } });
            Dictionary<string, object> lateUsage = lateUsageAssembler.Snapshot().Usage;
            add("thread_usage_after_completed", lateUsage != null && ReadLong(lateUsage, "prompt_tokens", 0) == 9 && ReadLong(lateUsage, "completion_tokens", 0) == 3,
                "A tokenUsage update arriving after turn/completed updates usage without reopening or changing completed text.");

            CodexRuntimeRequestContext isolated = CaptureCodexRequestContext(new Dictionary<string, object>(), new Dictionary<string, object>(), "dialogue", "gpt-test", "corr-thread", new Dictionary<string, object>
            {
                ["options"] = new Dictionary<string, object> { ["reuseThreads"] = true }, ["campaignId"] = "campaign", ["timelineId"] = "load-1", ["sessionId"] = "session", ["speakerId"] = "speaker", ["conversationMode"] = "individual", ["visibilityScope"] = "private", ["authorityHash"] = "authority", ["finalizationApproved"] = true,
                ["canonicalHistorySignature"] = "history-1", ["currentTurnText"] = "A trusted current turn."
            });
            CodexThreadIdentity identity;
            add("thread_isolation_requires_identity", CodexThreadIdentity.TryCreate(isolated, out identity) && !string.IsNullOrWhiteSpace(identity.Key),
                "Thread reuse requires campaign, load/timeline, session, speaker, mode, visibility and authority identity.");
            add("verification_is_bounded", ReadInt(BuildCodexModelVerificationPlan("gpt-5.4", new Dictionary<string, object>()), "providerCallCap", 0) == 1 && !ReadBool(BuildCodexModelVerificationPlan("gpt-5.4", new Dictionary<string, object>()), "execute", true),
                "GPT-5.4 verification is represented as an explicit bounded plan and never runs during discovery.");
            tests.AddRange(RunCodexRuntimeBoundarySelfTests());
            tests.Add(RunCodexInjectedAdapterTransportSelfTest());
            return tests;
        }

        private static List<Dictionary<string, object>> RunCodexRuntimeBoundarySelfTests()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => tests.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = passed, ["summary"] = summary });

            CodexFastResolution fast = CodexFastMode.Resolve(true, new CodexProtocolState { SupportsFastMode = true, FastModeField = "serviceTier" });
            CodexRpcException rejected = new CodexRpcException("turn/start", 400, "service tier fast is unsupported");
            add("fast_rejection_is_retryable_only_before_generation", CodexFastMode.IsExplicitPreStartRejection(rejected, fast)
                && !CodexFastMode.IsExplicitPreStartRejection(new CodexRpcException("turn/start", 408, "fast tier request timed out"), fast)
                && !CodexFastMode.IsExplicitPreStartRejection(new CodexRpcException("turn/start", 500, "fast service failed"), fast),
                "Only an explicit pre-generation Fast-tier rejection qualifies for the same-model Standard fallback; an ambiguous timeout does not.");
            tests.Add(RunCodexCatalogBoundarySelfTest());

            List<Dictionary<string, object>> previousModels;
            CodexCatalogState previousCatalog;
            lock (CodexStateLock)
            {
                previousModels = CodexModels;
                previousCatalog = CloneCodexCatalogState(CodexRuntimeCatalog);
                CodexModels = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["model"] = "gpt-selftest-capability",
                        ["supportedReasoningEfforts"] = new List<object>
                        {
                            new Dictionary<string, object> { ["reasoningEffort"] = "low", ["description"] = "low" }
                        }
                    }
                };
            }
            try
            {
                bool known;
                string resolution;
                string disabled = ResolveRequestedReasoningEffort("gpt-selftest-capability", "none", true, out known, out resolution);
                add("unsupported_disabled_reasoning_uses_lowest", known && disabled == "low" && resolution.Contains("cannot disable", StringComparison.OrdinalIgnoreCase),
                    "Disabled-reasoning intent resolves to the lowest advertised effort when the selected model cannot disable reasoning.");
            }
            finally
            {
                lock (CodexStateLock)
                {
                    CodexModels = previousModels;
                    CodexRuntimeCatalog = previousCatalog;
                }
            }

            CodexTurnEventAssembler negativeAssembler = new CodexTurnEventAssembler("negative-turn", "negative-thread");
            negativeAssembler.Accept(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object> { ["threadId"] = "other-thread", ["turnId"] = "negative-turn", ["itemId"] = "wrong", ["delta"] = "ignored" }
            });
            negativeAssembler.Accept(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object> { ["threadId"] = "negative-thread", ["turnId"] = "negative-turn", ["itemId"] = "answer", ["delta"] = "refusal" }
            });
            negativeAssembler.Accept(new Dictionary<string, object>
            {
                ["method"] = "turn/completed",
                ["params"] = new Dictionary<string, object>
                {
                    ["threadId"] = "negative-thread", ["turnId"] = "negative-turn",
                    ["turn"] = new Dictionary<string, object>
                    {
                        ["id"] = "negative-turn", ["status"] = "cancelled",
                        ["error"] = new Dictionary<string, object> { ["message"] = "cancelled by test" }
                    }
                }
            });
            negativeAssembler.Accept(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object> { ["threadId"] = "negative-thread", ["turnId"] = "negative-turn", ["itemId"] = "answer", ["delta"] = " late" }
            });
            CodexTurnAssembly negative = negativeAssembler.Snapshot();
            add("event_thread_turn_phase_isolation", negative.Completed && negative.Status == "cancelled"
                && negative.Error == "cancelled by test" && negative.Text == "refusal",
                "Wrong-thread events and late post-completion deltas are ignored while cancellation/refusal status remains authoritative.");

            CodexRuntimeRequestContext first = BuildCodexRuntimeSelfTestContext("session-reuse", "history-1", "first turn");
            CodexThreadReusePool pool = new CodexThreadReusePool();
            CodexThreadLease firstLease = pool.RegisterNew(first, "thread-reuse");
            pool.RecordPrompt(firstLease, first);
            pool.RecordAcceptedHistorySignature(firstLease, "history-1");
            string firstCleanup = pool.Finalize(firstLease, true, false, 12, true);
            CodexRuntimeRequestContext next = BuildCodexRuntimeSelfTestContext("session-reuse", "history-1", "second turn");
            CodexThreadLease reused = pool.Acquire(next);
            string delta;
            bool appendOnly = pool.TryGetAppendOnlyPrompt(reused, next, out delta) && delta.Contains("second turn", StringComparison.Ordinal);
            string repairedCleanup = pool.Finalize(reused, true, true, 12, true);
            add("thread_reuse_requires_trusted_append", firstLease != null && string.IsNullOrWhiteSpace(firstCleanup) && reused != null && appendOnly && repairedCleanup == "thread-reuse",
                "A reusable thread is acquired only when the canonical history signature matches, and a substantively repaired response invalidates it.");

            CodexThreadReusePool exactPool = new CodexThreadReusePool();
            CodexThreadLease exactLease = exactPool.RegisterNew(first, "thread-exact");
            CodexThreadLease wrongLease = new CodexThreadLease { IdentityKey = exactLease.IdentityKey, ThreadId = "thread-other" };
            string wrongCleanup = exactPool.Finalize(wrongLease, true, false, 1, true);
            bool stillLeased = exactPool.Acquire(next) == null;
            exactPool.Abandon(exactLease);
            add("thread_lease_exact_match", wrongCleanup == "thread-other" && stillLeased,
                "Finalization checks both identity and leased thread id so a stale completion cannot modify a replacement entry.");

            CodexThreadReusePool invalidationPool = new CodexThreadReusePool();
            CodexThreadLease leased = invalidationPool.RegisterNew(first, "thread-invalidate");
            List<string> unleasedInvalidated = invalidationPool.InvalidateAll();
            string leasedCleanup = invalidationPool.Finalize(leased, true, false, 1, true);
            add("invalidate_preserves_leased", !unleasedInvalidated.Contains("thread-invalidate", StringComparer.OrdinalIgnoreCase) && leasedCleanup == "thread-invalidate",
                "Save rollback, runtime stop, and provider changes mark leased entries invalid without deleting them until finalization.");

            CodexThreadReusePool boundedPool = new CodexThreadReusePool();
            for (int index = 0; index < 70; index++)
            {
                CodexRuntimeRequestContext boundedContext = BuildCodexRuntimeSelfTestContext("bounded-" + index, "history-" + index, "turn-" + index);
                CodexThreadLease boundedLease = boundedPool.RegisterNew(boundedContext, "thread-bounded-" + index);
                if (boundedLease != null) boundedPool.Finalize(boundedLease, true, false, 1, true);
            }
            Dictionary<string, object> boundedDiagnostics = boundedPool.Diagnostics();
            List<string> evicted = boundedPool.TakeEvictedThreadIds();
            add("thread_pool_bound_and_eviction", ReadInt(boundedDiagnostics, "count", 65) <= 64 && evicted.Count >= 6,
                "Thread reuse retains at most 64 idle entries and exposes evicted ids for bounded cleanup.");

            CodexThreadCleanupQueue failingCleanup = new CodexThreadCleanupQueue();
            int cleanupAttempts = 0;
            failingCleanup.Enqueue("cleanup-failure", id => { Interlocked.Increment(ref cleanupAttempts); return false; });
            Stopwatch cleanupWait = Stopwatch.StartNew();
            while (Interlocked.CompareExchange(ref cleanupAttempts, 0, 0) < 2 && cleanupWait.ElapsedMilliseconds < 2000)
                Thread.Sleep(5);
            failingCleanup.Stop();
            add("cleanup_failure_is_bounded", cleanupAttempts == 2 && ReadInt(failingCleanup.Diagnostics(), "queued", -1) == 0,
                "A failed deletion receives at most two attempts on the single owned cleanup worker and leaves no queued work behind.");

            BeginCodexTurnAttempt("late-thread");
            bool lateCreated = GetOrCreateCodexTurn("late-selftest", "late-thread") != null;
            RetireCodexTurn("late-selftest");
            HandleCodexMessage(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object> { ["threadId"] = "late-thread", ["turnId"] = "late-selftest", ["itemId"] = "late", ["delta"] = "late" }
            });
            lock (CodexStateLock) add("late_turn_tombstone", lateCreated && !CodexTurns.ContainsKey("late-selftest"),
                "Retired turn ids reject late notifications instead of recreating completed request state.");
            for (int index = 0; index < 520; index++)
            {
                string retiredId = "retired-selftest-" + index;
                string retiredThread = "retired-thread-" + index;
                BeginCodexTurnAttempt(retiredThread);
                RegisterCodexTurn(retiredId, retiredThread);
                RetireCodexTurn(retiredId);
            }
            HandleCodexMessage(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object>
                {
                    ["threadId"] = "retired-thread-0", ["turnId"] = "retired-selftest-0",
                    ["itemId"] = "late", ["delta"] = "must-not-recreate"
                }
            });
            lock (CodexStateLock) add("late_turn_gate_after_tombstone_eviction", !CodexTurns.ContainsKey("retired-selftest-0"),
                "Unknown late notifications remain gated by an active owned turn window after the bounded tombstone set evicts old ids.");
            return tests;
        }

        private static Dictionary<string, object> RunCodexCatalogBoundarySelfTest()
        {
            Func<string, Dictionary<string, object>, int, Dictionary<string, object>> previousTransport = CodexRpcTransportOverride;
            List<Dictionary<string, object>> previousModels;
            CodexCatalogState previousCatalog;
            lock (CodexStateLock)
            {
                previousModels = CodexModels;
                previousCatalog = CloneCodexCatalogState(CodexRuntimeCatalog);
            }
            bool passed = false;
            string summary = "";
            try
            {
                int pageCalls = 0;
                bool loopPages = false;
                CodexRpcTransportOverride = (method, parameters, timeout) =>
                {
                    if (method != "model/list") throw new InvalidOperationException("Unexpected catalog selftest method: " + method);
                    pageCalls++;
                    string cursor = ReadString(parameters, "cursor", "");
                    if (loopPages)
                    {
                        return new Dictionary<string, object>
                        {
                            ["data"] = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object> { ["model"] = "gpt-loop" }
                            },
                            ["nextCursor"] = "same-cursor"
                        };
                    }
                    return new Dictionary<string, object>
                    {
                        ["data"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["model"] = string.IsNullOrWhiteSpace(cursor) ? "gpt-5.5" : "gpt-5.3",
                                ["hidden"] = !string.IsNullOrWhiteSpace(cursor),
                                ["supportedReasoningEfforts"] = new List<object> { "low", "medium", "high" }
                            }
                        },
                        ["nextCursor"] = string.IsNullOrWhiteSpace(cursor) ? "page-2" : ""
                    };
                };
                Dictionary<string, object> paged = RefreshCodexModelsApi();
                Dictionary<string, object> missing = GetCodexModelVerificationStatus("gpt-5.4");
                bool pagedCorrectly = ReadBool(paged, "ok", false) && pageCalls == 2
                    && ReadInt(paged, "count", 0) == 2 && !ReadBool(missing, "listed", true)
                    && ReadString(missing, "status", "") == "unverified";
                loopPages = true;
                pageCalls = 0;
                Dictionary<string, object> repeated = RefreshCodexModelsApi();
                Dictionary<string, object> stale = CodexCatalogDiagnostics(CodexModels);
                Dictionary<string, object> substitution = EvaluateCodexModelVerification("gpt-5.4", "gpt-5.5", null, false, 1);
                Dictionary<string, object> inconclusive = EvaluateCodexModelVerification("gpt-5.4", "", null, false, 0);
                passed = pagedCorrectly && !ReadBool(repeated, "ok", true)
                    && ReadString(stale, "freshness", "fresh") == "stale"
                    && ReadString(substitution, "status", "") == "unavailable"
                    && ReadString(inconclusive, "status", "") == "inconclusive";
                summary = "Paginated catalog discovery, missing-model state, stale pagination failure, substitution, and inconclusive verification remain distinct.";
            }
            catch (Exception ex)
            {
                summary = "Catalog boundary selftest failed: " + LimitText(ex.Message, 500);
            }
            finally
            {
                CodexRpcTransportOverride = previousTransport;
                lock (CodexStateLock)
                {
                    CodexModels = previousModels;
                    CodexRuntimeCatalog = previousCatalog;
                }
            }
            return new Dictionary<string, object> { ["id"] = "catalog_pagination_and_verification", ["passed"] = passed, ["summary"] = summary };
        }

        /// <summary>
        /// Exercises the production adapter path with an injected JSON-RPC transport. No Codex
        /// process, network request, account, or provider usage is involved. This covers request
        /// construction, explicit Standard tier, fragmented Unicode events, final-item authority,
        /// known usage, and bounded cleanup.
        /// </summary>
        private static Dictionary<string, object> RunCodexInjectedAdapterTransportSelfTest()
        {
            Func<string, Dictionary<string, object>, int, Dictionary<string, object>> previousTransport = CodexRpcTransportOverride;
            CodexProtocolState previousProtocol = CodexRuntimeProtocol;
            List<string> calls = new List<string>();
            Dictionary<string, object> capturedThread = null;
            Dictionary<string, object> capturedTurn = null;
            bool passed = false;
            string summary = "";
            try
            {
                CodexRuntimeProtocol = new CodexProtocolState
                {
                    SupportsOutputSchema = true,
                    SupportsFastMode = true,
                    FastModeField = "serviceTierForTurn",
                    SupportsBaseInstructions = true,
                    BaseInstructionsField = "baseInstructions",
                    ThreadServiceTierField = "serviceTier",
                    SupportsThreadResume = true,
                    InspectionState = "offline_fixture"
                };
                CodexRpcTransportOverride = (method, parameters, timeout) =>
                {
                    calls.Add(method);
                    if (method == "thread/start")
                    {
                        capturedThread = parameters;
                        return new Dictionary<string, object> { ["thread"] = new Dictionary<string, object> { ["id"] = "offline-thread" } };
                    }
                    if (method == "turn/start")
                    {
                        capturedTurn = parameters;
                        CodexTurnWaiter turn = GetOrCreateCodexTurn("offline-turn", "offline-thread");
                        turn.Accept(new Dictionary<string, object> { ["method"] = "item/agentMessage/delta", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["itemId"] = "draft", ["delta"] = "draft"
                        }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "item/completed", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["item"] = new Dictionary<string, object>
                            {
                                ["id"] = "draft", ["type"] = "agentMessage", ["phase"] = "commentary", ["text"] = "rejected"
                            }
                        }});
                        foreach (string delta in new[] { "The ", "ruler’s ", "oath — 雪 😀" })
                            turn.Accept(new Dictionary<string, object> { ["method"] = "item/agentMessage/delta", ["params"] = new Dictionary<string, object>
                            {
                                ["turnId"] = "offline-turn", ["itemId"] = "answer", ["delta"] = delta
                            }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "item/completed", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["item"] = new Dictionary<string, object>
                            {
                                ["id"] = "answer", ["type"] = "agentMessage", ["phase"] = "final_answer", ["text"] = "The ruler’s oath — 雪 😀"
                            }
                        }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "thread/tokenUsage/updated", ["params"] = new Dictionary<string, object>
                        {
                            ["threadId"] = "offline-thread", ["turnId"] = "offline-turn",
                            ["tokenUsage"] = new Dictionary<string, object>
                            {
                                ["last"] = new Dictionary<string, object>
                                {
                                    ["cachedInputTokens"] = 4, ["inputTokens"] = 17, ["outputTokens"] = 9,
                                    ["reasoningOutputTokens"] = 3, ["totalTokens"] = 26, ["cacheWriteInputTokens"] = 1
                                },
                                ["total"] = new Dictionary<string, object>
                                {
                                    ["cachedInputTokens"] = 4, ["inputTokens"] = 117, ["outputTokens"] = 29,
                                    ["reasoningOutputTokens"] = 8, ["totalTokens"] = 154, ["cacheWriteInputTokens"] = 2
                                }
                            }
                        }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "model/rerouted", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["fromModel"] = "gpt-test", ["toModel"] = "gpt-test", ["reason"] = "offline_fixture"
                        }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "turn/completed", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["turn"] = new Dictionary<string, object>
                            {
                                ["id"] = "offline-turn", ["status"] = "completed", ["model"] = "gpt-test"
                            }
                        }});
                        return new Dictionary<string, object> { ["turn"] = new Dictionary<string, object> { ["id"] = "offline-turn" } };
                    }
                    if (method == "thread/delete") return new Dictionary<string, object>();
                    throw new InvalidOperationException("Unexpected offline Codex method: " + method);
                };
                Dictionary<string, object> settings = new Dictionary<string, object>
                {
                    ["llmProvider"] = CodexSubscriptionProvider,
                    ["providerRequestTimeoutMs"] = 1000,
                    ["codexOptions"] = new Dictionary<string, object>
                    {
                        ["schema"] = CodexOptionsSchema, ["reasoningMode"] = "selective", ["reasoningEffort"] = "high",
                        ["fastMode"] = false, ["structuredOutputs"] = false, ["compactMetadata"] = false,
                        ["stablePromptMapping"] = true,
                        ["parallelContextPreparation"] = false,
                        ["reuseThreads"] = false, ["asyncThreadCleanup"] = false
                    }
                };
                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    ["messages"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = "Shared rules." },
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = "Character rules." },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = "Return a Unicode-safe answer." }
                    }
                };
                Dictionary<string, object> context = new Dictionary<string, object>
                {
                    ["requestType"] = "dialogue", ["model"] = "gpt-test", ["correlationId"] = "offline-correlation",
                    ["requestedReasoningEffort"] = "high",
                    ["promptSegments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["name"] = "sharedRules", ["role"] = "system", ["content"] = "Shared rules." },
                        new Dictionary<string, object> { ["name"] = "character", ["role"] = "system", ["content"] = "Character rules." },
                        new Dictionary<string, object> { ["name"] = "currentTurn", ["role"] = "user", ["content"] = "Return a Unicode-safe answer." }
                    }
                };
                Dictionary<string, object> response = Json.Deserialize<Dictionary<string, object>>(CodexChatCompletionJson(body, settings, "gpt-test", context));
                Dictionary<string, object> choice = ReadDictionaryList(response, "choices").FirstOrDefault();
                Dictionary<string, object> message = ReadDictionary(choice, "message");
                Dictionary<string, object> usage = ReadDictionary(response, "usage");
                Dictionary<string, object> diagnostics = ReadDictionary(response, "codexDiagnostics");
                Dictionary<string, object> timing = ReadDictionary(diagnostics, "timings");
                Dictionary<string, object> stages = ReadDictionary(timing, "stages");
                List<Dictionary<string, object>> mappedInput = ReadDictionaryList(capturedTurn, "input");
                passed = calls.Count(method => method == "turn/start") == 1
                    && calls.Count(method => method == "thread/start") == 1
                    && calls.Count(method => method == "thread/delete") == 1
                    && ReadString(capturedThread, "serviceTier", "") == "default"
                    && ReadString(capturedThread, "baseInstructions", "").Contains("Shared rules.", StringComparison.Ordinal)
                    && !ReadString(capturedThread, "baseInstructions", "").Contains("Character rules.", StringComparison.Ordinal)
                    && ReadString(capturedTurn, "serviceTierForTurn", "") == "default"
                    && mappedInput.Count == 2
                    && !mappedInput.Any(item => ReadString(item, "text", "").Contains("Shared rules.", StringComparison.Ordinal))
                    && mappedInput.Any(item => ReadString(item, "text", "").Contains("Character rules.", StringComparison.Ordinal))
                    && ReadString(message, "content", "") == "The ruler’s oath — 雪 😀"
                    && usage != null && ReadLong(usage, "prompt_tokens", 0) == 17
                    && stages != null && stages.ContainsKey("cleanup")
                    && ReadBool(diagnostics, "effectiveModelKnown", true);
                summary = "Injected production adapter transport assembled fragmented Unicode events, used the authoritative final item, preserved one generation, and completed bounded cleanup.";
            }
            catch (Exception ex)
            {
                summary = "Injected adapter transport contract failed: " + LimitText(ex.Message, 500);
            }
            finally
            {
                CodexRpcTransportOverride = previousTransport;
                CodexRuntimeProtocol = previousProtocol;
                lock (CodexStateLock)
                {
                    CodexTurns.Clear();
                    CodexRetiredTurns.Clear();
                    CodexPendingTurnThreads.Clear();
                    CodexOwnedTurnThreads.Clear();
                }
            }
            return new Dictionary<string, object> { ["id"] = "injected_adapter_transport", ["passed"] = passed, ["summary"] = summary };
        }
    }
}
