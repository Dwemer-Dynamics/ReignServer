using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal enum CodexConversationContractKind
    {
        Unknown,
        Individual,
        Group,
        Castle,
        Social,
        Correspondence
    }

    internal sealed class CodexPromptSegment
    {
        public int Order { get; set; }
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
        public bool Stable { get; set; }
        public string Visibility { get; set; } = "public";
        public string Hash { get; set; } = "";

        public Dictionary<string, object> ToDictionary(bool includeContent = true)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["order"] = Order,
                ["name"] = Name,
                ["role"] = Role,
                ["stable"] = Stable,
                ["visibility"] = Visibility,
                ["hash"] = Hash
            };
            if (includeContent) result["content"] = Content ?? "";
            return result;
        }
    }

    internal sealed class CodexConversationSchemaSelection
    {
        public CodexConversationContractKind Kind { get; set; }
        public string Mode { get; set; } = "unknown";
        public string SchemaId { get; set; } = "";
        public string Applicability { get; set; } = "schema not applicable";
        public string Reason { get; set; } = "";
        public string ContractFingerprint { get; set; } = "";
        public bool BuiltInContract { get; set; }
        public Dictionary<string, object> Schema { get; set; }
        public Dictionary<string, object> OutputSchema { get; set; }
        public List<string> PreservedFields { get; set; } = new List<string>();
        public List<string> SchemaIntroducedNullPaths { get; set; } = new List<string>();
        public List<string> ContractDefinedNullPaths { get; set; } = new List<string>();

        public Dictionary<string, object> ToDictionary(bool includeSchema = true)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["kind"] = Kind.ToString().ToLowerInvariant(),
                ["mode"] = Mode,
                ["schemaId"] = SchemaId,
                ["applicability"] = Applicability,
                ["reason"] = Reason,
                ["contractFingerprint"] = ContractFingerprint,
                ["builtInContract"] = BuiltInContract,
                ["preservedFields"] = new List<string>(PreservedFields ?? new List<string>()),
                ["schemaIntroducedNullPaths"] = new List<string>(SchemaIntroducedNullPaths ?? new List<string>()),
                ["contractDefinedNullPaths"] = new List<string>(ContractDefinedNullPaths ?? new List<string>())
            };
            if (includeSchema && Schema != null) result["schema"] = Schema;
            if (includeSchema && OutputSchema != null) result["outputSchema"] = OutputSchema;
            return result;
        }
    }

    internal sealed class CodexConversationThreadIdentity
    {
        public string CampaignId { get; set; } = "";
        public string TimelineId { get; set; } = "";
        public string LoadGeneration { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ConversationMode { get; set; } = "";
        public string SpeakerId { get; set; } = "";
        public string VisibilityScope { get; set; } = "";
        public string OwnSessionId { get; set; } = "";
        public string AuthorityHash { get; set; } = "";
        public string PromptVersion { get; set; } = "";
        public string SchemaId { get; set; } = "";
        public string Model { get; set; } = "";
        public string ReasoningEffort { get; set; } = "";
        public bool FastMode { get; set; }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = CampaignId,
                ["timelineId"] = TimelineId,
                ["loadGeneration"] = LoadGeneration,
                ["sessionId"] = SessionId,
                ["conversationMode"] = ConversationMode,
                ["speakerId"] = SpeakerId,
                ["visibilityScope"] = VisibilityScope,
                ["ownSessionId"] = OwnSessionId,
                ["authorityHash"] = AuthorityHash,
                ["promptVersion"] = PromptVersion,
                ["schemaId"] = SchemaId,
                ["model"] = Model,
                ["reasoningEffort"] = ReasoningEffort,
                ["fastMode"] = FastMode,
                ["reusable"] = CanReuse(out string reason),
                ["reuseBlocker"] = reason
            };
        }

        public bool CanReuse(out string reason)
        {
            string[] required =
            {
                CampaignId, TimelineId, LoadGeneration, SessionId,
                ConversationMode, SpeakerId, VisibilityScope, OwnSessionId,
                AuthorityHash, PromptVersion, SchemaId, Model, ReasoningEffort
            };
            if (required.Any(string.IsNullOrWhiteSpace))
            {
                reason = "authoritative campaign, session, speaker, visibility, prompt, model, reasoning, or authority identity is incomplete";
                return false;
            }
            reason = "";
            return true;
        }
    }

    internal sealed class CodexContextPreparationTask
    {
        public string Id { get; set; } = "";
        public Func<object> Prepare { get; set; }
    }

    internal sealed class CodexParallelPreparationResult
    {
        public bool Requested { get; set; }
        public bool UsedParallel { get; set; }
        public bool FellBackToSequential { get; set; }
        public string FallbackReason { get; set; } = "";
        public List<object> Results { get; set; } = new List<object>();
        public List<string> Errors { get; set; } = new List<string>();
        public Dictionary<string, object> Diagnostics { get; set; } = new Dictionary<string, object>();
    }

    internal static class CodexConversationContracts
    {
        internal const string OptionsSchema = "reign-codex-options-v1";
        internal const string PromptMappingVersion = "reign-codex-prompt-mapping-v1";
        internal const string ThreadIdentityVersion = "reign-codex-thread-identity-v1";
        internal const int MaxParallelWorkers = 4;

        private static readonly string[] OptionKeys =
        {
            "structuredOutputs",
            "compactMetadata",
            "stablePromptMapping",
            "parallelContextPreparation",
            "reuseThreads",
            "asyncThreadCleanup"
        };

        internal static Dictionary<string, object> DefaultOptions()
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["schema"] = OptionsSchema,
                ["reasoningMode"] = "selective",
                ["reasoningEffort"] = "",
                ["fastMode"] = false
            };
            foreach (string key in OptionKeys) result[key] = false;
            return result;
        }

        internal static Dictionary<string, object> NormalizeOptions(Dictionary<string, object> source)
        {
            var result = DefaultOptions();
            foreach (KeyValuePair<string, object> pair in source ?? new Dictionary<string, object>())
            {
                if (pair.Key.Equals("schema", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("reasoningMode", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("reasoningEffort", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("fastMode", StringComparison.OrdinalIgnoreCase)
                    || OptionKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                    result[pair.Key] = pair.Value;
            }
            result["schema"] = OptionsSchema;
            foreach (string key in OptionKeys) result[key] = ToBool(result, key, false);
            result["fastMode"] = ToBool(result, "fastMode", false);
            result["reasoningMode"] = FirstNonEmpty(ToString(result, "reasoningMode"), "selective");
            result["reasoningEffort"] = ToString(result, "reasoningEffort");
            return result;
        }

        internal static CodexConversationContractKind NormalizeMode(
            string requestType,
            Dictionary<string, object> payload)
        {
            string value = FirstNonEmpty(
                ReadString(payload, "conversationMode"),
                ReadString(payload, "interactionMode"),
                ReadString(payload, "channel"),
                ReadString(payload, "mode"),
                requestType);
            string normalized = (value ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            // Schema selection is deliberately exact. A substring match would
            // route an unknown specialized contract (for example a court or
            // action classifier containing "conversation") into a smaller
            // dialogue schema and could silently discard its fields.
            switch (normalized)
            {
                case "correspondence":
                case "letter":
                case "mail":
                    return CodexConversationContractKind.Correspondence;
                case "castle":
                case "keep_chat":
                case "castle_chat":
                    return CodexConversationContractKind.Castle;
                case "party_chat":
                case "group":
                case "party":
                    return CodexConversationContractKind.Group;
                case "social_event":
                case "wilderness_event":
                case "settlement_home":
                case "feast":
                case "tournament":
                case "social":
                    return CodexConversationContractKind.Social;
                case "dialogue":
                case "individual":
                case "conversation":
                case "in_person":
                case "court_life":
                case "ambassador_official":
                    return CodexConversationContractKind.Individual;
                default:
                    return CodexConversationContractKind.Unknown;
            }
        }

        internal static List<CodexPromptSegment> BuildPromptSegments(
            IEnumerable<Dictionary<string, object>> messages)
        {
            List<Dictionary<string, object>> rows = (messages ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(x => x != null)
                .ToList();
            var result = new List<CodexPromptSegment>();
            for (int index = 0; index < rows.Count; index++)
            {
                string content = ReadString(rows[index], "content");
                bool shared = index == 0;
                bool character = index == 1 && rows.Count >= 3;
                result.Add(new CodexPromptSegment
                {
                    Order = index,
                    Name = shared ? "sharedRules" : character ? "character" : index == 2 ? "currentTurn" : "currentTurn_" + index,
                    Role = FirstNonEmpty(ReadString(rows[index], "role"), "user"),
                    Content = content,
                    Stable = shared || character,
                    Visibility = "provider",
                    Hash = Sha256(content)
                });
            }
            return result;
        }

        internal static List<Dictionary<string, object>> SegmentDictionaries(
            IEnumerable<CodexPromptSegment> segments,
            bool includeContent = true)
        {
            return (segments ?? Enumerable.Empty<CodexPromptSegment>())
                .Select(x => x == null ? new Dictionary<string, object>() : x.ToDictionary(includeContent))
                .ToList();
        }

        internal static Dictionary<string, object> StablePromptMapping(
            IEnumerable<CodexPromptSegment> source,
            IEnumerable<CodexPromptSegment> outgoing,
            bool requested,
            bool applied)
        {
            List<CodexPromptSegment> original = (source ?? Enumerable.Empty<CodexPromptSegment>()).ToList();
            List<CodexPromptSegment> sent = (outgoing ?? Enumerable.Empty<CodexPromptSegment>()).ToList();
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["schema"] = PromptMappingVersion,
                ["requested"] = requested,
                ["applied"] = applied,
                ["reason"] = applied ? "existing shared-rule, character, and current-turn envelope mapped without changing instruction precedence" : "experiment disabled",
                ["sourceSegments"] = SegmentDictionaries(original),
                ["outgoingSegments"] = SegmentDictionaries(sent),
                ["stableHashes"] = original.Where(x => x != null && x.Stable).ToDictionary(x => x.Name, x => (object)x.Hash, StringComparer.OrdinalIgnoreCase),
                ["cacheUsage"] = "unknown"
            };
        }

        internal static CodexConversationThreadIdentity BuildThreadIdentity(
            string campaignId,
            string timelineId,
            string loadGeneration,
            string sessionId,
            string conversationMode,
            string speakerId,
            string visibilityScope,
            string ownSessionId,
            string authorityHash,
            string promptVersion,
            string schemaId,
            string model,
            string reasoningEffort,
            bool fastMode)
        {
            return new CodexConversationThreadIdentity
            {
                CampaignId = campaignId ?? "",
                TimelineId = timelineId ?? "",
                LoadGeneration = loadGeneration ?? "",
                SessionId = sessionId ?? "",
                ConversationMode = conversationMode ?? "",
                SpeakerId = speakerId ?? "",
                VisibilityScope = visibilityScope ?? "",
                OwnSessionId = ownSessionId ?? "",
                AuthorityHash = authorityHash ?? "",
                PromptVersion = promptVersion ?? "",
                SchemaId = schemaId ?? "",
                Model = model ?? "",
                ReasoningEffort = reasoningEffort ?? "",
                FastMode = fastMode
            };
        }

        internal static bool ThreadIdentitiesCompatible(
            CodexConversationThreadIdentity existing,
            CodexConversationThreadIdentity requested,
            out string reason)
        {
            reason = "";
            if (existing == null || requested == null)
            {
                reason = "thread identity is missing";
                return false;
            }
            string existingReason;
            string requestedReason;
            bool existingReusable = existing.CanReuse(out existingReason);
            bool requestedReusable = requested.CanReuse(out requestedReason);
            if (!existingReusable || !requestedReusable)
            {
                reason = FirstNonEmpty(existingReason, requestedReason);
                return false;
            }
            string[] fields =
            {
                "CampaignId", "TimelineId", "LoadGeneration", "SessionId",
                "ConversationMode", "SpeakerId", "VisibilityScope", "OwnSessionId",
                "AuthorityHash", "PromptVersion", "SchemaId", "Model", "ReasoningEffort"
            };
            foreach (string field in fields)
            {
                object left = typeof(CodexConversationThreadIdentity).GetProperty(field).GetValue(existing, null);
                object right = typeof(CodexConversationThreadIdentity).GetProperty(field).GetValue(requested, null);
                if (!string.Equals(Convert.ToString(left), Convert.ToString(right), StringComparison.Ordinal))
                {
                    reason = field + " changed";
                    return false;
                }
            }
            if (existing.FastMode != requested.FastMode)
            {
                reason = "fast mode changed";
                return false;
            }
            reason = "compatible";
            return true;
        }

        internal static Dictionary<string, object> BuildParallelPreparationPlan(
            bool requested,
            bool consistentSnapshot,
            bool characterInitialized,
            bool requiredStateUpdatesComplete,
            IEnumerable<string> taskIds)
        {
            List<string> ordered = (taskIds ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            bool allowed = requested && consistentSnapshot && characterInitialized && requiredStateUpdatesComplete;
            string reason = allowed
                ? "independent context reads may use isolated workers"
                : !requested ? "experiment disabled"
                : !consistentSnapshot ? "consistent snapshot unavailable; sequential preparation required"
                : !characterInitialized ? "character initialization is incomplete"
                : "required state updates are incomplete";
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["requested"] = requested,
                ["allowed"] = allowed,
                ["maxWorkers"] = MaxParallelWorkers,
                ["taskCount"] = ordered.Count,
                ["taskIds"] = ordered,
                ["ordering"] = "input order",
                ["isolatedDataOnly"] = true,
                ["sharedMutablePayloadsAllowed"] = false,
                ["sharedConnectionsAllowed"] = false,
                ["sharedTransactionsAllowed"] = false,
                ["fallback"] = allowed ? "none" : "sequential",
                ["reason"] = reason
            };
        }

        internal static CodexParallelPreparationResult PrepareInParallel(
            IEnumerable<CodexContextPreparationTask> tasks,
            bool requested,
            bool consistentSnapshot,
            bool characterInitialized,
            bool requiredStateUpdatesComplete)
        {
            List<CodexContextPreparationTask> ordered = (tasks ?? Enumerable.Empty<CodexContextPreparationTask>())
                .Where(x => x != null && x.Prepare != null)
                .Select((x, index) => new { Task = x, Index = index })
                .OrderBy(x => x.Index)
                .Select(x => x.Task)
                .ToList();
            CodexParallelPreparationResult result = new CodexParallelPreparationResult
            {
                Requested = requested,
                Diagnostics = BuildParallelPreparationPlan(requested, consistentSnapshot, characterInitialized, requiredStateUpdatesComplete, ordered.Select(x => x.Id))
            };
            bool allowed = ToBool(result.Diagnostics, "allowed", false);
            if (!allowed)
            {
                result.FellBackToSequential = true;
                result.FallbackReason = ReadString(result.Diagnostics, "reason");
                result.Results = RunSequential(ordered, result.Errors);
                result.Diagnostics["usedParallel"] = false;
                result.Diagnostics["fallbackUsed"] = true;
                return result;
            }

            if (ordered.Count > MaxParallelWorkers)
            {
                result.Errors.Add("context preparation exceeded the four-worker limit");
                result.FellBackToSequential = true;
                result.FallbackReason = "worker limit exceeded; no partial parallel work was started";
                result.Results = RunSequential(ordered, result.Errors);
                result.Diagnostics["usedParallel"] = false;
                result.Diagnostics["fallbackUsed"] = true;
                result.Diagnostics["errorCount"] = result.Errors.Count;
                return result;
            }

            try
            {
                Task<object>[] workers = ordered.Select(x => Task.Run(x.Prepare)).ToArray();
                Task.WaitAll(workers);
                result.Results = workers.Select(x => x.Result).ToList();
                result.UsedParallel = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
                result.FellBackToSequential = true;
                result.FallbackReason = "parallel preparation failed; sequential preparation retained";
                result.Results = RunSequential(ordered, result.Errors);
            }
            result.Diagnostics["usedParallel"] = result.UsedParallel;
            result.Diagnostics["fallbackUsed"] = result.FellBackToSequential;
            result.Diagnostics["errorCount"] = result.Errors.Count;
            return result;
        }

        private static List<object> RunSequential(
            IEnumerable<CodexContextPreparationTask> tasks,
            List<string> errors)
        {
            var output = new List<object>();
            foreach (CodexContextPreparationTask task in tasks ?? Enumerable.Empty<CodexContextPreparationTask>())
            {
                try { output.Add(task.Prepare()); }
                catch (Exception ex) { errors.Add(task.Id + ": " + ex.Message); }
            }
            return output;
        }

        internal static Dictionary<string, object> CompactMetadataPolicy(string requestType)
        {
            bool correspondence = (requestType ?? "").IndexOf("correspondence", StringComparison.OrdinalIgnoreCase) >= 0;
            var preserve = correspondence
                ? new[] { "shouldReply", "body", "reason", "actionGate", "rebellionDecision", "campaignOrder", "evidenceSourceIds", "uncertainty" }
                : new[] { "reply", "participation", "reactionTargetHeroStringId", "decisionBrief", "politicalConduct", "emotion", "intent", "relationshipSignal", "relationshipAssessments", "actionGate", "conceptionGate", "socialSignals", "identityIntroductions", "memoryWrites", "beliefWrites", "obligationWrites", "comprehensionWrites", "dynamicCharacteristicWrites", "courtKnowledgeWrites", "sceneStateUpdates", "stateUpdates", "suggestedActions", "evidenceSourceIds", "uncertainty" };
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["schema"] = "reign-codex-compact-metadata-v1",
                ["requestType"] = requestType ?? "",
                ["instruction"] = "Keep visible dialogue and correspondence length rules unchanged. Use short explanations in private metadata and return empty collections when no item applies. Preserve every field meaning, evidence identifier, source quote, confidence, uncertainty, action classification, relationship classification, and durable memory; never omit a durable write to make JSON shorter. Do not output private reasoning.",
                ["preserveFields"] = preserve.ToList(),
                ["emptyCollectionsAllowed"] = true,
                ["dialogueLengthPolicy"] = "unchanged",
                ["durableWritesMayBeDropped"] = false
            };
        }

        /// <summary>
        /// Removes only null placeholders introduced by the strict Codex
        /// schemas. The legacy parser treats omitted optional fields as
        /// "unchanged"; replacing them with empty strings would clear durable
        /// character state. Contract-defined nulls, unknown fields, and every
        /// non-null value are preserved. A clone is returned so the raw
        /// provider response remains available for diagnostics.
        /// </summary>
        internal static Dictionary<string, object> NormalizeStructuredOptionalNulls(
            string schemaId,
            Dictionary<string, object> content,
            out List<string> removedPaths)
        {
            removedPaths = new List<string>();
            Dictionary<string, object> cloned = CloneStructuredDictionary(content);
            if (!IsBuiltInConversationSchema(schemaId) || cloned == null) return cloned;

            Dictionary<string, object> stateUpdates = AsStructuredDictionary(
                cloned.TryGetValue("stateUpdates", out object rawState) ? rawState : null);
            if (stateUpdates != null)
            {
                foreach (string key in new[] { "mood", "currentPlan", "currentCrisis" })
                {
                    if (stateUpdates.TryGetValue(key, out object value) && value == null)
                    {
                        stateUpdates.Remove(key);
                        removedPaths.Add("stateUpdates." + key);
                    }
                }
            }

            if (cloned.TryGetValue("dynamicCharacteristicWrites", out object rawWrites)
                && rawWrites is IEnumerable writes && !(rawWrites is string))
            {
                foreach (object rawWrite in writes)
                {
                    Dictionary<string, object> write = AsStructuredDictionary(rawWrite);
                    if (write != null
                        && write.TryGetValue("narrativeDevelopment", out object development)
                        && development == null)
                    {
                        write.Remove("narrativeDevelopment");
                        removedPaths.Add("dynamicCharacteristicWrites[].narrativeDevelopment");
                    }
                }
            }
            return cloned;
        }

        private static bool IsBuiltInConversationSchema(string schemaId)
        {
            return new[]
            {
                "reign-codex-individual-v1", "reign-codex-group-v1",
                "reign-codex-castle-v1", "reign-codex-social-v1",
                "reign-codex-correspondence-v1"
            }.Contains(schemaId ?? "", StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> CloneStructuredDictionary(
            Dictionary<string, object> source)
        {
            if (source == null) return null;
            return AsStructuredDictionary(CloneStructuredValue(source));
        }

        private static object CloneStructuredValue(object value)
        {
            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (object rawKey in dictionary.Keys)
                {
                    string key = Convert.ToString(rawKey) ?? "";
                    result[key] = CloneStructuredValue(dictionary[rawKey]);
                }
                return result;
            }
            if (value is IEnumerable enumerable && !(value is string))
            {
                var result = new List<object>();
                foreach (object item in enumerable) result.Add(CloneStructuredValue(item));
                return result;
            }
            return value;
        }

        private static Dictionary<string, object> AsStructuredDictionary(object value)
        {
            return value as Dictionary<string, object>
                ?? (value as IDictionary)?.Cast<DictionaryEntry>().ToDictionary(
                    pair => Convert.ToString(pair.Key) ?? "",
                    pair => CloneStructuredValue(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
        }

        internal static string Sha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static string CanonicalValue(object value)
        {
            if (value == null) return "null";
            if (value is string text) return "\"" + Escape(text) + "\"";
            if (value is bool boolean) return boolean ? "true" : "false";
            if (value is IDictionary dictionary)
            {
                List<string> keys = new List<string>();
                foreach (object key in dictionary.Keys) keys.Add(Convert.ToString(key) ?? "");
                keys.Sort(StringComparer.Ordinal);
                return "{" + string.Join(",", keys.Select(key => "\"" + Escape(key) + "\":" + CanonicalValue(dictionary[key]))) + "}";
            }
            if (value is IEnumerable enumerable && !(value is string))
            {
                var rows = new List<string>();
                foreach (object item in enumerable) rows.Add(CanonicalValue(item));
                return "[" + string.Join(",", rows) + "]";
            }
            if (value is IFormattable formattable) return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            return "\"" + Escape(Convert.ToString(value) ?? "") + "\"";
        }

        internal static string AuthorityHash(
            Dictionary<string, object> payload,
            Dictionary<string, object> metadata,
            Dictionary<string, object> context)
        {
            string supplied = FirstNonEmpty(
                ReadString(context, "authorityHash"),
                ReadString(metadata, "authorityHash"),
                ReadString(payload, "authorityHash"));
            if (!string.IsNullOrWhiteSpace(supplied)) return supplied;
            if (metadata != null && metadata.Count > 0)
                return "sha256:" + Sha256(CanonicalValue(metadata));
            return "";
        }

        internal static string FirstNonEmpty(params string[] values)
        {
            return (values ?? new string[0]).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
        }

        private static string Escape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string ReadString(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null) return "";
            return value as string ?? Convert.ToString(value) ?? "";
        }

        private static bool ToBool(Dictionary<string, object> source, string key, bool fallback)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null) return fallback;
            if (value is bool boolean) return boolean;
            return bool.TryParse(Convert.ToString(value), out bool parsed) ? parsed : fallback;
        }

        private static string ToString(Dictionary<string, object> source, string key)
        {
            return ReadString(source, key);
        }

        private static IEnumerable<Dictionary<string, object>> AsDictionaryList(object raw)
        {
            if (raw is IEnumerable<Dictionary<string, object>> typed) return typed;
            if (raw is IEnumerable enumerable)
                return enumerable.OfType<Dictionary<string, object>>();
            return Enumerable.Empty<Dictionary<string, object>>();
        }
    }

    internal static partial class Program
    {
        private static void EnrichCodexGenerationContext(
            Dictionary<string, object> settings,
            Dictionary<string, object> payload,
            string requestType,
            string model,
            Dictionary<string, object> context,
            Dictionary<string, object> requestBody)
        {
            if (!UsesCodexSubscription(settings) || context == null || requestBody == null) return;
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> options = CodexConversationContracts.NormalizeOptions(
                ReadDictionary(settings, "codexOptions"));
            context["requestType"] = FirstNonEmpty(ReadString(context, "requestType", ""), requestType, ReadString(payload, "requestType", "dialogue"));
            context["model"] = FirstNonEmpty(ReadString(context, "model", ""), model, ReadString(payload, "model", ""));
            context["options"] = options;
            context["correlationId"] = FirstNonEmpty(ReadString(context, "correlationId", ""), ReadString(payload, "correlationId", ""));
            context["attemptId"] = FirstNonEmpty(ReadString(context, "attemptId", ""), ReadString(payload, "attemptId", ""));
            // Only the server-built context may authorize thread reuse.  Do not
            // treat arbitrary /llm/chat payload metadata as campaign authority.
            Dictionary<string, object> authoritativeMetadata = ReadDictionary(context, "authoritativeMetadata")
                ?? new Dictionary<string, object>();
            context["authoritativeMetadata"] = new Dictionary<string, object>(authoritativeMetadata, StringComparer.OrdinalIgnoreCase);

            // The production conversation path may attach these values after it
            // resolves the campaign snapshot. Prefer that trusted envelope over
            // request fields that can be supplied by a caller.
            string campaignId = FirstNonEmpty(
                ReadString(authoritativeMetadata, "campaignId", ""),
                ReadString(context, "campaignId", ""),
                ReadString(payload, "campaignId", ""));
            string timelineId = FirstNonEmpty(
                ReadString(authoritativeMetadata, "timelineId", ""),
                ReadString(authoritativeMetadata, "loadGeneration", ""),
                ReadString(context, "timelineId", ""),
                CodexConversationTimeline(campaignId));
            string loadGeneration = FirstNonEmpty(
                ReadString(authoritativeMetadata, "loadGeneration", ""),
                ReadString(context, "loadGenerationId", ""),
                ReadString(context, "loadGeneration", ""),
                timelineId);
            string sessionId = FirstNonEmpty(
                ReadString(authoritativeMetadata, "sessionId", ""),
                ReadString(authoritativeMetadata, "conversationSessionId", ""),
                ReadString(context, "sessionId", ""),
                ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId"));
            string speakerId = FirstNonEmpty(
                ReadString(authoritativeMetadata, "speakerHeroStringId", ""),
                ReadString(authoritativeMetadata, "speakerId", ""),
                ReadString(context, "speakerId", ""),
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId", "characterId"));
            string visibility = FirstNonEmpty(
                ReadString(authoritativeMetadata, "visibilityScope", ""),
                ReadString(authoritativeMetadata, "visibility", ""),
                ReadString(context, "visibilityScope", ""),
                ReadString(payload, "visibilityScope", ""),
                ReadString(payload, "visibility", ""));
            string conversationMode = FirstNonEmpty(
                ReadString(authoritativeMetadata, "conversationMode", ""),
                ReadString(context, "conversationMode", ""),
                ReadString(payload, "conversationMode", ""),
                ReadString(payload, "mode", ""));
            string ownSessionId = FirstNonEmpty(
                ReadString(authoritativeMetadata, "ownSessionId", ""),
                ReadString(authoritativeMetadata, "reignSessionId", ""),
                ReadString(context, "ownSessionId", ""),
                ReadFirstString(payload, "reignSessionId", "ownedSessionId", "sessionOwnerId"));
            context["campaignId"] = campaignId;
            context["timelineId"] = timelineId;
            context["loadGenerationId"] = loadGeneration;
            context["sessionId"] = sessionId;
            context["speakerId"] = speakerId;
            context["conversationMode"] = conversationMode;
            context["visibilityScope"] = visibility;

            List<Dictionary<string, object>> messages = ReadDictionaryList(requestBody, "messages");
            List<CodexPromptSegment> originalSegments = CodexConversationContracts.BuildPromptSegments(messages);
            List<CodexPromptSegment> outgoingSegments = originalSegments;
            bool compactRequested = ReadBool(options, "compactMetadata", false);
            if (compactRequested)
            {
                Dictionary<string, object> compact = CodexConversationContracts.CompactMetadataPolicy(requestType);
                context["compactMetadata"] = compact;
                List<Dictionary<string, object>> requestMessages = messages.Select(CloneDictionary).ToList();
                if (requestMessages.Count > 0)
                {
                    int last = requestMessages.Count - 1;
                    string content = ReadString(requestMessages[last], "content", "");
                    requestMessages[last]["content"] = content.TrimEnd() + "\n\nCOMPACT PRIVATE METADATA POLICY\n" + ReadString(compact, "instruction", "");
                    requestBody["messages"] = requestMessages;
                }
                outgoingSegments = CodexConversationContracts.BuildPromptSegments(requestMessages);
            }
            else
            {
                context["compactMetadata"] = new Dictionary<string, object>
                {
                    ["requested"] = false,
                    ["applied"] = false,
                    ["reason"] = "experiment disabled"
                };
            }

            bool stableRequested = ReadBool(options, "stablePromptMapping", false);
            context["promptSegments"] = CodexConversationContracts.SegmentDictionaries(outgoingSegments);
            context["sourcePromptSegments"] = CodexConversationContracts.SegmentDictionaries(originalSegments);
            context["stablePromptMapping"] = CodexConversationContracts.StablePromptMapping(
                originalSegments, outgoingSegments, stableRequested, stableRequested);
            context["promptMappingVersion"] = CodexConversationContracts.PromptMappingVersion;

            string authorityHash = CodexConversationContracts.AuthorityHash(payload, authoritativeMetadata, context);
            context["authorityHash"] = authorityHash;
            Dictionary<string, object> schemaPayload = CloneDictionary(payload);
            foreach (KeyValuePair<string, object> pair in authoritativeMetadata)
                if (!schemaPayload.ContainsKey(pair.Key)) schemaPayload[pair.Key] = pair.Value;
            // This private marker is created only from the active server-owned
            // conversation scope. It lets schema selection distinguish a real
            // production envelope from an arbitrary /llm/chat payload that
            // merely names requestType=dialogue.
            if (authoritativeMetadata.Count > 0)
                schemaPayload["__reignAuthoritativeConversation"] = true;
            CodexConversationSchemaSelection selection = ResolveCodexConversationSchema(requestType, schemaPayload);
            context["schemaSelection"] = selection.ToDictionary(ReadBool(options, "structuredOutputs", false));
            context["schemaId"] = selection.SchemaId;
            context["schemaApplicability"] = selection.Applicability;
            context["schemaReason"] = selection.Reason;
            context["schemaContractFingerprint"] = selection.ContractFingerprint;
            context["structuredSchemaIntroducedNullPaths"] = new List<string>(selection.SchemaIntroducedNullPaths ?? new List<string>());
            context["structuredContractDefinedNullPaths"] = new List<string>(selection.ContractDefinedNullPaths ?? new List<string>());
            bool structuredRequested = ReadBool(options, "structuredOutputs", false);
            context["structuredOutputsRequested"] = structuredRequested;
            context["structuredOutputsApplied"] = structuredRequested && selection.Schema != null && selection.BuiltInContract;
            if (structuredRequested && selection.Schema != null && selection.BuiltInContract)
            {
                // Codex app-server's turn/start.outputSchema field receives the
                // raw JSON Schema. The OpenAI-style envelope is retained only
                // as diagnostics for adapters that need it later.
                context["outputSchema"] = selection.Schema;
            }
            else
            {
                context.Remove("outputSchema");
            }

            string mode = selection.Mode;
            if (string.IsNullOrWhiteSpace(mode) || mode == "unknown") mode = FirstNonEmpty(conversationMode, "unknown");
            string reasoning = FirstNonEmpty(
                ReadString(context, "requestedReasoningEffort", ""),
                ReadString(context, "resolvedReasoningEffort", ""),
                ReadString(context, "selectedReasoningEffort", ""),
                ReadString(context, "reasoningEffort", ""),
                ReadString(options, "reasoningEffort", ""),
                ReadString(payload, "reasoningEffort", ""));
            CodexConversationThreadIdentity identity = CodexConversationContracts.BuildThreadIdentity(
                campaignId,
                timelineId,
                loadGeneration,
                sessionId,
                mode,
                speakerId,
                visibility,
                ownSessionId,
                authorityHash,
                FirstNonEmpty(ReadString(context, "promptVersion", ""), CodexConversationContracts.PromptMappingVersion),
                selection.SchemaId,
                model,
                reasoning,
                ReadBool(options, "fastMode", false));
            context["threadIdentity"] = identity.ToDictionary();
            context["threadReuseRequested"] = ReadBool(options, "reuseThreads", false);
            context["threadReuseSafe"] = ReadBool(options, "reuseThreads", false) && identity.CanReuse(out _);

            Dictionary<string, object> parallel = ReadDictionary(payload, "parallelPreparation") ?? new Dictionary<string, object>();
            bool consistentSnapshot = ReadBool(parallel, "consistentSnapshot", ReadBool(payload, "consistentSnapshot", false));
            bool characterInitialized = ReadBool(parallel, "characterInitialized", true);
            bool requiredStateUpdatesComplete = ReadBool(parallel, "requiredStateUpdatesComplete", true);
            context["parallelContextPreparation"] = CodexConversationContracts.BuildParallelPreparationPlan(
                ReadBool(options, "parallelContextPreparation", false), consistentSnapshot,
                characterInitialized, requiredStateUpdatesComplete,
                ReadStringList(parallel, "taskIds"));
            context["codexContextVersion"] = "reign-codex-generation-context-v1";
        }

        private static CodexConversationSchemaSelection ResolveCodexConversationSchema(
            string requestType,
            Dictionary<string, object> payload)
        {
            CodexConversationContractKind kind = CodexConversationContracts.NormalizeMode(requestType, payload);
            if (!HasProductionConversationEvidence(payload))
            {
                return CodexConversationSchemas.LegacyFallback(kind,
                    "production promptEnvelope or server-authoritative conversation metadata is required; arbitrary requests use the legacy response path");
            }
            Dictionary<string, string> actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> builtIns = DefaultPromptTemplates();
            foreach (string name in CodexConversationSchemas.TemplateNames(kind))
            {
                try { actual[name] = LoadPromptTemplate(name); }
                catch { actual[name] = ""; }
                defaults[name] = builtIns.ContainsKey(name) ? builtIns[name] : "";
            }
            return CodexConversationSchemas.Select(kind, actual, defaults, payload);
        }

        private static bool HasProductionConversationEvidence(Dictionary<string, object> payload)
        {
            if (payload == null) return false;
            if (ReadBool(payload, "__reignAuthoritativeConversation", false)) return true;
            Dictionary<string, object> envelope = ReadDictionary(payload, "promptEnvelope");
            return envelope != null
                && envelope.Count > 0
                && envelope.ContainsKey("layoutVersion")
                && envelope.ContainsKey("requestType");
        }

    }
}
