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
    internal static partial class OperationsService
    {
        internal static Dictionary<string, object> ReadLogApi(Dictionary<string, string> query)
        {
            string file = query.TryGetValue("file", out string requestedFile) ? requestedFile : "server-log.jsonl";
            string campaignId = query.TryGetValue("campaignId", out string requestedCampaignId) ? requestedCampaignId : "";
            int limit = query.TryGetValue("limit", out string limitText) && int.TryParse(limitText, out int parsedLimit) ? parsedLimit : 250;
            limit = Math.Max(1, Math.Min(500, limit));
            string search = query.TryGetValue("search", out string requestedSearch)
                ? (requestedSearch ?? string.Empty).Trim()
                : string.Empty;
            if (search.Length > 200) search = search.Substring(0, 200);

            string path = ResolveLogFile(file, campaignId);
            List<Dictionary<string, object>> candidates = Program.ReadJsonLinesFromPath(path)
                .Reverse<Dictionary<string, object>>()
                .Select(RedactSensitive)
                .Where(row => string.IsNullOrWhiteSpace(search)
                    || Json.Serialize(row).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(limit)
                .ToList();
            const int maxResponseCharacters = 256 * 1024;
            int responseCharacters = 0;
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> row in candidates)
            {
                int rowCharacters = Json.Serialize(row).Length;
                if (rows.Count > 0 && responseCharacters + rowCharacters > maxResponseCharacters) break;
                rows.Add(row);
                responseCharacters += rowCharacters;
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["file"] = Path.GetFileName(path),
                ["campaignId"] = CampaignFileService.ResolveLogCampaignId(campaignId),
                ["path"] = path,
                ["search"] = search,
                ["requestedLimit"] = limit,
                ["returnedCount"] = rows.Count,
                ["responseCharacters"] = responseCharacters,
                ["truncated"] = rows.Count < candidates.Count,
                ["entries"] = rows
            };
        }

        internal static Dictionary<string, object> ListCampaigns()
        {
            return PersistenceService.CampaignListApi();
        }

        internal static Dictionary<string, object> ClearLogs(Dictionary<string, object> payload)
        {
            string file = ReadString(payload, "file", "server-log.jsonl");
            string campaignId = ReadString(payload, "campaignId", "");
            string path = ResolveLogFile(file, campaignId);
            lock (Program.FileLock)
            {
                File.WriteAllText(path, string.Empty, Encoding.UTF8);
            }

            LogOperational("logs.clear", new Dictionary<string, object> { ["file"] = Path.GetFileName(path), ["campaignId"] = CampaignFileService.ResolveLogCampaignId(campaignId) });
            return new Dictionary<string, object> { ["ok"] = true, ["file"] = Path.GetFileName(path), ["campaignId"] = CampaignFileService.ResolveLogCampaignId(campaignId) };
        }

        internal static void WriteLogExport(NetworkStream stream, Dictionary<string, string> query)
        {
            string file = query.TryGetValue("file", out string requestedFile) ? requestedFile : "server-log.jsonl";
            string campaignId = query.TryGetValue("campaignId", out string requestedCampaignId) ? requestedCampaignId : "";
            string path = ResolveLogFile(file, campaignId);
            string content = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            WriteText(stream, 200, "application/x-ndjson; charset=utf-8", RedactDiagnosticLogContent(content), Path.GetFileName(path));
        }

        internal static Dictionary<string, object> AuditIngest(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", CampaignFileService.ResolveLogCampaignId(""));
            string correlationId = EnsureCorrelationId(payload);
            WriteAudit(
                campaignId,
                correlationId,
                ReadString(payload, "source", "game_client"),
                ReadString(payload, "mode", ""),
                ReadString(payload, "phase", "client.audit"),
                ReadFirstString(payload, "heroId", "heroStringId", "speakerHeroStringId"),
                ReadFirstString(payload, "actionId", "serverActionId"),
                ReadFirstString(payload, "eventId", "socialEventId"),
                ReadString(payload, "status", "info"),
                ReadLong(payload, "durationMs", 0),
                ReadString(payload, "summary", ""),
                ReadDictionary(payload, "data") ?? payload);

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId
            };
        }

        internal static Dictionary<string, object> AuditQuery(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = CampaignFileService.ResolveLogCampaignId(query.TryGetValue("campaignId", out string requestedCampaign) ? requestedCampaign : "");
            int limit = query.TryGetValue("limit", out string limitText) && int.TryParse(limitText, out int parsedLimit) ? parsedLimit : 250;
            limit = Math.Max(1, Math.Min(5000, limit));
            bool newestFirst = !query.TryGetValue("oldestFirst", out string oldestFirstText) || !oldestFirstText.Equals("true", StringComparison.OrdinalIgnoreCase);
            string heroId = query.TryGetValue("heroId", out string heroFilter) ? heroFilter : "";
            string actionId = query.TryGetValue("actionId", out string actionFilter) ? actionFilter : "";
            string eventId = query.TryGetValue("eventId", out string eventFilter) ? eventFilter : "";
            string correlationId = query.TryGetValue("correlationId", out string correlationFilter) ? correlationFilter : "";
            string phase = query.TryGetValue("phase", out string phaseFilter) ? phaseFilter : "";
            string status = query.TryGetValue("status", out string statusFilter) ? statusFilter : "";
            string mode = query.TryGetValue("mode", out string modeFilter) ? modeFilter : "";
            string source = query.TryGetValue("source", out string sourceFilter) ? sourceFilter : "";

            string path = CampaignFileService.CampaignFile(campaignId, "audit", "audit.jsonl");
            IEnumerable<Dictionary<string, object>> rows = Program.ReadJsonLinesFromPath(path);
            rows = rows.Where(row =>
                AuditFilterMatches(row, "heroId", heroId)
                && AuditFilterMatches(row, "actionId", actionId)
                && AuditFilterMatches(row, "eventId", eventId)
                && AuditFilterMatches(row, "correlationId", correlationId)
                && AuditFilterMatches(row, "phase", phase)
                && AuditFilterMatches(row, "status", status)
                && AuditFilterMatches(row, "mode", mode)
                && AuditFilterMatches(row, "source", source));

            rows = newestFirst
                ? rows.Reverse().Take(limit)
                : rows.Take(limit);

            List<Dictionary<string, object>> entries = rows.ToList();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["path"] = path,
                ["count"] = entries.Count,
                ["entries"] = entries
            };
        }

        private static bool AuditFilterMatches(Dictionary<string, object> row, string key, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            string value = ReadString(row, key, "");
            return value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string EnsureCorrelationId(Dictionary<string, object> payload)
        {
            string correlationId = ReadFirstString(payload, "correlationId", "correlationID", "requestId", "clientRequestId");
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                correlationId = "corr-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (payload != null)
                {
                    payload["correlationId"] = correlationId;
                }
            }

            return correlationId;
        }

        internal static void WriteAudit(
            string campaignId,
            string correlationId,
            string source,
            string mode,
            string phase,
            string heroId,
            string actionId,
            string eventId,
            string status,
            long durationMs,
            string summary,
            Dictionary<string, object> data)
        {
            campaignId = string.IsNullOrWhiteSpace(campaignId) ? CampaignFileService.ResolveLogCampaignId("") : campaignId;
            correlationId = string.IsNullOrWhiteSpace(correlationId) ? "corr-" + Guid.NewGuid().ToString("N") : correlationId;
            ConversationResponseService.ObserveDiagnosticAudit(campaignId, correlationId, mode, phase, heroId, status, durationMs, summary, data);
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["auditId"] = Guid.NewGuid().ToString("N"),
                ["correlationId"] = correlationId,
                ["campaignId"] = campaignId,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
                ["source"] = source ?? "",
                ["mode"] = mode ?? "",
                ["phase"] = phase ?? "",
                ["heroId"] = heroId ?? "",
                ["actionId"] = actionId ?? "",
                ["eventId"] = eventId ?? "",
                ["status"] = status ?? "info",
                ["durationMs"] = durationMs,
                ["summary"] = DialogueService.LimitText(summary ?? "", 1200),
                ["data"] = SanitizeAuditValue(DialogueService.WithoutConversationReferenceMetadata(data ?? new Dictionary<string, object>()), 0)
            };

            CampaignFileService.AppendBoundedJsonLineToPath(CampaignFileService.CampaignFile(campaignId, "audit", "audit.jsonl"), row,
                8L * 1024L * 1024L, 4L * 1024L * 1024L);
        }

        internal static object SanitizeAuditValue(object value, int depth)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string text)
            {
                return LimitAuditString(text);
            }

            Type valueType = value.GetType();
            if (valueType.IsPrimitive
                || value is decimal
                || value is DateTime
                || value is DateTimeOffset
                || value is Guid)
            {
                return value;
            }

            // Depth limits protect nested containers from unbounded audit growth.
            // Scalar leaves remain evidence even when their containing list is at
            // the maximum depth; otherwise stable IDs become the literal
            // "[audit depth limit]" and exactly-once verification cannot resolve
            // the corresponding persisted record.
            if (depth > Program.AuditMaxDepth)
            {
                return "[audit depth limit]";
            }

            if (value is Dictionary<string, object> dictionary)
            {
                return SanitizeAuditDictionary(dictionary, depth + 1);
            }

            if (value is IDictionary genericDictionary)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in genericDictionary)
                {
                    copy[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = entry.Value;
                }
                return SanitizeAuditDictionary(copy, depth + 1);
            }

            if (value is IEnumerable enumerable && !(value is byte[]))
            {
                List<object> items = new List<object>();
                int count = 0;
                foreach (object item in enumerable)
                {
                    if (count >= Program.AuditMaxListItems)
                    {
                        items.Add("[audit item limit reached]");
                        break;
                    }

                    items.Add(SanitizeAuditValue(item, depth + 1));
                    count++;
                }

                return items;
            }

            return value;
        }

        private static Dictionary<string, object> SanitizeAuditDictionary(Dictionary<string, object> source, int depth)
        {
            Dictionary<string, object> clean = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> pair in source ?? new Dictionary<string, object>())
            {
                string key = pair.Key ?? "";
                if (IsAuditSecretKey(key))
                {
                    clean[key] = "[redacted]";
                }
                else if (IsAuditBlobKey(key))
                {
                    clean[key] = "[omitted large image/blob data]";
                }
                else if (key.Equals("actionResolutionIndex", StringComparison.OrdinalIgnoreCase))
                {
                    clean[key] = SummarizeActionResolutionIndex(pair.Value);
                }
                else
                {
                    clean[key] = SanitizeAuditValue(pair.Value, depth + 1);
                }
            }

            return clean;
        }

        internal static bool IsAuditSecretKey(string key)
        {
            key = (key ?? "").ToLowerInvariant();
            return key.Contains("apikey")
                || key.Contains("api_key")
                || key.Contains("authorization")
                || key.Contains("bearer")
                || key.Contains("password")
                || key.Contains("secret")
                || key.Equals("token")
                || key.EndsWith("token", StringComparison.Ordinal);
        }

        internal static bool IsAuditBlobKey(string key)
        {
            key = (key ?? "").ToLowerInvariant();
            return key.Contains("base64")
                || key.Contains("imagedata")
                || key.Contains("imagebytes")
                || key.Contains("sourceimage")
                || key.Contains("capturebytes")
                || key.Contains("pngbytes")
                || key.Contains("binary");
        }

        private static string LimitAuditString(string text)
        {
            text = text ?? "";
            if (text.Length <= Program.AuditMaxStringChars)
            {
                return text;
            }

            return text.Substring(0, Program.AuditMaxStringChars) + "\n[audit string truncated: " + text.Length.ToString(CultureInfo.InvariantCulture) + " chars]";
        }

        private static Dictionary<string, object> SummarizeActionResolutionIndex(object value)
        {
            Dictionary<string, object> index = value as Dictionary<string, object>;
            if (index == null && value is IDictionary genericDictionary)
            {
                index = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in genericDictionary)
                {
                    index[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = entry.Value;
                }
            }

            if (index == null)
            {
                return new Dictionary<string, object> { ["summary"] = "[unreadable actionResolutionIndex]" };
            }

            Dictionary<string, object> summary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["version"] = ReadString(index, "version", ""),
                ["currentSettlement"] = ReadString(index, "currentSettlement", "")
            };
            foreach (string key in new[] { "settlements", "heroes", "clans", "kingdoms", "mobileParties", "armies", "prisoners", "items", "assets", "aliases", "recentContext", "concepts" })
            {
                object raw = index.ContainsKey(key) ? index[key] : null;
                summary[key + "Count"] = AuditCollectionCount(raw);
            }

            return summary;
        }

        private static int AuditCollectionCount(object raw)
        {
            if (raw == null)
            {
                return 0;
            }

            if (raw is IDictionary dictionary)
            {
                return dictionary.Count;
            }

            if (raw is ICollection collection)
            {
                return collection.Count;
            }

            if (raw is IEnumerable enumerable && !(raw is string))
            {
                int count = 0;
                foreach (object ignored in enumerable)
                {
                    count++;
                }

                return count;
            }

            return 1;
        }

        private static string ResolveLogFile(string file, string campaignId)
        {
            string name = Path.GetFileName(string.IsNullOrWhiteSpace(file) ? "server-log.jsonl" : file);
            if (name.Equals("server-log.jsonl", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Program.LogsDir);
                return Path.Combine(Program.LogsDir, "server-log.jsonl");
            }

            if (name.Equals("llm-log.jsonl", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Program.LogsDir);
                return Path.Combine(Program.LogsDir, name);
            }

            if (name.Equals("events.jsonl", StringComparison.OrdinalIgnoreCase)
                || name.Equals("memories.jsonl", StringComparison.OrdinalIgnoreCase))
            {
                return CampaignFileService.CampaignFile(CampaignFileService.ResolveLogCampaignId(campaignId), "world", name);
            }

            if (name.Equals("actions.jsonl", StringComparison.OrdinalIgnoreCase)
                || name.Equals("action-queue.json", StringComparison.OrdinalIgnoreCase))
            {
                return CampaignFileService.CampaignFile(CampaignFileService.ResolveLogCampaignId(campaignId), "actions", name);
            }

            if (name.Equals("audit.jsonl", StringComparison.OrdinalIgnoreCase))
            {
                return CampaignFileService.CampaignFile(CampaignFileService.ResolveLogCampaignId(campaignId), "audit", "audit.jsonl");
            }

            Directory.CreateDirectory(Program.LogsDir);
            return Path.Combine(Program.LogsDir, "server-log.jsonl");
        }

        internal static void LogRequest(Program.HttpRequest request, int status)
        {
            Dictionary<string, object> settings = SettingsService.LoadSettings();
            if (!ReadBool(settings, "enableRequestLogging", true))
            {
                return;
            }

            LogOperational("http.request", new Dictionary<string, object>
            {
                ["method"] = request.Method,
                ["path"] = request.Path,
                ["status"] = status
            });
        }

        internal static void LogOperational(string eventType, Dictionary<string, object> data)
        {
            try
            {
                Directory.CreateDirectory(Program.LogsDir);
                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["eventType"] = eventType,
                    ["data"] = RedactSensitive(data)
                };

                lock (Program.FileLock)
                {
                    CampaignFileService.AppendBoundedJsonLineToPathLocked(Path.Combine(Program.LogsDir, "server-log.jsonl"), row,
                        16L * 1024L * 1024L, 8L * 1024L * 1024L);
                }
            }
            catch
            {
                // Logging should never interrupt the server.
            }
        }

        internal static Dictionary<string, object> RedactSensitive(Dictionary<string, object> source)
        {
            var copy = new Dictionary<string, object>();
            foreach (var pair in source ?? new Dictionary<string, object>())
            {
                copy[pair.Key] = IsSensitiveDiagnosticKey(pair.Key) ? "***" : RedactDiagnosticValue(pair.Value);
            }
            return copy;
        }

        private static bool IsSensitiveDiagnosticKey(string name)
        {
            string normalized = (name ?? "").Replace("_", "").Replace("-", "");
            return new[] { "authorization", "secret", "token" }.Contains(normalized, StringComparer.OrdinalIgnoreCase)
                || new[] { "apiKey", "accessToken", "refreshToken", "clientSecret", "password" }
                    .Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private static object RedactDiagnosticValue(object value)
        {
            if (value is string text) return RedactSupportText(text);
            if (value is IDictionary dictionary)
            {
                var values = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary) values[Convert.ToString(entry.Key)] = entry.Value;
                return RedactSensitive(values);
            }
            if (value is IEnumerable sequence)
            {
                var values = new List<object>();
                foreach (object item in sequence) values.Add(RedactDiagnosticValue(item));
                return values;
            }
            return value;
        }

        internal static string RedactDiagnosticLogContent(string content)
            => string.Join("\n", (content ?? "").Replace("\r\n", "\n").Split('\n').Select(line =>
            {
                var row = TryParseJsonObject(line);
                return row == null ? RedactSupportText(line) : Json.Serialize(RedactSensitive(row));
            }));
    }
}
