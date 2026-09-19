using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Npgsql;
using Reign.Core.Contracts.Platform;

namespace ReignBetaServer
{
    internal sealed class ReignPostgreSqlOptions
    {
        public const string RequiredDatabaseName = "reign";
        public const string ValidationDatabaseName = "ReignValidation";
        public const string RequiredEncoding = "UTF8";

        public string Host { get; private set; }
        public int Port { get; private set; }
        public string Database { get; private set; }
        public string Username { get; private set; }
        public string Password { get; private set; }
        public int MaximumPoolSize { get; private set; }
        public bool IsValidation { get; private set; }

        public static ReignPostgreSqlOptions FromEnvironment()
        {
            string database = ReadEnvironment("REIGN_DB_NAME", RequiredDatabaseName);
            bool validation = string.Equals(
                ReadEnvironment("REIGN_VALIDATION_MODE", "0"),
                "1",
                StringComparison.Ordinal);
            string permittedDatabase = validation
                ? ValidationDatabaseName
                : RequiredDatabaseName;
            if (!string.Equals(database, permittedDatabase, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    validation
                        ? "Reign validation requires the isolated PostgreSQL database to be named exactly '"
                            + ValidationDatabaseName + "'."
                        : "Reign requires the PostgreSQL database to be named exactly '"
                            + RequiredDatabaseName + "'.");
            }

            return new ReignPostgreSqlOptions
            {
                Host = ReadEnvironment("REIGN_DB_HOST", "127.0.0.1"),
                Port = ReadIntEnvironment("REIGN_DB_PORT", 5432, 1, 65535),
                Database = database,
                Username = ReadEnvironment("REIGN_DB_USER", "dwemer"),
                Password = ReadEnvironment("REIGN_DB_PASSWORD", "dwemer"),
                IsValidation = validation,
                MaximumPoolSize = ReadIntEnvironment(
                    "REIGN_DB_MAX_POOL_SIZE",
                    Math.Max(32, Environment.ProcessorCount * 4),
                    8,
                    512)
            };
        }

        public string BuildConnectionString(string databaseOverride = null)
        {
            NpgsqlConnectionStringBuilder builder = new NpgsqlConnectionStringBuilder
            {
                Host = Host,
                Port = Port,
                Database = databaseOverride ?? Database,
                Username = Username,
                Password = Password,
                Pooling = true,
                MinPoolSize = 2,
                MaxPoolSize = MaximumPoolSize,
                MaxAutoPrepare = 100,
                AutoPrepareMinUsages = 2,
                ConnectionIdleLifetime = 300,
                ConnectionPruningInterval = 30,
                ReadBufferSize = 65536,
                WriteBufferSize = 65536,
                Timeout = 15,
                CommandTimeout = 60,
                KeepAlive = 30,
                ApplicationName = "Bannerlord Reign"
            };
            return builder.ConnectionString;
        }

        private static string ReadEnvironment(string name, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static int ReadIntEnvironment(string name, int fallback, int minimum, int maximum)
        {
            int parsed;
            return int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? Math.Max(minimum, Math.Min(maximum, parsed))
                : fallback;
        }
    }

    internal static class ReignPostgreSqlStorage
    {
        private const string MetadataSchema = "reign_meta";
        private const string CampaignSchemaPrefix = "reign_campaign_";
        private const string SnapshotSchemaPrefix = "reign_save_";
        private static readonly object InfrastructureLock = new object();
        private static readonly object CampaignSchemaRegistrationLock =
            new object();
        private static readonly ConcurrentDictionary<string, byte>
            RegisteredCampaignSchemas =
                new ConcurrentDictionary<string, byte>(
                    StringComparer.OrdinalIgnoreCase);
        private static readonly ConditionalWeakTable<NpgsqlConnection, CampaignConnectionIdentity>
            CampaignConnectionIdentities =
                new ConditionalWeakTable<NpgsqlConnection, CampaignConnectionIdentity>();
        private static bool InfrastructureReady;
        public const int RequiredDatabaseSchemaVersion = 2;
        public static int DatabaseSchemaVersion { get; private set; }

        private sealed class CampaignConnectionIdentity
        {
            internal string CampaignId = string.Empty;
        }

        private static readonly Lazy<ReignPostgreSqlOptions> ConfiguredOptions =
            new Lazy<ReignPostgreSqlOptions>(ReignPostgreSqlOptions.FromEnvironment);
        public static ReignPostgreSqlOptions Options => ConfiguredOptions.Value;

