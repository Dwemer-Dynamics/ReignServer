using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The local Codex app-server subscription (see CodexAppServerProvider.cs and CodexRuntime*.cs).</summary>
    internal sealed class CodexSubscriptionChatProvider : ILlmProvider
    {
        public string Id => LlmProviderService.CodexSubscriptionProvider;

        public bool Handles(Dictionary<string, object> settings, string apiUrl) => LlmProviderService.UsesCodexSubscription(settings);

        public string PostChat(LlmProviderRequest request)
            => LlmProviderService.CodexChatCompletionWithRecovery(request.RequestBody, request.Settings, request.Model,
                request.CodexContext, request.ContentDelta, request.Canceled);
    }

    /// <summary>
    /// Shared HTTP chat-completions path: the campaign provider wait, then the provider middleware
    /// (ProviderMiddleware.cs: concurrency slots, response cache, streaming).
    /// </summary>
    internal abstract class HttpChatProvider : ILlmProvider
    {
        public abstract string Id { get; }

        public abstract bool Handles(Dictionary<string, object> settings, string apiUrl);

        public string PostChat(LlmProviderRequest request)
            => PersistenceService.WithCampaignProviderWait(request.RequestType,
                () => LlmProviderService.ProviderPostJson(request.ApiUrl, request.ApiKey, request.OutboundJson, request.Settings,
                    request.CacheRouting, request.CorrelationId, request.RequestType, request.Model, request.ContentDelta, request.Canceled));
    }

    /// <summary>OpenRouter (openrouter.ai endpoints).</summary>
    internal sealed class OpenRouterChatProvider : HttpChatProvider
    {
        public override string Id => LlmProviderService.OpenRouterProvider;

        public override bool Handles(Dictionary<string, object> settings, string apiUrl) => LlmProviderService.IsOpenRouterApiUrl(apiUrl);
    }

    /// <summary>Ollama Cloud (ollama.com endpoints).</summary>
    internal sealed class OllamaCloudChatProvider : HttpChatProvider
    {
        public override string Id => LlmProviderService.OllamaCloudProvider;

        public override bool Handles(Dictionary<string, object> settings, string apiUrl) => LlmProviderService.IsOllamaCloudApiUrl(apiUrl);
    }

    /// <summary>Any other OpenAI-compatible chat-completions endpoint, NanoGPT included.</summary>
    internal sealed class OpenAiCompatibleChatProvider : HttpChatProvider
    {
        public override string Id => LlmProviderService.OpenAiCompatibleProvider;

        public override bool Handles(Dictionary<string, object> settings, string apiUrl) => true;
    }
}
