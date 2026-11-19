using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>
    /// One chat-completion provider behind <see cref="LlmProviderService"/>'s transport (the provider drivers, like
    /// chevo's drivers/llm). <see cref="LlmProviders.Select"/> picks the provider for a request; the transport has
    /// already built the request body, resolved the endpoint and key and applied cache and reasoning routing.
    /// </summary>
    internal interface ILlmProvider
    {
        /// <summary>A stable provider id for diagnostics, matching the settings provider ids where one exists.</summary>
        string Id { get; }

        /// <summary>Whether this provider serves a request with these settings sent to <paramref name="apiUrl"/>.</summary>
        bool Handles(Dictionary<string, object> settings, string apiUrl);

        /// <summary>Sends the chat request and returns the provider's raw response JSON.</summary>
        string PostChat(LlmProviderRequest request);
    }

    /// <summary>A chat request as the transport hands it to a provider.</summary>
    internal sealed class LlmProviderRequest
    {
        public string ApiUrl;
        public string ApiKey;
        public string OutboundJson;
        public Dictionary<string, object> RequestBody;
        public Dictionary<string, object> Settings;
        public Dictionary<string, object> CacheRouting;
        public string CorrelationId;
        public string RequestType;
        public string Model;
        public Dictionary<string, object> CodexContext;
        public Action<string> ContentDelta;
        public Func<bool> Canceled;
    }
}
