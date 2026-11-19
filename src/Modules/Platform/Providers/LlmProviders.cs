using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    /// <summary>
    /// The registered chat providers in selection order. The Codex subscription provider is chosen whenever the
    /// settings select it; every other request goes to an HTTP provider by endpoint (OpenRouter, Ollama Cloud, or the
    /// OpenAI-compatible fallback that also serves NanoGPT and custom endpoints).
    /// </summary>
    internal static class LlmProviders
    {
        internal static readonly IReadOnlyList<ILlmProvider> All = new ILlmProvider[]
        {
            new CodexSubscriptionChatProvider(),
            new OpenRouterChatProvider(),
            new OllamaCloudChatProvider(),
            new OpenAiCompatibleChatProvider()
        };

        internal static ILlmProvider Select(Dictionary<string, object> settings, string apiUrl)
            => All.First(provider => provider.Handles(settings, apiUrl));
    }
}
