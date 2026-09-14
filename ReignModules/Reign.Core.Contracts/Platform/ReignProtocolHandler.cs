using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Reign.Core.Contracts.Platform
{
    // Handshake before sending application traffic. The server also checks the
    // protocol header on every request, including between periodic handshakes.
    public sealed class ReignProtocolHandler : DelegatingHandler
    {
        public const string HeaderName = "X-Reign-Protocol";
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private string verifiedAuthority = string.Empty;
        private DateTime verifiedUntil;

        public ReignProtocolHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidDataException("Reign request has no endpoint.");
            string authority = uri.GetLeftPart(UriPartial.Authority);
            if (uri.AbsolutePath != "/health")
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (authority != verifiedAuthority || DateTime.UtcNow >= verifiedUntil)
                    {
                        using (var probe = new HttpRequestMessage(HttpMethod.Get, authority + "/health"))
                        using (var response = await base.SendAsync(probe, cancellationToken).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            if (bytes.Length > 65536) throw new InvalidDataException("ReignServer health response is too large.");
                            using (var stream = new MemoryStream(bytes))
                            {
                                var health = (Health?)new DataContractJsonSerializer(typeof(Health)).ReadObject(stream);
                                if (health?.Service != "BannerlordReignServer" || health.Protocol != ReignInstallation.SupportedProtocol)
                                    throw new InvalidDataException("Reign and ReignServer are incompatible. Install the matching Reign package before continuing.");
                            }
                        }
                        verifiedAuthority = authority;
                        verifiedUntil = DateTime.UtcNow.AddSeconds(30);
                    }
                }
                finally { gate.Release(); }
            }
            request.Headers.Remove(HeaderName);
            request.Headers.Add(HeaderName, ReignInstallation.SupportedProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        [DataContract]
        private sealed class Health
        {
            [DataMember(Name = "service")] public string Service { get; set; } = string.Empty;
            [DataMember(Name = "protocolVersion")] public int Protocol { get; set; }
        }
    }
}
