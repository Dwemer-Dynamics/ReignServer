using System;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool TryHandleReputationRoute(HttpRequest request, out object response)
        {
            response = null;
            if (request.Method == "POST")
            {
                switch (request.Path)
                {
                    case "/rumors/submit": response = RegisterSocialOccurrenceApi(request.JsonBody); return true;
                    case "/rumors/status": response = SocialCharacterStatusApi(request.JsonBody); return true;
                    case "/rumors/correct": response = CorrectSocialOccurrenceApi(request.JsonBody); return true;
                    case "/social/catalog": response = SocialCatalogUpdateApi(request.JsonBody); return true;
                }
            }
            if (request.Method == "GET" && request.Path == "/social/catalog")
            {
                response = SocialCatalogApi(request.Query.ToDictionary(
                    pair => pair.Key, pair => (object)pair.Value,
                    StringComparer.OrdinalIgnoreCase));
                return true;
            }
            return false;
        }
    }
}
