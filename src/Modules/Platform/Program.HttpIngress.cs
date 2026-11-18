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
    internal static partial class Program
    {
        // HTTP/1.x ingress. The listener binds loopback only; Apache (8089) and direct local tools (5101) both arrive here.
        // Framing, size and access policy are decided from the headers, before any body allocation or upload, campaign
        // lock or route side effect. Ingress deadlines cover only reading the request; provider work afterwards is not
        // bounded by them.
        private const int IngressMaxRequestLineBytes = 8 * 1024;
        private const int IngressMaxHeaderBytes = 64 * 1024;
        private const int IngressMaxHeaderFields = 128;
        private const int IngressMaxIdentityHeaderChars = 256;
        private const long IngressMaxJsonBodyBytes = 64L * 1024L * 1024L;
        private const long IngressMaxSupportBundleBytes = 8L * 1024L * 1024L;
        // The view-insertion acknowledgment carries exactly 11 bounded strings.
        private const long IngressMaxViewInsertionAckBytes = 16L * 1024L;
        private const long IngressMaxCampaignUploadBytes = PersistenceService.CampaignBackupMaxExpandedBytes;
        private const int IngressIdleTimeoutMs = 30000;
        private const int IngressHeaderBudgetMs = 30000;
        private const long IngressBodyBudgetBaseMs = 60000;
        private const long IngressBodyMinimumBytesPerSecond = 1024L * 1024L;
        private const int IngressMaxConcurrentConnections = 256;
        // Shared across all connections: buffered JSON/support bodies reserve their declared length here before the buffer
        // is allocated and hold it until the route exits, so concurrent maximum-size bodies cannot multiply the per-request
        // cap. Campaign backups stream to disk instead and are staged one at a time.
        private const long IngressMaxActiveJsonBytes = 128L * 1024L * 1024L;
        private static int ActiveIngressConnections;
        private static long ActiveIngressJsonBytes;
        private static int ActiveCampaignUploads;

        // Framing- or trust-relevant fields: a repeated field is ambiguous and the request is refused.
        private static readonly HashSet<string> IngressSingletonHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Content-Length", "Transfer-Encoding", "Content-Type", "Origin", "Sec-Fetch-Site",
            "X-Reign-Peer", "X-Reign-Host", "X-Reign-Keys", Reign.Core.Contracts.Platform.ReignProtocolHandler.HeaderName
        };

        // Liveness and the static Control Center shell stay public: only the exact raw method token "GET" with one of these
        // exact raw paths (query aside) and no body. Case variants, aliases, dot segments and percent-encodings are ordinary
        // routes and pass IngressAccessRefusal.
        private static readonly HashSet<string> PublicIngressRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/health", "/", "/index.html", "/assets/reign-icon.webp", "/assets/ReignLogo.png"
        };

        private sealed class HttpIngressException : Exception
        {
            internal readonly int Status;
            internal readonly string RequestPath, Reason;

            internal HttpIngressException(int status, string message, string path = "", string reason = "")
                : base(message)
            {
                Status = status;
                RequestPath = path ?? "";
                Reason = string.IsNullOrEmpty(reason) ? message : reason;
            }
        }

        // Every socket read gets the smaller of the idle allowance and what is left of the phase budget, so neither a
        // silent nor a dribbling client can hold a handler past the budget.
        private sealed class IngressReader
        {
            private readonly Stream stream;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private long deadlineMs;
            private byte[] pending = Array.Empty<byte>();
            private int pendingOffset;

            internal IngressReader(Stream stream) { this.stream = stream; }

            internal void Budget(long milliseconds) => deadlineMs = clock.ElapsedMilliseconds + Math.Max(1, milliseconds);

            internal void Unread(byte[] data, int offset, int count)
            {
                pending = new byte[Math.Max(0, count)];
                if (count > 0) Buffer.BlockCopy(data, offset, pending, 0, count);
                pendingOffset = 0;
            }

            internal int Read(byte[] buffer, int offset, int count)
            {
                if (pendingOffset < pending.Length)
                {
                    int copied = Math.Min(count, pending.Length - pendingOffset);
                    Buffer.BlockCopy(pending, pendingOffset, buffer, offset, copied);
                    pendingOffset += copied;
                    return copied;
                }
                long remaining = deadlineMs - clock.ElapsedMilliseconds;
                if (remaining <= 0) throw new HttpIngressException(408, "Request timed out.", reason: "budget");
                if (stream.CanTimeout) stream.ReadTimeout = (int)Math.Max(1, Math.Min(IngressIdleTimeoutMs, remaining));
                try
                {
                    return stream.Read(buffer, offset, count);
                }
                catch (IOException ex) when (ex.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.TimedOut)
                {
                    throw new HttpIngressException(408, "Request timed out.", reason: "idle");
                }
            }

            internal void ReadExactly(byte[] buffer, int count)
            {
                int read = 0;
                while (read < count)
                {
                    int n = Read(buffer, read, count - read);
                    if (n <= 0) throw new HttpIngressException(400, "Incomplete request body.", reason: "body_eof");
                    read += n;
                }
            }

            internal void Release()
            {
                if (stream.CanTimeout) stream.ReadTimeout = Timeout.Infinite;
            }
        }

        private static bool IsHttpToken(string value) =>
            !string.IsNullOrEmpty(value) && value.All(c => c < 0x7F && (char.IsLetterOrDigit(c) || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0));

        // Null when absent so presence is distinguishable from an empty value; overlong values are refused, never ignored.
        private static string IngressIdentityHeader(Dictionary<string, string> headers, string name)
        {
            if (!headers.TryGetValue(name, out string value)) return null;
            if (value.Length > IngressMaxIdentityHeaderChars)
                throw new HttpIngressException(400, "Malformed request header.", reason: "overlong_" + name);
            return value;
        }

        // Canonical textual addresses only: Apache always writes REMOTE_ADDR canonically, so another spelling is not Apache.
        private static bool TryParseIngressAddress(string text, out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(text) || !IPAddress.TryParse(text, out IPAddress parsed) || parsed.ToString() != text) return false;
            address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
            return true;
        }

        private static bool IsLoopbackAuthority(string authority, int port)
        {
            if (string.IsNullOrEmpty(authority)) return false;
            string suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
            string value = authority.ToLowerInvariant();
            return value == "127.0.0.1" + suffix || value == "localhost" + suffix || value == "[::1]" + suffix;
        }

        // Central access policy. Trust comes from the transport only: the accepted socket must be loopback, and Apache's
        // X-Reign-Peer/X-Reign-Host (always overwritten by Apache from the real client address and Host) are honoured only
        // on that loopback hop, together, once each and canonical. The client peer must be loopback or the exact WSL
        // default gateway (Windows localhost forwarding), and the authority an exact local host. Origin and Sec-Fetch-Site
        // are browser CSRF/DNS-rebinding checks only; their absence never grants access by itself, and no protocol header
        // is a credential. Public routes never reach this check (ReadRequest decides them from the raw request line).
        // Returns null when allowed, otherwise a short reason for the operational log.
        private static string IngressAccessRefusal(HttpRequest request, Func<string, bool> localPeer = null, int? directPort = null)
        {
            if (request == null) return "request";
            localPeer = localPeer ?? GlobalKeyLocalPeer;
            if (!TryParseIngressAddress(request.RemoteAddress, out IPAddress socketPeer) || !IPAddress.IsLoopback(socketPeer)) return "socket_peer";
            int port = directPort ?? (ActiveServerPort > 0 ? ActiveServerPort : 5101);
            if (!IsLoopbackAuthority(request.Host, port)) return "host";
            string authority = request.Host.ToLowerInvariant();
            // A client can name X-Reign-Peer/X-Reign-Host in Connection; Apache's mod_proxy then strips them after
            // mod_headers set them, leaving a loopback socket and rewritten loopback Host that look direct. With
            // ProxyAddHeaders On (Apache's default, explicit on 8089) mod_proxy adds X-Forwarded-For after that stripping,
            // so its presence alone marks a proxied request that must still carry both overwritten fields. Its value is
            // never parsed or trusted; this provenance depends on ProxyAddHeaders staying On.
            if (request.ProxyForwarded || request.ProxyPeer != null || request.ProxyHost != null)
            {
                if (request.ProxyPeer == null || request.ProxyHost == null) return "proxy_incomplete";
                if (!TryParseIngressAddress(request.ProxyPeer, out IPAddress clientPeer) || !localPeer(clientPeer.ToString())) return "proxy_peer";
                authority = request.ProxyHost.ToLowerInvariant();
                if (!GlobalKeyApacheHosts.Contains(authority)) return "proxy_host";
            }
            if (request.Origin != null) return request.Origin == "http://" + authority ? null : "origin";
            if (request.FetchSite != null && request.FetchSite != "same-origin" && !(request.FetchSite == "none" && request.Method == "GET"))
                return "fetch_site";
            return null;
        }

        // Exact under concurrency: the compare-exchange commits a reservation only against the total it checked.
        private static bool TryReserveIngressBytes(ref long active, long amount, long budget)
        {
            while (true)
            {
                long current = Interlocked.Read(ref active);
                if (amount > budget - current) return false;
                if (Interlocked.CompareExchange(ref active, current + amount, current) == current) return true;
            }
        }

        // Idempotent; HandleClient's finally calls it at route exit, ReadRequest on any failure before it returns.
        private static void ReleaseIngressReservations(HttpRequest request)
        {
            if (request == null) return;
            long bytes = Interlocked.Exchange(ref request.ReservedJsonBytes, 0);
            if (bytes > 0) Interlocked.Add(ref ActiveIngressJsonBytes, -bytes);
            if (Interlocked.Exchange(ref request.HoldsUploadSlot, 0) == 1) Interlocked.Exchange(ref ActiveCampaignUploads, 0);
        }

        private static HttpRequest ReadRequest(Stream stream, string remoteAddress = "", Func<string, bool> localPeer = null, int? directPort = null)
        {
            var reader = new IngressReader(stream);
            reader.Budget(IngressHeaderBudgetMs);
            byte[] head = new byte[IngressMaxHeaderBytes];
            int length = 0, end = -1;
            while (end < 0)
            {
                if (length == head.Length) throw new HttpIngressException(431, "Request headers are too large.", reason: "header_bytes");
                int n = reader.Read(head, length, head.Length - length);
                if (n <= 0)
                {
                    if (length == 0) return null;
                    throw new HttpIngressException(400, "Incomplete request headers.", reason: "header_eof");
                }
                int from = Math.Max(0, length - 3);
                length += n;
                for (int i = from; i + 3 < length; i++)
                {
                    if (head[i] == 13 && head[i + 1] == 10 && head[i + 2] == 13 && head[i + 3] == 10) { end = i; break; }
                }
            }
            reader.Unread(head, end + 4, length - end - 4);

            string[] lines = Encoding.Latin1.GetString(head, 0, end).Split("\r\n");
            if (lines[0].Length > IngressMaxRequestLineBytes) throw new HttpIngressException(414, "Request target is too long.", reason: "request_line");
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || !IsHttpToken(requestLine[0]) || !requestLine[1].StartsWith("/", StringComparison.Ordinal)
                || requestLine[1].Any(c => c <= ' ' || c >= 0x7F) || (requestLine[2] != "HTTP/1.1" && requestLine[2] != "HTTP/1.0"))
                throw new HttpIngressException(400, "Malformed request line.", reason: "request_line");
            if (lines.Length - 1 > IngressMaxHeaderFields) throw new HttpIngressException(431, "Too many request headers.", reason: "header_fields");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                string name = colon > 0 ? line.Substring(0, colon) : "";
                if (!IsHttpToken(name)) throw new HttpIngressException(400, "Malformed request header.", reason: "header_syntax");
                string value = line.Substring(colon + 1).Trim(' ', '\t');
                if (value.Any(c => (c < 0x20 && c != '\t') || c == 0x7F))
                    throw new HttpIngressException(400, "Malformed request header.", reason: "header_control");
                if (headers.ContainsKey(name) && IngressSingletonHeaders.Contains(name))
                    throw new HttpIngressException(400, "Duplicate request header.", reason: "duplicate_" + name);
                headers[name] = value;
            }

            bool hasLength = headers.TryGetValue("Content-Length", out string lengthText);
            if (headers.ContainsKey("Transfer-Encoding"))
                throw hasLength ? new HttpIngressException(400, "Conflicting request framing.", reason: "te_and_cl")
                    : new HttpIngressException(411, "Content-Length is required.", reason: "transfer_encoding");
            long bodyLength = 0;
            if (hasLength && (lengthText.Length == 0 || lengthText.Length > 18 || !lengthText.All(c => c >= '0' && c <= '9')
                || !long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out bodyLength)))
                throw new HttpIngressException(400, "Invalid Content-Length.", reason: "content_length");

            string rawTarget = requestLine[1];
            string requestPath = rawTarget.Split('?')[0];
            var request = new HttpRequest
            {
                Method = requestLine[0].ToUpperInvariant(),
                ReignProtocol = headers.TryGetValue(Reign.Core.Contracts.Platform.ReignProtocolHandler.HeaderName, out string reignProtocol) ? reignProtocol : "",
                Path = requestPath,
                Query = ParseQuery(rawTarget),
                JsonBody = new Dictionary<string, object>(),
                UploadFilePath = "",
                BodyLength = bodyLength,
                ContentType = headers.TryGetValue("Content-Type", out string requestedContentType) ? requestedContentType : "",
                Host = IngressIdentityHeader(headers, "Host"),
                Origin = IngressIdentityHeader(headers, "Origin"),
                ProxyPeer = IngressIdentityHeader(headers, "X-Reign-Peer"),
                ProxyHost = IngressIdentityHeader(headers, "X-Reign-Host"),
                ProxyForwarded = headers.ContainsKey("X-Forwarded-For"),
                FetchSite = IngressIdentityHeader(headers, "Sec-Fetch-Site"),
                KeysHeader = headers.TryGetValue("X-Reign-Keys", out string keysHeader) && keysHeader.Length <= 16 ? keysHeader : "",
                RemoteAddress = remoteAddress ?? ""
            };

            // The public exemption is decided from the raw method token and raw path, and a declared body (any nonzero
            // Content-Length, however large) is refused from the headers alone: nothing is read or allocated for it.
            if (requestLine[0] == "GET" && PublicIngressRoutes.Contains(requestPath))
            {
                if (bodyLength != 0) throw new HttpIngressException(400, "This request cannot carry a body.", requestPath, "public_body");
            }
            else
            {
                string refusal = IngressAccessRefusal(request, localPeer, directPort);
                if (refusal != null) throw new HttpIngressException(403, "Forbidden.", requestPath, refusal);
            }

            bool campaignUpload = request.Method == "POST" && requestPath.Equals("/api/campaigns/import", StringComparison.OrdinalIgnoreCase)
                && !request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
            long limit = campaignUpload ? IngressMaxCampaignUploadBytes
                : requestPath.Equals("/api/support-bundle", StringComparison.OrdinalIgnoreCase) ? IngressMaxSupportBundleBytes
                : requestPath == Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ProposedRoute
                    ? Reign.Core.Contracts.Actions.TerminalActionReportProtocol.MaxRequestBodyBytes
                : requestPath == DialogueService.IndividualViewInsertionRoute ? IngressMaxViewInsertionAckBytes : IngressMaxJsonBodyBytes;
            if (bodyLength > limit) throw new HttpIngressException(413, "Request body is too large.", requestPath, "body_limit");
            if (bodyLength == 0)
            {
                reader.Release();
                return request;
            }

            // Temporary overload is 429 + Retry-After (WriteJson), which Apache's ProxyErrorOverride (502/503/504) passes
            // through unchanged. Reservations are taken before staging or allocation and released by ReleaseIngressReservations.
            if (campaignUpload)
            {
                if (Interlocked.CompareExchange(ref ActiveCampaignUploads, 1, 0) != 0)
                    throw new HttpIngressException(429, "Another campaign backup upload is in progress. Retry when it finishes.", requestPath, "upload_busy");
                request.HoldsUploadSlot = 1;
            }
            else
            {
                if (!TryReserveIngressBytes(ref ActiveIngressJsonBytes, bodyLength, IngressMaxActiveJsonBytes))
                    throw new HttpIngressException(429, "The server is busy with other large requests. Retry shortly.", requestPath, "json_budget");
                request.ReservedJsonBytes = bodyLength;
            }

            try
            {
                reader.Budget(IngressBodyBudgetBaseMs + bodyLength * 1000L / IngressBodyMinimumBytesPerSecond);
                if (campaignUpload)
                {
                    string uploadRoot = Path.Combine(DataDir, "backups", "uploads");
                    Directory.CreateDirectory(uploadRoot);
                    string uploadFilePath = Path.Combine(uploadRoot, Guid.NewGuid().ToString("N") + ".reignbackup");
                    try
                    {
                        byte[] buffer = new byte[1024 * 1024];
                        long remaining = bodyLength;
                        using (FileStream output = new FileStream(uploadFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            while (remaining > 0)
                            {
                                int n = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                                if (n <= 0) throw new HttpIngressException(400, "Incomplete request body.", requestPath, "body_eof");
                                output.Write(buffer, 0, n);
                                remaining -= n;
                            }
                        }
                    }
                    catch
                    {
                        PersistenceService.TryDeleteFile(uploadFilePath);
                        throw;
                    }
                    request.UploadFilePath = uploadFilePath;
                }
                else
                {
                    byte[] bodyBytes = new byte[(int)bodyLength];
                    reader.ReadExactly(bodyBytes, bodyBytes.Length);
                    string body = Encoding.UTF8.GetString(bodyBytes);
                    try
                    {
                        request.JsonBody = string.IsNullOrWhiteSpace(body) ? new Dictionary<string, object>()
                            : Json.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>();
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        throw new HttpIngressException(400, "Malformed JSON request body.", requestPath, "json");
                    }
                }
                reader.Release();
            }
            catch
            {
                // Parse errors, timeouts and disconnects: the caller never receives this request, so clean up here.
                if (!string.IsNullOrEmpty(request.UploadFilePath)) PersistenceService.TryDeleteFile(request.UploadFilePath);
                ReleaseIngressReservations(request);
                throw;
            }
            return request;
        }

        // A refused request gets a short, generic reply. Its unread body is briefly drained so the peer reads the status
        // instead of a connection reset; the drain is bounded in bytes and time.
        private static void WriteIngressRejection(TcpClient client, NetworkStream stream, int status, string message)
        {
            try
            {
                stream.WriteTimeout = 2000;
                WriteJson(stream, status, new Dictionary<string, object> { ["ok"] = false, ["error"] = message });
                client?.Client?.Shutdown(SocketShutdown.Send);
                stream.ReadTimeout = 250;
                var sink = new byte[8192];
                var clock = Stopwatch.StartNew();
                for (long drained = 0; drained < 64 * 1024 && clock.ElapsedMilliseconds < 1000;)
                {
                    int n = stream.Read(sink, 0, sink.Length);
                    if (n <= 0) break;
                    drained += n;
                }
            }
            catch (Exception)
            {
                // The peer may already be gone; the rejection has already been logged.
            }
        }

        // Runs on the accept thread: a fresh socket's send buffer takes this short reply without blocking, and the write
        // timeout bounds the worst case. The request is not read. 429 rather than 503: Apache's ProxyErrorOverride would
        // replace a 503 with the offline page although the server is running.
        private static void RejectBusyConnection(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.SendTimeout = 1000;
                    byte[] body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"Server busy.\"}");
                    byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 429 Too Many Requests\r\nContent-Type: application/json; charset=utf-8\r\n"
                        + "Content-Length: " + body.Length + "\r\nRetry-After: 1\r\nConnection: close\r\n\r\n");
                    client.Client.Send(header);
                    client.Client.Send(body);
                }
                catch (Exception)
                {
                    // Overload rejection is best effort.
                }
            }
        }

        private static void WriteFile(NetworkStream stream, string path, string contentType)
        {
            if (!File.Exists(path))
            {
                WriteJson(stream, 404, new Dictionary<string, object> { ["ok"] = false, ["error"] = "File not found." });
                return;
            }

            byte[] body = File.ReadAllBytes(path);
            string header = "HTTP/1.1 200 OK\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Cache-Control: no-cache\r\n"
                + "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
        }

        internal static void WriteJson(NetworkStream stream, int status, object payload, string method = "", string path = "")
        {
            string statusText = HttpStatusText(status);
            byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(DialogueService.ConversationReferenceTransportPayload(payload, method, path)));
            string header = "HTTP/1.1 " + status + " " + statusText + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + (status == 429 ? "Retry-After: 1\r\n" : "")
                + "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
        }

        private static void WriteHtml(NetworkStream stream, string html)
        {
            WriteText(stream, 200, "text/html; charset=utf-8", html, null);
        }

        internal static List<Dictionary<string, object>> ReadJsonLinesFromPath(string path)
        {
            if (!File.Exists(path))
            {
                return new List<Dictionary<string, object>>();
            }

            lock (FileLock)
            {
                return File.ReadAllLines(path, Encoding.UTF8)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => Json.Deserialize<Dictionary<string, object>>(x))
                    .ToList();
            }
        }

        private static Dictionary<string, string> ParseQuery(string rawTarget)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int index = rawTarget.IndexOf('?');
            if (index < 0 || index == rawTarget.Length - 1)
            {
                return result;
            }

            string query = rawTarget.Substring(index + 1);
            foreach (string part in query.Split('&'))
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                string[] pieces = part.Split(new[] { '=' }, 2);
                string key = WebUtility.UrlDecode(pieces[0] ?? string.Empty);
                string value = pieces.Length > 1 ? WebUtility.UrlDecode(pieces[1] ?? string.Empty) : string.Empty;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    result[key] = value;
                }
            }

            return result;
        }
    }
}
