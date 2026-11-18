using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP route table. Each module declares its routes in Program.<Module>Routes.cs: method, exact path or
        // prefix (with its comparison), handler and campaign-gate policy. HandleClient dispatches the first route
        // that matches, in registration order, exactly as the former if/else chain did. A route registered with
        // Legacy* has no handler yet: it is still served by the remaining branch of that chain in HandleClient,
        // which keeps the old relative order because no registered route overlaps another (see
        // RouteTableContractSelfTests).

        // Returned by a handler that has already written its own response (HTML, a file or a download) and logged
        // the request. HandleClient then returns without writing JSON, exactly like the former early returns.
        private static readonly object RouteResponseWritten = new object();

        private delegate object RouteHandler(HttpRequest request, RouteContext route);

        private sealed class RouteContext
        {
            public NetworkStream Stream;
            // Only the request that published the pending delete-all owner signals its response.
            public bool DeleteAllResponsePending;
        }

        private enum RouteMatch
        {
            Exact,
            Prefix
        }

        private enum CampaignGatePolicy
        {
            // The request holds a CampaignDataGate read lease for its whole lifetime.
            Read,
            // The path reads no campaign state, or owns the gate itself, and runs without a campaign lease.
            None
        }

        private sealed class ReignRoute
        {
            public string Method;
            public RouteMatch Match;
            public string Path;
            public StringComparison Comparison = StringComparison.Ordinal;
            // Null while the route is still served by HandleClient's legacy if/else chain.
            public RouteHandler Handler;
            public CampaignGatePolicy CampaignGate = CampaignGatePolicy.Read;

            public bool Matches(string method, string path)
            {
                if (method != Method || path == null) return false;
                return Match == RouteMatch.Exact ? path == Path : path.StartsWith(Path, Comparison);
            }

            // The campaign gate is decided by path before dispatch, for every method, so this exempts the path.
            public ReignRoute WithoutCampaignGate()
            {
                CampaignGate = CampaignGatePolicy.None;
                return this;
            }

            public string ContractKey => Method + " " + (Match == RouteMatch.Exact ? "exact" : "prefix") + " "
                + Comparison + " " + Path;
        }

        private sealed class CampaignGateExemptArea
        {
            public string Prefix;
            // A path equal to this, or below it, stays gated (compared case-insensitively, like the prefix).
            public string GatedSubtree;

            public bool Contains(string path)
            {
                if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
                if (GatedSubtree == null) return true;
                return !(path.Equals(GatedSubtree, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(GatedSubtree + "/", StringComparison.OrdinalIgnoreCase));
            }
        }

        private sealed class ReignRouteTable
        {
            private readonly List<ReignRoute> _routes = new List<ReignRoute>();
            private readonly List<CampaignGateExemptArea> _exemptAreas = new List<CampaignGateExemptArea>();
            private HashSet<string> _exemptPaths = new HashSet<string>(StringComparer.Ordinal);

            public IReadOnlyList<ReignRoute> Routes => _routes;
            public IReadOnlyList<CampaignGateExemptArea> ExemptAreas => _exemptAreas;

            public ReignRoute Get(string path, Func<HttpRequest, object> handler) => Add("GET", RouteMatch.Exact, path, StringComparison.Ordinal, Wrap(handler));
            public ReignRoute Post(string path, Func<HttpRequest, object> handler) => Add("POST", RouteMatch.Exact, path, StringComparison.Ordinal, Wrap(handler));
            public ReignRoute Get(string path, RouteHandler handler) => Add("GET", RouteMatch.Exact, path, StringComparison.Ordinal, handler);
            public ReignRoute Post(string path, RouteHandler handler) => Add("POST", RouteMatch.Exact, path, StringComparison.Ordinal, handler);

            public ReignRoute GetPrefix(string prefix, StringComparison comparison, Func<HttpRequest, object> handler) =>
                Add("GET", RouteMatch.Prefix, prefix, comparison, Wrap(handler));
            public ReignRoute PostPrefix(string prefix, StringComparison comparison, Func<HttpRequest, object> handler) =>
                Add("POST", RouteMatch.Prefix, prefix, comparison, Wrap(handler));

            // Declares a route still served by HandleClient's legacy if/else chain.
            public ReignRoute Legacy(string method, string path) =>
                Add(method, RouteMatch.Exact, path, StringComparison.Ordinal, null);
            public ReignRoute LegacyPrefix(string method, string prefix, StringComparison comparison) =>
                Add(method, RouteMatch.Prefix, prefix, comparison, null);

            // Every path below the prefix runs without a campaign lease, registered or not, except gatedSubtree.
            public void CampaignGateExemptArea(string prefix, string gatedSubtree = null)
            {
                _exemptAreas.Add(new CampaignGateExemptArea { Prefix = prefix, GatedSubtree = gatedSubtree });
            }

            // Called once after registration; the table is read-only afterwards.
            public ReignRouteTable Seal()
            {
                HashSet<string> exemptPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (ReignRoute route in _routes)
                {
                    if (route.Match == RouteMatch.Exact && route.CampaignGate == CampaignGatePolicy.None)
                        exemptPaths.Add(route.Path);
                }
                _exemptPaths = exemptPaths;
                return this;
            }

            public ReignRoute Resolve(string method, string path)
            {
                foreach (ReignRoute route in _routes)
                {
                    if (route.Matches(method, path)) return route;
                }
                return null;
            }

            public bool IsCampaignGateExempt(string path)
            {
                path = path ?? string.Empty;
                if (_exemptPaths.Contains(path)) return true;
                foreach (CampaignGateExemptArea area in _exemptAreas)
                {
                    if (area.Contains(path)) return true;
                }
                return false;
            }

            private ReignRoute Add(string method, RouteMatch match, string path, StringComparison comparison, RouteHandler handler)
            {
                ReignRoute route = new ReignRoute
                {
                    Method = method,
                    Match = match,
                    Path = path,
                    Comparison = comparison,
                    Handler = handler
                };
                _routes.Add(route);
                return route;
            }

            private static RouteHandler Wrap(Func<HttpRequest, object> handler) => (request, route) => handler(request);
        }

        private static readonly Lazy<ReignRouteTable> ServerRoutes =
            new Lazy<ReignRouteTable>(BuildServerRouteTable, LazyThreadSafetyMode.ExecutionAndPublication);

        private static ReignRouteTable BuildServerRouteTable()
        {
            ReignRouteTable routes = new ReignRouteTable();
            RegisterPlatformRoutes(routes);
            RegisterPersistenceRoutes(routes);
            RegisterCharactersRoutes(routes);
            RegisterDialogueRoutes(routes);
            RegisterRelationshipsRoutes(routes);
            RegisterReputationRoutes(routes);
            RegisterWorldSimulationRoutes(routes);
            RegisterDiplomacyRoutes(routes);
            RegisterGovernmentRoutes(routes);
            RegisterRebellionRoutes(routes);
            RegisterPortraitsRoutes(routes);
#if !REIGN_EXCLUDE_COURT
            RegisterCourtRoutes(routes);
            RegisterSpymasterRoutes(routes);
#endif
            return routes.Seal();
        }

        // False when no registered route matches or the match is still served by the legacy chain; HandleClient
        // then continues into that chain, which ends in the 404 response.
        private static bool TryDispatchRegisteredRoute(HttpRequest request, RouteContext route, out object response)
        {
            response = null;
            ReignRoute match = ServerRoutes.Value.Resolve(request.Method, request.Path);
            if (match == null || match.Handler == null) return false;
            response = match.Handler(request, route);
            return true;
        }

        private static bool IsCampaignGateExemptPath(string path) => ServerRoutes.Value.IsCampaignGateExempt(path);
    }
}
