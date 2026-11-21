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
        private const int LogEntryBytes = 16 * 1024;
        private const int LogPageBytes = 256 * 1024;
        private const int LogCursorLength = 10 + 1 + 64; // Int32 row index, dot, SHA-256.
        private static readonly byte[] LogCursorKey = RandomNumberGenerator.GetBytes(32);
        private static readonly HashSet<string> LogMetadataKeys = new(StringComparer.Ordinal)
        {
            "ts", "timestamp", "eventType", "phase", "requestType", "type", "kind", "severity", "level",
            "status", "ok", "method", "path", "source", "mode", "model", "provider", "exceptionType",
            "id", "campaignId", "correlationId", "auditId", "eventId", "actionId", "serverActionId", "heroId", "traceId",
            "payloadId", "evidenceId", "promptEvidenceId", "durationMs", "requestChars", "responseChars"
        };

        internal static Dictionary<string, object> ReadLogApi(Dictionary<string, string> query)
        {
            query ??= new();
            string Parameter(string name) => query.GetValueOrDefault(name) ?? "";
            string file = Parameter("file"), campaignId = Parameter("campaignId");
            if (campaignId.Length > 160) return LogQueryError("Campaign ID exceeds 160 characters.");
            campaignId = CampaignFileService.ResolveLogCampaignId(campaignId);
            string path = ResolveLogFile(file, campaignId);
            int limit = query.TryGetValue("limit", out string limitText) && int.TryParse(limitText, out int parsedLimit) ? parsedLimit : 250;
            limit = Math.Clamp(limit, 1, 500);
            string search = Parameter("search").Trim();
            if (search.Length > 200) search = search.Substring(0, 200);
            bool searchNeedsEscaping = Json.Serialize(search).Length > search.Length + 2;
            string eventType = Parameter("eventType").Trim();
            string severity = Parameter("severity").Trim();
            string cursor = Parameter("cursor");
            if (eventType.Length > 160 || severity.Length > 32 || cursor.Length > LogCursorLength)
                return LogQueryError("Invalid log filter or continuation.");

            var source = ReadLogSnapshot(path);
            int index = source.Count - 1;
            if (cursor.Length > 0)
            {
                int dot = cursor.IndexOf('.');
                if (dot <= 0 || dot != cursor.Length - 65
                    || !int.TryParse(cursor.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out index)
                    || index >= source.Count)
                    return LogQueryError("Log continuation is invalid or expired. Reload the first page.");
            }

            // Reserve the largest possible serialized envelope, including a continuation and counters.
            // Only entries and the commas between them consume the remaining UTF-8 body budget.
            var entries = new List<Dictionary<string, object>>();
            var response = new Dictionary<string, object>
            {
                ["ok"] = true, ["file"] = Path.GetFileName(path), ["campaignId"] = campaignId, ["path"] = path,
                ["searchApplied"] = search.Length > 0, ["requestedLimit"] = limit,
                ["maxEntryBytes"] = LogEntryBytes, ["maxResponseBytes"] = LogPageBytes,
                ["returnedCount"] = limit, ["elidedCount"] = limit, ["responseCharacters"] = LogPageBytes,
                ["truncated"] = false, ["responseLimitReached"] = false, ["hasMore"] = false,
                ["nextCursor"] = new string('0', LogCursorLength), ["entries"] = entries
            };
            int remaining = LogPageBytes - Encoding.UTF8.GetByteCount(Json.Serialize(response));
            if (remaining < LogEntryBytes) return LogQueryError("Log metadata exceeds the response budget.");
            string scope = Json.Serialize(new[] { path, search, eventType, severity });
            string CursorFor(int position, string json)
            {
                string ordinal = position.ToString(CultureInfo.InvariantCulture);
                byte[] anchor = Encoding.UTF8.GetBytes(scope + "\n" + ordinal + "\n" + json);
                return ordinal + "." + Convert.ToHexString(HMACSHA256.HashData(LogCursorKey, anchor));
            }
            int characters = 0, elided = 0;
            string nextCursor = null;
            for (; index >= 0; index--)
            {
                var row = (Dictionary<string, object>)DialogueService.WithoutConversationReferenceMetadata(RedactSensitive(source[index]));
                string json = Json.Serialize(row);
                if (cursor.Length > 0)
                {
                    if (cursor != CursorFor(index, json)) return LogQueryError("Log continuation is invalid or expired. Reload the first page.");
                    cursor = "";
                }
                var data = ReadDictionary(row, "data");
                string rowEvent = ReadFirstString(row, "eventType", "phase", "requestType", "type", "kind");
                if (eventType.Length > 0 && !eventType.Equals(rowEvent, StringComparison.OrdinalIgnoreCase)) continue;
                if (severity.Length > 0 && !severity.Equals(LogSeverity(row, data, rowEvent), StringComparison.OrdinalIgnoreCase)) continue;
                if (search.Length > 0 && !json.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !(searchNeedsEscaping && LogTextMatches(row, search))) continue;
                if (entries.Count == limit) { nextCursor = CursorFor(index, json); break; }

                var entry = ProjectLogEntry(row, data, Path.GetFileName(path), campaignId, Encoding.UTF8.GetByteCount(json));
                string entryJson = Json.Serialize(entry);
                int cost = Encoding.UTF8.GetByteCount(entryJson) + (entries.Count == 0 ? 0 : 1);
                if (cost > remaining)
                {
                    nextCursor = CursorFor(index, json);
                    response["responseLimitReached"] = response["truncated"] = true;
                    break;
                }
                entries.Add(entry);
                remaining -= cost;
                characters += entryJson.Length;
                if (entry.ContainsKey("elided")) elided++;
            }
            response["nextCursor"] = nextCursor;
            response["hasMore"] = nextCursor != null;
            response["returnedCount"] = entries.Count;
            response["elidedCount"] = elided;
            response["responseCharacters"] = characters;
            return response;
        }

        private static Dictionary<string, object> LogQueryError(string error) => new() { ["ok"] = false, ["error"] = error };

        private static List<Dictionary<string, object>> ReadLogSnapshot(string path)
        {
            if (!Path.GetFileName(path).Equals("action-queue.json", StringComparison.OrdinalIgnoreCase))
                return Program.ReadJsonLinesFromPath(path);
            lock (Program.FileLock)
                return File.Exists(path) ? ActionPipelineService.ReadActionQueueFromPath(path) : new();
        }

        private static bool LogTextMatches(object value, string search) => value switch
        {
            string text => text.Contains(search, StringComparison.OrdinalIgnoreCase),
            Dictionary<string, object> fields => fields.Any(pair => pair.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
                || LogTextMatches(pair.Value, search)),
            IEnumerable<object> items => items.Any(item => LogTextMatches(item, search)),
            _ => false
        };

        private static string LogSeverity(Dictionary<string, object> row, Dictionary<string, object> data, string eventType)
        {
            string level = FirstNonEmpty(ReadFirstString(row, "severity", "level"), ReadFirstString(data, "severity", "level"));
            if (level.Length > 0) return level.Equals("warn", StringComparison.OrdinalIgnoreCase) ? "warning" : level;
            string status = FirstNonEmpty(ReadString(row, "status", ""), data?.GetValueOrDefault("status") as string).ToLowerInvariant();
            if (status is "warn" or "warning" || eventType.EndsWith("_warning", StringComparison.OrdinalIgnoreCase)
                || eventType.EndsWith(".warning", StringComparison.OrdinalIgnoreCase)) return "warning";
            if (status is "failed" or "error" || eventType.EndsWith(".error", StringComparison.OrdinalIgnoreCase)
                || eventType.EndsWith("_failed", StringComparison.OrdinalIgnoreCase)
                || eventType.EndsWith(".failed", StringComparison.OrdinalIgnoreCase)) return "error";
            int code = ReadInt(data, "status", 0);
            if (eventType == "http.request" && code >= 100)
                return code >= 500 ? "error" : code >= 400 ? "warning" : "info";
            if ((row.GetValueOrDefault("ok") ?? data?.GetValueOrDefault("ok")) is bool success) return success ? "info" : "error";
            return status.Length == 0 ? "unclassified" : status;
        }

        // Logs contain arbitrary campaign text, provider messages and returned reasoning, even in tiny rows.
        // Keep diagnostic fields in their original places. A raw text prefix can contain the entire private message.
        private static Dictionary<string, object> LogMetadata(Dictionary<string, object> source)
        {
            var result = new Dictionary<string, object>();
            foreach (var pair in source)
            {
                if (!LogMetadataKeys.Contains(pair.Key)) continue;
                if (pair.Value is string value)
                {
                    if (value.Length > 160 || !value.EnumerateRunes().All(c => Rune.IsLetterOrDigit(c) || c.IsAscii && "._:/+-".Contains((char)c.Value))) continue;
                }
                else if (pair.Value is not (null or bool or int or long or decimal or double)) continue;
                result[pair.Key] = pair.Value;
            }
            return result;
        }

        private static Dictionary<string, object> ProjectLogEntry(Dictionary<string, object> row, Dictionary<string, object> data,
            string file, string campaignId, int redactedBytes)
        {
            var entry = LogMetadata(row);
            bool omittedData = false;
            if (data != null)
            {
                var fields = LogMetadata(data);
                entry["data"] = fields;
                omittedData = fields.Count != data.Count;
            }
            if (redactedBytes <= LogEntryBytes && entry.Count == row.Count && !omittedData) return entry;
            entry["elided"] = true;
            entry["elisionReason"] = redactedBytes > LogEntryBytes ? "row_limit" : "payload_omitted";
            entry["redactedRowBytes"] = redactedBytes;
            entry["unabridgedRow"] = "unavailable_from_this_response";
            var evidence = LogEvidenceReference(row, data) ?? (data == null ? null : LogEvidenceReference(data, row));
            if (evidence == null && file == "audit.jsonl" && ConversationResponseService.IsDiagnosticLegacyResponse(row)
                && IsLogEvidenceId(ReadString(row, "auditId", "")))
            {
                string id = ReadString(row, "auditId", "");
                evidence = new() { ["route"] = "/api/diagnostics/conversations/payload", ["campaignId"] = campaignId,
                    ["traceId"] = "legacy-" + id, ["payloadId"] = id };
            }
            if (evidence != null) entry["relatedEvidence"] = evidence;
            // Even many individually valid fields can exceed the row ceiling when JSON-escaped.
            if (Encoding.UTF8.GetByteCount(Json.Serialize(entry)) > LogEntryBytes)
                return new() { ["elided"] = true, ["elisionReason"] = "row_limit", ["redactedRowBytes"] = redactedBytes,
                    ["unabridgedRow"] = "unavailable_from_this_response", ["relatedEvidence"] = evidence };
            return entry;
        }

        private static bool IsLogEvidenceId(string value) => value.Length == 32
            && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

        private static Dictionary<string, object> LogEvidenceReference(Dictionary<string, object> row, Dictionary<string, object> context)
        {
            var ids = LogMetadata(row);
            string trace = ReadString(ids, "traceId", ""), payload = ReadString(ids, "payloadId", "");
            if (IsLogEvidenceId(trace) && IsLogEvidenceId(payload))
                return new() { ["route"] = "/api/diagnostics/conversations/payload", ["traceId"] = trace, ["payloadId"] = payload };

            string prompt = ReadString(ids, "promptEvidenceId", "");
            if (ReadString(row, "schema", "") == "reign-prompt-evidence-v1") prompt = ReadString(ids, "evidenceId", "");
            if (!IsLogEvidenceId(prompt)) return null;
            var contextIds = context == null ? null : LogMetadata(context);
            string ScopeId(string key) => ReadString(row.ContainsKey(key) ? ids : contextIds, key, "");
            string campaign = ScopeId("campaignId"), correlation = ScopeId("correlationId");
            return campaign.Length > 0 && correlation.Length > 0
                ? new() { ["route"] = "/audit/prompt", ["campaignId"] = campaign, ["correlationId"] = correlation, ["evidenceId"] = prompt }
                : null;
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
            return new[] { "authorization", "bearer", "secret", "token" }.Contains(normalized, StringComparer.OrdinalIgnoreCase)
                || new[] { "apiKey", "accessToken", "refreshToken", "authToken", "clientSecret", "password" }
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
