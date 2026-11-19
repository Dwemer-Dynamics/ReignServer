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
        internal static readonly object CodexRuntimeStateLock = new object();
        private static CodexCatalogState CodexRuntimeCatalog = new CodexCatalogState();
        private static CodexProtocolState CodexRuntimeProtocol = new CodexProtocolState();
        private static Dictionary<string, object> CodexLastCapabilityStatus = new Dictionary<string, object>();
        internal static readonly CodexThreadReusePool CodexRuntimeThreads = new CodexThreadReusePool();
        private static readonly CodexThreadCleanupQueue CodexRuntimeCleanup = new CodexThreadCleanupQueue();
        internal static readonly Dictionary<string, CodexThreadLease> CodexPendingThreadLeases =
            new Dictionary<string, CodexThreadLease>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A request-local snapshot. The snapshot is deliberately independent of the mutable settings
        /// dictionary so a provider switch cannot move an in-flight request to another provider.
        /// </summary>
        internal sealed class CodexRuntimeOptions
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

        internal sealed class CodexRuntimeRequestContext
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
            public bool PersistentRoleplay;
            public int ContextInputLimitTokens;
            public string IncrementalContext = "";
            public DialogueService.CodexRoleplayMemoryAccess MemoryAccess;

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
                    ["persistentRoleplay"] = PersistentRoleplay,
                    ["contextInputLimitTokens"] = ContextInputLimitTokens,
                    ["lookupMode"] = MemoryAccess == null ? "auto" : "direct",
                    ["lookupCalls"] = MemoryAccess?.CallCount ?? 0,
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
            result.PersistentRoleplay = ReadBool(source, "persistentRoleplay", false);
            result.ContextInputLimitTokens = ReadInt(source, "contextInputLimitTokens", 0);
            result.IncrementalContext = ReadString(source, "incrementalContext", "");
            if (source.TryGetValue("memoryAccess", out object memory)) result.MemoryAccess = memory as DialogueService.CodexRoleplayMemoryAccess;
            if (result.PersistentRoleplay) result.Options.ReuseThreads = true;
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

        internal sealed class CodexFastResolution
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
    }
}
