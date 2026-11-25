using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class SettingsService
    {
        internal static Dictionary<string, object> DefaultSettings()
        {
            var settings = new Dictionary<string, object>
            {
                ["host"] = "127.0.0.1",
                ["port"] = 5101,
                ["llmProvider"] = LlmProviderService.OpenAiCompatibleProvider,
                ["codexExecutable"] = "codex",
                ["codexHome"] = "",
                ["apiUrl"] = "https://nano-gpt.com/api/v1/chat/completions",
                ["apiKey"] = "",
                ["siteUrl"] = "",
                ["appTitle"] = "Bannerlord Reign",
                ["dialogueModel"] = "zai-org/glm-5.2",
                ["personalGossip"] = DialogueService.DefaultPersonalGossipSettings(),
                ["personalGossipEnabled"] = true,
                ["personalGossipShareAdjustment"] = 0,
                ["personalGossipNegativeAdjustment"] = 0,
                ["personalGossipDisclosureAdjustment"] = 0,
                ["personalGossipDistortionAdjustment"] = 0,
                ["personalGossipRetellAdjustment"] = 0,
                ["personalGossipRaiseAdjustment"] = 0,
                ["characterConstructionModel"] = "zai-org/glm-5.2",
                ["diplomacyModel"] = "zai-org/glm-5.2",
                ["eventsModel"] = "zai-org/glm-5.2",
                ["fastModel"] = "",
                ["memoryModel"] = "zai-org/glm-5.2",
                ["relationshipModel"] = "zai-org/glm-5.2",
                ["correspondenceModel"] = "zai-org/glm-5.2",
                ["strategyModel"] = "zai-org/glm-5.2",
                ["enableMinimeMemoryWorker"] = true,
                ["minimeTopicUrl"] = "http://127.0.0.1:8082/topic",
                ["minimeMaxTopics"] = 5,
                ["minimeTimeoutMs"] = 750,
                ["minimeRetryCooldownSeconds"] = 30,
                ["enableMinimeMemoryReranking"] = false,
                ["minimeRerankUrl"] = "http://127.0.0.1:8082/rerank",
                ["minimeRerankCandidateCount"] = 32,
                ["minimeRerankTimeoutMs"] = 15000,
                ["minimeRerankWeight"] = 35,
                ["enableSemanticMemory"] = true,
                ["vectorProvider"] = "local",
                ["embeddingModel"] = "BAAI/bge-small-en-v1.5",
                ["vectorWorkerUrl"] = "http://127.0.0.1:8082",
                ["vectorSearchTopK"] = 96,
                ["vectorScoreWeight"] = 30,
                ["vectorQueryTimeoutMs"] = 10000,
                ["embeddingBatchSize"] = 8,
                ["qdrantUrl"] = "",
                ["qdrantApiKey"] = "",
                ["enableMemoryConsolidation"] = true,
                ["legacyMemoryMode"] = false,
                ["conversationInputTokenLimit"] = 120000,
                ["memoryReferenceGraphMaxNodes"] = 32,
                ["memoryReferenceGraphHops"] = 32,
                ["memoryReferenceGraphMaxCharacters"] = 96000,
                ["useMemoryLlmForConsolidation"] = true,
                ["memoryConsolidationMinItems"] = 4,
                ["memoryConsolidationWindowMinutes"] = 30,
                ["memoryConsolidationMaxGroups"] = 3,
                ["memoryConsolidationMaxSourceItems"] = 24,
                ["memoryConsolidationMaxSummaryChars"] = 1800,
                ["actionRouterModel"] = "",
                ["actionPlannerModel"] = "",
                ["enableHiddenActionPlanner"] = true,
                ["showActionGateDebug"] = true,
                ["enableActionRouterLlm"] = true,
                ["enableActionRouterRepair"] = true,
                ["actionRouterAlwaysRun"] = false,
                ["actionRouterCandidateCount"] = 10,
                ["actionRouterMaxTokens"] = 700,
                ["actionPlannerMaxTokens"] = 900,
                ["constructCharactersWithLlm"] = true,
                ["backgroundCharacterEnrichmentEnabled"] = false,
                ["temperature"] = 0.7d,
                ["maxTokens"] = 900,
                ["characterConstructionMaxTokens"] = Program.CharacterConstructionTokenCeiling,
                ["promptCacheMode"] = "implicit",
                ["promptCacheFallbackCooldownSeconds"] = 300,
                ["reasoningMode"] = "selective",
                ["reasoningEffort"] = "medium",
                ["reasoningFallbackCooldownSeconds"] = 300,
                ["portraitProvider"] = "NanoGPT",
                ["portraitGenerationMethod"] = "text",
                ["portraitVisionProvider"] = "Codex",
                ["portraitVisionModel"] = "runtime-default",
                ["portraitVisionNanoGptModel"] = PortraitsService.DefaultNanoPortraitVisionModel,
                ["portraitNanoGptImageGenerationsUrl"] = "https://nano-gpt.com/api/v1/images/generations",
                ["portraitNanoGptChatCompletionsUrl"] = "https://nano-gpt.com/api/v1/chat/completions",
                [PortraitsService.LocalModelGenerationsKey] = PortraitsService.DefaultLocalModelGenerationsUrl,
                [PortraitsService.LocalModelEditsKey] = PortraitsService.DefaultLocalModelEditsUrl,
                ["portraitNanoGptApiKey"] = "",
                ["portraitNanoGptImageModel"] = "gpt-image-1.5",
                ["portraitAtlasGenerateImageUrl"] = "https://api.atlascloud.ai/api/v1/model/generateImage",
                ["portraitAtlasUploadMediaUrl"] = "https://api.atlascloud.ai/api/v1/model/uploadMedia",
                ["portraitAtlasPredictionUrlPrefix"] = "https://api.atlascloud.ai/api/v1/model/prediction/",
                ["portraitAtlasApiKey"] = "",
                ["portraitAtlasImageModel"] = "xai/grok-imagine-image/edit",
                ["portraitAtlasWanNegativePrompt"] = PortraitsService.DefaultAtlasWan25NegativePrompt,
                ["portraitImageStrength"] = 0.75d,
                ["portraitInferenceSteps"] = 28,
                ["portraitGuidanceScale"] = 3.5d,
                ["portraitNativeSourceMode"] = "required",
                ["portraitNativeSourceGeneratorPath"] = "",
                ["sceneryImageProfileInitialized"] = false,
                ["sceneryProvider"] = "NanoGPT",
                ["sceneryNanoGptImageModel"] = "gpt-image-1.5",
                ["sceneryAtlasImageModel"] = "xai/grok-imagine-image/edit",
                ["sceneryAtlasWanNegativePrompt"] = PortraitsService.DefaultAtlasWan25NegativePrompt,
                ["sceneryImageStrength"] = 0.75d,
                ["sceneryInferenceSteps"] = 28,
                ["sceneryGuidanceScale"] = 3.5d,
                ["enableLiveLog"] = true,
                ["enableRequestLogging"] = true,
                ["enableLlmLogging"] = true,
                ["prettyJsonLogs"] = false,
                ["maxContextMemories"] = 12,
                ["recentRawConversationTurns"] = 30,
                ["recentRawConversationCharBudget"] = 48000,
                ["promptWarningCharacterLimit"] = 100000,
                ["promptHardCharacterLimit"] = 240000,
                ["openTelemetryOtlpEndpoint"] = "",
                ["providerMaxConcurrency"] = 4,
                ["providerQueueTimeoutMs"] = 30000,
                ["providerRequestTimeoutMs"] = 120000,
                ["providerCircuitFailureThreshold"] = 4,
                ["providerCircuitBreakSeconds"] = 30,
                ["autoCaptureReplayCorpus"] = true,
                ["memoryTags"] = "war, peace, tribute, siege, army, betrayal, oath, marriage, fief, ransom, trade, recruit",
                ["settingsRevision"] = "",
                ["textureTraceEnabled"] = false,
                ["saveSyncEnabled"] = true,
                ["reignXpEnabled"] = true,
                ["playerApproachesEnabled"] = true,
                ["playerAttentionTrackingEnabled"] = true,
                ["playerJealousyApproachesEnabled"] = true,
                ["playerKeepInvitationsEnabled"] = true,
                ["playerPatrolInterceptionsEnabled"] = true,
                ["playerBattleRescueDialogueEnabled"] = true,
                ["playerPrisonRescueDialogueEnabled"] = true,
                ["correspondenceFrequencyMultiplier"] = 1.0,
                ["correspondenceTravelTimeMultiplier"] = 1.0,
                ["reignXpMultiplier"] = 1.0,
                ["reignXpConversationMultiplier"] = 1.0,
                ["reignXpSocialCharmMultiplier"] = 1.0,
                ["reignXpSocialBonusMultiplier"] = 1.0,
                ["reignXpPetitionMultiplier"] = 1.0,
                ["banterEnabled"] = true,
                ["banterMaxResponses"] = 6,
                ["banterMaxCharacters"] = 360,
                ["banterProvider"] = "",
                ["banterModel"] = "",
                ["autoOpenBrowser"] = true
            };
            foreach (var pair in Reign.Core.Contracts.Economy.WarEconomyOptions.DefaultValues()) settings[pair.Key] = pair.Value;
            foreach (string key in ReignBeta.Shared.Rulers.RulerOptions.Switches.Keys) settings[key] = true;
            foreach (string key in Reign.Core.Contracts.World.ReignNewsCategories.Switches.Keys) settings[key] = true;
            return settings;
        }

        internal static Dictionary<string, object> LoadSettings()
        {
            if (Monitor.IsEntered(Program.CampaignSchemaLock) && !Monitor.IsEntered(Program.FileLock))
                throw new InvalidOperationException("Settings require FileLock before CampaignSchemaLock.");
            ReignDatabase.CampaignSchemaSettingsReadObserverForTests?.Invoke(Monitor.IsEntered(Program.CampaignSchemaLock));
            lock(SettingsSaveGate)return LoadSettingsCore();
        }
        private static Dictionary<string, object> LoadSettingsCore()
        {
            if (LlmProviderService.ActiveCodexConversationScope.Value != null) return CharactersService.DeepCloneProfileDictionary(LlmProviderService.ActiveCodexConversationScope.Value.Settings);
            if (LlmProviderService.CodexPerformanceSettings.Value != null) return CharactersService.DeepCloneProfileDictionary(LlmProviderService.CodexPerformanceSettings.Value);
            if (CharactersService.NarrativeAuthoringSettings != null) return CharactersService.DeepCloneProfileDictionary(CharactersService.NarrativeAuthoringSettings);
            Dictionary<string, object> settings = DefaultSettings();
            bool settingsFileExisted = File.Exists(Program.SettingsPath);
            if (File.Exists(Program.SettingsPath))
            {
                try
                {
                    Dictionary<string, object> saved = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Program.SettingsPath, Encoding.UTF8));
                    foreach (KeyValuePair<string, object> pair in saved)
                    {
                        settings[pair.Key] = pair.Value;
                    }
                }
                catch (Exception ex)
                {
                    OperationsService.LogOperational("settings.load_failed", new Dictionary<string, object> { ["error"] = ex.Message });
                    // A fallback read must never normalize over the unreadable original.
                    return settings;
                }
            }
            bool secretsRecovered = RecoverInstalledSettingsSecrets(settings, !settingsFileExisted);
            SanitizeBoundKeySlots(settings);
            bool portraitSettingsChanged = NormalizePortraitImageSettings(settings);
            bool constructionBudgetChanged = CharactersService.NormalizeCharacterConstructionBudget(settings);
            bool memorySettingsChanged = NormalizeMemorySettings(settings);
            bool architectureSettingsChanged = NormalizeArchitectureSettings(settings);
            bool llmProviderSettingsChanged = LlmProviderService.NormalizeLlmProviderSettings(settings);
            bool codexOptionsChanged = LlmProviderService.NormalizeCodexOptions(settings);
            DialogueService.NormalizePromptCacheSettings(settings);
            DialogueService.NormalizeReasoningSettings(settings);
            if (!settingsFileExisted || secretsRecovered || portraitSettingsChanged || constructionBudgetChanged || memorySettingsChanged || architectureSettingsChanged || llmProviderSettingsChanged || codexOptionsChanged)
            {
                settings["settingsRevision"] = DateTime.UtcNow.ToString("o");
                Directory.CreateDirectory(Program.DataDir);
                CampaignFileService.WriteJsonObject(Program.SettingsPath, settings);
                OperationsService.LogOperational("settings.normalized", new Dictionary<string, object>
                {
                    ["portraitSettingsChanged"] = portraitSettingsChanged, ["memorySettingsChanged"] = memorySettingsChanged,
                    ["constructionBudgetChanged"] = constructionBudgetChanged,
                    ["architectureSettingsChanged"] = architectureSettingsChanged,
                    ["llmProviderSettingsChanged"] = llmProviderSettingsChanged,
                    ["secretsRecovered"] = secretsRecovered,
                    ["settingsFileExisted"] = settingsFileExisted
                });
            }
            // Reign's local vector worker uses its own loopback port 5102.
            settings["vectorWorkerUrl"] = "http://127.0.0.1:5102";
            PersistInstalledSettingsSecrets(settings);
            LlmProviderService.ApplySettingsProviderConcurrency(settings);
            return settings;
        }

        // Reuse the existing file coordinator: callers already holding FileLock can
        // read settings without a second lock-order dependency. Atomic replacement
        // and revision checks cover both user saves and normalization writes.
        internal static object SettingsSaveGate => Program.FileLock;
        private static readonly object SettingsEffectsGate = new object();
        internal static Dictionary<string, object> SaveSettings(Dictionary<string, object> incoming)
        {
            Dictionary<string, object> applied;
            bool invalidate;
            Dictionary<string, object> result;
            lock (SettingsSaveGate) result = SaveSettingsCore(incoming, out applied, out invalidate);
            if (applied != null)
            {
                lock (SettingsEffectsGate)
                {
                    if (invalidate) LlmProviderService.InvalidateCodexThreads("provider_or_codex_settings_changed");
                    OperationsService.LogOperational("settings.save", OperationsService.RedactSensitive(applied));
                    if (Program.ActiveListener != null)
                {
                    // Concurrent saves may have committed since ours: apply the latest state.
                    var current = LoadSettings();
                    if (ReadBool(current, "enableSemanticMemory", true)) DialogueService.StartSemanticMemorySubsystem(current);
                    else DialogueService.StopSemanticMemorySubsystem();
                }
                }
            }
            return result;
        }
        // settingsPathForTests: contract tests save to a disposable file instead of the live settings; the installed vault is
        // then not written. Never set from a request.
        private static Dictionary<string, object> SaveSettingsCore(Dictionary<string, object> incoming,
            out Dictionary<string, object> applied, out bool invalidate, string settingsPathForTests = null)
        {
            string settingsPath = settingsPathForTests ?? Program.SettingsPath;
            applied = null;
            invalidate = false;
            string gossipError = ValidatePersonalGossipOptions(incoming);
            if (!string.IsNullOrEmpty(gossipError)) return TestDict("ok", false, "error", gossipError);
            string announcementError = ValidateAnnouncementOptions(incoming);
            if (!string.IsNullOrEmpty(announcementError)) return TestDict("ok", false, "error", announcementError);
            string rulerError = ValidateRulerOptions(incoming);
            if (!string.IsNullOrEmpty(rulerError)) return TestDict("ok", false, "error", rulerError);
            string npcLivesError = ValidateNpcLivesOptions(incoming);
            if (!string.IsNullOrEmpty(npcLivesError)) return TestDict("ok", false, "error", npcLivesError);
            string correspondenceError = ValidateCorrespondenceOptions(incoming);
            if (!string.IsNullOrEmpty(correspondenceError)) return TestDict("ok", false, "error", correspondenceError);
            string approachError = ValidatePlayerApproachOptions(incoming);
            if (!string.IsNullOrEmpty(approachError)) return TestDict("ok", false, "error", approachError);
            string banterError = ValidateBanterOptions(incoming);
            if (!string.IsNullOrEmpty(banterError)) return TestDict("ok", false, "error", banterError);
            string xpError = ValidateReignXpOptions(incoming);
            if (!string.IsNullOrEmpty(xpError))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = xpError };
            string imageProviderError = PortraitsService.ValidateCodexImageSettings(incoming);
            if (!string.IsNullOrEmpty(imageProviderError))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = imageProviderError };
            string visionError = PortraitsService.ValidatePortraitVisionSettings(incoming);
            if (!string.IsNullOrEmpty(visionError)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = visionError };
            Dictionary<string, object> settings = DefaultSettings();
            if (File.Exists(settingsPath))
            {
                try
                {
                    Dictionary<string, object> saved = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsPath, Encoding.UTF8));
                    foreach (KeyValuePair<string, object> pair in saved)
                    {
                        settings[pair.Key] = pair.Value;
                    }
                }
                catch (Exception ex)
                {
                    OperationsService.LogOperational("settings.save_load_existing_failed", new Dictionary<string, object> { ["error"] = ex.Message });
                    return TestDict("ok", false, "error", "Existing settings could not be read. The original file was preserved; repair it before saving.");
                }
            }

            // Stale pre-upgrade shadows are cleared from the stored state before the merge, so a Reign-only key in this
            // save is never mistaken for one.
            SanitizeBoundKeySlots(settings);
            string localKeyRefusal = LocalProviderKeyWriteRefusal(incoming, settings);
            if (localKeyRefusal != null) return TestDict("ok", false, "error", localKeyRefusal, "outcome", "global_keys_required");
            string processSettingRefusal = LlmProviderService.OperatorOwnedProcessSettingRefusal(settings, incoming);
            if (processSettingRefusal != null) return TestDict("ok", false, "error", processSettingRefusal, "outcome", "operator_setting_required");
            var storedEconomy = settings.Where(p => Reign.Core.Contracts.Economy.WarEconomyOptions.Switches.ContainsKey(p.Key)
                || Reign.Core.Contracts.Economy.WarEconomyOptions.Numbers.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (!Reign.Core.Contracts.Economy.WarEconomyOptions.TryCreate(storedEconomy, false, out var validStoredEconomy, out var storedEconomyError))
                return TestDict("ok", false, "error", "Existing economy settings are invalid and were preserved: " + storedEconomyError);
            foreach (var pair in validStoredEconomy.Values()) settings[pair.Key] = pair.Value;
            string economyError = ValidateWarEconomyPatch(settings, incoming, requireRevision:true);
            if (!string.IsNullOrEmpty(economyError)) return TestDict("ok", false, "error", economyError);
            if (incoming.ContainsKey("expectedSettingsRevision"))
            {
                incoming = new Dictionary<string, object>(incoming);
                incoming.Remove("expectedSettingsRevision");
            }
            // A Reign-only custom endpoint key belongs to the API URL the caller saw, exactly like Save key: the save carries
            // that URL and is refused before anything is stored unless its origin is the endpoint this save leaves in place.
            if (incoming.ContainsKey("expectedApiUrl"))
            {
                string effectiveApiUrl = incoming.ContainsKey("apiUrl") ? ReadString(incoming, "apiUrl", "") : ReadString(settings, "apiUrl", "");
                string expectedOrigin = NormalizedOrigin(ReadString(incoming, "expectedApiUrl", ""));
                if (expectedOrigin == null || expectedOrigin != NormalizedOrigin(effectiveApiUrl))
                    return TestDict("ok", false, "outcome", "endpoint_changed",
                        "error", "The saved API URL differs from the one shown. Save Changes (or reload) first; nothing was saved.");
                incoming = new Dictionary<string, object>(incoming);
                incoming.Remove("expectedApiUrl");
            }
            string previousCodexSettingsIdentity = LlmProviderService.CodexSettingsIdentity(settings);
            string settingsBeforeMerge = Json.Serialize(settings);
            incoming = WithoutGlobalKeyMigrationPatch(incoming);
            if (incoming.ContainsKey(PortraitsService.LocalModelGenerationsKey) || incoming.ContainsKey(PortraitsService.LocalModelEditsKey))
            {
                incoming = new Dictionary<string, object>(incoming);
                foreach (string key in new[] { PortraitsService.LocalModelGenerationsKey, PortraitsService.LocalModelEditsKey })
                {
                    if (!incoming.ContainsKey(key)) continue;
                    bool edit = key == PortraitsService.LocalModelEditsKey;
                    if (!PortraitsService.TryNormalizeLocalModelImageEndpoint(ReadString(incoming, key, ""), edit, out string normalized))
                        return TestDict("ok", false, "error", "Local Model image URLs must use the matching HTTP loopback generations or edits route without credentials, query or fragment.");
                    incoming[key] = normalized;
                }
            }
            List<string> clearKeys = LlmProviderService.MergeProviderSettings(settings, incoming);
            foreach (var pair in ReadWarEconomyOptions(settings).Values()) settings[pair.Key] = pair.Value;
            var npcLivesOptions = ReadNpcLivesOptions(settings);
            if (npcLivesOptions.Number("npcLivesMajorMinDays") > npcLivesOptions.Number("npcLivesMajorMaxDays"))
                return TestDict("ok", false, "error", "The minimum major-story cooldown cannot exceed its maximum.");

            if (incoming != null && incoming.Keys.Any(key =>
                key.StartsWith("scenery", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "sceneryImageProfileInitialized", StringComparison.OrdinalIgnoreCase)))
            {
                settings["sceneryImageProfileInitialized"] = true;
            }

            NormalizePortraitImageSettings(settings);
            if (incoming != null && incoming.ContainsKey("characterConstructionMaxTokens"))
                settings["characterConstructionBudgetInitialized"] = true;
            CharactersService.NormalizeCharacterConstructionBudget(settings);
            DialogueService.NormalizePromptCacheSettings(settings);
            DialogueService.NormalizeReasoningSettings(settings);
            NormalizeMemorySettings(settings);
            NormalizeArchitectureSettings(settings);
            LlmProviderService.NormalizeLlmProviderSettings(settings);
            if(clearKeys.Count==0 && Json.Serialize(settings)==settingsBeforeMerge)
            {
                if (settingsPathForTests == null) LlmProviderService.ApplySettingsProviderConcurrency(settings);
                var unchanged=SettingsForUi(settings);unchanged["ok"]=true;unchanged["unchanged"]=true;return unchanged;
            }
            SanitizeBoundKeySlots(settings);
            settings["settingsRevision"] = DateTime.UtcNow.ToString("o");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            CampaignFileService.WriteJsonObject(settingsPath, settings);
            if (settingsPathForTests == null)
            {
                LlmProviderService.ApplySettingsProviderConcurrency(settings);
                PersistInstalledSettingsSecrets(settings, clearKeys.Count > 0);
            }
            applied = settings;
            invalidate = previousCodexSettingsIdentity != LlmProviderService.CodexSettingsIdentity(settings);

            Dictionary<string, object> result = SettingsForUi(settings);
            result["ok"] = true;
            return result;
        }

        internal static bool NormalizePortraitImageSettings(Dictionary<string, object> settings)
        {
            if (settings == null)
            {
                return false;
            }

            bool changed = false;
            string nativeSourceMode = ReadString(settings, "portraitNativeSourceMode", "required").Trim().ToLowerInvariant();
            if (nativeSourceMode != "required" && nativeSourceMode != "preferred" && nativeSourceMode != "off")
            {
                nativeSourceMode = "required";
            }
            if (!settings.ContainsKey("portraitNativeSourceMode")
                || !string.Equals(ReadString(settings, "portraitNativeSourceMode", ""), nativeSourceMode, StringComparison.Ordinal))
            {
                settings["portraitNativeSourceMode"] = nativeSourceMode;
                changed = true;
            }
            if (!settings.ContainsKey("portraitNativeSourceGeneratorPath"))
            {
                settings["portraitNativeSourceGeneratorPath"] = "";
                changed = true;
            }
            if (!ReadBool(settings, "sceneryImageProfileInitialized", false))
            {
                string[] profileSuffixes =
                {
                    "Provider", "NanoGptImageModel", "AtlasImageModel", "OpenRouterImageModel", "AtlasWanNegativePrompt",
                    "ImageStrength", "InferenceSteps", "GuidanceScale"
                };
                foreach (string suffix in profileSuffixes)
                {
                    settings["scenery" + suffix] = settings.TryGetValue("portrait" + suffix, out object value)
                        ? value
                        : null;
                }
                settings["sceneryImageProfileInitialized"] = true;
                changed = true;
            }
            if (settings.Remove("portraitNanoGptImageEditsUrl"))
            {
                changed = true;
            }

            bool legacyDeskGenerations = PortraitsService.IsLegacyLocalModelDeskEndpoint(
                ReadString(settings, "portraitNanoGptImageGenerationsUrl", ""), false);
            bool legacyDeskChat = PortraitsService.IsLegacyLocalModelDeskEndpoint(
                ReadString(settings, "portraitNanoGptChatCompletionsUrl", ""), true);
            bool migratedDeskGenerations = false;
            bool migratedDeskChat = false;
            changed |= settings.TryAdd(PortraitsService.LocalModelGenerationsKey, PortraitsService.DefaultLocalModelGenerationsUrl);
            changed |= settings.TryAdd(PortraitsService.LocalModelEditsKey, PortraitsService.DefaultLocalModelEditsUrl);
            foreach (string prefix in PortraitsService.ImageProfilePrefixes)
            {
                bool adult = prefix.StartsWith("adult", StringComparison.Ordinal);
                if (adult)
                {
                    var defaults = new Dictionary<string, object> { ["Provider"] = "AtlasCloud",
                        ["NanoGptImageModel"] = "gpt-image-1.5", ["AtlasImageModel"] = PortraitsService.AtlasModelSeedreamV5ProEdit,
                        ["AtlasWanNegativePrompt"] = PortraitsService.DefaultAtlasWan25NegativePrompt };
                    foreach (var pair in defaults)
                        if (!settings.ContainsKey(prefix + pair.Key)) { settings[prefix + pair.Key] = pair.Value; changed = true; }
                }
                if (PortraitsService.IsRetiredImageProvider(ReadString(settings, prefix + "Provider", "")))
                {
                    settings[prefix + "Provider"] = "AtlasCloud";
                    settings[prefix + "AtlasImageModel"] = PortraitsService.AtlasModelSeedreamV5ProEdit;
                    changed = true;
                }
                foreach (string retired in new[] { "ReignGeneratorNegativePrompt", "ReignGeneratorSteps", "ReignGeneratorCfg" })
                    changed |= settings.Remove(prefix + retired);
                string modelKey = prefix + "NanoGptImageModel";
                if (string.Equals(ReadString(settings, modelKey, ""), "step-image-edit-2", StringComparison.OrdinalIgnoreCase))
                {
                    settings[modelKey] = "gpt-image-1.5";
                    changed = true;
                }
                HashSet<string> supportedNanoModels = new HashSet<string>(PortraitsService.NanoImageModels, StringComparer.OrdinalIgnoreCase);
                if (!supportedNanoModels.Contains(ReadString(settings, modelKey, "")))
                {
                    settings[modelKey] = "gpt-image-1.5";
                    changed = true;
                }

                string openRouterModelKey = prefix + "OpenRouterImageModel";
                if (!PortraitsService.OpenRouterImageModels.Contains(ReadString(settings, openRouterModelKey, "")))
                { settings[openRouterModelKey] = PortraitsService.DefaultOpenRouterImageModel; changed = true; }

                string atlasModelKey = prefix + "AtlasImageModel";
                HashSet<string> supportedAtlasModels = new HashSet<string>(PortraitsService.AtlasImageModels, StringComparer.OrdinalIgnoreCase);
                if (!supportedAtlasModels.Contains(ReadString(settings, atlasModelKey, "")))
                {
                    settings[atlasModelKey] = adult ? PortraitsService.AtlasModelSeedreamV5ProEdit : PortraitsService.DefaultAtlasImageModel;
                    changed = true;
                }

                string providerKey = prefix + "Provider";
                string configuredProvider = ReadString(settings, providerKey, "");
                string normalizedProvider = PortraitsService.NormalizeImageProvider(configuredProvider);
                bool usesNanoGptChatImage = string.Equals(ReadString(settings, modelKey, ""),
                    "gemini-3-pro-image-preview", StringComparison.OrdinalIgnoreCase);
                if (normalizedProvider == "NanoGPT" && (usesNanoGptChatImage ? legacyDeskChat : legacyDeskGenerations))
                {
                    normalizedProvider = PortraitsService.LocalModelImageProviderId;
                    if (usesNanoGptChatImage) migratedDeskChat = true;
                    else migratedDeskGenerations = true;
                }
                if (!string.Equals(configuredProvider, normalizedProvider, StringComparison.Ordinal))
                {
                    settings[providerKey] = normalizedProvider;
                    changed = true;
                }

                Action<string, int, int, int> normalizeInt = (suffix, fallback, minimum, maximum) =>
                {
                    string key = prefix + suffix;
                    int prior = ReadInt(settings, key, fallback);
                    int value = Math.Max(minimum, Math.Min(maximum, prior));
                    if (!settings.ContainsKey(key) || prior != value || !(settings[key] is int)) changed = true;
                    settings[key] = value;
                };
                Action<string, double, double, double> normalizeDouble = (suffix, fallback, minimum, maximum) =>
                {
                    string key = prefix + suffix;
                    double prior = ReadDouble(settings, key, fallback);
                    double value = DialogueService.ClampDouble(prior, minimum, maximum);
                    if (!settings.ContainsKey(key) || Math.Abs(prior - value) > 0.0001d) changed = true;
                    settings[key] = value;
                };

                normalizeDouble("ImageStrength", 0.75d, 0.4d, 1d);
                normalizeInt("InferenceSteps", 28, 10, 50);
                normalizeDouble("GuidanceScale", 3.5d, 1d, 10d);
                changed |= PortraitsService.NormalizeLocalModelImageProfile(settings, prefix);
            }
            // Restore only legacy routes whose profiles moved to the keyless Desk provider.
            if (migratedDeskGenerations)
                settings["portraitNanoGptImageGenerationsUrl"] = PortraitsService.NanoGptImageGenerationsUrl;
            if (migratedDeskChat)
                settings["portraitNanoGptChatCompletionsUrl"] = PortraitsService.NanoGptChatCompletionsUrl;
            return changed;
        }

        internal static bool NormalizeMemorySettings(Dictionary<string, object> settings)
        {
            if (settings == null) return false;
            bool changed = false;
            bool legacy = ReadBool(settings, "legacyMemoryMode", false);
            if (!settings.TryGetValue("legacyMemoryMode", out var priorLegacy) || priorLegacy is not bool)
                changed = true;
            settings["legacyMemoryMode"] = legacy;
            Action<string, int, int, int> normalizeInt = (key, fallback, minimum, maximum) =>
            {
                int prior = ReadInt(settings, key, fallback);
                int value = Math.Max(minimum, Math.Min(maximum, prior));
                if (!settings.ContainsKey(key) || prior != value || !(settings[key] is int)) changed = true;
                settings[key] = value;
            };
            normalizeInt("maxContextMemories", 12, 1, 100);
            normalizeInt("recentRawConversationTurns", 30, 2, 100);
            normalizeInt("recentRawConversationCharBudget", 48000, 1200, 50000);
            normalizeInt("vectorSearchTopK", 96, 8, 256);
            normalizeInt("vectorQueryTimeoutMs", 10000, 8000, 30000);
            normalizeInt("embeddingBatchSize", 8, 1, 128);
            normalizeInt("memoryConsolidationMinItems", 4, 2, 20);
            normalizeInt("memoryConsolidationWindowMinutes", 30, 5, 240);
            normalizeInt("memoryConsolidationMaxGroups", 3, 1, 10);
            normalizeInt("memoryConsolidationMaxSourceItems", 24, 4, 80);
            normalizeInt("memoryConsolidationMaxSummaryChars", 1800, 500, 5000);
            double priorWeight = ReadDouble(settings, "vectorScoreWeight", 30d);
            double weight = DialogueService.ClampDouble(priorWeight, 0d, 100d);
            if (!settings.ContainsKey("vectorScoreWeight") || Math.Abs(priorWeight - weight) > 0.0001d) changed = true;
            settings["vectorScoreWeight"] = weight;
            return changed;
        }

        internal static bool NormalizeArchitectureSettings(Dictionary<string, object> settings)
        {
            if (settings == null) return false;
            bool changed = false;
            Action<string, int, int, int> normalize = (key, fallback, minimum, maximum) =>
            {
                int prior = ReadInt(settings, key, fallback);
                int value = Math.Max(minimum, Math.Min(maximum, prior));
                if (!settings.ContainsKey(key) || prior != value || !(settings[key] is int)) changed = true;
                settings[key] = value;
            };
            normalize("providerMaxConcurrency", 4, 1, 16);
            normalize("providerQueueTimeoutMs", 30000, 1000, 120000);
            normalize("providerRequestTimeoutMs", 120000, 15000, 600000);
            normalize("providerCircuitFailureThreshold", 4, 2, 20);
            normalize("providerCircuitBreakSeconds", 30, 5, 600);
            normalize("llmTransientMaxAttempts", 4, 1, 4);
            normalize("promptWarningCharacterLimit", 100000, 20000, 500000);
            normalize("promptHardCharacterLimit", 240000, 50000, 1000000);
            int warningLimit = ReadInt(settings, "promptWarningCharacterLimit", 100000);
            int hardLimit = ReadInt(settings, "promptHardCharacterLimit", 240000);
            if (hardLimit <= warningLimit)
            {
                settings["promptHardCharacterLimit"] = Math.Min(1000000, warningLimit + 50000);
                changed = true;
            }
            if (!settings.ContainsKey("openTelemetryOtlpEndpoint")) { settings["openTelemetryOtlpEndpoint"] = ""; changed = true; }
            if (!settings.ContainsKey("autoCaptureReplayCorpus")) { settings["autoCaptureReplayCorpus"] = true; changed = true; }
            return changed;
        }

        // settingsPathForTests: contract tests clear in a disposable settings file; never set from a request.
        internal static Dictionary<string, object> ClearSettingsKey(Dictionary<string, object> payload, Program.HttpRequest request = null,
            string settingsPathForTests = null)
        {
            string key = ReadString(payload, "key", "");
            if (!SensitiveSettingsKeys().Contains(key))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Unknown or non-sensitive key cannot be cleared." };
            }

            // A Reign-only key is cleared first, even on a bound slot, so the slot returns to its global key. Clearing a bound
            // slot without one unbinds it (the global key stays) and is a key mutation: same guard as the key routes.
            bool globalSlot = GlobalKeySlots.Contains(key);
            bool hadReignOnlyKey = false;
            if (globalSlot)
            {
                Dictionary<string, object> current;
                if (settingsPathForTests == null) current = LoadSettings();
                else current = ReadSettingsFileForTests(settingsPathForTests);
                hadReignOnlyKey = !string.IsNullOrWhiteSpace(ReadString(current, key, ""));
                if (GlobalKeyBinding(current, key) != null && !hadReignOnlyKey)
                    return request == null || !GlobalKeyRequestAllowed(request) ? GlobalKeyFail("forbidden") : UnbindGlobalKey(new Dictionary<string, object> { ["slot"] = key });
            }
            Dictionary<string, object> incoming = new Dictionary<string, object>
            {
                ["clearKeys"] = new List<string> { key }
            };
            Dictionary<string, object> result;
            if (settingsPathForTests == null) result = SaveSettings(incoming);
            else lock (SettingsSaveGate) result = SaveSettingsCore(incoming, out _, out _, settingsPathForTests);
            if (globalSlot && ReadBool(result, "ok", false))
            {
                // The explicit clear still runs (it also clears the vault copy); the outcome says whether a key was removed.
                if (hadReignOnlyKey) result["outcome"] = "local_cleared";
                else result["outcome"] = "unchanged";
            }
            OperationsService.LogOperational("settings.clear_key", new Dictionary<string, object> { ["key"] = key });
            return result;
        }

        // The stored settings as a save would see them (file contents over defaults, one-time shadow cleanup applied).
        private static Dictionary<string, object> ReadSettingsFileForTests(string path)
        {
            Dictionary<string, object> settings = DefaultSettings();
            if (File.Exists(path))
                foreach (var pair in Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8)) ?? new Dictionary<string, object>())
                    settings[pair.Key] = pair.Value;
            SanitizeBoundKeySlots(settings);
            return settings;
        }

        internal static HashSet<string> SensitiveSettingsKeys()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "apiKey", "nanoGptApiKey", "openRouterApiKey", "ollamaCloudApiKey", "portraitNanoGptApiKey", "portraitAtlasApiKey", "qdrantApiKey" };
        }

        internal static bool IsMaskedSecretValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            return trimmed.Length >= 8 && trimmed.All(ch => ch == '*' || ch == 'x' || ch == 'X' || ch == '•' || ch == '●' || ch == '.' || ch == '|');
        }

        internal static Dictionary<string, object> SettingsForUi(Dictionary<string, object> settings)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(settings ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in ReadWarEconomyOptions(settings ?? new Dictionary<string, object>()).Values()) result[pair.Key] = pair.Value;
            foreach (string key in SensitiveSettingsKeys())
            {
                string value = ReadString(settings, key, "");
                bool present = !string.IsNullOrWhiteSpace(value);
                int length = present ? value.Length : 0;
                result[key] = present ? new string('*', Math.Min(Math.Max(length, 12), 32)) : "";
                result[key + "Present"] = present;
                result[key + "Length"] = length;
                result[key + "Bound"] = GlobalKeyBinding(settings, key) != null;
            }

            result["settingsPath"] = Program.SettingsPath;
            result["dataDir"] = Program.DataDir;
            result["serverProcessFolder"] = AppDomain.CurrentDomain.BaseDirectory;
            result["reasoningPolicy"] = DialogueService.ReasoningPolicyStatus(settings);
            return result;
        }

        internal static string SettingsFingerprint(Dictionary<string, object> settings)
        {
            settings = settings ?? LoadSettings();
            string apiKey = ReadString(settings, LlmApiKeySlot(settings), "");
            return "llmProvider=" + LlmProviderService.NormalizeLlmProvider(ReadString(settings, "llmProvider", LlmProviderService.OpenAiCompatibleProvider))
                + "|apiUrl=" + LlmProviderService.LlmApiUrl(settings)
                + "|codexExecutable=" + ReadString(settings, "codexExecutable", "codex")
                + "|dialogueModel=" + ActionPipelineService.ModelForRequest(settings, "dialogue")
                + "|constructionModel=" + ActionPipelineService.ModelForRequest(settings, "character_construction")
                + "|constructionMaxTokens=" + ReadInt(settings, "characterConstructionMaxTokens", Program.CharacterConstructionTokenCeiling)
                + "|apiKeyPresent=" + ProviderKeyConfigured(settings, LlmApiKeySlot(settings), false)
                + "|apiKeyBinding=" + ReadString(GlobalKeyBinding(settings, LlmApiKeySlot(settings)), "keyId", "")
                + "|apiKeyLength=" + apiKey.Length;
        }

        internal static Dictionary<string, object> ServerDiagnostics()
        {
            Dictionary<string, object> settings = LoadSettings();
            string apiKey = ReadString(settings, LlmApiKeySlot(settings), "");
            string nanoKey = ReadString(settings, "portraitNanoGptApiKey", "");
            string atlasKey = ReadString(settings, "portraitAtlasApiKey", "");
            FileInfo settingsInfo = File.Exists(Program.SettingsPath) ? new FileInfo(Program.SettingsPath) : null;
            Dictionary<string, object> failed = FailedCharacterSummary(ReadString(settings, "latestCampaignId", ""));
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["characterSchemaVersion"] = Program.CharacterSchemaVersion,
                ["latestCampaignId"] = LatestCampaignId(),
                ["serverProcessFolder"] = AppDomain.CurrentDomain.BaseDirectory,
                ["dataDir"] = Program.DataDir,
                ["settingsPath"] = Program.SettingsPath,
                ["settingsExists"] = File.Exists(Program.SettingsPath),
                ["settingsLastWriteUtc"] = settingsInfo == null ? "" : settingsInfo.LastWriteTimeUtc.ToString("o"),
                ["settingsRevision"] = ReadString(settings, "settingsRevision", ""),
                ["settingsFingerprint"] = SettingsFingerprint(settings),
                ["llmProvider"] = LlmProviderService.NormalizeLlmProvider(ReadString(settings, "llmProvider", LlmProviderService.OpenAiCompatibleProvider)),
                ["codex"] = LlmProviderService.CodexStatusApi(),
                ["apiUrl"] = LlmProviderService.LlmApiUrl(settings),
                ["dialogueModel"] = ActionPipelineService.ModelForRequest(settings, "dialogue"),
                ["characterConstructionModel"] = ActionPipelineService.ModelForRequest(settings, "character_construction"),
                ["characterConstructionMaxTokens"] = ReadInt(settings, "characterConstructionMaxTokens", Program.CharacterConstructionTokenCeiling),
                ["apiKeyPresent"] = !string.IsNullOrWhiteSpace(apiKey),
                ["apiKeyLength"] = apiKey.Length,
                ["portraitProvider"] = ReadString(settings, "portraitProvider", "NanoGPT"),
                ["portraitNanoGptImageModel"] = ReadString(settings, "portraitNanoGptImageModel", "gpt-image-1.5"),
                ["portraitAtlasImageModel"] = ReadString(settings, "portraitAtlasImageModel", PortraitsService.DefaultAtlasImageModel),
                ["portraitNativeSourceMode"] = ReadString(settings, "portraitNativeSourceMode", "required"),
                ["portraitNativeSourceGeneratorPath"] = ReadString(settings, "portraitNativeSourceGeneratorPath", ""),
                ["sceneryProvider"] = ReadString(settings, "sceneryProvider", "NanoGPT"),
                ["sceneryNanoGptImageModel"] = ReadString(settings, "sceneryNanoGptImageModel", "gpt-image-1.5"),
                ["sceneryAtlasImageModel"] = ReadString(settings, "sceneryAtlasImageModel", PortraitsService.DefaultAtlasImageModel),
                ["portraitNanoGptKeyPresent"] = !string.IsNullOrWhiteSpace(nanoKey),
                ["portraitNanoGptKeyLength"] = nanoKey.Length,
                ["portraitAtlasKeyPresent"] = !string.IsNullOrWhiteSpace(atlasKey),
                ["portraitAtlasKeyLength"] = atlasKey.Length,
                ["database"] = ReignPostgreSqlStorage.Diagnostics(),
                ["failedCharacters"] = failed,
                ["unresolvedActionDeliveries"] = DialogueService.UnresolvedActionDeliverySummary(LatestCampaignId()),
                ["characterConstructionPolicy"] = CharactersService.CharacterConstructionPolicyStatus()
            };
        }

        internal static Dictionary<string, object> TestLlmKey(Dictionary<string, object> payload)
        {
            Stopwatch timer = Stopwatch.StartNew();
            Dictionary<string, object> settings = LoadSettings();
            string requestType = ReadString(payload, "requestType", "diagnostics");
            Dictionary<string, object> llm = LlmProviderService.ChatWithLlm(new Dictionary<string, object>
            {
                ["requestType"] = requestType,
                ["maxTokens"] = 24,
                ["temperature"] = 0d,
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = "Return strict JSON only." },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = "Return {\"ok\":true,\"message\":\"diagnostic-ok\"}." }
                },
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            });
            timer.Stop();
            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = ReadBool(llm, "ok", false),
                ["provider"] = ReadString(llm, "provider", LlmProviderService.NormalizeLlmProvider(ReadString(settings, "llmProvider", LlmProviderService.OpenAiCompatibleProvider))),
                ["apiUrl"] = LlmProviderService.LlmApiUrl(settings),
                ["model"] = ReadString(llm, "model", ActionPipelineService.ModelForRequest(settings, requestType)),
                ["apiKeyPresent"] = ProviderKeyConfigured(settings, LlmApiKeySlot(settings)),
                ["apiKeyLength"] = ReadString(settings, LlmApiKeySlot(settings), "").Length,
                ["providerDurationMs"] = ReadLong(llm, "durationMs", 0),
                ["totalMs"] = timer.ElapsedMilliseconds,
                ["content"] = ReadString(llm, "content", ""),
                ["error"] = ReadString(llm, "error", "")
            };
            OperationsService.LogOperational("diagnostics.test_llm", response);
            return response;
        }

        private static Dictionary<string, object> FailedCharacterSummary(string campaignId)
        {
            campaignId = string.IsNullOrWhiteSpace(campaignId) ? LatestCampaignId() : campaignId;
            string charactersDir = Path.Combine(CampaignFileService.CampaignDirectory(campaignId), "characters");
            int failedCount = 0;
            List<Dictionary<string, object>> recent = new List<Dictionary<string, object>>();
            if (Directory.Exists(charactersDir))
            {
                foreach (string dir in Directory.GetDirectories(charactersDir))
                {
                    Dictionary<string, object> constructed = CampaignFileService.ReadJsonObject(Path.Combine(dir, "constructed.json"));
                    if (!string.Equals(ReadString(constructed, "status", ""), "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    failedCount++;
                    recent.Add(new Dictionary<string, object>
                    {
                        ["heroStringId"] = Path.GetFileName(dir),
                        ["failedUtc"] = ReadString(constructed, "failedUtc", ""),
                        ["failureKind"] = FirstNonEmpty(ReadString(constructed, "failureKind", ""), CharactersService.CharacterFailureKindFromText(ReadString(constructed, "error", ""))),
                        ["settingsRevision"] = ReadString(constructed, "settingsRevision", ""),
                        ["diagnostic"] = DialogueService.LimitText(ReadString(constructed, "diagnostic", ReadString(constructed, "error", "")), 500),
                        ["settingsNewer"] = CharactersService.ShouldRetryFailedConstruction(constructed, LoadSettings())
                    });
                }
            }

            return new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["failedCount"] = failedCount,
                ["recentFailures"] = recent.OrderByDescending(x => ReadString(x, "failedUtc", "")).Take(20).ToList()
            };
        }

        internal static string LatestCampaignId()
        {
            try
            {
				// Registry updated_utc also changes during maintenance and Save Sync,
				// so it is not authoritative for the campaign currently being played.
				// World Test orders real campaigns by their latest native heartbeat.
				Dictionary<string, object> observed = WorldSimulationService.WorldTestCampaignsApi(
					new Dictionary<string, string>());
				string observedId = ReadString(observed, "latestCampaignId", "");
				if (!string.IsNullOrWhiteSpace(observedId)
					&& !observedId.Equals("default", StringComparison.OrdinalIgnoreCase))
					return observedId;
                Dictionary<string, object> latest =
                    ReignPostgreSqlStorage.ListCampaignMetadata().FirstOrDefault(row =>
                    {
                        string campaignId = ReadString(row, "campaignId", "");
                        string directoryPath = CampaignFileService.CampaignDirectory(campaignId);
                        return Directory.Exists(directoryPath)
                            && CampaignFileService.IsRealCampaignDirectory(
                                new DirectoryInfo(directoryPath));
                    });
                return latest == null
                    ? "default"
                    : ReadString(latest, "campaignId", "default");
            }
            catch { return "default"; }
        }
    }
}