        public static NpgsqlConnection OpenCampaignConnection(string campaignId)
        {
            Program.ThrowIfCampaignRequestReplaced();
            EnsureInfrastructure();
            string normalizedCampaignId = NormalizeCampaignId(campaignId);
            string schema = CampaignSchemaName(normalizedCampaignId);
            NpgsqlConnection connection = OpenDatabaseConnection();
            try
            {
                if (!RegisteredCampaignSchemas.ContainsKey(normalizedCampaignId))
                {
                    lock (CampaignSchemaRegistrationLock)
                    {
                        if (!RegisteredCampaignSchemas.ContainsKey(
                            normalizedCampaignId))
                        {
                            using (NpgsqlTransaction transaction =
                                connection.BeginTransaction(
                                    IsolationLevel.ReadCommitted))
                            {
                                ExecuteNonQuery(connection, transaction,
                                    "SELECT pg_advisory_xact_lock(hashtextextended(@campaign_id, 0));",
                                    new Dictionary<string, object>
                                    {
                                        ["campaign_id"] =
                                            normalizedCampaignId
                                    });
                                ExecuteNonQuery(connection, transaction,
                                    "CREATE SCHEMA IF NOT EXISTS "
                                    + QuoteIdentifier(schema) + ";");
                                ExecuteNonQuery(connection, transaction, @"
INSERT INTO reign_meta.campaign_registry(campaign_id, schema_name, created_utc, updated_utc)
VALUES(@campaign_id, @schema_name, now(), now())
ON CONFLICT(campaign_id) DO UPDATE
SET schema_name=excluded.schema_name;",
                                    new Dictionary<string, object>
                                    {
                                        ["campaign_id"] =
                                            normalizedCampaignId,
                                        ["schema_name"] = schema
                                    });
                                transaction.Commit();
                            }
                            DeduplicateSchemaIndexes(connection, schema);
                            RegisteredCampaignSchemas[
                                normalizedCampaignId] = 0;
                        }
                    }
                }

                SetSearchPath(connection, schema);
                CampaignConnectionIdentities.Add(
                    connection,
                    new CampaignConnectionIdentity
                    {
                        CampaignId = normalizedCampaignId
                    });
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public static NpgsqlConnection OpenMetadataConnection()
        {
            EnsureInfrastructure();
            NpgsqlConnection connection = OpenDatabaseConnection();
            SetSearchPath(connection, MetadataSchema);
            return connection;
        }

        public static NpgsqlConnection OpenUtilityConnection(string schemaName)
        {
            EnsureInfrastructure();
            if (string.IsNullOrWhiteSpace(schemaName)
                || !System.Text.RegularExpressions.Regex.IsMatch(
                    schemaName, "^[a-z][a-z0-9_]{0,62}$"))
                throw new ArgumentException(
                    "Invalid PostgreSQL utility schema name.",
                    nameof(schemaName));
            NpgsqlConnection connection = OpenDatabaseConnection();
            try
            {
                using (NpgsqlCommand command = new NpgsqlCommand(
                    "CREATE SCHEMA IF NOT EXISTS "
                    + QuoteIdentifier(schemaName)
                    + " AUTHORIZATION " + QuoteIdentifier(Options.Username) + ";", connection))
                    command.ExecuteNonQuery();
                SetSearchPath(connection, schemaName);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public static void EnsureInfrastructure()
        {
            if (InfrastructureReady)
                return;

            lock (InfrastructureLock)
            {
                if (InfrastructureReady)
                    return;

                if (Options.IsValidation)
                    EnsureValidationDatabaseExists();
                using (NpgsqlConnection connection = OpenDatabaseConnection())
                {
                    DatabaseSchemaVersion = ApplyInfrastructureMigrations(connection);
                }

                InfrastructureReady = true;
            }
        }

        // Keep schema changes and their version stamp atomic across concurrent startup/update attempts.
        internal static int ApplyInfrastructureMigrations(NpgsqlConnection connection)
        {
            ValidateDatabaseIdentity(connection);
            using (NpgsqlTransaction transaction = connection.BeginTransaction())
            using (NpgsqlCommand command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(1380271950);", connection, transaction))
            {
                command.CommandTimeout = 180;
                command.ExecuteNonQuery();
                command.CommandText = "SELECT to_regclass('reign_meta.storage_version') IS NOT NULL;";
                if (Convert.ToBoolean(command.ExecuteScalar(), CultureInfo.InvariantCulture))
                {
                    command.CommandText = "SELECT version FROM reign_meta.storage_version WHERE component='reign_postgresql';";
                    object installed = command.ExecuteScalar();
                    if (installed != null && installed != DBNull.Value)
                        ValidateDatabaseSchemaVersion(Convert.ToInt32(installed, CultureInfo.InvariantCulture));
                }
                command.CommandText = ReadEmbeddedSql("ReignBetaServer.PostgreSql.reign_meta.sql");
                command.ExecuteNonQuery();
                command.CommandText = "SELECT version FROM reign_meta.storage_version WHERE component='reign_postgresql';";
                int version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (version != RequiredDatabaseSchemaVersion)
                    throw new InvalidOperationException("Reign database migration did not reach the required schema version.");
                transaction.Commit();
                return version;
            }
        }

        // A code rollback must never rewrite schema helpers belonging to a newer database release.
        internal static void ValidateDatabaseSchemaVersion(int version)
        {
            if (version > RequiredDatabaseSchemaVersion)
                throw new InvalidOperationException("Reign database schema version " + version
                    + " is newer than this server supports (" + RequiredDatabaseSchemaVersion
                    + "). Install a compatible server; database downgrades are not automatic.");
        }

        private static void EnsureValidationDatabaseExists()
        {
            using (NpgsqlConnection connection = new NpgsqlConnection(
                Options.BuildConnectionString("postgres")))
            {
                DatabaseAvailability.Open(connection);
                using (NpgsqlCommand exists = new NpgsqlCommand(
                    "SELECT 1 FROM pg_database WHERE datname=@database;",
                    connection))
                {
                    exists.Parameters.AddWithValue(
                        "database",
                        ReignPostgreSqlOptions.ValidationDatabaseName);
                    if (exists.ExecuteScalar() != null)
                        return;
                }
                using (NpgsqlCommand create = new NpgsqlCommand(
                    "CREATE DATABASE \"ReignValidation\" OWNER " + QuoteIdentifier(Options.Username) + " ENCODING 'UTF8' TEMPLATE template0;",
                    connection))
                {
                    create.CommandTimeout = 60;
                    create.ExecuteNonQuery();
                }
            }
        }

        public static string CampaignSchemaName(string campaignId)
        {
            return CampaignSchemaPrefix + StableHex(NormalizeCampaignId(campaignId), 32);
        }

        public static string SnapshotSchemaName(string campaignId, string savePointId)
        {
            return SnapshotSchemaPrefix + StableHex(
                NormalizeCampaignId(campaignId) + "\n" + (savePointId ?? string.Empty).Trim(),
                36);
        }

        public static void CloneCampaignToSnapshot(string campaignId, string savePointId)
        {
            EnsureInfrastructure();
            string normalizedCampaignId = NormalizeCampaignId(campaignId);
            string sourceSchema = CampaignSchemaName(normalizedCampaignId);
            string snapshotSchema = SnapshotSchemaName(normalizedCampaignId, savePointId);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (NpgsqlConnection connection = OpenDatabaseConnection())
                    using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                    {
                        AcquireCampaignLock(connection, transaction, normalizedCampaignId);
                        ExecuteNonQuery(connection, transaction,
                            "SELECT reign_meta.deduplicate_schema_indexes(@schema_name);",
                            new Dictionary<string, object>
                            {
                                ["schema_name"] = sourceSchema
                            });
                        ExecuteNonQuery(connection, transaction,
                            "SELECT reign_meta.drop_schema_safe(@schema_name);",
                            new Dictionary<string, object> { ["schema_name"] = snapshotSchema });
                        ExecuteNonQuery(connection, transaction,
                            "SELECT reign_meta.clone_schema(@source_schema, @destination_schema);",
                            new Dictionary<string, object>
                            {
                                ["source_schema"] = sourceSchema,
                                ["destination_schema"] = snapshotSchema
                            });
                        ExecuteNonQuery(connection, transaction, @"
INSERT INTO reign_meta.save_sync_snapshots(
    campaign_id, save_point_id, schema_name, created_utc, source_schema_size_bytes)
VALUES(@campaign_id, @save_point_id, @schema_name, now(), reign_meta.get_schema_size(@source_schema))
ON CONFLICT(campaign_id, save_point_id) DO UPDATE
SET schema_name=excluded.schema_name,
    created_utc=excluded.created_utc,
    source_schema_size_bytes=excluded.source_schema_size_bytes;",
                            new Dictionary<string, object>
                            {
                                ["campaign_id"] = normalizedCampaignId,
                                ["save_point_id"] = savePointId ?? string.Empty,
                                ["schema_name"] = snapshotSchema,
                                ["source_schema"] = sourceSchema
                            });
                        transaction.Commit();
                        return;
                    }
                }
                catch (PostgresException ex) when (
                    (ex.SqlState == PostgresErrorCodes.SerializationFailure
                        || ex.SqlState == PostgresErrorCodes.DeadlockDetected)
                    && attempt < 5)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }

        public static int DeduplicateCampaignIndexes(string campaignId)
        {
            EnsureInfrastructure();
            string normalizedCampaignId = NormalizeCampaignId(campaignId);
            using (NpgsqlConnection connection = OpenDatabaseConnection())
                return DeduplicateSchemaIndexes(connection,
                    CampaignSchemaName(normalizedCampaignId));
        }

        private static int DeduplicateSchemaIndexes(
            NpgsqlConnection connection, string schemaName)
        {
            using (NpgsqlCommand command = new NpgsqlCommand(
                "SELECT reign_meta.deduplicate_schema_indexes(@schema_name);",
                connection))
            {
                command.CommandTimeout = 180;
                command.Parameters.AddWithValue("schema_name", schemaName);
                return Convert.ToInt32(command.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }
        }

        public static void RestoreCampaignSnapshot(string campaignId, string savePointId)
        {
            Program.InvalidateWaitingCampaignRequests();
            EnsureInfrastructure();
            string normalizedCampaignId = NormalizeCampaignId(campaignId);
            string campaignSchema = CampaignSchemaName(normalizedCampaignId);
            string snapshotSchema = SnapshotSchemaName(normalizedCampaignId, savePointId);
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                AcquireCampaignLock(connection, transaction, normalizedCampaignId);
                ExecuteNonQuery(connection, transaction,
                    "SELECT reign_meta.replace_schema(@source_schema, @destination_schema);",
                    new Dictionary<string, object>
                    {
                        ["source_schema"] = snapshotSchema,
                        ["destination_schema"] = campaignSchema
                    });
                transaction.Commit();
            }
            Program.InvalidateCampaignSchemaCaches(normalizedCampaignId);
            NpgsqlConnection.ClearAllPools();
        }

        public static bool SnapshotExists(string campaignId, string savePointId)
        {
            EnsureInfrastructure();
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlCommand command = new NpgsqlCommand(@"
SELECT EXISTS(
    SELECT 1
    FROM reign_meta.save_sync_snapshots s
    JOIN pg_namespace n ON n.nspname=s.schema_name
    WHERE s.campaign_id=@campaign_id AND s.save_point_id=@save_point_id
);", connection))
            {
                command.Parameters.AddWithValue("campaign_id",
                    NormalizeCampaignId(campaignId));
                command.Parameters.AddWithValue("save_point_id",
                    savePointId ?? string.Empty);
                return Convert.ToBoolean(command.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }
        }

        public static long CampaignSizeBytes(string campaignId)
        {
            return SchemaSizeBytes(CampaignSchemaName(campaignId));
        }

        public static long SnapshotSizeBytes(string campaignId, string savePointId)
        {
            return SchemaSizeBytes(SnapshotSchemaName(campaignId, savePointId));
        }

        public static string CampaignStateToken(string campaignId)
        {
            return SchemaStateToken(CampaignSchemaName(campaignId));
        }

        public static string SnapshotStateToken(string campaignId, string savePointId)
        {
            return SchemaStateToken(SnapshotSchemaName(campaignId, savePointId));
        }

        public static void DropSnapshot(string campaignId, string savePointId)
        {
            EnsureInfrastructure();
            string normalizedCampaignId = NormalizeCampaignId(campaignId);
            string snapshotSchema = SnapshotSchemaName(
                normalizedCampaignId, savePointId);
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlTransaction transaction =
                connection.BeginTransaction(IsolationLevel.Serializable))
            {
                AcquireCampaignLock(connection, transaction,
                    normalizedCampaignId);
                ExecuteNonQuery(connection, transaction,
                    "SELECT reign_meta.drop_schema_safe(@schema_name);",
                    new Dictionary<string, object>
                    {
                        ["schema_name"] = snapshotSchema
                    });
                ExecuteNonQuery(connection, transaction, @"
DELETE FROM reign_meta.save_sync_snapshots
WHERE campaign_id=@campaign_id AND save_point_id=@save_point_id;",
                    new Dictionary<string, object>
                    {
                        ["campaign_id"] = normalizedCampaignId,
                        ["save_point_id"] = savePointId ?? string.Empty
                    });
                transaction.Commit();
            }
            NpgsqlConnection.ClearAllPools();
        }

        public static List<Dictionary<string, object>> ListCampaigns()
        {
            return ReadCampaignRegistry(includeDatabaseSizes: true);
        }

        public static List<Dictionary<string, object>> ListCampaignMetadata()
        {
            return ReadCampaignRegistry(includeDatabaseSizes: false);
        }

        private static List<Dictionary<string, object>> ReadCampaignRegistry(
            bool includeDatabaseSizes)
        {
            EnsureInfrastructure();
            List<Dictionary<string, object>> rows =
                new List<Dictionary<string, object>>();
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlCommand command = new NpgsqlCommand(@"
SELECT r.campaign_id,r.schema_name,r.created_utc,r.updated_utc,
       " + (includeDatabaseSizes ? "reign_meta.get_schema_size(r.schema_name)" : "NULL::bigint") + @" AS size_bytes,
       (SELECT count(*) FROM reign_meta.save_sync_snapshots s
        WHERE s.campaign_id=r.campaign_id) AS snapshot_count
FROM reign_meta.campaign_registry r
WHERE EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname=r.schema_name)
ORDER BY r.updated_utc DESC,r.campaign_id;", connection))
            using (NpgsqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    Dictionary<string, object> row = new Dictionary<string, object>
                    {
                        ["campaignId"] = reader.GetString(0),
                        ["schemaName"] = reader.GetString(1),
                        ["createdUtc"] = reader.GetDateTime(2).ToUniversalTime()
                            .ToString("o", CultureInfo.InvariantCulture),
                        ["updatedUtc"] = reader.GetDateTime(3).ToUniversalTime()
                            .ToString("o", CultureInfo.InvariantCulture),
                        ["snapshotCount"] = reader.GetInt64(5)
                    };
                    if (includeDatabaseSizes)
                        row["databaseBytes"] = reader.GetInt64(4);
                    rows.Add(row);
                }
            }
            return rows;
        }

        public static bool CampaignExists(string campaignId)
        {
            if (string.IsNullOrWhiteSpace(campaignId))
                return false;
            EnsureInfrastructure();
            string normalizedCampaignId = NormalizeCampaignId(campaignId);
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlCommand command = new NpgsqlCommand(@"
SELECT EXISTS(
    SELECT 1
    FROM reign_meta.campaign_registry r
    JOIN pg_namespace n ON n.nspname=r.schema_name
    WHERE r.campaign_id=@campaign_id);", connection))
            {
                command.Parameters.AddWithValue(
                    "campaign_id", normalizedCampaignId);
                return Convert.ToBoolean(
                    command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        public static List<Dictionary<string, object>> ListSnapshots(
            string campaignId)
        {
            EnsureInfrastructure();
            List<Dictionary<string, object>> rows =
                new List<Dictionary<string, object>>();
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlCommand command = new NpgsqlCommand(@"
SELECT save_point_id,schema_name,created_utc,source_schema_size_bytes
FROM reign_meta.save_sync_snapshots
WHERE campaign_id=@campaign_id
  AND EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname=schema_name)
ORDER BY created_utc,save_point_id;", connection))
            {
                command.Parameters.AddWithValue(
                    "campaign_id", NormalizeCampaignId(campaignId));
                using (NpgsqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(new Dictionary<string, object>
                        {
                            ["savePointId"] = reader.GetString(0),
                            ["schemaName"] = reader.GetString(1),
                            ["createdUtc"] = reader.GetDateTime(2)
                                .ToUniversalTime()
                                .ToString("o", CultureInfo.InvariantCulture),
                            ["databaseBytes"] = reader.GetInt64(3)
                        });
                    }
                }
            }
            return rows;
        }

        public static void ExportCampaignArchive(
            string campaignId,
            string outputPath)
        {
            EnsureInfrastructure();
            string normalizedCampaignId = NormalizeCampaignId(campaignId);
            List<string> schemas = new List<string>
            {
                CampaignSchemaName(normalizedCampaignId)
            };
            schemas.AddRange(ListSnapshots(normalizedCampaignId)
                .Select(row => Convert.ToString(
                    row["schemaName"], CultureInfo.InvariantCulture)));
            List<string> arguments = new List<string>
            {
                "pg_dump",
                "--host", Options.Host,
                "--port", Options.Port.ToString(CultureInfo.InvariantCulture),
                "--username", Options.Username,
                "--dbname", Options.Database,
                "--format", "custom",
                "--compress", "6",
                "--no-owner",
                "--no-privileges",
                "--file", PostgreSqlTools.ArchivePath(outputPath, !string.IsNullOrWhiteSpace(NativePostgreSqlBin()))
            };
            foreach (string schema in schemas
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal))
            {
                arguments.Add("--schema");
                arguments.Add(schema);
            }
            RunPostgreSqlTool(arguments, 600000);
            if (!File.Exists(outputPath)
                || new FileInfo(outputPath).Length <= 0)
                throw new InvalidDataException(
                    "PostgreSQL campaign export did not produce an archive.");
        }

        public static void ImportCampaignArchive(
            string campaignId,
            string archivePath,
            IEnumerable<Dictionary<string, object>> snapshots,
            bool replace)
        {
            EnsureInfrastructure();
            string normalizedCampaignId = NormalizeCampaignId(campaignId);
            string campaignSchema = CampaignSchemaName(normalizedCampaignId);
            bool exists = ListCampaignMetadata().Any(row =>
                string.Equals(
                    Convert.ToString(
                        row["campaignId"], CultureInfo.InvariantCulture),
                    normalizedCampaignId,
                    StringComparison.OrdinalIgnoreCase));
            if (exists && !replace)
                throw new InvalidOperationException(
                    "This campaign already exists in PostgreSQL.");
            if (exists)
                DropCampaign(normalizedCampaignId);

            List<string> arguments = new List<string>
            {
                "pg_restore",
                "--host", Options.Host,
                "--port", Options.Port.ToString(CultureInfo.InvariantCulture),
                "--username", Options.Username,
                "--dbname", Options.Database,
                "--no-owner",
                "--no-privileges",
                "--exit-on-error",
                PostgreSqlTools.ArchivePath(archivePath, !string.IsNullOrWhiteSpace(NativePostgreSqlBin()))
            };
            RunPostgreSqlTool(arguments, 600000);

            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlTransaction transaction =
                connection.BeginTransaction(IsolationLevel.Serializable))
            {
                if (!SchemaExists(connection, transaction, campaignSchema))
                    throw new InvalidDataException(
                        "The PostgreSQL archive does not contain the expected campaign schema.");
                ExecuteNonQuery(connection, transaction, @"
INSERT INTO reign_meta.campaign_registry(
    campaign_id,schema_name,created_utc,updated_utc,migration_state)
VALUES(@campaign_id,@schema_name,now(),now(),'postgresql_archive')
ON CONFLICT(campaign_id) DO UPDATE
SET schema_name=excluded.schema_name,
    updated_utc=excluded.updated_utc,
    migration_state=excluded.migration_state;",
                    new Dictionary<string, object>
                    {
                        ["campaign_id"] = normalizedCampaignId,
                        ["schema_name"] = campaignSchema
                    });
                foreach (Dictionary<string, object> snapshot
                    in snapshots ?? Enumerable.Empty<Dictionary<string, object>>())
                {
                    string savePointId = Convert.ToString(
                        snapshot.ContainsKey("savePointId")
                            ? snapshot["savePointId"] : "",
                        CultureInfo.InvariantCulture) ?? string.Empty;
                    string schemaName = SnapshotSchemaName(
                        normalizedCampaignId, savePointId);
                    if (!SchemaExists(connection, transaction, schemaName))
                        throw new InvalidDataException(
                            "The PostgreSQL archive is missing Save Sync schema for "
                            + savePointId + ".");
                    ExecuteNonQuery(connection, transaction, @"
INSERT INTO reign_meta.save_sync_snapshots(
    campaign_id,save_point_id,schema_name,created_utc,
    source_schema_size_bytes)
VALUES(@campaign_id,@save_point_id,@schema_name,now(),
       reign_meta.get_schema_size(@schema_name))
ON CONFLICT(campaign_id,save_point_id) DO UPDATE
SET schema_name=excluded.schema_name,
    created_utc=excluded.created_utc,
    source_schema_size_bytes=excluded.source_schema_size_bytes;",
                        new Dictionary<string, object>
                        {
                            ["campaign_id"] = normalizedCampaignId,
                            ["save_point_id"] = savePointId,
                            ["schema_name"] = schemaName
                        });
                }
                transaction.Commit();
            }
            NpgsqlConnection.ClearAllPools();
        }

        public static void DropAllCampaigns()
        {
            foreach (Dictionary<string, object> campaign in ListCampaignMetadata())
            {
                DropCampaign(Convert.ToString(
                    campaign["campaignId"],
                    CultureInfo.InvariantCulture));
            }
        }

        public static Dictionary<string, object> Diagnostics()
        {
            EnsureInfrastructure();
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlCommand command = new NpgsqlCommand(@"
SELECT current_database(),current_user,
       (SELECT pg_get_userbyid(datdba) FROM pg_database
        WHERE datname=current_database()),
       current_setting('server_encoding'),
       current_setting('server_version'),
       (SELECT count(*) FROM reign_meta.campaign_registry r
        WHERE EXISTS(SELECT 1 FROM pg_namespace n
                     WHERE n.nspname=r.schema_name)),
       (SELECT count(*) FROM reign_meta.save_sync_snapshots s
        WHERE EXISTS(SELECT 1 FROM pg_namespace n
                     WHERE n.nspname=s.schema_name)),
       (SELECT COALESCE(sum(reign_meta.get_schema_size(r.schema_name)),0)
        FROM reign_meta.campaign_registry r);", connection))
            using (NpgsqlDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    throw new InvalidOperationException(
                        "PostgreSQL diagnostics returned no row.");
                return new Dictionary<string, object>
                {
                    ["provider"] = "postgresql",
                    ["host"] = Options.Host,
                    ["port"] = Options.Port,
                    ["database"] = reader.GetString(0),
                    ["ownerUser"] = reader.GetString(1),
                    ["databaseOwner"] = reader.GetString(2),
                    ["encoding"] = reader.GetString(3),
                    ["version"] = reader.GetString(4),
                    ["campaignSchemas"] = reader.GetInt64(5),
                    ["saveSyncSchemas"] = reader.GetInt64(6),
                    ["campaignDatabaseBytes"] = reader.GetInt64(7),
                    ["poolMaximum"] = Options.MaximumPoolSize,
                    ["healthy"] = true
                };
            }
        }

        public static int TableRowCount(string campaignId, string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName)
                || !System.Text.RegularExpressions.Regex.IsMatch(
                    tableName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                return 0;
            using (NpgsqlConnection connection =
                OpenCampaignConnection(campaignId))
            using (NpgsqlCommand command = new NpgsqlCommand(
                "SELECT COUNT(*) FROM " + QuoteIdentifier(tableName) + ";",
                connection))
            {
                try
                {
                    return Convert.ToInt32(
                        command.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                catch (PostgresException ex)
                    when (ex.SqlState
                        == PostgresErrorCodes.UndefinedTable)
                {
                    return 0;
                }
            }
        }


        public static void DropCampaign(string campaignId)
        {
            Program.InvalidateWaitingCampaignRequests();
            EnsureInfrastructure();
            string normalizedCampaignId = NormalizeCampaignId(campaignId);
            RegisteredCampaignSchemas.TryRemove(normalizedCampaignId,
                out byte ignoredRegistration);
            Program.InvalidateCampaignSchemaCaches(normalizedCampaignId);
            Program.InvalidateRelationshipPairStateCache(
                normalizedCampaignId);
            string campaignSchema = CampaignSchemaName(normalizedCampaignId);
            List<string> snapshotSchemas = new List<string>();
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            {
                using (NpgsqlCommand command = new NpgsqlCommand(@"
SELECT schema_name
FROM reign_meta.save_sync_snapshots
WHERE campaign_id=@campaign_id;", connection))
                {
                    command.Parameters.AddWithValue("campaign_id", normalizedCampaignId);
                    using (NpgsqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshotSchemas.Add(reader.GetString(0));
                    }
                }

                // PostgreSQL retains relation locks until transaction end. Dropping
                // every large Save Sync schema in one transaction can exhaust the
                // cluster lock table, so retire one snapshot per transaction. Each
                // step is idempotent and the registry remains authoritative until
                // the final campaign-schema transaction commits.
                foreach (string schema in snapshotSchemas
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal))
                {
                    using (NpgsqlTransaction snapshotTransaction =
                        connection.BeginTransaction(IsolationLevel.ReadCommitted))
                    {
                        AcquireCampaignLock(connection, snapshotTransaction,
                            normalizedCampaignId);
                        ExecuteNonQuery(connection, snapshotTransaction,
                            "SELECT reign_meta.drop_schema_safe(@schema_name);",
                            new Dictionary<string, object>
                                { ["schema_name"] = schema });
                        ExecuteNonQuery(connection, snapshotTransaction, @"
DELETE FROM reign_meta.save_sync_snapshots
WHERE campaign_id=@campaign_id AND schema_name=@schema_name;",
                            new Dictionary<string, object>
                            {
                                ["campaign_id"] = normalizedCampaignId,
                                ["schema_name"] = schema
                            });
                        snapshotTransaction.Commit();
                    }
                }

                using (NpgsqlTransaction campaignTransaction =
                    connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    AcquireCampaignLock(connection, campaignTransaction,
                        normalizedCampaignId);
                    ExecuteNonQuery(connection, campaignTransaction,
                        "SELECT reign_meta.drop_schema_safe(@schema_name);",
                        new Dictionary<string, object>
                            { ["schema_name"] = campaignSchema });
                    ExecuteNonQuery(connection, campaignTransaction, @"
DELETE FROM reign_meta.save_sync_snapshots
WHERE campaign_id=@campaign_id;",
                        new Dictionary<string, object>
                            { ["campaign_id"] = normalizedCampaignId });
                    ExecuteNonQuery(connection, campaignTransaction,
                        "DELETE FROM reign_meta.campaign_registry WHERE campaign_id=@campaign_id;",
                        new Dictionary<string, object>
                            { ["campaign_id"] = normalizedCampaignId });
                    campaignTransaction.Commit();
                }
            }
            NpgsqlConnection.ClearAllPools();
        }

        public static void ClearAllPools()
        {
            NpgsqlConnection.ClearAllPools();
        }

        public static string CampaignIdForConnection(
            System.Data.Common.DbConnection connection)
        {
            NpgsqlConnection postgres = connection as NpgsqlConnection;
            if (postgres == null)
                return string.Empty;
            return CampaignConnectionIdentities.TryGetValue(
                postgres, out CampaignConnectionIdentity identity)
                ? identity.CampaignId
                : string.Empty;
        }

        public static void ValidateDatabaseIdentity(NpgsqlConnection connection)
        {
            using (NpgsqlCommand command = new NpgsqlCommand(
                @"SELECT current_database(),
       current_setting('server_encoding'),
       current_user,
       pg_get_userbyid(datdba)
FROM pg_database
WHERE datname=current_database();", connection))
            using (NpgsqlDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    throw new InvalidOperationException("PostgreSQL did not return database identity.");

                string database = reader.GetString(0);
                string encoding = reader.GetString(1);
                string currentUser = reader.GetString(2);
                string databaseOwner = reader.GetString(3);
                if (!string.Equals(database, Options.Database, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Connected PostgreSQL database is '" + database + "'; expected exactly '"
                        + Options.Database + "'.");
                }
                if (!string.Equals(encoding, ReignPostgreSqlOptions.RequiredEncoding, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "PostgreSQL database '" + database + "' uses encoding '" + encoding
                        + "'; Reign requires UTF8.");
                }
                if (!string.Equals(currentUser, Options.Username, StringComparison.Ordinal)
                    || !string.Equals(databaseOwner, Options.Username, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "PostgreSQL database '" + database
                        + "' must be connected as and owned by the configured user '" + Options.Username + "'. "
                        + "Current user is '" + currentUser
                        + "' and database owner is '" + databaseOwner + "'.");
                }
            }
        }

        private static NpgsqlConnection OpenDatabaseConnection()
        {
            NpgsqlConnection connection = new NpgsqlConnection(Options.BuildConnectionString());
            try
            {
                DatabaseAvailability.Open(connection);
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        private static bool SchemaExists(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string schema)
        {
            using (NpgsqlCommand command = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname=@schema);",
                connection, transaction))
            {
                command.Parameters.AddWithValue("schema", schema);
                return Convert.ToBoolean(
                    command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static void RunPostgreSqlTool(
            IList<string> arguments,
            int timeoutMilliseconds)
        {
            string distro = Environment.GetEnvironmentVariable(
                "REIGN_POSTGRES_WSL_DISTRO");
            if (string.IsNullOrWhiteSpace(distro))
                distro = "DwemerAI4Skyrim3";
            string nativeBin = NativePostgreSqlBin();
            ProcessStartInfo start = PostgreSqlTools.CreateCommand(nativeBin, distro,
                arguments, Options.Password, AppDomain.CurrentDomain.BaseDirectory);
            if (!string.IsNullOrWhiteSpace(nativeBin) && !File.Exists(start.FileName))
                throw new FileNotFoundException("The installed PostgreSQL archive tool is missing. Repair ReignServer.", start.FileName);
            using (Process process = Process.Start(start))
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException(
                        "PostgreSQL archive command timed out.");
                }
                string standardOutput = outputTask.GetAwaiter().GetResult();
                string standardError = errorTask.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    if (!string.IsNullOrEmpty(Options.Password))
                    {
                        standardOutput = standardOutput.Replace(Options.Password, "[redacted]");
                        standardError = standardError.Replace(Options.Password, "[redacted]");
                    }
                    throw new InvalidOperationException(
                        "PostgreSQL archive command failed: "
                        + standardError.Trim()
                        + (string.IsNullOrWhiteSpace(standardOutput)
                            ? "" : " " + standardOutput.Trim()));
                }
            }
        }

        private static string NativePostgreSqlBin()
        {
            string configured = Environment.GetEnvironmentVariable("REIGN_POSTGRES_BIN");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            return ReignInstallation.TryLoadCurrent()?.PostgresBin;
        }

        private static long SchemaSizeBytes(string schema)
        {
            EnsureInfrastructure();
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlCommand command = new NpgsqlCommand(
                "SELECT reign_meta.get_schema_size(@schema_name);",
                connection))
            {
                command.Parameters.AddWithValue("schema_name", schema);
                return Convert.ToInt64(command.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }
        }

        private static string SchemaStateToken(string schema)
        {
            EnsureInfrastructure();
            using (NpgsqlConnection connection = OpenDatabaseConnection())
            using (NpgsqlCommand command = new NpgsqlCommand(@"
SELECT md5(
    COALESCE(string_agg(
        c.relname || ':' || c.reltuples::bigint::text || ':'
        || pg_total_relation_size(c.oid)::text,
        '|' ORDER BY c.relname), '')
)
FROM pg_class c
JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname=@schema_name AND c.relkind IN ('r','p');",
                connection))
            {
                command.Parameters.AddWithValue("schema_name", schema);
                return Convert.ToString(command.ExecuteScalar(),
                    CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        private static void SetSearchPath(NpgsqlConnection connection, string schema)
        {
            using (NpgsqlCommand command = connection.CreateCommand())
            {
                command.CommandText = "SET search_path TO " + QuoteIdentifier(schema) + ", reign_meta, public;";
                command.ExecuteNonQuery();
            }
        }

        private static void AcquireCampaignLock(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string campaignId)
        {
            ExecuteNonQuery(connection, transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended(@campaign_id, 0));",
                new Dictionary<string, object> { ["campaign_id"] = campaignId });
        }

        private static void ExecuteNonQuery(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string sql,
            IDictionary<string, object> parameters = null)
        {
            using (NpgsqlCommand command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.CommandTimeout = 180;
                if (parameters != null)
                {
                    foreach (KeyValuePair<string, object> parameter in parameters)
                        command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
                }
                command.ExecuteNonQuery();
            }
        }

        private static string QuoteIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                throw new ArgumentException("PostgreSQL identifier is required.", nameof(identifier));
            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }

        private static string NormalizeCampaignId(string campaignId)
        {
            string normalized = string.IsNullOrWhiteSpace(campaignId) ? "default" : campaignId.Trim();
            if (normalized.Length > 512)
                throw new ArgumentOutOfRangeException(nameof(campaignId), "Campaign id is too long.");
            return normalized;
        }

        private static string StableHex(string value, int characters)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                string hex = BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
                return hex.Substring(0, Math.Min(characters, hex.Length));
            }
        }

        private static string ReadEmbeddedSql(string resourceName)
        {
            Assembly assembly = typeof(ReignPostgreSqlStorage).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Missing PostgreSQL resource: " + resourceName);
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                    return reader.ReadToEnd();
            }
        }
    }
}
