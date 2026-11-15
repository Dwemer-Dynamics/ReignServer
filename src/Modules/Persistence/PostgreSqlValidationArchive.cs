using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Reign.Core.Contracts.Platform;

namespace ReignBetaServer
{
    internal static partial class ReignPostgreSqlStorage
    {
        internal sealed class ValidationCampaignArchiveReceipt
        {
            internal readonly string CampaignId, Schema, ArchiveSha256, Owner, ToolApplicationName;
            internal readonly long SchemaOid;
            internal ValidationCampaignArchiveReceipt(string campaign, string schema, string hash, string owner, long oid, string applicationName)
            { CampaignId = campaign; Schema = schema; ArchiveSha256 = hash; Owner = owner; SchemaOid = oid; ToolApplicationName = applicationName; }
        }

        internal static bool ValidationArchiveGate(bool validation, string database, IEnumerable<string> arguments) =>
            validation && database == ReignPostgreSqlOptions.ValidationDatabaseName
                && (arguments ?? Array.Empty<string>()).Contains("--run-verification", StringComparer.Ordinal);

        internal static bool ValidationArchiveOwnerMatches(long expectedOid, string expectedOwner, long actualOid, string actualOwner) =>
            expectedOid > 0 && expectedOid == actualOid && !string.IsNullOrEmpty(expectedOwner)
                && string.Equals(expectedOwner, actualOwner, StringComparison.Ordinal);

