global using static ReignBetaServer.ServerCore;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web.Script.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace ReignBetaServer
{
    // Shared stateless helpers. Available to every type in the assembly through
    // the global using static above, so call sites stay unqualified.
    // Only stateless helpers belong here: no Program state, campaign caches or settings.
    internal static class ServerCore
    {
        internal static Dictionary<string, object> TestDict(params object[] pairs)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>();
            AddPairs(dict, pairs);
            return dict;
        }

        internal static void AddPairs(Dictionary<string, object> dict, params object[] pairs)
        {
            if (dict == null || pairs == null)
            {
                return;
            }

            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                string key = Convert.ToString(pairs[i], CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    dict[key] = pairs[i + 1];
                }
            }
        }

        internal static ArrayList TestList(params object[] items)
        {
            ArrayList list = new ArrayList();
            if (items != null)
            {
                foreach (object item in items)
                {
                    list.Add(item);
                }
            }

            return list;
        }

        internal static string ReadString(Dictionary<string, object> source, string key, string fallback)
        {
            return source != null && source.TryGetValue(key, out object value) && value != null ? Convert.ToString(value) : fallback;
        }

        internal static int ReadInt(Dictionary<string, object> source, string key, int fallback)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return fallback;
            }

            string text = Convert.ToString(value);
            if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }

            if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out parsed))
            {
                return parsed;
            }

            string digits = new string((text ?? string.Empty).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out parsed) ? parsed : fallback;
        }

        internal static long ReadLong(Dictionary<string, object> source, string key, long fallback)
        {
            return source != null && source.TryGetValue(key, out object value) && value != null && long.TryParse(Convert.ToString(value), out long parsed) ? parsed : fallback;
        }

        internal static bool ReadBool(Dictionary<string, object> source, string key, bool fallback)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return fallback;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            return bool.TryParse(Convert.ToString(value), out bool parsed) ? parsed : fallback;
        }

        internal static double ReadDouble(Dictionary<string, object> source, string key, double fallback)
        {
            return source != null && source.TryGetValue(key, out object value) && value != null && double.TryParse(Convert.ToString(value), out double parsed) ? parsed : fallback;
        }

        internal static string ReadFirstString(Dictionary<string, object> source, params string[] keys)
        {
            if (source == null || keys == null)
            {
                return string.Empty;
            }

            foreach (string key in keys)
            {
                string value = ReadString(source, key, "");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        internal static List<Dictionary<string, object>> ReadDictionaryList(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return new List<Dictionary<string, object>>();
            }

            if (value is ArrayList array)
            {
                return array.Cast<object>().OfType<Dictionary<string, object>>().ToList();
            }

            if (value is IEnumerable<object> enumerable)
            {
                return enumerable.OfType<Dictionary<string, object>>().ToList();
            }

            Dictionary<string, object> single = value as Dictionary<string, object>;
            return single == null ? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>> { single };
        }

        internal static List<string> ReadStringList(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return new List<string>();
            }

            if (value is ArrayList array)
            {
                return array.Cast<object>().Select(Convert.ToString).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }

            if (value is IEnumerable<object> enumerable)
            {
                return enumerable.Select(Convert.ToString).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }

            string text = Convert.ToString(value);
            return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text };
        }

        internal static Dictionary<string, object> ReadDictionary(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value))
            {
                return null;
            }

            return value as Dictionary<string, object>;
        }

        internal static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return "";
        }

        // Shared JSON serializer (formerly Program.Json). Stateless apart from its fixed limits.
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // JSON parsing and canonical serialization helpers.
        internal static string UnwrapJsonResponse(string text) => UnwrapJsonResponse(text, out _);

        internal static string UnwrapJsonResponse(string text, out int sourceOffset)
        {
            string original = text ?? "";
            sourceOffset = original.Length - original.TrimStart().Length;
            string source = original.Trim();
            if (!source.StartsWith("```", StringComparison.Ordinal)) return source;
            int line = source.IndexOf('\n');
            if (line < 0 || !source.EndsWith("```", StringComparison.Ordinal)) return source;
            string language = source.Substring(3, line - 3).Trim();
            if (language.Length != 0 && !language.Equals("json", StringComparison.OrdinalIgnoreCase)) return source;
            string body = source.Substring(line + 1, source.Length - line - 4);
            sourceOffset += line + 1 + body.Length - body.TrimStart().Length;
            return body.Trim();
        }

        // Generic storage/configuration parsing never searches nested values or
        // quoted JSON for a replacement document. Recovery belongs to the caller.
        internal static Dictionary<string, object> TryParseStrictJsonObject(string text)
        {
            string source = UnwrapJsonResponse(text);
            if (!source.StartsWith("{", StringComparison.Ordinal)) return null;
            try
            {
                using var document = JsonDocument.Parse(source);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (document.RootElement.EnumerateObject().Any(property => !names.Add(property.Name))) return null;
                return Json.Deserialize<Dictionary<string, object>>(source);
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        internal static Dictionary<string, object> TryParseJsonObject(string text) => TryParseStrictJsonObject(text);

        internal static string CanonicalJson(object value)
        {
            return Json.Serialize(CanonicalizeJsonValue(value));
        }

        internal static object CanonicalizeJsonValue(object value)
        {
            if (value == null || value is string || value is bool || value is char || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal) return value;
            if (value is IDictionary dictionary)
            {
                SortedDictionary<string, object> sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary) sorted[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = CanonicalizeJsonValue(entry.Value);
                return sorted;
            }
            if (value is IEnumerable enumerable)
            {
                List<object> list = new List<object>();
                foreach (object item in enumerable) list.Add(CanonicalizeJsonValue(item));
                return list;
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        // Stateless PostgreSQL helpers used by QuerySql, ExecuteSql and the transaction paths.
        internal static bool IsPostgreSqlSchemaMutation(string sql)
        {
            string value = (sql ?? string.Empty).TrimStart();
            return value.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("TRUNCATE ", StringComparison.OrdinalIgnoreCase);
        }

        internal static void AcquirePostgreSqlTransactionMutationLock(
            ReignDbConnection connection,
            ReignDbTransaction transaction = null)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    PlatformStore.SelectSql;
                command.CommandTimeout = 180;
                if (transaction != null)
                    command.Transaction = transaction;
                command.ExecuteNonQuery();
            }
        }

        internal static bool IsStalePostgreSqlPreparedPlan(Exception exception)
        {
            Npgsql.PostgresException postgres = exception as Npgsql.PostgresException;
            return postgres != null
                && postgres.SqlState == "0A000"
                && postgres.MessageText.IndexOf("cached plan",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static void ResetPostgreSqlPreparedPlans(
            ReignDbConnection connection)
        {
            Npgsql.NpgsqlConnection postgres = connection as Npgsql.NpgsqlConnection;
            if (postgres != null) postgres.UnprepareAll();
        }

        internal static bool IsTransientDatabaseContention(Exception exception)
        {
            Npgsql.PostgresException postgres = exception as Npgsql.PostgresException;
            return postgres != null
                && (postgres.SqlState == Npgsql.PostgresErrorCodes.SerializationFailure
                    || postgres.SqlState == Npgsql.PostgresErrorCodes.DeadlockDetected
                    || postgres.SqlState == Npgsql.PostgresErrorCodes.LockNotAvailable);
        }

        internal static void AddSqlParameters(ReignDbCommand command, Dictionary<string, object> parameters)
        {
            foreach (KeyValuePair<string, object> pair in parameters ?? new Dictionary<string, object>())
            {
                System.Data.Common.DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = ReignPostgreSqlDialect.ParameterName(command.Connection, pair.Key);
                parameter.Value = pair.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        internal static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        internal static double Clamp(double min, double max, double value) => Math.Max(min, Math.Min(max, value));

        internal static string Sha256Hex(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        internal static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes ?? new byte[0]))
                    .Replace("-", "")
                    .ToLowerInvariant();
            }
        }

        internal static int ParseInt(string value) { return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0; }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
        }
#endif

        internal static string HttpStatusText(int status) => status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 408 => "Request Timeout",
            411 => "Length Required", 413 => "Content Too Large", 414 => "URI Too Long", 429 => "Too Many Requests", 431 => "Request Header Fields Too Large",
            503 => "Service Unavailable", _ => "Server Error"
        };

        internal static void WriteText(NetworkStream stream, int status, string contentType, string text, string downloadName)
        {
            string statusText = HttpStatusText(status);
            byte[] body = Encoding.UTF8.GetBytes(text ?? string.Empty);
            string header = "HTTP/1.1 " + status + " " + statusText + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + (string.IsNullOrWhiteSpace(downloadName) ? string.Empty : "Content-Disposition: attachment; filename=\"" + downloadName + "\"\r\n")
                + "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
        }

        internal static void WriteText(NpgsqlBinaryImporter writer, string value)
        {
            writer.Write(value ?? string.Empty, NpgsqlDbType.Text);
        }

        // Shared helpers that used to sit in the test-case file; stateless apart from the Dialogue receipt copy.
        internal static string ArgValue(string[] args, string key, string fallback)
        {
            if (args == null)
            {
                return fallback;
            }

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return fallback;
        }

        internal static Dictionary<string, object> CloneDictionary(Dictionary<string, object> source)
        {
            if (source == null)
            {
                return new Dictionary<string, object>();
            }

            Dictionary<string, object> clone;
            try
            {
                clone = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(source)) ?? new Dictionary<string, object>();
            }
            catch
            {
                clone = new Dictionary<string, object>(source);
            }
            DialogueService.CopyConversationStructureReceipts(source, clone);
            return clone;
        }
    }
}
