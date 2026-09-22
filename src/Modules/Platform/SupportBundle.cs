using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const long SupportFileLimit = 8L * 1024 * 1024;
        private const long SupportTotalLimit = 64L * 1024 * 1024;
        private const int SupportFileCountLimit = 200;
        private static readonly Regex SupportSecretField = new Regex(
            "(?i)(\\\"?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|password|client[_-]?secret|qdrant[_-]?api[_-]?key)\\\"?\\s*[:=]\\s*\\\"?)[^\\\",\\s}]+",
            RegexOptions.Compiled);
        private static readonly Regex SupportBearer = new Regex(
            "(?i)\\bBearer\\s+[A-Za-z0-9._~+/-]{8,}", RegexOptions.Compiled);
        private static readonly Regex SupportKnownKey = new Regex(
            "\\b(?:sk-[A-Za-z0-9_-]{12,}|AIza[A-Za-z0-9_-]{20,})\\b", RegexOptions.Compiled);
        private static readonly Regex SupportUrlCredential = new Regex(
            "(?i)(https?://)[^/@\\s:]+:[^/@\\s]+@", RegexOptions.Compiled);

        private static string RedactSupportText(string value)
        {
            string text = value ?? string.Empty;
            text = SupportSecretField.Replace(text, "$1[REDACTED]");
            text = SupportBearer.Replace(text, "Bearer [REDACTED]");
            text = SupportKnownKey.Replace(text, "[REDACTED_KEY]");
            return SupportUrlCredential.Replace(text, "$1[REDACTED]@");
        }

        private static void WriteSupportBundle(NetworkStream stream, Dictionary<string, object> payload)
        {
            string description = ReadString(payload, "description", "").Trim();
            if (description.Length < 10 || description.Length > 4000)
            {
                WriteJson(stream, 400, new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Describe the issue in 10 to 4000 characters before downloading."
                });
                return;
            }

            string requestedCampaign = ReadString(payload, "campaignId", "").Trim();
            if (requestedCampaign.Length > 120 || requestedCampaign == "." || requestedCampaign == ".."
                || (requestedCampaign.Length > 0 && !Regex.IsMatch(requestedCampaign, "^[A-Za-z0-9_][A-Za-z0-9_.-]*$")))
            {
                WriteJson(stream, 400, new Dictionary<string, object> { ["ok"] = false, ["error"] = "Invalid campaign ID." });
                return;
            }

            string campaignId = ResolveLogCampaignId(requestedCampaign);
            byte[] body;
            try
            {
                body = CreateSupportBundle(payload, campaignId);
            }
            catch (InvalidDataException)
            {
                WriteJson(stream, 400, new Dictionary<string, object> { ["ok"] = false, ["error"] = "A selected screenshot is invalid or too large." });
                return;
            }
            catch (Exception ex)
            {
                LogOperational("support_bundle.failed", new Dictionary<string, object> { ["errorType"] = ex.GetType().Name });
                WriteJson(stream, 500, new Dictionary<string, object> { ["ok"] = false, ["error"] = "Could not create the support bundle. Check server logs." });
                return;
            }

            string filename = "reign-support-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip";
            string header = "HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: " + body.Length
                + "\r\nContent-Disposition: attachment; filename=\"" + filename
                + "\"\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
        }

        private static byte[] CreateSupportBundle(Dictionary<string, object> payload, string campaignId)
        {
            DateTime capturedUtc = DateTime.UtcNow;
            List<Dictionary<string, object>> files = new List<Dictionary<string, object>>();
            long collectedBytes = 0;
            using (MemoryStream output = new MemoryStream())
            {
                using (ZipArchive zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    string description = RedactSupportText(ReadString(payload, "description", "").Trim());
                    string report = "Reign Alpha issue report\nCaptured UTC: " + capturedUtc.ToString("o")
                        + "\nCampaign ID: " + campaignId + "\n\nIssue\n" + description
                        + "\n\nSteps to reproduce\n" + RedactSupportText(LimitText(ReadString(payload, "steps", ""), 4000))
                        + "\n\nExpected result\n" + RedactSupportText(LimitText(ReadString(payload, "expected", ""), 2000))
                        + "\n\nActual result\n" + RedactSupportText(LimitText(ReadString(payload, "actual", ""), 2000))
                        + "\n\nApproximate occurrence time\n" + RedactSupportText(LimitText(ReadString(payload, "occurredAt", ""), 120))
                        + "\n\nNotes\n" + RedactSupportText(LimitText(ReadString(payload, "notes", ""), 2000)) + "\n";
                    AddSupportEntry(zip, "issue-report.txt", Encoding.UTF8.GetBytes(report), files, "generated", false);
                    AddSupportScreenshots(zip, payload, files, ref collectedBytes);
                    Dictionary<string, object> settings = LoadSettings();
                    AddSupportEntry(zip, "system.json", Encoding.UTF8.GetBytes(RedactSupportText(Json.Serialize(new Dictionary<string, object>
                    {
                        ["serverVersion"] = Reign.Core.Contracts.Platform.ReignInstallation.TryLoadCurrent()?.Version ?? "development",
                        ["protocolVersion"] = Reign.Core.Contracts.Platform.ReignInstallation.SupportedProtocol,
                        ["databaseSchemaVersion"] = ReignPostgreSqlStorage.DatabaseSchemaVersion,
                        ["requiredDatabaseSchemaVersion"] = ReignPostgreSqlStorage.RequiredDatabaseSchemaVersion,
                        ["runtime"] = Environment.Version.ToString(),
                        ["operatingSystem"] = Environment.OSVersion.VersionString,
                        ["settingsRevision"] = ReadString(settings, "settingsRevision", ""),
                        ["llmProvider"] = NormalizeLlmProvider(ReadString(settings, "llmProvider", OpenAiCompatibleProvider)),
                        ["requestLoggingEnabled"] = ReadBool(settings, "enableRequestLogging", true),
                        ["llmLoggingEnabled"] = ReadBool(settings, "enableLlmLogging", true)
                    }))), files, "generated", false);

                    AddSupportFile(zip, Path.Combine(LogsDir, "server.log"), "server/server.log", files, ref collectedBytes);
                    AddSupportFile(zip, Path.Combine(LogsDir, "server-log.jsonl"), "server/server-log.jsonl", files, ref collectedBytes);
                    AddSupportFile(zip, Path.Combine(LogsDir, "llm-log.jsonl"), "server/llm-log.jsonl", files, ref collectedBytes);
                    AddSupportFile(zip, "/var/log/apache2/reign_error.log", "web/reign_error.log", files, ref collectedBytes);
                    AddSupportFile(zip, "/var/log/apache2/reign_access.log", "web/reign_access.log", files, ref collectedBytes);

                    string campaignRoot = CampaignDirectory(campaignId);
                    foreach (string relative in new[] { "audit/audit.jsonl", "actions/actions.jsonl", "actions/action-queue.json", "world/events.jsonl", "world/memories.jsonl" })
                        AddSupportFile(zip, Path.Combine(campaignRoot, relative.Replace('/', Path.DirectorySeparatorChar)),
                            "campaign/" + relative, files, ref collectedBytes);
                    string eventRoot = Path.Combine(campaignRoot, "events");
                    if (Directory.Exists(eventRoot))
                    {
                        string[] transcripts = Directory.EnumerateFiles(eventRoot, "transcript.jsonl", new EnumerationOptions
                            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                            .OrderByDescending(File.GetLastWriteTimeUtc).Take(101).ToArray();
                        foreach (string path in transcripts.Take(100))
                        {
                            string relative = Path.GetRelativePath(eventRoot, path).Replace('\\', '/');
                            AddSupportFile(zip, path, "campaign/events/" + relative, files, ref collectedBytes);
                        }
                        if (transcripts.Length > 100)
                            AddSupportUnavailable(files, "campaign/events/older-transcripts", "only the 100 most recent event transcripts are included");
                    }

                    // The installation bridge is read only to locate the deployed Windows module.
                    // Its contents and native-generator path never enter the archive.
                    string bridgePath = Path.Combine(DataDir, "windows-bridge.json");
                    try
                    {
                        Dictionary<string, object> bridge = ReadJsonObject(bridgePath);
                        string modules = ReadString(bridge, "bannerlordModules", "");
                        if (Path.IsPathRooted(modules))
                        {
                            string clientLog = Path.Combine(modules, "ReignBeta", "logs", "reignbeta.log");
                            AddSupportFile(zip, clientLog, "client/reignbeta.log", files, ref collectedBytes);
                        }
                        else AddSupportUnavailable(files, "client/reignbeta.log", "Windows module bridge unavailable");
                    }
                    catch (Exception ex) { AddSupportUnavailable(files, "client/reignbeta.log", ex.GetType().Name); }

                    // Native logs are optional on Linux hosts without a mounted Windows drive.
                    string nativeRoot = "/mnt/c/ProgramData/Mount and Blade II Bannerlord/logs";
                    if (Directory.Exists(nativeRoot))
                    {
                        foreach (string path in Directory.EnumerateFiles(nativeRoot, "rgl_log*.txt")
                            .OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
                            AddSupportFile(zip, path, "native/" + Path.GetFileName(path), files, ref collectedBytes);
                    }
                    else AddSupportUnavailable(files, "native/rgl_log*.txt", "native log directory unavailable");

                    Dictionary<string, object> manifest = new Dictionary<string, object>
                    {
                        ["schema"] = "reign-support-bundle-v1",
                        ["capturedUtc"] = capturedUtc.ToString("o"),
                        ["campaignId"] = campaignId,
                        ["serverVersion"] = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".version_number.txt"))
                            ? File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".version_number.txt")).Trim() : "unknown",
                        ["fileLimitBytes"] = SupportFileLimit,
                        ["totalLimitBytes"] = SupportTotalLimit,
                        ["redaction"] = "Text secrets are replaced. Screenshots are included unchanged. Review before sharing; gameplay text and images may contain personal information.",
                        ["files"] = files
                    };
                    AddSupportEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(Json.Serialize(manifest)), null, "generated", false);
                }
                return output.ToArray();
            }
        }

        private static void AddSupportFile(ZipArchive zip, string path, string entryName,
            List<Dictionary<string, object>> manifest, ref long collectedBytes)
        {
            if (manifest.Count >= SupportFileCountLimit) { AddSupportUnavailable(manifest, entryName, "file count limit reached"); return; }
            try
            {
                if (!File.Exists(path)) { AddSupportUnavailable(manifest, entryName, "missing or unreadable"); return; }
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                { AddSupportUnavailable(manifest, entryName, "linked file excluded"); return; }
                long remaining = SupportTotalLimit - collectedBytes;
                if (remaining <= 0) { AddSupportUnavailable(manifest, entryName, "bundle size limit reached"); return; }
                byte[] bytes;
                long originalLength;
                using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    originalLength = input.Length;
                    int count = (int)Math.Min(originalLength, Math.Min(SupportFileLimit, remaining));
                    input.Seek(-count, SeekOrigin.End);
                    bytes = new byte[count];
                    int read = 0;
                    while (read < count)
                    {
                        int n = input.Read(bytes, read, count - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != count) Array.Resize(ref bytes, read);
                }
                collectedBytes += bytes.Length;
                string content = Encoding.UTF8.GetString(bytes);
                if (originalLength > bytes.Length)
                {
                    int firstLineEnd = content.IndexOf('\n');
                    content = firstLineEnd >= 0 ? content.Substring(firstLineEnd + 1) : string.Empty;
                }
                AddSupportEntry(zip, entryName, Encoding.UTF8.GetBytes(RedactSupportText(content)), manifest,
                    "included", originalLength > bytes.Length, originalLength);
            }
            catch (Exception ex) { AddSupportUnavailable(manifest, entryName, ex.GetType().Name); }
        }

        private static void AddSupportScreenshots(ZipArchive zip, Dictionary<string, object> payload,
            List<Dictionary<string, object>> manifest, ref long collectedBytes)
        {
            if (payload == null || !payload.TryGetValue("screenshots", out object value))
                return;
            if (!(value is IEnumerable enumerable) || value is string)
                throw new InvalidDataException("Invalid screenshot list.");
            List<object> screenshots = enumerable.Cast<object>().ToList();
            if (screenshots.Count > 2) throw new InvalidDataException("Too many screenshots.");
            for (int i = 0; i < screenshots.Count; i++)
            {
                Dictionary<string, object> screenshot = screenshots[i] as Dictionary<string, object>;
                string kind = ReadString(screenshot, "type", "");
                string extension = kind == "image/png" ? ".png" : kind == "image/jpeg" ? ".jpg" : kind == "image/webp" ? ".webp" : "";
                string data = ReadString(screenshot, "data", "");
                if (extension.Length == 0 || data.Length > 3 * 1024 * 1024)
                    throw new InvalidDataException("Unsupported screenshot format or size.");
                byte[] bytes = Convert.FromBase64String(data);
                if (bytes.Length > 2 * 1024 * 1024 || bytes.Length < 12 || !SupportImageSignature(bytes, kind))
                    throw new InvalidDataException("Invalid screenshot content.");
                collectedBytes += bytes.Length;
                AddSupportEntry(zip, "screenshots/screenshot-" + (i + 1) + extension,
                    bytes, manifest, "user-supplied-unredacted", false, bytes.Length);
            }
        }

        private static bool SupportImageSignature(byte[] bytes, string kind)
        {
            if (kind == "image/png") return bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71;
            if (kind == "image/jpeg") return bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
            return kind == "image/webp" && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP";
        }

        private static void AddSupportEntry(ZipArchive zip, string name, byte[] data,
            List<Dictionary<string, object>> manifest, string status, bool truncated, long originalBytes = 0)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream target = entry.Open()) target.Write(data, 0, data.Length);
            if (manifest != null)
            {
                using (SHA256 hash = SHA256.Create())
                    manifest.Add(new Dictionary<string, object>
                    {
                        ["path"] = name, ["status"] = status, ["truncated"] = truncated,
                        ["sourceBytes"] = originalBytes, ["exportBytes"] = data.Length,
                        ["sha256"] = BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant()
                    });
            }
        }

        private static void AddSupportUnavailable(List<Dictionary<string, object>> manifest, string name, string reason)
        {
            manifest.Add(new Dictionary<string, object> { ["path"] = name, ["status"] = "unavailable", ["reason"] = reason });
        }
    }
}