        internal static string ValidationArchiveSelectToc(string toc, string schema, bool includeSchemaDefinition = true)
        {
            if (!Regex.IsMatch(schema ?? "", @"\Areign_campaign_[0-9a-f]{32}\z") || toc == null || toc.Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Invalid native archive table of contents.");
            var selected = new List<string>();
            int schemaDefinitions = 0;
            // pg_restore --schema omits SCHEMA entries (their namespace is '-').
            // Select exact namespace tokens, including the one schema definition;
            // never use a substring match or rewrite SQL/schema-qualified bodies.
            const string kinds = "SEQUENCE OWNED BY|MATERIALIZED VIEW DATA|MATERIALIZED VIEW|TEXT SEARCH CONFIGURATION|TEXT SEARCH DICTIONARY|TEXT SEARCH PARSER|TEXT SEARCH TEMPLATE|OPERATOR CLASS|OPERATOR FAMILY|TABLE DATA|INDEX ATTACH|SEQUENCE SET|FK CONSTRAINT|DEFAULT ACL|ROW SECURITY|TABLE|SEQUENCE|CONSTRAINT|FUNCTION|PROCEDURE|AGGREGATE|INDEX|TRIGGER|COMMENT|ACL|TYPE|DOMAIN|OPERATOR|COLLATION|CONVERSION|POLICY|VIEW|RULE|STATISTICS|DEFAULT";
            string objectPattern = @"\A\d+;\s+\d+\s+\d+\s+(?:" + kinds + @")\s+" + Regex.Escape(schema) + @"\s+";
            string schemaPattern = @"\A\d+;\s+\d+\s+\d+\s+SCHEMA\s+-\s+" + Regex.Escape(schema) + @"\s+";
            foreach (string line in toc.Split('\n'))
            {
                string row = line.TrimEnd('\r');
                if (Regex.IsMatch(row, schemaPattern)) { if (includeSchemaDefinition) selected.Add(row); schemaDefinitions++; }
                else if (Regex.IsMatch(row, objectPattern)) selected.Add(row);
                else if (Regex.IsMatch(row, @"\A\d+;\s+\d+\s+\d+\s+.*\s" + Regex.Escape(schema) + @"\s"))
                    throw new InvalidDataException("Unrecognized native archive entry; refusing incomplete selection.");
            }
            if (schemaDefinitions != 1 || selected.Count < (includeSchemaDefinition ? 2 : 1))
                throw new InvalidOperationException("Archive does not contain one complete native campaign schema.");
            return string.Join("\n", selected) + "\n";
        }

        // Native identities remain unchanged inside the separate validation DB.
        // This deliberately does not call ImportCampaignArchive: that API imports
        // every Save Sync schema and is appropriate for a complete user restore.
        internal static T WithValidationCampaignArchive<T>(string campaignId, string archivePath,
            string expectedSha256, Func<ValidationCampaignArchiveReceipt, T> inspect, Action<string> fault = null)
        {
            if (!ValidationArchiveGate(Options.IsValidation, Options.Database, Environment.GetCommandLineArgs()))
                throw new InvalidOperationException("Native archive replay requires the isolated Verification Lab database and CLI.");
            if (!OperatingSystem.IsLinux())
                throw new InvalidOperationException("Native archive replay requires the Linux Verification Lab; a Windows WSL launcher cannot own restore termination.");
            if (inspect == null) throw new ArgumentNullException(nameof(inspect));
            if (expectedSha256 == null || expectedSha256.Length != 64 || expectedSha256.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("A complete native archive SHA256 is required.");
            archivePath = Path.GetFullPath(archivePath ?? "");
            string campaign = NormalizeCampaignId(campaignId), schema = CampaignSchemaName(campaign);
            using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 5 or > 16L * 1024 * 1024 * 1024
                || !Convert.ToHexString(SHA256.HashData(file)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native archive bytes do not match their pinned hash.");
            file.Position = 0;
            if (!new byte[] { 80, 71, 68, 77, 80 }.All(value => file.ReadByte() == value))
                throw new InvalidDataException("Native archive must be a PostgreSQL custom archive.");
            // No database access or infrastructure creation precedes byte checks.
            EnsureInfrastructure();
            string owner = "reign-validation-archive/" + Guid.NewGuid().ToString("N") + "/" + expectedSha256.ToLowerInvariant();
            string registryOwner = "validation_archive:" + owner.Split('/')[1];
            string toolApplicationName = "reign_validation_archive_" + owner.Split('/')[1];
            // The ownership lock must never return to a pool after an unlock
            // failure. Disposing this dedicated connection closes its session.
            var gateOptions = new NpgsqlConnectionStringBuilder(Options.BuildConnectionString()) { Pooling = false };
            using var connection = new NpgsqlConnection(gateOptions.ConnectionString);
            connection.Open();
            using (var command = new NpgsqlCommand("SELECT current_database();", connection))
                if (!string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture),
                    ReignPostgreSqlOptions.ValidationDatabaseName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Native archive connection is not the validation database.");
            string lockKey = "validation-native-archive:" + campaign;
            using (var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@key,0));", connection))
            {
                command.Parameters.AddWithValue("key", lockKey);
                if (command.ExecuteScalar() is not true) throw new InvalidOperationException("Native archive replay is already owned.");
            }
            bool restored = false;
            long oid = 0;
            HashSet<string> before = null;
            T result = default;
            var failures = new List<Exception>();
            try
            {
                using (var command = new NpgsqlCommand(@"SELECT
EXISTS(SELECT 1 FROM pg_namespace WHERE nspname=@schema)
OR EXISTS(SELECT 1 FROM reign_meta.campaign_registry WHERE campaign_id=@campaign OR schema_name=@schema)
OR EXISTS(SELECT 1 FROM reign_meta.save_sync_snapshots WHERE campaign_id=@campaign);", connection))
                {
                    command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("campaign", campaign);
                    if (command.ExecuteScalar() is not false)
                        throw new InvalidOperationException("Native archive replay refuses existing campaign, schema or snapshot state.");
                }
                before = ValidationArchiveSchemas(connection);
                RegisteredCampaignSchemas.TryRemove(campaign, out _);
                string archiveArgument = PostgreSqlTools.ArchivePath(archivePath, !string.IsNullOrWhiteSpace(NativePostgreSqlBin()));
                string toc = RunPostgreSqlTool(new List<string> { "pg_restore", "--list", archiveArgument }, 60000);
                string selectedToc = ValidationArchiveSelectToc(toc, schema, includeSchemaDefinition: false);
                // Validate the archive's definition but create our own empty,
                // stamped namespace atomically before starting pg_restore.
                using (var transaction = connection.BeginTransaction())
                {
                    ExecuteNonQuery(connection, transaction, "CREATE SCHEMA " + QuoteIdentifier(schema) + ";");
                    ExecuteNonQuery(connection, transaction, "COMMENT ON SCHEMA " + QuoteIdentifier(schema) + " IS '" + owner + "';");
                    using var command = new NpgsqlCommand("SELECT oid::bigint FROM pg_namespace WHERE nspname=@schema;", connection, transaction);
                    command.Parameters.AddWithValue("schema", schema);
                    oid = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                    transaction.Commit(); restored = true;
                }
                string tocPath = Path.Combine(Path.GetTempPath(), "reign-validation-archive-" + Guid.NewGuid().ToString("N") + ".toc");
                try
                {
                    using (var tocFile = new FileStream(tocPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    { byte[] bytes = new UTF8Encoding(false).GetBytes(selectedToc); tocFile.Write(bytes); }
                    RunPostgreSqlTool(new List<string> { "pg_restore", "--host", Options.Host, "--port", Options.Port.ToString(CultureInfo.InvariantCulture),
                        "--username", Options.Username, "--dbname", "dbname=" + ReignPostgreSqlOptions.ValidationDatabaseName
                            + " application_name=" + toolApplicationName + " connect_timeout=10",
                        "--use-list", PostgreSqlTools.ArchivePath(tocPath, !string.IsNullOrWhiteSpace(NativePostgreSqlBin())),
                        "--single-transaction", "--exit-on-error", "--no-owner", "--no-privileges", archiveArgument }, 600000, requireProcessTermination: true);
                }
                finally
                {
                    try { File.Delete(tocPath); }
                    catch (IOException ex) { failures.Add(ex); }
                    catch (UnauthorizedAccessException ex) { failures.Add(ex); }
                }
                using (var command = new NpgsqlCommand("SELECT oid::bigint,obj_description(oid,'pg_namespace') FROM pg_namespace WHERE nspname=@schema;", connection))
                {
                    command.Parameters.AddWithValue("schema", schema); using var reader = command.ExecuteReader();
                    if (!reader.Read()) throw new InvalidDataException("Filtered archive has no native campaign schema.");
                    if (!ValidationArchiveOwnerMatches(oid, owner, reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1)))
                        throw new InvalidOperationException("Native restore changed namespace ownership; cleanup refused.");
                }
                if (oid <= 0 || !ValidationArchiveSchemas(connection).SetEquals(before.Append(schema)))
                    throw new InvalidOperationException("Filtered native restore changed an unexpected namespace; no foreign namespace will be removed.");
                fault?.Invoke("after_restore_before_registry");
                file.Position = 0;
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Native archive changed during restore.");
                ExecuteNonQuery(connection, null, @"INSERT INTO reign_meta.campaign_registry
(campaign_id,schema_name,created_utc,updated_utc,migration_state) VALUES(@campaign,@schema,now(),now(),@owner);",
                    new Dictionary<string, object> { ["campaign"] = campaign, ["schema"] = schema, ["owner"] = registryOwner });
                result = inspect(new ValidationCampaignArchiveReceipt(campaign, schema, expectedSha256.ToLowerInvariant(), owner, oid, toolApplicationName));
            }
            catch (Exception ex) { failures.Add(ex); }
            finally
            {
                try
                {
                    ValidationArchiveQuiesceTool(connection, toolApplicationName);
                    if (restored)
                    {
                        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                        ExecuteNonQuery(connection, transaction, "SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s';");
                        AcquireCampaignLock(connection, transaction, campaign);
                        using (var command = new NpgsqlCommand(@"SELECT n.oid::bigint,obj_description(n.oid,'pg_namespace')
FROM pg_namespace n WHERE n.nspname=@schema;", connection, transaction))
                        {
                            command.Parameters.AddWithValue("schema", schema); using var reader = command.ExecuteReader();
                            if (!reader.Read() || !ValidationArchiveOwnerMatches(oid, owner, reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1)))
                                throw new InvalidOperationException("Native replay ownership changed; cleanup refused.");
                        }
                        using (var command = new NpgsqlCommand("SELECT count(*) FROM reign_meta.save_sync_snapshots WHERE campaign_id=@campaign;", connection, transaction))
                        {
                            command.Parameters.AddWithValue("campaign", campaign);
                            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                                throw new InvalidOperationException("Native replay acquired unexpected snapshots; cleanup refused.");
                        }
                        using (var command = new NpgsqlCommand(@"SELECT EXISTS(SELECT 1 FROM reign_meta.campaign_registry
WHERE (campaign_id=@campaign OR schema_name=@schema)
AND NOT(campaign_id=@campaign AND schema_name=@schema AND migration_state=@owner));", connection, transaction))
                        {
                            command.Parameters.AddWithValue("campaign", campaign); command.Parameters.AddWithValue("schema", schema);
                            command.Parameters.AddWithValue("owner", registryOwner);
                            if (command.ExecuteScalar() is not false)
                                throw new InvalidOperationException("Native replay registry ownership changed; cleanup refused.");
                        }
                        var relations = new List<string>();
                        using (var command = new NpgsqlCommand(@"SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname=@schema AND c.relkind IN ('r','p','m') ORDER BY c.relname;", connection, transaction))
                        {
                            command.Parameters.AddWithValue("schema", schema); using var reader = command.ExecuteReader();
                            while (reader.Read()) relations.Add(QuoteIdentifier(schema) + "." + QuoteIdentifier(reader.GetString(0)));
                        }
                        if (relations.Count > 0) ExecuteNonQuery(connection, transaction,
                            "LOCK TABLE " + string.Join(",", relations) + " IN ACCESS EXCLUSIVE MODE;");
                        if (ValidationArchiveHasForeignDependents(connection, transaction, oid, schema))
                            throw new InvalidOperationException("Native replay has foreign dependent objects; cleanup refused.");
                        ExecuteNonQuery(connection, transaction, "DROP SCHEMA " + QuoteIdentifier(schema) + " CASCADE;");
                        ExecuteNonQuery(connection, transaction, "DELETE FROM reign_meta.campaign_registry WHERE campaign_id=@campaign AND schema_name=@schema AND migration_state=@owner;",
                            new Dictionary<string, object> { ["campaign"] = campaign, ["schema"] = schema, ["owner"] = registryOwner });
                        transaction.Commit(); RegisteredCampaignSchemas.TryRemove(campaign, out _);
                        if (!ValidationArchiveSchemas(connection).SetEquals(before))
                            throw new InvalidOperationException("Validation namespaces differ after native replay cleanup; foreign state was preserved.");
                    }
                }
                catch (Exception ex) { failures.Add(ex); }
                finally
                {
                    try
                    {
                        using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key,0));", connection) { CommandTimeout = 10 };
                        command.Parameters.AddWithValue("key", lockKey); command.ExecuteScalar();
                    }
                    catch (Exception ex) { failures.Add(ex); }
                }
            }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("Native archive inspection and/or cleanup failed.", failures);
            return result;
        }

        private static HashSet<string> ValidationArchiveSchemas(NpgsqlConnection connection)
        {
            using var command = new NpgsqlCommand("SELECT nspname FROM pg_namespace ORDER BY nspname;", connection);
            using var reader = command.ExecuteReader(); var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        }

        private static void ValidationArchiveQuiesceTool(NpgsqlConnection connection, string applicationName)
        {
            if (!Regex.IsMatch(applicationName ?? "", @"\Areign_validation_archive_[0-9a-f]{32}\z"))
                throw new InvalidOperationException("Invalid native restore session owner.");
            using (var command = new NpgsqlCommand(@"SELECT pg_terminate_backend(pid) FROM pg_stat_activity
WHERE datname=current_database() AND usename=current_user AND application_name=@application AND pid<>pg_backend_pid();", connection) { CommandTimeout = 10 })
            { command.Parameters.AddWithValue("application", applicationName); command.ExecuteNonQuery(); }
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                using var command = new NpgsqlCommand(@"SELECT EXISTS(SELECT 1 FROM pg_stat_activity
WHERE datname=current_database() AND usename=current_user AND application_name=@application AND pid<>pg_backend_pid());", connection) { CommandTimeout = 10 };
                command.Parameters.AddWithValue("application", applicationName);
                if (command.ExecuteScalar() is false) return;
                if (deadline.ElapsedMilliseconds > 10000)
                    throw new InvalidOperationException("Native restore session did not quiesce; owned schema retained and cleanup refused.");
                System.Threading.Thread.Sleep(50);
            }
        }

        private static bool ValidationArchiveHasForeignDependents(NpgsqlConnection connection,
            NpgsqlTransaction transaction, long schemaOid, string schema)
        {
            // Inspect the cascade closure rather than assuming that a new schema
            // cannot acquire a view/constraint/function owned by somebody else.
            using var command = new NpgsqlCommand(@"WITH RECURSIVE removed(classid,objid,objsubid) AS (
 SELECT 'pg_namespace'::regclass::oid,@oid::oid,0
 UNION
 SELECT d.classid,d.objid,d.objsubid FROM pg_depend d JOIN removed r
 ON d.refclassid=r.classid AND d.refobjid=r.objid AND (r.objsubid=0 OR d.refobjsubid=r.objsubid)
), scoped AS (
 SELECT r.*,COALESCE(i.schema,CASE r.classid
 WHEN 'pg_trigger'::regclass THEN (SELECT n.nspname FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE t.oid=r.objid)
 WHEN 'pg_rewrite'::regclass THEN (SELECT n.nspname FROM pg_rewrite w JOIN pg_class c ON c.oid=w.ev_class JOIN pg_namespace n ON n.oid=c.relnamespace WHERE w.oid=r.objid)
 WHEN 'pg_attrdef'::regclass THEN (SELECT n.nspname FROM pg_attrdef a JOIN pg_class c ON c.oid=a.adrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE a.oid=r.objid)
 END) AS object_schema
 FROM removed r CROSS JOIN LATERAL pg_identify_object(r.classid,r.objid,r.objsubid) i
 WHERE NOT(r.classid='pg_namespace'::regclass AND r.objid=@oid::oid)
)
SELECT EXISTS(SELECT 1 FROM scoped WHERE object_schema IS NULL OR object_schema NOT IN (@schema,'pg_toast'));", connection, transaction);
            command.Parameters.AddWithValue("oid", schemaOid); command.Parameters.AddWithValue("schema", schema);
            return command.ExecuteScalar() is not false;
        }
    }
}
