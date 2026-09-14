using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Reign.LegacySqliteImporter;

internal static class Program
{
    private const string Confirmation = "import legacy Reign SQLite";

    private static int Main(string[] args)
    {
        try
        {
            var sourcePath = ReadArgument(args, "--source");
            var campaignId = ReadArgument(args, "--campaign-id");
            var confirmation = ReadArgument(args, "--confirm");
            if (!string.Equals(confirmation, Confirmation,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Exact confirmation is required: {Confirmation}");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException(
                    "The legacy SQLite database was not found.", sourcePath);
            if (!Regex.IsMatch(campaignId, "^[A-Za-z0-9_.-]{1,160}$"))
                throw new ArgumentException("campaign-id is invalid.");

            var receipt = Import(sourcePath, campaignId);
            Console.WriteLine(JsonSerializer.Serialize(receipt,
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false,
                error = exception.Message,
                sourcePreserved = true
            }));
            return 1;
        }
    }

    private static object Import(string sourcePath, string campaignId)
    {
        using var source = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(sourcePath),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
        using var destination = new NpgsqlConnection(BuildConnectionString());
        source.Open();
        destination.Open();

        var schema = ReadCampaignSchema(destination, campaignId);
        var tables = ReadSourceTables(source);
        var importedTables = 0;
        long sourceRows = 0;
        long destinationRows = 0;
        using var transaction = destination.BeginTransaction(
            IsolationLevel.Serializable);
        using (var lockCommand = new NpgsqlCommand(
                   "SELECT pg_advisory_xact_lock(hashtextextended(@campaign, 0));",
                   destination, transaction))
        {
            lockCommand.Parameters.AddWithValue("campaign", campaignId);
            lockCommand.ExecuteNonQuery();
        }

        foreach (var table in tables)
        {
            var sourceColumns = ReadSourceColumns(source, table);
            var destinationColumns = ReadDestinationColumns(
                destination, transaction, schema, table);
            var columns = sourceColumns
                .Where(destinationColumns.Contains)
                .ToArray();
            if (columns.Length == 0) continue;

            var targetCount = CountRows(destination, transaction, schema, table);
            if (targetCount != 0)
                throw new InvalidOperationException(
                    $"Target table {table} is not empty. Import requires a freshly initialized campaign schema.");

            var copied = CopyTable(source, destination, transaction,
                schema, table, columns);
            var verified = CountRows(destination, transaction, schema, table);
            if (copied != verified)
                throw new InvalidDataException(
                    $"Row-count verification failed for {table}: {copied} source rows, {verified} destination rows.");
            sourceRows += copied;
            destinationRows += verified;
            importedTables++;
        }

        transaction.Commit();
        return new
        {
            ok = true,
            campaignId,
            schema,
            importedTables,
            sourceRows,
            destinationRows,
            sourcePreserved = true,
            completedUtc = DateTimeOffset.UtcNow
        };
    }

    private static string BuildConnectionString()
    {
        return new NpgsqlConnectionStringBuilder
        {
            Host = ReadEnvironment("REIGN_DB_HOST", "127.0.0.1"),
            Port = int.TryParse(Environment.GetEnvironmentVariable(
                "REIGN_DB_PORT"), out var port) ? port : 5432,
            Database = "Reign",
            Username = ReadEnvironment("REIGN_DB_USER", "dwemer"),
            Password = ReadEnvironment("REIGN_DB_PASSWORD", "dwemer"),
            Pooling = false,
            Timeout = 15,
            CommandTimeout = 120,
            ApplicationName = "Reign Legacy SQLite Importer"
        }.ConnectionString;
    }

    private static string ReadCampaignSchema(
        NpgsqlConnection connection, string campaignId)
    {
        using var command = new NpgsqlCommand(@"
SELECT schema_name
FROM reign_meta.campaign_registry
WHERE campaign_id=@campaign;", connection);
        command.Parameters.AddWithValue("campaign", campaignId);
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException(
                "The target campaign schema does not exist. Initialize the campaign once with the PostgreSQL Reign server before importing.");
    }

    private static IReadOnlyList<string> ReadSourceTables(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT name, sql
FROM sqlite_master
WHERE type='table' AND name NOT LIKE 'sqlite_%'
ORDER BY name;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var sql = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (name.Equals("schema_meta", StringComparison.OrdinalIgnoreCase))
                continue;
            if (sql.Contains("USING fts", StringComparison.OrdinalIgnoreCase)
                || IsFtsShadowTable(name)) continue;
            if (IsIdentifier(name)) result.Add(name);
        }
        return result;
    }

    private static IReadOnlyList<string> ReadSourceColumns(
        SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteSqlite(table)});";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }

    private static HashSet<string> ReadDestinationColumns(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table)
    {
        using var command = new NpgsqlCommand(@"
SELECT column_name
FROM information_schema.columns
WHERE table_schema=@schema AND table_name=@table
ORDER BY ordinal_position;", connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static long CopyTable(
        SqliteConnection source,
        NpgsqlConnection destination,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        IReadOnlyList<string> columns)
    {
        using var select = source.CreateCommand();
        select.CommandText = "SELECT "
            + string.Join(",", columns.Select(QuoteSqlite))
            + " FROM " + QuoteSqlite(table) + ";";
        using var reader = select.ExecuteReader();
        using var insert = new NpgsqlCommand(
            "INSERT INTO " + QuotePostgreSql(schema) + "."
            + QuotePostgreSql(table) + " ("
            + string.Join(",", columns.Select(QuotePostgreSql))
            + ") VALUES ("
            + string.Join(",", columns.Select((_, index) => "@p" + index))
            + ");", destination, transaction);
        for (var index = 0; index < columns.Count; index++)
            insert.Parameters.Add(new NpgsqlParameter("p" + index,
                DBNull.Value));
        long copied = 0;
        while (reader.Read())
        {
            for (var index = 0; index < columns.Count; index++)
                insert.Parameters[index].Value = reader.IsDBNull(index)
                    ? DBNull.Value
                    : reader.GetValue(index);
            insert.ExecuteNonQuery();
            copied++;
        }
        return copied;
    }

    private static long CountRows(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table)
    {
        using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM " + QuotePostgreSql(schema) + "."
            + QuotePostgreSql(table) + ";", connection, transaction);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool IsFtsShadowTable(string name) =>
        Regex.IsMatch(name,
            "_(data|idx|content|docsize|config)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsIdentifier(string value) =>
        Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$");

    private static string QuoteSqlite(string value) =>
        "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string QuotePostgreSql(string value) =>
        "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string ReadArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name,
                    StringComparison.OrdinalIgnoreCase))
                return args[index + 1].Trim();
        throw new ArgumentException($"Required argument is missing: {name}");
    }

    private static string ReadEnvironment(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
