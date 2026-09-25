using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string DiagnosticQuery(Dictionary<string, string> query, string key) => query != null && query.TryGetValue(key, out var value) ? value ?? "" : "";
        private static int DiagnosticBound(Dictionary<string, string> query, string key, int fallback, int max)
        {
            string value = DiagnosticQuery(query, key);
            if (value.Length == 0) return fallback;
            if (!int.TryParse(value, out int number) || number < 0 || number > max) throw new ArgumentException("Invalid " + key + ".");
            return number;
        }
        private static string DiagnosticId(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$")) throw new ArgumentException("An exact diagnostic identifier is required.");
            return id;
        }
        private static string DiagnosticTracePath(string id) => Path.Combine(DiagnosticRoot, "traces", DiagnosticId(id));
        private static Dictionary<string, object> DiagnosticMeta(string id)
        {
            var value = DiagnosticReadJson(Path.Combine(DiagnosticTracePath(id), "manifest.json"));
            if (value.Count == 0) throw new FileNotFoundException("This diagnostic capture is unavailable or was cleared.");
            if (ReadString(value, "status", "") == "in_progress" && !ActiveDiagnosticTraces.ContainsKey(id))
            {
                value["status"] = "interrupted";
                value["interruptionReason"] = "The server stopped before this response completed.";
            }
            if (ActiveDiagnosticTraces.TryGetValue(id, out var active))
            { value["elapsedMs"] = ReadLong(value, "responseElapsedMs", active.Timer.ElapsedMilliseconds); value["captureError"] = active.CaptureError; }
            value["conversationKey"] = DiagnosticConversationKey(value);
            return value;
        }
        private static string DiagnosticConversationKey(Dictionary<string, object> value)
        {
            string session = ReadString(value, "conversationId", "");
            if (session.Length == 0) session = ReadString(value, "mode", "") + ":" + ReadString(value, "heroId", "");
            return PromptContentHash(ReadString(value, "campaignId", "") + "|" + ReadString(value, "timelineId", "") + "|" + session).ToLowerInvariant().Substring(0, 32);
        }
        private static List<Dictionary<string, object>> DiagnosticManifestIndex()
        {
            var list = new List<Dictionary<string, object>>();
            string root = Path.Combine(DiagnosticRoot, "traces");
            if (!Directory.Exists(root)) return list;
            // Manifests are the rebuildable summary index; payloads are never opened
            // by list/search requests. Damaged entries cannot hide other captures.
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                try { list.Add(DiagnosticMeta(Path.GetFileName(directory))); }
                catch (Exception ex) { DiagnosticFailure(ex); }
            }
            return list;
        }
        private static List<Dictionary<string, object>> DiagnosticLegacyRows(string campaign)
        {
            if (string.IsNullOrWhiteSpace(campaign)) return new List<Dictionary<string, object>>();
            var rows = DiagnosticReadRecords(CampaignFile(campaign, "audit", "audit.jsonl"), FileLock, out string warning);
            if (warning.Length > 0) DiagnosticLastError = warning;
            return rows;
        }

        private static List<Dictionary<string, object>> DiagnosticReadRecords(string path, object gate, out string warning)
        {
            warning = "";
            var rows = new List<Dictionary<string, object>>();
            lock (gate)
            {
                if (!File.Exists(path)) return rows;
                foreach (string line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { var row = Json.Deserialize<Dictionary<string, object>>(line); if (row != null) rows.Add(row); }
                    catch { warning = "Some retained step records are incomplete or damaged. Valid records are shown; this trace is partial."; }
                }
            }
            return rows;
        }
        private static List<Dictionary<string, object>> DiagnosticLegacyIndex(string campaign)
        {
            return DiagnosticLegacyRows(campaign).Where(row => {
                string phase = ReadString(row, "phase", "");
                return phase.EndsWith(".response") && !phase.StartsWith("llm.") && !string.IsNullOrWhiteSpace(ReadString(ReadDictionary(row, "data"), "reply", ""));
            }).Select(row => {
                var data = ReadDictionary(row, "data");
                var player = ReadDictionary(data, "playerLine");
                var npc = ReadDictionary(data, "npcLine");
                var exchange = ReadDictionary(data, "conversationExchange");
                var meta = new Dictionary<string, object> {
                    ["traceId"] = "legacy-" + ReadString(row, "auditId", ""), ["campaignId"] = campaign,
                    ["correlationId"] = ReadString(row, "correlationId", ""), ["mode"] = ReadString(row, "mode", ""),
                    ["heroId"] = ReadString(row, "heroId", ""), ["heroName"] = ReadString(npc, "speaker", ""),
                    ["conversationId"] = FirstNonEmpty(ReadString(exchange, "sessionId", ""), ReadString(row, "eventId", "")),
                    ["playerText"] = ReadString(player, "text", ""), ["reply"] = ReadString(data, "reply", ""),
                    ["startedUtc"] = ReadString(row, "timestamp", ""), ["elapsedMs"] = ReadLong(row, "durationMs", 0),
                    ["status"] = ReadString(row, "status", "completed"), ["legacy"] = true, ["fullCapture"] = false,
                    ["evidenceNotice"] = "Partial historical audit. Original provider bytes were not retained; some steps or payloads may be missing or truncated."
                };
                meta["conversationKey"] = DiagnosticConversationKey(meta); return meta;
            }).ToList();
        }
        private static Dictionary<string, object> ConversationDiagnosticsListApi(Dictionary<string, string> query)
        {
            string campaign = DiagnosticQuery(query, "campaignId"), text = DiagnosticQuery(query, "search"), mode = DiagnosticQuery(query, "mode"), status = DiagnosticQuery(query, "status");
            var rows = DiagnosticManifestIndex();
            string legacyCampaign = FirstNonEmpty(campaign, LatestCampaignId());
            if (legacyCampaign.Length > 0)
            {
                var known = new HashSet<string>(rows.Select(x => ReadString(x, "correlationId", "")));
                rows.AddRange(DiagnosticLegacyIndex(legacyCampaign).Where(x => !known.Contains(ReadString(x, "correlationId", ""))));
            }
            var filtered = rows.Where(x => (campaign.Length == 0 || ReadString(x, "campaignId", "") == campaign)
                && (mode.Length == 0 || (mode == "other" ? string.IsNullOrWhiteSpace(ReadString(x, "reply", "")) && string.IsNullOrWhiteSpace(ReadString(x, "playerText", "")) : ReadString(x, "mode", "") == mode))
                && (status.Length == 0 || (status == "repaired" ? ReadInt(x, "repairCount", 0) > 0 : ReadString(x, "status", "") == status))
                && (text.Length == 0 || new[] { "heroName", "heroId", "playerText", "reply", "correlationId" }.Any(k => ReadString(x, k, "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0))
                && (DiagnosticQuery(query, "from") == "" || string.CompareOrdinal(ReadString(x, "startedUtc", ""), DiagnosticQuery(query, "from")) >= 0)
                && (DiagnosticQuery(query, "to") == "" || string.CompareOrdinal(ReadString(x, "startedUtc", ""), DiagnosticQuery(query, "to") + "T23:59:59.9999999Z") <= 0)).ToList();
            string key = DiagnosticQuery(query, "conversationKey");
            int offset = DiagnosticBound(query, "offset", 0, 10000000), limit = Math.Max(1, DiagnosticBound(query, "limit", 40, 100));
            var ordered = filtered.Where(x => key.Length == 0 || ReadString(x, "conversationKey", "") == key)
                .OrderBy(x => ReadString(x, "startedUtc", ""), StringComparer.Ordinal).ThenBy(x => ReadString(x, "traceId", ""), StringComparer.Ordinal).ToList();
            var conversations = filtered.GroupBy(x => ReadString(x, "conversationKey", "")).Select(g => {
                var last = g.OrderBy(x => ReadString(x, "startedUtc", ""), StringComparer.Ordinal).Last();
                return new Dictionary<string, object> { ["conversationKey"] = g.Key, ["campaignId"] = ReadString(last, "campaignId", ""),
                    ["title"] = FirstNonEmpty(ReadString(last, "heroName", ""), ReadString(last, "heroId", ""), "Other LLM activity"),
                    ["mode"] = ReadString(last, "mode", ""), ["lastUtc"] = ReadString(last, "startedUtc", ""), ["responseCount"] = g.Count(),
                    ["preview"] = LimitText(FirstNonEmpty(ReadString(last, "reply", ""), ReadString(last, "playerText", "")), 160) };
            }).OrderByDescending(x => ReadString(x, "lastUtc", ""), StringComparer.Ordinal).ToList();
            return new Dictionary<string, object> { ["ok"] = true, ["schema"] = ConversationDiagnosticSchema,
                ["conversations"] = conversations.Skip(offset).Take(limit).ToList(), ["conversationCount"] = conversations.Count,
                ["responses"] = key.Length == 0 ? new List<Dictionary<string, object>>() : ordered.Skip(offset).Take(limit).ToList(),
                ["responseCount"] = ordered.Count, ["offset"] = offset, ["limit"] = limit,
                ["campaigns"] = rows.Select(x => ReadString(x, "campaignId", "")).Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToList(),
                ["modes"] = rows.Select(x => ReadString(x, "mode", "")).Distinct().OrderBy(x => x).ToList(), ["error"] = DiagnosticLastError };
        }
        private static Dictionary<string, object> ConversationDiagnosticsTraceApi(Dictionary<string, string> query)
        {
            string id = DiagnosticQuery(query, "traceId");
            if (id.StartsWith("legacy-", StringComparison.Ordinal))
            {
                string campaign = DiagnosticQuery(query, "campaignId");
                var meta = DiagnosticLegacyIndex(campaign).FirstOrDefault(x => ReadString(x, "traceId", "") == id);
                if (meta == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Historical evidence is no longer retained." };
                string correlation = ReadString(meta, "correlationId", "");
                var steps = DiagnosticLegacyRows(campaign).Where(x => ReadString(x, "correlationId", "") == correlation || ReadString(x, "correlationId", "").StartsWith(correlation + "-", StringComparison.Ordinal))
                    .OrderBy(x => ReadString(x, "timestamp", ""), StringComparer.Ordinal).Select((x, i) => new Dictionary<string, object> {
                        ["stepId"] = ReadString(x, "auditId", ""), ["parentStepId"] = "", ["sequence"] = i,
                        ["kind"] = "legacy", ["title"] = DiagnosticPhaseTitle(ReadString(x, "phase", "")), ["status"] = ReadString(x, "status", ""),
                        ["durationMs"] = ReadLong(x, "durationMs", 0), ["explanation"] = ReadString(x, "summary", ""),
                        ["evidence"] = new[] { new Dictionary<string, object> { ["payloadId"] = ReadString(x, "auditId", ""), ["label"] = "Retained sanitized audit (may be truncated)" } }
                    }).ToList();
                return new Dictionary<string, object> { ["ok"] = true, ["schema"] = ConversationDiagnosticSchema, ["response"] = meta, ["steps"] = steps, ["complete"] = false };
            }
            var manifest = DiagnosticMeta(id);
            ActiveDiagnosticTraces.TryGetValue(id, out var active);
            var records = DiagnosticReadRecords(Path.Combine(DiagnosticTracePath(id), "steps.jsonl"), active?.Gate ?? DiagnosticExportLock, out string warning);
            if (warning.Length > 0) manifest["captureError"] = warning;
            var grouped = records.GroupBy(x => ReadString(x, "stepId", "")).Select(g => {
                var last = new Dictionary<string, object>(g.Last());
                last["sequence"] = ReadInt(g.First(), "sequence", 0);
                last["evidence"] = g.Where(x => ReadString(x, "payloadId", "").Length > 0).Select(x => new Dictionary<string, object> {
                    ["payloadId"] = ReadString(x, "payloadId", ""), ["label"] = ReadString(x, "status", "") == "in_progress" ? "Input / request" : "Result / decision"
                }).ToList();
                if (ReadString(last, "status", "") == "in_progress" && ReadString(manifest, "status", "") == "interrupted") last["status"] = "interrupted";
                return last;
            }).OrderBy(x => ReadInt(x, "sequence", 0)).ToList();
            return new Dictionary<string, object> { ["ok"] = true, ["schema"] = ConversationDiagnosticSchema, ["response"] = manifest,
                ["steps"] = grouped, ["complete"] = ReadBool(manifest, "fullCapture", false) && ReadString(manifest, "captureError", "").Length == 0,
                ["timingNote"] = "The response total is wall-clock time. Parent steps include their children; do not add nested durations." };
        }
        private static Dictionary<string, object> ConversationDiagnosticsPayloadApi(Dictionary<string, string> query)
        {
            string trace = DiagnosticQuery(query, "traceId"), id = DiagnosticId(DiagnosticQuery(query, "payloadId"));
            int offset = DiagnosticBound(query, "offset", 0, int.MaxValue), length = Math.Max(2, DiagnosticBound(query, "length", 12000, 12000));
            string text; Dictionary<string, object> meta;
            if (trace.StartsWith("legacy-", StringComparison.Ordinal))
            {
                var detail = ConversationDiagnosticsTraceApi(query);
                if (!ReadDictionaryList(detail, "steps").Any(x => ReadString(x, "stepId", "") == id)) throw new ArgumentException("The payload does not belong to this response.");
                var row = DiagnosticLegacyRows(DiagnosticQuery(query, "campaignId")).FirstOrDefault(x => ReadString(x, "auditId", "") == id);
                text = Json.Serialize(ReadDictionary(row, "data"));
                meta = new Dictionary<string, object> { ["complete"] = false, ["sanitizedLegacy"] = true, ["characters"] = text.Length };
            }
            else
            {
                string directory = Path.Combine(DiagnosticTracePath(trace), "payloads");
                meta = DiagnosticReadJson(Path.Combine(directory, id + ".json"));
                if (meta.Count == 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Full payload was not captured, capture failed, or it was cleared." };
                using (var file = File.OpenRead(Path.Combine(directory, id + ".txt.gz")))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Encoding.UTF8)) text = reader.ReadToEnd();
            }
            if (offset > text.Length || (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1]))) throw new ArgumentException("Invalid payload offset; use nextOffset.");
            int count = Math.Min(length, text.Length - offset);
            if (count > 0 && offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) count--;
            return new Dictionary<string, object> { ["ok"] = true, ["schema"] = ConversationDiagnosticSchema, ["traceId"] = trace, ["payloadId"] = id,
                ["offset"] = offset, ["nextOffset"] = offset + count, ["hasMore"] = offset + count < text.Length,
                ["content"] = text.Substring(offset, count), ["metadata"] = meta };
        }
        private static Dictionary<string, object> ConversationDiagnosticsCaptureApi(Dictionary<string, object> changes = null)
        {
            lock (DiagnosticSettingsLock)
            {
                if (changes != null)
                {
                    if (!changes.TryGetValue("enabled", out var enabled) || !(enabled is bool)) throw new ArgumentException("enabled must be a boolean.");
                    DiagnosticWriteJson(Path.Combine(DiagnosticRoot, "capture.json"), new Dictionary<string, object> {
                        ["enabled"] = enabled, ["changedUtc"] = DateTimeOffset.UtcNow.ToString("o"), ["retention"] = "until_cleared"
                    });
                }
                var settings = DiagnosticReadJson(Path.Combine(DiagnosticRoot, "capture.json"));
                long bytes = 0;
                if (Directory.Exists(DiagnosticRoot)) foreach (var path in Directory.EnumerateFiles(DiagnosticRoot, "*", SearchOption.AllDirectories))
                    try { bytes += new FileInfo(path).Length; } catch (IOException) { }
                return new Dictionary<string, object> { ["ok"] = true, ["schema"] = ConversationDiagnosticSchema,
                    ["enabled"] = ReadBool(settings, "enabled", false), ["changedUtc"] = ReadString(settings, "changedUtc", ""),
                    ["retention"] = "until_cleared", ["survivesRestart"] = true, ["storageBytes"] = bytes,
                    ["activeCaptures"] = ActiveDiagnosticTraces.Values.Count(x => x.FullCapture && !x.Closed), ["error"] = DiagnosticLastError,
                    ["note"] = "Changes apply to new responses. In-flight captures finish recording. Credentials are excluded." };
            }
        }
        private static Dictionary<string, object> ConversationDiagnosticsClearApi(Dictionary<string, object> payload)
        {
            var ids = ReadStringList(payload, "traceIds").Distinct().ToList();
            if (ReadString(payload, "confirmation", "") != "clear selected diagnostic captures" || ids.Count == 0 || ids.Count > 500) throw new ArgumentException("Select 1–500 exact completed captures and confirm their deletion.");
            foreach (var id in ids) { DiagnosticId(id); if (ActiveDiagnosticTraces.ContainsKey(id)) throw new InvalidOperationException("An in-flight capture cannot be cleared."); }
            int deleted = 0;
            lock (DiagnosticExportLock) foreach (var id in ids)
            {
                string target = DiagnosticTracePath(id); // Validated 32-character ID under this private store only.
                if (Directory.Exists(target)) { Directory.Delete(target, true); deleted++; }
            }
            return new Dictionary<string, object> { ["ok"] = true, ["deletedCaptures"] = deleted, ["campaignDataChanged"] = false };
        }
        private static void WriteConversationDiagnosticsExport(NetworkStream stream, Dictionary<string, string> query)
        {
            string key = DiagnosticQuery(query, "conversationKey"), traceId = DiagnosticQuery(query, "traceId");
            if (key.Length == 0 && traceId.Length == 0) throw new ArgumentException("Select a conversation or response to export.");
            if (key.Length > 0) DiagnosticId(key);
            if (traceId.Length > 0) DiagnosticId(traceId);
            var selected = DiagnosticManifestIndex().Where(x => traceId.Length > 0 ? ReadString(x, "traceId", "") == traceId : ReadString(x, "conversationKey", "") == key).ToList();
            if (selected.Count == 0) throw new FileNotFoundException("No full diagnostic records are retained for this selection.");
            string directory = Path.Combine(DiagnosticRoot, "exports"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var output = File.Create(path)) WriteConversationDiagnosticArchive(output, selected);
                WriteFile(stream, path, "application/zip");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static void WriteConversationDiagnosticArchive(Stream output, List<Dictionary<string, object>> selected)
        {
            lock (DiagnosticExportLock)
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var manifest = archive.CreateEntry("export.json");
                using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false))) writer.Write(Json.Serialize(new Dictionary<string, object> {
                    ["schema"] = ConversationDiagnosticSchema, ["exportedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["responses"] = selected, ["note"] = "Original provider content may include private NPC context and returned reasoning. Credentials are excluded. In-flight responses are snapshots."
                }));
                foreach (var item in selected)
                {
                    string id = ReadString(item, "traceId", ""), root = DiagnosticTracePath(id);
                    ActiveDiagnosticTraces.TryGetValue(id, out var active);
                    lock (active?.Gate ?? DiagnosticExportLock)
                        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(x => !x.EndsWith(".tmp")))
                            archive.CreateEntryFromFile(file, id + "/" + Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Fastest);
                }
            }
        }
    }
}
