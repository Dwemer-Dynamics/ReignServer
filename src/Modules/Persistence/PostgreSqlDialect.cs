using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using Npgsql;

namespace ReignBetaServer
{
    internal static class ReignPostgreSqlDialect
    {
        private static readonly Regex NamedParameter = new Regex(
            @"(?<!\$)\$([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex InsertOrReplace = new Regex(
            @"^\s*INSERT\s+OR\s+REPLACE\s+INTO\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\s*" +
            @"\((?<columns>[^)]+)\)\s*VALUES\s*\((?<values>.+)\)\s*;?\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex InsertOrIgnore = new Regex(
            @"^\s*INSERT\s+OR\s+IGNORE\s+INTO\s+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex VirtualFts = new Regex(
            @"^\s*CREATE\s+VIRTUAL\s+TABLE\s+IF\s+NOT\s+EXISTS\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\s+" +
            @"USING\s+fts5\s*\((?<columns>.+)\)\s*;?\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex PragmaTableInfo = new Regex(
            @"^\s*PRAGMA\s+table_info\s*\(\s*(?<table>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*;?\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex SqliteMasterSource = new Regex(
            @"\bFROM\s+sqlite_master\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex NestedNumericClamp = new Regex(
            @"\bMAX\s*\(\s*(?<low>-?\d+(?:\.\d+)?)\s*,\s*MIN\s*\(\s*(?<high>-?\d+(?:\.\d+)?)\s*,\s*(?<value>[^()]+)\)\s*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex FlatScalarMin = new Regex(
            @"\bMIN\s*\(\s*(?<left>[^(),]+)\s*,\s*(?<right>[^()]+)\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex FlatScalarMax = new Regex(
            @"\bMAX\s*\(\s*(?<left>[^(),]+)\s*,\s*(?<right>[^()]+)\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex JsonExtractText = new Regex(
            @"\bjson_extract\s*\(\s*(?<column>[A-Za-z_][A-Za-z0-9_\.]*)\s*,\s*'\$\.(?<key>[A-Za-z_][A-Za-z0-9_]*)'\s*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex FtsMatch = new Regex(
            @"\b(?<table>conversation_turn_fts|memory_fts|event_fts|summary_fts|world_history_fts)\s+MATCH\s+(?<query>[$@:][A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly IReadOnlyDictionary<string, string[]> FtsSearchColumns =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["conversation_turn_fts"] = new[] { "text" },
                ["memory_fts"] = new[] { "text", "tags", "entities" },
                ["event_fts"] = new[] { "text", "tags", "entities" },
                ["summary_fts"] = new[] { "text", "tags", "entities" },
                ["world_history_fts"] = new[]
                {
                    "text", "entities", "roles", "location", "event_type"
                }
            };
        private static readonly ConcurrentDictionary<string, string[]> ConflictColumns =
            new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public static bool IsPostgreSql(DbConnection connection)
        {
            return connection is NpgsqlConnection;
        }

