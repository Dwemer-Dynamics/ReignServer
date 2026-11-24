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
    internal static partial class LlmProviderService
    {
        internal static Dictionary<string, object> ChatWithLlm(Dictionary<string, object> payload) => ChatWithLlm(payload, null, null);

        internal static Dictionary<string, object> ChatWithLlm(Dictionary<string, object> payload, Action<string> contentDelta, Func<bool> canceled, Dictionary<string, object> frozenSettings = null) =>
            ChatWithLlm(payload, contentDelta, canceled, frozenSettings, null);

        internal static Dictionary<string, object> ChatWithPartyAgencyServingAttempt(Dictionary<string, object> payload,
            out ServingProviderAttempt attempt)
        {
            ServingProviderAttempt observed = null;
            var result = ChatWithLlm(payload, null, null, null, winner => observed = winner);
            attempt = ReadBool(result, "ok", false) ? observed : null;
            return result;
        }

        private static Dictionary<string, object> ChatWithLlm(Dictionary<string, object> payload,
            Action<string> contentDelta, Func<bool> canceled, Dictionary<string, object> frozenSettings,
            Action<ServingProviderAttempt> captureServingAttempt)
        {
            PersistenceService.ThrowIfCampaignRequestReplaced();
            payload = payload ?? new Dictionary<string, object>();
            if (IsRetiredMemoryRequest(ReadString(payload, "requestType", "")))
                return TestDict("ok", false, "errorCode", "memory_llm_stage_retired", "providerCallCount", 0);
            Dictionary<string, object> settings = SettingsService.BanterProviderSettings(payload, frozenSettings ?? SettingsService.LoadSettings());
            bool singleAttempt = ReadBool(payload, "singleProviderAttempt", false);
            if (singleAttempt)
            {
                settings = CloneDictionary(settings);
                settings["llmTransientMaxAttempts"] = 1;
            }
            bool codexSubscription = UsesCodexSubscription(settings);
            string apiUrl = LlmRequestApiUrl(settings, payload);
            if (codexSubscription) apiUrl = "codex-app-server://local";
            else if (!string.IsNullOrWhiteSpace(apiUrl) && !IsDispatchableProviderUrl(apiUrl))
                return TestDict("ok", false, "error", ProviderUrlInvalidError, "errorCode", "provider_url_invalid", "providerCallCount", 0);
            else if (!string.IsNullOrWhiteSpace(apiUrl) && StoredKeyForeignDestination(settings, payload, apiUrl))
                return TestDict("ok", false, "error", "The saved API key is only sent to the endpoint configured in API Settings, and this request "
                    + "named a different one. Nothing was sent. Use the configured endpoint, or include an API key for this request.",
                    "errorCode", "stored_key_origin_mismatch", "providerCallCount", 0);
            string apiKey = ResolveLlmRequestApiKey(settings, payload, out bool usesGlobalBinding, apiUrl);
            if (usesGlobalBinding && string.IsNullOrEmpty(apiKey))
                return TestDict("ok", false, "error", SettingsService.GlobalKeyUnavailableError, "errorCode", "global_key_unavailable", "providerCallCount", 0);
            if (!codexSubscription && IsNamedLlmApiProvider(settings) && string.IsNullOrWhiteSpace(apiKey))
                return TestDict("ok", false, "error", "Configure the selected provider's API key in API Settings.",
                    "errorCode", "api_key_missing", "providerCallCount", 0);
            string requestType = ReadString(payload, "requestType", "dialogue");
            string model = ReadString(payload, "model", ActionPipelineService.ModelForRequest(settings, requestType));
            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            using var diagnosticCall = ConversationResponseService.BeginDiagnosticLlm(payload, apiKey);
            string heroId = ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId", "characterId");
            string eventId = ReadFirstString(payload, "eventId", "socialEventId");
            string actionId = ReadFirstString(payload, "actionId", "serverActionId");

            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                OperationsService.WriteAudit(campaignId, correlationId, "server", requestType, "llm.request", heroId, actionId, eventId, "failed", 0, "LLM request blocked because API URL is empty.", new Dictionary<string, object>
                {
                    ["requestType"] = requestType,
                    ["model"] = model,
                    ["error"] = "API URL is empty."
                });
                diagnosticCall.End("failed", "API URL is empty. No provider request was sent.");
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "API URL is empty." };
            }

            OperationsService.ReignTraceScope providerTrace = OperationsService.BeginReignSpan("llm.provider", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["requestType"] = requestType,
                ["model"] = model, ["heroId"] = heroId, ["eventId"] = eventId
            });

            List<Dictionary<string, object>> messages = ReadDictionaryList(payload, "messages");
            if (codexSubscription) messages = messages.Select(CharactersService.DeepCloneProfileDictionary).ToList();
            if (messages.Count == 0)
            {
                string system = ReadString(payload, "system", "You are Bannerlord Reign, a Bannerlord AI world assistant.");
                string prompt = ReadString(payload, "prompt", ReadString(payload, "text", ""));
                messages.Add(new Dictionary<string, object> { ["role"] = "system", ["content"] = system });
                messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = prompt });
            }
            Dictionary<string, object> codexContext = BuildCodexGenerationContext(settings, payload, requestType, model, correlationId);
            if (captureServingAttempt != null && codexSubscription && codexContext != null)
                codexContext["captureServingTurnDigest"] = true;
            if ((codexSubscription ? ReadString(codexContext, "requestedReasoningEffort", "none") : DialogueService.EffectiveReasoningEffort(settings, payload, requestType)) != "none")
            {
                DialogueService.ApplyPrivateReasoningPolicyToMessages(messages);
                Dictionary<string, object> promptDiagnostics = ReadDictionary(payload, "promptEnvelope");
                Dictionary<string, object> systemMessage = messages.FirstOrDefault(x => ReadString(x, "role", "").Equals("system", StringComparison.OrdinalIgnoreCase));
                if (promptDiagnostics != null && systemMessage != null)
                {
                    string systemContent = ReadString(systemMessage, "content", "");
                    promptDiagnostics["globalPrefixHash"] = DialogueService.PromptHash(systemContent);
                    promptDiagnostics["globalPrefixChars"] = systemContent.Length;
                    promptDiagnostics["reasoningPolicyIncluded"] = true;
                }
            }

            Dictionary<string, object> requestBody = BuildChatRequestBody(settings, payload, messages, model, out bool ollamaConversation);
            if (contentDelta != null && !codexSubscription) requestBody["stream"] = true;
            if (codexSubscription) DialogueService.EnrichCodexGenerationContext(settings, payload, requestType, model, codexContext, requestBody);

            Dictionary<string, object> cacheRouting = DialogueService.ConfigurePromptCacheRouting(settings, payload, requestBody, apiUrl, model, requestType);
            Dictionary<string, object> reasoningRouting = DialogueService.ConfigureReasoningRouting(settings, payload, requestBody, apiUrl, model, requestType);
            if (codexSubscription)
            {
                reasoningRouting["mode"] = ReadString(ReadDictionary(settings, "codexOptions"), "reasoningMode", "selective");
                reasoningRouting["effort"] = ReadString(codexContext, "requestedReasoningEffort", "none");
                reasoningRouting["requested"] = ReadString(codexContext, "requestedReasoningEffort", "none") != "none";
                reasoningRouting["adapter"] = "codex_subscription";
            }
            string outboundJson = Json.Serialize(requestBody);
            var continuityPreflight = DialogueService.NeedsContinuityProviderPreflight(settings, payload, requestType, codexSubscription)
                ? DialogueService.BuildContinuityProviderPreflight(settings, payload, requestBody, apiUrl, ReadString(requestBody, "model", model)) : null;
            if (continuityPreflight != null) payload["continuityProviderPreflight"] = continuityPreflight;
            if (codexContext != null && ReadBool(continuityPreflight, "capacityVerified", false))
                codexContext["contextInputLimitTokens"] = ReadInt(continuityPreflight, "inputAllowance", 0);
            int totalRequestChars = outboundJson.Length;
            var countedMessages = ollamaConversation ? ReadDictionaryList(requestBody, "messages") : messages;
            int messageChars = countedMessages.Sum(message => ReadString(message, "content", "").Length);
            int warningCharacterLimit = ReadInt(settings, "promptWarningCharacterLimit", 100000);
            int hardCharacterLimit = DialogueService.ConversationPromptHardCharacterLimit(settings, continuityPreflight);
            Dictionary<string, object> promptPreflight = new Dictionary<string, object>
            {
                ["messageCharacters"] = messageChars,
                ["requestCharacters"] = outboundJson.Length,
                ["estimatedPromptTokens"] = continuityPreflight == null ? (int)Math.Ceiling(messageChars / 4d) : ReadInt(continuityPreflight, "inputTokenEstimate", 0),
                ["continuity"] = continuityPreflight,
                ["warningCharacterLimit"] = warningCharacterLimit,
                ["hardCharacterLimit"] = hardCharacterLimit,
                ["configuredHardCharacterLimit"] = ReadInt(settings, "promptHardCharacterLimit", 240000),
                ["warningExceeded"] = outboundJson.Length > warningCharacterLimit,
                ["hardLimitExceeded"] = outboundJson.Length > hardCharacterLimit,
                ["estimatePolicy"] = "advisory_after_best_effort_preparation",
                ["dispatchAllowed"] = DialogueService.ConversationProviderPreflightAllowsDispatch(continuityPreflight)
            };
            payload["promptPreflight"] = promptPreflight;
            Dictionary<string, object> envelopeDiagnostics = ReadDictionary(payload, "promptEnvelope");
            if (envelopeDiagnostics != null) envelopeDiagnostics["providerPreflight"] = promptPreflight;
            providerTrace.Set("messageCharacters", messageChars);
            providerTrace.Set("requestCharacters", outboundJson.Length);
            providerTrace.Set("estimatedPromptTokens", (int)Math.Ceiling(messageChars / 4d));
            if (outboundJson.Length > warningCharacterLimit || (continuityPreflight != null && !ReadBool(continuityPreflight, "fits", false)))
            {
                OperationsService.LogOperational("prompt.preflight_warning", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["requestType"] = requestType,
                    ["heroId"] = heroId, ["eventId"] = eventId, ["preflight"] = promptPreflight,
                    ["sectionSizes"] = envelopeDiagnostics == null ? new Dictionary<string, object>() : ReadDictionary(envelopeDiagnostics, "sectionSizes")
                });
            }
            if (!DialogueService.ConversationProviderPreflightAllowsDispatch(continuityPreflight))
            {
                string error = "Required conversation evidence failed its exact-content or current-generation check before the provider call.";
                diagnosticCall.End("failed", error, promptPreflight);
                providerTrace.Set("blocked", true);
                providerTrace.Dispose();
                OperationsService.WriteAudit(campaignId, correlationId, "server", requestType, "llm.request", heroId, actionId, eventId, "failed", 0, error,
                    new Dictionary<string, object> { ["model"] = model, ["promptPreflight"] = promptPreflight, ["promptEnvelope"] = envelopeDiagnostics ?? new Dictionary<string, object>() });
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["error"] = error, ["errorCode"] = "prompt_required_source_missing",
                    ["requestType"] = requestType, ["model"] = model, ["promptPreflight"] = promptPreflight
                };
            }
            Stopwatch llmTimer = new Stopwatch();
            try
            {
                OperationsService.WriteAudit(campaignId, correlationId, "server", requestType, "llm.request", heroId, actionId, eventId, "started", 0, "LLM request started.", new Dictionary<string, object>
                {
                    ["requestType"] = requestType,
                    ["model"] = model,
                    ["apiUrl"] = apiUrl,
                    ["requestChars"] = outboundJson.Length,
                    ["promptCache"] = cacheRouting,
                    ["reasoningPolicy"] = reasoningRouting,
                    ["request"] = requestBody
                });
                llmTimer.Start();
                var selectedAdapter = LlmProviders.Select(settings, apiUrl);
                string responseText;
                try
                {
                    DialogueService.CapturePromptEvidence(campaignId, correlationId, requestType, model, requestBody, envelopeDiagnostics, apiKey);
                    responseText = PostLlmProviderJson(selectedAdapter, apiUrl, apiKey, outboundJson, requestBody, settings, cacheRouting, correlationId, requestType, model, codexContext, contentDelta, canceled);
                }
                catch (Exception reasoningException) when (!singleAttempt && DialogueService.ShouldRetryWithoutReasoning(reasoningRouting, reasoningException.Message))
                {
                    DialogueService.StartReasoningFallbackCooldown(settings, apiUrl, model);
                    reasoningRouting["fallbackUsed"] = true;
                    reasoningRouting["attemptCount"] = 2;
                    reasoningRouting["fallbackError"] = DialogueService.LimitText(reasoningException.Message, 1200);
                    reasoningRouting["cooldownUntilUtc"] = DialogueService.ReasoningFallbackCooldownUntil(DialogueService.ReasoningRouteKey(apiUrl, model)).ToString("o");
                    requestBody.Remove("reasoning");
                    requestBody.Remove("reasoning_effort");
                    outboundJson = Json.Serialize(requestBody);
                    totalRequestChars += outboundJson.Length;
                    DialogueService.CapturePromptEvidence(campaignId, correlationId, requestType, model, requestBody, envelopeDiagnostics, apiKey);
                    responseText = PostLlmProviderJson(selectedAdapter, apiUrl, apiKey, outboundJson, requestBody, settings, cacheRouting, correlationId, requestType, model, codexContext);
                }
                catch (Exception cacheRouteException) when (!singleAttempt && DialogueService.ShouldRetryWithoutCache(cacheRouting, cacheRouteException.Message))
                {
                    DialogueService.StartPromptCacheCooldown(settings, apiUrl, model);
                    cacheRouting["fallbackUsed"] = true;
                    cacheRouting["attemptCount"] = 2;
                    cacheRouting["cacheRouteError"] = DialogueService.LimitText(cacheRouteException.Message, 1200);
                    cacheRouting["cooldownUntilUtc"] = DialogueService.PromptCacheCooldownUntil(DialogueService.PromptCacheRouteKey(apiUrl, model)).ToString("o");
                    requestBody.Remove("caching");
                    requestBody.Remove("stickyprovider");
                    outboundJson = Json.Serialize(requestBody);
                    totalRequestChars += outboundJson.Length;
                    try
                    {
                        DialogueService.CapturePromptEvidence(campaignId, correlationId, requestType, model, requestBody, envelopeDiagnostics, apiKey);
                        responseText = PostLlmProviderJson(selectedAdapter, apiUrl, apiKey, outboundJson, requestBody, settings, cacheRouting, correlationId, requestType, model, codexContext);
                    }
                    catch (Exception reasoningException) when (!singleAttempt && DialogueService.ShouldRetryWithoutReasoning(reasoningRouting, reasoningException.Message))
                    {
                        DialogueService.StartReasoningFallbackCooldown(settings, apiUrl, model);
                        reasoningRouting["fallbackUsed"] = true;
                        reasoningRouting["attemptCount"] = 2;
                        reasoningRouting["fallbackError"] = DialogueService.LimitText(reasoningException.Message, 1200);
                        reasoningRouting["cooldownUntilUtc"] = DialogueService.ReasoningFallbackCooldownUntil(DialogueService.ReasoningRouteKey(apiUrl, model)).ToString("o");
                        requestBody.Remove("reasoning");
                        requestBody.Remove("reasoning_effort");
                        outboundJson = Json.Serialize(requestBody);
                        totalRequestChars += outboundJson.Length;
                            DialogueService.CapturePromptEvidence(campaignId, correlationId, requestType, model, requestBody, envelopeDiagnostics, apiKey);
                            responseText = PostLlmProviderJson(selectedAdapter, apiUrl, apiKey, outboundJson, requestBody, settings, cacheRouting, correlationId, requestType, model, codexContext);
                    }
                }
                llmTimer.Stop();
                Dictionary<string, object> raw = Json.Deserialize<Dictionary<string, object>>(responseText);
                if (continuityPreflight != null)
                {
                    int actual = ReadInt(ReadDictionary(raw, "usage"), "prompt_tokens", ReadInt(ReadDictionary(raw, "usage"), "input_tokens", 0));
                    continuityPreflight["actualInputTokens"] = actual > 0 ? (object)actual : null;
                    continuityPreflight["estimateErrorTokens"] = actual > 0 ? (object)(ReadInt(continuityPreflight, "inputTokenEstimate", 0) - actual) : null;
                    OperationsService.LogOperational("conversation.continuity_preflight", continuityPreflight);
                }
                string content = DialogueService.SanitizeReasoningContent(ExtractAssistantContent(raw));
                diagnosticCall.Trace?.Record("cleanup", "Extract and clean provider content", "completed",
                    content == ExtractAssistantContent(raw) ? "Assistant content was preserved." : "Reign changed the extracted content. Compare the original and cleaned values below.", 0,
                    new Dictionary<string, object> { ["extracted"] = ExtractAssistantContent(raw), ["cleaned"] = content }, diagnosticCall.Id);
                if (codexSubscription && ReadBool(ReadDictionary(ReadDictionary(raw, "codexDiagnostics"), "request"), "schemaApplied", false))
                {
                    var structuredContent = TryParseJsonObject(content);
                    if (structuredContent != null)
                    {
                        List<string> removedPaths;
                        var normalized = CodexConversationContracts.NormalizeStructuredOptionalNulls(
                            ReadString(codexContext, "schemaId", ""), structuredContent, out removedPaths);
                        if (removedPaths.Count > 0) content = Json.Serialize(normalized);
                        var diagnostics = ReadDictionary(raw, "codexDiagnostics");
                        if (diagnostics != null) diagnostics["schemaOmittedNullPaths"] = removedPaths;
                    }
                }
                Dictionary<string, object> safeRaw = DialogueService.SanitizeLlmResponseForStorage(raw);
                Dictionary<string, object> cacheDiagnostics = DialogueService.BuildPromptCacheDiagnostics(raw, cacheRouting, llmTimer.ElapsedMilliseconds, requestType, model, "");
                cacheDiagnostics["reasoningPolicy"] = reasoningRouting;
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["provider"] = codexSubscription ? CodexSubscriptionProvider : OpenAiCompatibleProvider,
                    ["requestType"] = requestType,
                    ["model"] = model,
                    ["durationMs"] = llmTimer.ElapsedMilliseconds,
                    ["requestChars"] = totalRequestChars,
                    ["responseChars"] = responseText.Length,
                    ["content"] = content,
                    ["providerAssistantContentSha256"] = DialogueService.ConversationExactTextHash(ExtractAssistantContent(raw)),
                    ["finishReason"] = ExtractFinishReason(raw),
                    ["cache"] = cacheDiagnostics,
                    ["reasoning"] = reasoningRouting,
                    ["raw"] = safeRaw
                };
                result = CheckConversationProviderResult(result, requestType, ollamaConversation);
                if (captureServingAttempt != null && ReadBool(result, "ok", false))
                    captureServingAttempt(ObserveServingProviderAttempt(selectedAdapter.Id, campaignId, correlationId,
                        heroId, requestType, requestBody, outboundJson, raw, responseText, ExtractAssistantContent(raw),
                        cacheRouting, reasoningRouting, contentDelta != null,
                        codexSubscription && CodexRpcTransportOverride != null, apiKey));
                providerTrace.Set("ok", true);
                if (codexSubscription)
                {
                    result["codexDiagnostics"] = ReadDictionary(raw, "codexDiagnostics") ?? new Dictionary<string, object>();
                    var appliedRequest = ReadDictionary(ReadDictionary(raw, "codexDiagnostics"), "request");
                    reasoningRouting["resolvedEffort"] = ReadString(appliedRequest, "resolvedReasoningEffort", "unknown");
                    reasoningRouting["resolution"] = ReadString(appliedRequest, "reasoningResolution", "unknown");
                    reasoningRouting["providerControlsApplied"] = appliedRequest != null;
                    ActiveCodexConversationScope.Value?.Results.Add(Tuple.Create(CharactersService.DeepCloneProfileDictionary(result), codexContext));
                }
                providerTrace.Set("providerDurationMs", llmTimer.ElapsedMilliseconds);
                providerTrace.Set("requestChars", totalRequestChars);
                providerTrace.Set("responseChars", responseText.Length);

                LogLlm(requestType, model, requestBody, safeRaw, cacheDiagnostics, true, null, llmTimer.ElapsedMilliseconds, totalRequestChars, responseText.Length);
                OperationsService.WriteAudit(campaignId, correlationId, "server", requestType, "llm.response", heroId, actionId, eventId, "completed", llmTimer.ElapsedMilliseconds, "LLM response received.", new Dictionary<string, object>
                {
                    ["requestType"] = requestType,
                    ["model"] = model,
                    ["durationMs"] = llmTimer.ElapsedMilliseconds,
                    ["requestChars"] = totalRequestChars,
                    ["responseChars"] = responseText.Length,
                    ["finishReason"] = ExtractFinishReason(raw),
                    ["content"] = content,
                    ["promptCache"] = cacheDiagnostics,
                    ["reasoningPolicy"] = reasoningRouting,
                    ["raw"] = safeRaw
                });
                providerTrace.Dispose();
                diagnosticCall.End("completed", "Provider output was received and processed; later validation determines whether it is usable.", result);
                return result;
            }
            catch (Exception ex)
            {
                if (llmTimer.IsRunning)
                {
                    llmTimer.Stop();
                }

                if (ex is PersistenceService.CampaignRequestReplacedException)
                {
                    providerTrace.Fail(ex);
                    providerTrace.Dispose();
                    throw;
                }
                Dictionary<string, object> cacheDiagnostics = DialogueService.BuildPromptCacheDiagnostics(new Dictionary<string, object>(), cacheRouting, llmTimer.ElapsedMilliseconds, requestType, model, ex.Message);
                cacheDiagnostics["reasoningPolicy"] = reasoningRouting;
                LogLlm(requestType, model, requestBody, new Dictionary<string, object> { ["error"] = ex.Message }, cacheDiagnostics, false, ex.Message, llmTimer.ElapsedMilliseconds, totalRequestChars, 0);
                OperationsService.WriteAudit(campaignId, correlationId, "server", requestType, "llm.response", heroId, actionId, eventId, "failed", llmTimer.ElapsedMilliseconds, "LLM request failed.", new Dictionary<string, object>
                {
                    ["requestType"] = requestType,
                    ["model"] = model,
                    ["durationMs"] = llmTimer.ElapsedMilliseconds,
                    ["requestChars"] = totalRequestChars,
                    ["promptCache"] = cacheDiagnostics,
                    ["reasoningPolicy"] = reasoningRouting,
                    ["error"] = ex.Message
                });
                providerTrace.Fail(ex);
                providerTrace.Set("ok", false);
                providerTrace.Dispose();
                var failedResult = new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["provider"] = codexSubscription ? CodexSubscriptionProvider : OpenAiCompatibleProvider,
                    ["requestType"] = requestType,
                    ["model"] = model,
                    ["durationMs"] = llmTimer.ElapsedMilliseconds,
                    ["requestChars"] = totalRequestChars,
                    ["cache"] = cacheDiagnostics,
                    ["reasoning"] = reasoningRouting,
                    ["error"] = ex.Message
                };
                if (codexSubscription)
                {
                    failedResult["codexDiagnostics"] = ex.Data["codexDiagnostics"] as Dictionary<string, object> ?? new Dictionary<string, object>();
                    failedResult["codexFailure"] = ex.Data["codexFailure"] as Dictionary<string, object> ?? TestDict();
                    ActiveCodexConversationScope.Value?.Results.Add(Tuple.Create(CharactersService.DeepCloneProfileDictionary(failedResult), codexContext));
                }
                diagnosticCall.End("failed", ex.Message, failedResult);
                return failedResult;
            }
        }

        private static string PostJsonToLlmWithTransientRetry(
            string apiUrl,
            string apiKey,
            string requestJson,
            Dictionary<string, object> settings,
            Dictionary<string, object> diagnostics,
            string correlationId,
            string requestType,
            string model)
        {
            int maxAttempts = Math.Max(1, Math.Min(4, ReadInt(settings, "llmTransientMaxAttempts", 4)));
            if (FinalConversationGauntletProviderBudget
                .IsRegisteredCorrelation(correlationId))
                maxAttempts = Math.Min(maxAttempts, 3);
            List<string> retryErrors = new List<string>();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Dictionary<string, object> reservation =
                    FinalConversationGauntletProviderBudget
                        .TryReserveRegisteredPhysicalDispatch(
                            correlationId, requestType, model, attempt);
                if (ReadBool(reservation, "scoped", false)
                    && !ReadBool(reservation, "allowed", false))
                    throw new FinalGauntletProviderBudgetExceededException(
                        "Final conversation gauntlet exhausted its hard "
                        + ReadInt(reservation, "maximum", 500)
                            .ToString(CultureInfo.InvariantCulture)
                        + "-call external-provider budget.");
                try
                {
                    string response = PostJsonToLlm(apiUrl, apiKey, requestJson, settings);
                    FinalConversationGauntletProviderBudget
                        .CompleteRegisteredPhysicalDispatch(
                            reservation, true, "");
                    if (diagnostics != null)
                    {
                        diagnostics["transientRetryCount"] = retryErrors.Count;
                        diagnostics["transientRetryErrors"] = retryErrors;
                        diagnostics["attemptCount"] = Math.Max(ReadInt(diagnostics, "attemptCount", 1), attempt);
                    }
                    return response;
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransientLlmFailure(ex.Message))
                {
                    FinalConversationGauntletProviderBudget
                        .CompleteRegisteredPhysicalDispatch(
                            reservation, false, ex.Message);
                    retryErrors.Add(DialogueService.LimitText(ex.Message, 600));
                    if (diagnostics != null)
                    {
                        diagnostics["transientRetryCount"] = retryErrors.Count;
                        diagnostics["transientRetryErrors"] = retryErrors;
                        diagnostics["attemptCount"] = Math.Max(ReadInt(diagnostics, "attemptCount", 1), attempt + 1);
                    }
                    Thread.Sleep(Math.Min(8000, 2000 * attempt));
                }
                catch (Exception ex)
                {
                    FinalConversationGauntletProviderBudget
                        .CompleteRegisteredPhysicalDispatch(
                            reservation, false, ex.Message);
                    throw;
                }
            }
            throw new InvalidOperationException("Transient LLM retry loop exhausted unexpectedly.");
        }

        internal static bool IsTransientLlmFailure(string message)
        {
            string value = (message ?? "").ToLowerInvariant();
            return value.Contains("billing reservation failed")
                || value.Contains("async_billing_reservation_failed")
                || value.Contains("status 408") || value.Contains("(408)")
                || value.Contains("status 429") || value.Contains("(429)")
                || value.Contains("status 500") || value.Contains("(500)")
                || value.Contains("status 502") || value.Contains("(502)")
                || value.Contains("status 503") || value.Contains("(503)")
                || value.Contains("status 504") || value.Contains("(504)")
                || (value.Contains("\"type\":\"invalid_request_error\"")
                    && value.Contains("\"code\":\"provider_error\""))
                || value.Contains("temporarily unavailable")
                || value.Contains("timed out") || value.Contains("timeout")
                || value.Contains("connection reset") || value.Contains("connection closed")
                || value.Contains("connection was closed") || value.Contains("underlying connection");
        }

        private const string ProviderUrlInvalidError = "The provider URL must be an http or https URL without embedded credentials.";

        // One destination and redirect policy for both provider senders (PostJsonToLlm and the streaming
        // PostStreamingProviderJson). Only an absolute http(s) URL without userinfo is handed to the transport; the check
        // runs before the test guard, diagnostics or any socket. A request that carries a key (saved, global or an explicit
        // one-off payload key) never follows a redirect: auto-redirect is off, so a 3xx comes back as the response and
        // ProviderRedirectError turns it into a failure before its body is read, and the key reaches only the configured
        // URL. A deliberately keyless request (e.g. a local OpenAI-compatible server) keeps the framework's ordinary
        // http(s) redirect following, which carries no credential.
        private static HttpWebRequest CreateProviderRequest(string url, string key, Dictionary<string, object> settings, string accept)
        {
            if (!IsDispatchableProviderUrl(url)) throw new InvalidOperationException(ProviderUrlInvalidError);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.AllowAutoRedirect = string.IsNullOrWhiteSpace(key);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = accept;
            request.Timeout = request.ReadWriteTimeout = Math.Clamp(ReadInt(settings, "providerRequestTimeoutMs", 120000), 15000, 600000);
            if (!string.IsNullOrWhiteSpace(key)) request.Headers["Authorization"] = "Bearer " + key;
            string site = ReadString(settings, "siteUrl", ""), title = ReadString(settings, "appTitle", "ReignBeta");
            if (!string.IsNullOrWhiteSpace(site)) { request.Headers["HTTP-Referer"] = site; request.Headers["Referer"] = site; }
            if (!string.IsNullOrWhiteSpace(title))
            {
                request.Headers["X-Title"] = title;
                if (IsOpenRouterApiUrl(url)) request.Headers["X-OpenRouter-Title"] = title;
            }
            return request;
        }

        // Null unless the response is a 3xx. A redirect is never a provider reply on either sender, whether it arrives as
        // the response (auto-redirect off) or inside a WebException.
        private static string ProviderRedirectError(WebResponse response)
        {
            int status = response is HttpWebResponse http ? (int)http.StatusCode : 0;
            if (status < 300 || status >= 400) return null;
            return "The provider redirected the request (HTTP " + status.ToString(CultureInfo.InvariantCulture)
                + ") and no credential was forwarded. Configure the provider's final URL.";
        }

        internal static string PostJsonToLlm(string apiUrl, string apiKey, string requestJson, Dictionary<string, object> settings)
        {
            HttpWebRequest request = CreateProviderRequest(apiUrl, apiKey, settings, "application/json");
            Program.ProviderTestGuard.BeforeDispatch("http_text_or_image");
            using var diagnosticAttempt = ConversationResponseService.BeginDiagnosticAttempt(apiUrl, requestJson, apiKey);
            byte[] bytes = Encoding.UTF8.GetBytes(requestJson);
            request.ContentLength = bytes.Length;
            try
            {
                using (Stream requestStream = request.GetRequestStream())
                {
                    requestStream.Write(bytes, 0, bytes.Length);
                }
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8))
                {
                    string redirectError = ProviderRedirectError(response);
                    if (redirectError != null)
                    {
                        diagnosticAttempt.End("failed", redirectError, new Dictionary<string, object> { ["httpStatus"] = (int)response.StatusCode, ["error"] = redirectError });
                        throw new InvalidOperationException(redirectError);
                    }
                    string responseBody = reader.ReadToEnd();
                    diagnosticAttempt.End("received", "Original HTTP response captured before JSON parsing or reasoning cleanup.",
                        new Dictionary<string, object> { ["httpStatus"] = (int)response.StatusCode, ["responseBody"] = responseBody,
                            ["requestId"] = response.Headers["x-request-id"], ["contentType"] = response.ContentType });
                    return responseBody;
                }
            }
            catch (WebException ex) when (ProviderRedirectError(ex.Response) is string redirectError)
            {
                diagnosticAttempt.End("failed", redirectError, new Dictionary<string, object> { ["httpStatus"] = (int)((HttpWebResponse)ex.Response).StatusCode,
                    ["error"] = redirectError, ["transportStatus"] = ex.Status.ToString() });
                ex.Response.Dispose();
                throw new InvalidOperationException(redirectError, ex);
            }
            catch (WebException ex)
            {
                string body = "";
                if (ex.Response != null)
                {
                    using (Stream responseStream = ex.Response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }
                }

                diagnosticAttempt.End("failed", ex.Message, new Dictionary<string, object> { ["responseBody"] = body,
                    ["httpStatus"] = ex.Response is HttpWebResponse failedHttp ? (object)(int)failedHttp.StatusCode : null,
                    ["error"] = ex.Message, ["transportStatus"] = ex.Status.ToString() });
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? ex.Message : body, ex);
            }
        }

        // Windows and Linux JSON adapters expose different IList implementations.
        internal static string ExtractAssistantContent(Dictionary<string, object> raw)
        {
            if (raw == null || !raw.TryGetValue("choices", out object choicesValue) || !(choicesValue is IList choices) || choices.Count == 0)
            {
                return "";
            }

            Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
            if (first == null)
            {
                return "";
            }

            Dictionary<string, object> message = first.ContainsKey("message") ? first["message"] as Dictionary<string, object> : null;
            string content = ReadString(message, "content", "");
            return string.IsNullOrWhiteSpace(content) ? ReadString(first, "text", "") : content;
        }

        internal static string ExtractFinishReason(Dictionary<string, object> raw)
        {
            if (raw == null || !raw.TryGetValue("choices", out object choicesValue) || !(choicesValue is IList choices) || choices.Count == 0)
            {
                return "";
            }

            Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
            return ReadString(first, "finish_reason", "");
        }

        private static void LogLlm(string requestType, string model, Dictionary<string, object> requestBody, Dictionary<string, object> responseBody, Dictionary<string, object> cacheDiagnostics, bool ok, string error, long durationMs, int requestChars, int responseChars)
        {
            Dictionary<string, object> settings = SettingsService.LoadSettings();
            if (!ReadBool(settings, "enableLlmLogging", true))
            {
                return;
            }

            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
                ["ok"] = ok,
                ["requestType"] = requestType,
                ["model"] = model,
                ["durationMs"] = durationMs,
                ["requestChars"] = requestChars,
                ["responseChars"] = responseChars,
                ["promptCache"] = cacheDiagnostics ?? new Dictionary<string, object>(),
                ["request"] = requestBody,
                ["response"] = responseBody
            };
            if (!string.IsNullOrWhiteSpace(error))
            {
                row["error"] = error;
            }

            lock (Program.FileLock)
            {
                CampaignFileService.AppendBoundedJsonLineToPathLocked(Path.Combine(Program.LogsDir, "llm-log.jsonl"), row,
                    16L * 1024L * 1024L, 8L * 1024L * 1024L);
            }
        }

        internal static Dictionary<string, object> ToCaseInsensitiveDictionary(Dictionary<string, object> source)
        {
            return new Dictionary<string, object>(source ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
        }

        internal static void NormalizeChatAiTerms(Dictionary<string, object> raw, Dictionary<string, object> terms)
        {
            if (raw == null || terms == null)
            {
                return;
            }

            CopyTermAlias(raw, terms, "TargetSettlement", "targetSettlement");
            CopyTermAlias(raw, terms, "TargetSettlement", "settlementName");
            CopyTermAlias(raw, terms, "TargetHero", "targetHero");
            CopyTermAlias(raw, terms, "TargetClan", "targetClan");
            CopyTermAlias(raw, terms, "TargetKingdom", "targetKingdom");
            CopyTermAlias(raw, terms, "TargetParty", "targetParty");
            CopyTermAlias(raw, terms, "TargetArmy", "targetArmy");
            CopyTermAlias(raw, terms, "KillerHero", "killerHero");
            CopyTermAlias(raw, terms, "FromHero", "fromHero");
            CopyTermAlias(raw, terms, "ToHero", "toHero");
            CopyTermAlias(raw, terms, "FromClan", "fromClan");
            CopyTermAlias(raw, terms, "ToClan", "toClan");
            CopyTermAlias(raw, terms, "FromKingdom", "fromKingdom");
            CopyTermAlias(raw, terms, "ToKingdom", "toKingdom");
            CopyTermAlias(raw, terms, "Policy", "policy");
            CopyTermAlias(raw, terms, "SocialAction", "socialAction");
            CopyTermAlias(raw, terms, "Claim", "claim");
            CopyTermAlias(raw, terms, "Promise", "promiseText");
            CopyTermAlias(raw, terms, "proposal", "packageText");
            CopyTermAlias(raw, terms, "treatyType", "treatyKind");
            CopyTermAlias(raw, terms, "treaty", "treatyKind");

            string gold = ReadFirstString(raw, "GoldAmount", "goldAmount", "gold");
            if (!string.IsNullOrWhiteSpace(gold))
            {
                ActionPipelineService.AddIfMissing(terms, "gold", gold);
                ActionPipelineService.AddIfMissing(terms, "reparationsGold", gold);
            }

            string item = ReadFirstString(raw, "Item", "item");
            if (!string.IsNullOrWhiteSpace(item))
            {
                ActionPipelineService.AddIfMissing(terms, "item", item);
            }

            string workshop = ReadFirstString(raw, "Workshop", "workshop");
            if (!string.IsNullOrWhiteSpace(workshop))
            {
                ActionPipelineService.AddIfMissing(terms, "workshop", workshop);
            }

            string amount = ReadFirstString(raw, "Amount", "amount");
            if (!string.IsNullOrWhiteSpace(amount))
            {
                ActionPipelineService.AddIfMissing(terms, "amount", amount);
            }

            string prisoner = ReadFirstString(raw, "PrisonerHero", "prisonerHero");
            if (!string.IsNullOrWhiteSpace(prisoner))
            {
                ActionPipelineService.AddIfMissing(terms, "prisonerHero", prisoner);
            }
        }

        private static void CopyTermAlias(Dictionary<string, object> raw, Dictionary<string, object> terms, string rawKey, string termKey)
        {
            if (raw == null || terms == null || string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(termKey))
            {
                return;
            }

            string value = ReadString(raw, rawKey, "");
            if (!string.IsNullOrWhiteSpace(value))
            {
                ActionPipelineService.AddIfMissing(terms, termKey, value);
            }
        }
    }
}