        public static string Normalize(DbConnection connection, string sql)
        {
            if (!IsPostgreSql(connection) || string.IsNullOrWhiteSpace(sql))
                return sql;

            string normalized = sql.Trim();
            normalized = Regex.Replace(
                normalized,
                @"\s+INDEXED\s+BY\s+[A-Za-z_][A-Za-z0-9_]*",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            normalized = Regex.Replace(
                normalized,
                @"\browid\b",
                "ctid",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (Regex.IsMatch(normalized, @"^BEGIN\s+(IMMEDIATE|EXCLUSIVE)\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return "BEGIN;";
            Match standaloneSavepoint = Regex.Match(
                normalized,
                @"^SAVEPOINT\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (standaloneSavepoint.Success)
                return "BEGIN; SAVEPOINT " + standaloneSavepoint.Groups["name"].Value + ";";
            Match standaloneRelease = Regex.Match(
                normalized,
                @"^RELEASE(?:\s+SAVEPOINT)?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (standaloneRelease.Success)
                return "RELEASE SAVEPOINT " + standaloneRelease.Groups["name"].Value + "; COMMIT;";
            Match rollbackTo = Regex.Match(
                normalized,
                @"^ROLLBACK\s+TO(?:\s+SAVEPOINT)?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (rollbackTo.Success)
                return "ROLLBACK TO SAVEPOINT " + rollbackTo.Groups["name"].Value + ";";
            if (normalized.StartsWith("PRAGMA ", StringComparison.OrdinalIgnoreCase))
            {
                Match tableInfo = PragmaTableInfo.Match(normalized);
                if (tableInfo.Success)
                {
                    return @"SELECT column_name AS name,
       ordinal_position - 1 AS cid,
       data_type AS type,
       CASE WHEN is_nullable='NO' THEN 1 ELSE 0 END AS notnull,
       column_default AS dflt_value,
       0 AS pk
FROM information_schema.columns
WHERE table_schema=current_schema() AND table_name='"
                        + tableInfo.Groups["table"].Value.Replace("'", "''")
                        + "' ORDER BY ordinal_position;";
                }
                return "SELECT 1;";
            }

            // A large amount of older Reign schema code asks sqlite_master only
            // whether a table or index exists. Keep that code backend-neutral
            // while the PostgreSQL repositories are being made explicit. The
            // compatibility source is scoped to the current campaign schema and
            // deliberately reports only relations visible there.
            if (SqliteMasterSource.IsMatch(normalized))
            {
                normalized = SqliteMasterSource.Replace(
                    normalized,
                    @"FROM (
SELECT CASE c.relkind
         WHEN 'i' THEN 'index'
         WHEN 'v' THEN 'view'
         WHEN 'm' THEN 'view'
         ELSE 'table'
       END AS type,
       c.relname AS name,
       CASE WHEN c.relkind='i' THEN COALESCE(t.relname,c.relname) ELSE c.relname END AS tbl_name,
       CASE WHEN c.relkind='i' THEN pg_get_indexdef(c.oid) ELSE NULL END AS sql
FROM pg_class c
JOIN pg_namespace n ON n.oid=c.relnamespace
LEFT JOIN pg_index ix ON c.relkind='i' AND ix.indexrelid=c.oid
LEFT JOIN pg_class t ON t.oid=ix.indrelid
WHERE n.nspname=current_schema()
  AND c.relkind IN ('r','p','i','v','m')
UNION ALL
SELECT 'trigger' AS type,
       trigger.tgname AS name,
       relation.relname AS tbl_name,
       pg_get_triggerdef(trigger.oid) AS sql
FROM pg_trigger trigger
JOIN pg_class relation ON relation.oid=trigger.tgrelid
JOIN pg_namespace trigger_namespace
  ON trigger_namespace.oid=relation.relnamespace
WHERE trigger_namespace.nspname=current_schema()
  AND NOT trigger.tgisinternal
) AS sqlite_master");
            }

            Match fts = VirtualFts.Match(normalized);
            if (fts.Success)
                return BuildFtsTable(fts);

            if (normalized.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase))
            {
                normalized = Regex.Replace(normalized, @"\bINTEGER\b", "bigint", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\bREAL\b", "double precision", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\bBLOB\b", "bytea", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\s+WITHOUT\s+ROWID\b", string.Empty, RegexOptions.IgnoreCase);
            }

            Match replace = InsertOrReplace.Match(normalized);
            if (replace.Success)
                normalized = BuildUpsert(connection, replace);
            else if (InsertOrIgnore.IsMatch(normalized))
            {
                normalized = InsertOrIgnore.Replace(normalized, "INSERT INTO ", 1);
                normalized = AppendBeforeTerminator(normalized, " ON CONFLICT DO NOTHING");
            }

            normalized = normalized
                .Replace("strftime('%s','now')", "CAST(EXTRACT(EPOCH FROM clock_timestamp()) AS bigint)")
                .Replace("strftime(\"%s\",\"now\")", "CAST(EXTRACT(EPOCH FROM clock_timestamp()) AS bigint)");
            normalized = JsonExtractText.Replace(
                normalized,
                "(CAST(${column} AS jsonb) ->> '${key}')");
            normalized = FtsMatch.Replace(normalized, match =>
                BuildFtsPredicate(match.Groups["table"].Value,
                    match.Groups["query"].Value));
            normalized = RewriteScalarMinMax(normalized);
            return NamedParameter.Replace(normalized, "@$1");
        }

        public static string ParameterName(DbConnection connection, string name)
        {
            string normalized = (name ?? string.Empty).Trim();
            while (normalized.StartsWith("$", StringComparison.Ordinal)
                   || normalized.StartsWith("@", StringComparison.Ordinal)
                   || normalized.StartsWith(":", StringComparison.Ordinal))
                normalized = normalized.Substring(1);
            return IsPostgreSql(connection) ? normalized : "$" + normalized;
        }

        private static string BuildFtsTable(Match match)
        {
            string table = match.Groups["table"].Value;
            string[] columns = SplitCommaList(match.Groups["columns"].Value)
                .Select(value => Regex.Replace(value, @"\s+UNINDEXED\s*$", string.Empty, RegexOptions.IgnoreCase).Trim())
                .Where(value => Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                .ToArray();
            if (columns.Length == 0)
                throw new InvalidOperationException("FTS compatibility table has no valid columns: " + table);
            string searchable = BuildFtsDocumentExpression(table,
                FtsSearchColumns.TryGetValue(table, out string[] configured)
                    ? configured.Where(column => columns.Contains(column,
                        StringComparer.OrdinalIgnoreCase)).ToArray()
                    : columns.Skip(1).ToArray());
            return "CREATE TABLE IF NOT EXISTS " + table + " ("
                + string.Join(",", columns.Select(column => column
                    + " text NOT NULL DEFAULT ''")) + ");"
                + "CREATE INDEX IF NOT EXISTS idx_" + table
                + "_search ON " + table + " USING GIN (" + searchable + ");";
        }

        private static string BuildFtsPredicate(string table, string query)
        {
            if (!FtsSearchColumns.TryGetValue(table, out string[] columns))
                throw new InvalidOperationException(
                    "Unknown PostgreSQL full-text table: " + table);
            return BuildFtsDocumentExpression(table, columns)
                + " @@ websearch_to_tsquery('simple', " + query + ")";
        }

        private static string BuildFtsDocumentExpression(
            string table, IEnumerable<string> columns)
        {
            string[] safeColumns = (columns ?? Enumerable.Empty<string>())
                .Where(column => Regex.IsMatch(column ?? string.Empty,
                    @"^[A-Za-z_][A-Za-z0-9_]*$"))
                .ToArray();
            if (safeColumns.Length == 0)
                return "to_tsvector('simple', '')";
            string document = string.Join(" || ' ' || ", safeColumns.Select(
                column => "COALESCE(" + table + "." + column + ",'')"));
            return "to_tsvector('simple', " + document + ")";
        }

        private static string BuildUpsert(DbConnection connection, Match match)
        {
            string table = match.Groups["table"].Value;
            string[] columns = SplitCommaList(match.Groups["columns"].Value)
                .Select(value => value.Trim())
                .ToArray();
            string[] conflicts = ResolveConflictColumns(connection, table);
            if (conflicts.Length == 0)
                throw new InvalidOperationException("INSERT OR REPLACE requires a primary or unique key on " + table + ".");

            string[] updates = columns
                .Where(column => !conflicts.Contains(column, StringComparer.OrdinalIgnoreCase))
                .Select(column => column + "=excluded." + column)
                .ToArray();
            string action = updates.Length == 0
                ? "DO NOTHING"
                : "DO UPDATE SET " + string.Join(",", updates);
            return "INSERT INTO " + table + "(" + string.Join(",", columns) + ") VALUES("
                + match.Groups["values"].Value + ") ON CONFLICT("
                + string.Join(",", conflicts) + ") " + action + ";";
        }

        private static string[] ResolveConflictColumns(DbConnection connection, string table)
        {
            string cacheKey = connection.Database + "|" + CurrentSchema(connection) + "|" + table;
            return ConflictColumns.GetOrAdd(cacheKey, ignored =>
            {
                const string sql = @"
WITH chosen AS (
    SELECT i.indrelid, i.indkey
    FROM pg_index i
    JOIN pg_class t ON t.oid=i.indrelid
    JOIN pg_namespace n ON n.oid=t.relnamespace
    WHERE n.nspname=current_schema()
      AND t.relname=@table
      AND (i.indisprimary OR i.indisunique)
    ORDER BY i.indisprimary DESC, i.indexrelid
    LIMIT 1
)
SELECT a.attname
FROM chosen
JOIN unnest(chosen.indkey) WITH ORDINALITY AS key(attnum, ordinal_position) ON true
JOIN pg_attribute a ON a.attrelid=chosen.indrelid AND a.attnum=key.attnum
ORDER BY key.ordinal_position;";
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    DbParameter parameter = command.CreateParameter();
                    parameter.ParameterName = "table";
                    parameter.Value = table;
                    command.Parameters.Add(parameter);
                    using (DbDataReader reader = command.ExecuteReader())
                    {
                        List<string> columns = new List<string>();
                        while (reader.Read())
                        {
                            // Rows are ordered with the primary key first. Reign tables
                            // use a single primary/unique constraint for replacement.
                            string name = reader.GetString(0);
                            if (!columns.Contains(name, StringComparer.OrdinalIgnoreCase))
                                columns.Add(name);
                        }
                        return columns.ToArray();
                    }
                }
            });
        }

        private static string CurrentSchema(DbConnection connection)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT current_schema();";
                return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
            }
        }

        private static string AppendBeforeTerminator(string sql, string suffix)
        {
            string trimmed = sql.TrimEnd();
            if (trimmed.EndsWith(";", StringComparison.Ordinal))
                return trimmed.Substring(0, trimmed.Length - 1) + suffix + ";";
            return trimmed + suffix;
        }

        private static string RewriteScalarMinMax(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return sql;
            System.Text.StringBuilder output = new System.Text.StringBuilder(sql.Length + 32);
            for (int index = 0; index < sql.Length;)
            {
                Match function = Regex.Match(
                    sql.Substring(index),
                    @"^(?<name>MIN|MAX)\s*\(",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!function.Success)
                {
                    output.Append(sql[index++]);
                    continue;
                }

                int open = index + function.Length - 1;
                int depth = 1;
                bool quoted = false;
                int commas = 0;
                int close = open + 1;
                for (; close < sql.Length && depth > 0; close++)
                {
                    char current = sql[close];
                    if (current == '\'' && (close == 0 || sql[close - 1] != '\\'))
                        quoted = !quoted;
                    if (quoted) continue;
                    if (current == '(') depth++;
                    else if (current == ')') depth--;
                    else if (current == ',' && depth == 1) commas++;
                }
                if (depth != 0)
                {
                    output.Append(sql[index++]);
                    continue;
                }

                int closingIndex = close - 1;
                string arguments = sql.Substring(open + 1, closingIndex - open - 1);
                string name = function.Groups["name"].Value;
                output.Append(commas > 0
                    ? (name.Equals("MIN", StringComparison.OrdinalIgnoreCase) ? "LEAST" : "GREATEST")
                    : name);
                output.Append('(');
                output.Append(RewriteScalarMinMax(arguments));
                output.Append(')');
                index = closingIndex + 1;
            }
            return output.ToString();
        }

        private static string[] SplitCommaList(string value)
        {
            // Reign's compatibility inserts and FTS declarations use flat column
            // lists. Keeping this parser deliberately strict prevents rewriting
            // arbitrary SQL expressions as identifiers.
            return (value ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
