using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Npgsql;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static Dictionary<string, object> RunValidationNativeArchiveContracts(Dictionary<string, object> input, string inputHash)
        {
            if (Program.ActiveServerPort > 0 || !ReignPostgreSqlStorage.ValidationArchiveGate(ReignPostgreSqlStorage.Options.IsValidation,
                ReignPostgreSqlStorage.Options.Database, Environment.GetCommandLineArgs())
                || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value) || input.Count != 1)
                throw new InvalidOperationException("Native archive contracts require an isolated schema-only Lab request.");
            var checks = RunValidationNativeArchiveSelfTests();
            var expected = new[] { "live_database_gate_rejected", "validation_name_alone_is_not_authority", "ordinary_process_cannot_import",
                "wrong_database_rejected", "validation_lab_gate", "cleanup_requires_exact_oid_and_owner", "toc_retains_schema_definition_and_exact_namespace",
                "toc_missing_schema_definition_rejected", "toc_unknown_native_entry_rejected", "invalid_hash_length_rejected",
                "hash_mismatch_refused_before_import", "existing_campaign_schema_and_snapshot_refused",
                "second_session_cannot_claim_active_import", "filtered_restore_preserves_ids_bytes_and_native_qualified_function",
                "save_sync_schemas_are_not_imported", "owned_import_removed_after_callback", "advisory_lock_released_after_callback",
                "failed_inspection_cleans_only_owned_import", "owned_restore_backend_quiesced_before_cleanup", "unowned_backend_is_preserved",
                "missing_schema_restore_is_atomic", "failure_before_registry_cleans_stamped_owned_schema",
                "foreign_dependency_prevents_cascade_cleanup", "primary_failure_survives_cleanup_refusal",
                "foreign_schema_and_registry_preserved_after_refusal", "unrelated_campaign_remains_unchanged" }
                .Select(id => "native_archive_" + id).ToHashSet(StringComparer.Ordinal);
            bool ok = checks.Count == expected.Count && expected.SetEquals(checks.Select(c => ReadString(c, "caseId", "")))
                && checks.All(c => ReadBool(c, "passed", false));
            var report = TestDict("schema", "reign-native-archive-contracts-result-v1", "ok", ok,
                "inputSha256", inputHash, "cases", checks, "completedCases", checks.Count,
                "fullyReplayedCases", 0, "providerCalls", 0, "certificateEligible", false,
                "scope", "Synthetic run-owned PostgreSQL archive, source identity and cleanup contracts only; no retained native campaign imported.");
            string path = Path.Combine(Program.TestsDir, "memory-accuracy", "evidence-result-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            CampaignFileService.WriteJsonObject(path, report);
            return TestDict("ok", ok, "resultPath", path, "inputSha256", inputHash,
                "completedCases", checks.Count, "fullyReplayedCases", 0, "providerCalls", 0, "certificateEligible", false);
        }

        private static List<Dictionary<string, object>> RunValidationNativeArchiveSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "native_archive_" + id,
                "passed", pass, "summary", id, "data", data));
            Check("live_database_gate_rejected", !ReignPostgreSqlStorage.ValidationArchiveGate(false, "reign", new[] { "--run-verification" }));
            Check("validation_name_alone_is_not_authority", !ReignPostgreSqlStorage.ValidationArchiveGate(false, "ReignValidation", new[] { "--run-verification" }));
            Check("ordinary_process_cannot_import", !ReignPostgreSqlStorage.ValidationArchiveGate(true, "ReignValidation", Array.Empty<string>()));
            Check("wrong_database_rejected", !ReignPostgreSqlStorage.ValidationArchiveGate(true, "reign", new[] { "--run-verification" }));
            Check("validation_lab_gate", ReignPostgreSqlStorage.ValidationArchiveGate(true, "ReignValidation", new[] { "--run-verification" }));
            Check("cleanup_requires_exact_oid_and_owner", ReignPostgreSqlStorage.ValidationArchiveOwnerMatches(7, "owner", 7, "owner")
                && !ReignPostgreSqlStorage.ValidationArchiveOwnerMatches(7, "owner", 8, "owner")
                && !ReignPostgreSqlStorage.ValidationArchiveOwnerMatches(7, "owner", 7, "other")
                && !ReignPostgreSqlStorage.ValidationArchiveOwnerMatches(0, "", 0, ""));
            const string tocSchema = "reign_campaign_0123456789abcdef0123456789abcdef";
            string tocFixture = "1; 2615 1 SCHEMA - " + tocSchema + " owner\n2; 1259 2 TABLE " + tocSchema + " source_probe owner\n"
                + "3; 2615 3 SCHEMA - reign_save_other owner\n4; 1259 4 TABLE reign_save_other source_probe owner\n";
            string selectedToc = ReignPostgreSqlStorage.ValidationArchiveSelectToc(tocFixture, tocSchema);
            Check("toc_retains_schema_definition_and_exact_namespace", selectedToc.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2
                && selectedToc.Contains("SCHEMA - " + tocSchema, StringComparison.Ordinal) && !selectedToc.Contains("reign_save_other", StringComparison.Ordinal)
                && ReignPostgreSqlStorage.ValidationArchiveSelectToc(tocFixture, tocSchema, false) == "2; 1259 2 TABLE " + tocSchema + " source_probe owner\n");
            bool tocRefused = false;
            try { ReignPostgreSqlStorage.ValidationArchiveSelectToc("2; 1259 2 TABLE " + tocSchema + " source_probe owner\n", tocSchema); }
            catch (InvalidOperationException) { tocRefused = true; }
            Check("toc_missing_schema_definition_rejected", tocRefused);
            tocRefused = false;
            try { ReignPostgreSqlStorage.ValidationArchiveSelectToc(tocFixture + "5; 1259 5 UNKNOWN KIND " + tocSchema + " object owner\n", tocSchema); }
            catch (InvalidDataException) { tocRefused = true; }
            Check("toc_unknown_native_entry_rejected", tocRefused);
            // Other legacy self-test entrypoints can check the pure guard, but
            // only the artifact-bound Lab may perform the archive integration.
            if (!ReignPostgreSqlStorage.ValidationArchiveGate(ReignPostgreSqlStorage.Options.IsValidation,
                ReignPostgreSqlStorage.Options.Database, Environment.GetCommandLineArgs())) return results;
            string campaign = "native_archive_test_" + Guid.NewGuid().ToString("N");
            string control = "native_archive_control_" + Guid.NewGuid().ToString("N");
            string schema = ReignPostgreSqlStorage.CampaignSchemaName(campaign);
            string controlSchema = ReignPostgreSqlStorage.CampaignSchemaName(control);
            string root = Path.Combine(Program.TestsDir, "memory-accuracy", "native-archive-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string archive = Path.Combine(root, "fixture.dump");
            const string original = "Original quoted source.\nThe condition remains attached.";
            void Sql(NpgsqlConnection connection, string sql)
            { using var command = new NpgsqlCommand(sql, connection); command.ExecuteNonQuery(); }
            bool Exists(string id) => ReignPostgreSqlStorage.ListCampaignMetadata().Any(r => ReadString(r, "campaignId", "") == id);
            bool SchemaExists(string id)
            {
                using var connection = ReignPostgreSqlStorage.OpenMetadataConnection();
                using var command = new NpgsqlCommand(DialogueStore.SelectPgNamespaceSql, connection);
                command.Parameters.AddWithValue("schema", ReignPostgreSqlStorage.CampaignSchemaName(id));
                return command.ExecuteScalar() is true;
            }
            try
            {
                using (var connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaign))
                {
                    Sql(connection, DialogueSchema.CreateSourceProbeSql);
                    using var insert = new NpgsqlCommand(DialogueStore.InsertSourceProbeSql, connection);
                    insert.Parameters.AddWithValue("body", original); insert.ExecuteNonQuery();
                    Sql(connection, DialogueSchema.CreateSql(schema));
                }
                using (var connection = ReignPostgreSqlStorage.OpenCampaignConnection(control))
                    Sql(connection, DialogueSchema.CreateSentinelSql);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "unselected_snapshot");
                ReignPostgreSqlStorage.ExportCampaignArchive(campaign, archive);
                string pin = PersistenceService.FileSha256(archive);
                bool refused = false;
                try { ReignPostgreSqlStorage.WithValidationCampaignArchive(campaign, archive, "short", _ => false); }
                catch (InvalidDataException) { refused = true; }
                Check("invalid_hash_length_rejected", refused && Exists(campaign));
                refused = false;
                try { ReignPostgreSqlStorage.WithValidationCampaignArchive(campaign, archive, new string('0', 64), _ => false); }
                catch (InvalidDataException) { refused = true; }
                Check("hash_mismatch_refused_before_import", refused && Exists(campaign));
                refused = false;
                try { ReignPostgreSqlStorage.WithValidationCampaignArchive(campaign, archive, pin, _ => false); }
                catch (InvalidOperationException ex) { refused = ex.Message.Contains("existing campaign", StringComparison.Ordinal); }
                Check("existing_campaign_schema_and_snapshot_refused", refused && ReignPostgreSqlStorage.SnapshotExists(campaign, "unselected_snapshot"));
                ReignPostgreSqlStorage.DropCampaign(campaign);
                var restored = ReignPostgreSqlStorage.WithValidationCampaignArchive(campaign, archive, pin.ToUpperInvariant(), receipt =>
                {
                    bool secondRefused = false;
                    try { ReignPostgreSqlStorage.WithValidationCampaignArchive(campaign, archive, pin, _ => false); }
                    catch (InvalidOperationException ex) { secondRefused = ex.Message.Contains("already owned", StringComparison.Ordinal); }
                    Check("second_session_cannot_claim_active_import", secondRefused);
                    using var connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaign);
                    using var command = new NpgsqlCommand(DialogueStore.SelectSourceProbeSql, connection);
                    using var reader = command.ExecuteReader();
                    bool exact = reader.Read() && reader.GetString(0) == "original_turn" && reader.GetString(1) == original
                        && reader.GetString(2) == original && !reader.Read();
                    return TestDict("exact", exact, "nativeName", receipt.Schema == schema,
                        "snapshotExcluded", !ReignPostgreSqlStorage.SnapshotExists(campaign, "unselected_snapshot"));
                });
                Check("filtered_restore_preserves_ids_bytes_and_native_qualified_function", ReadBool(restored, "exact", false) && ReadBool(restored, "nativeName", false));
                Check("save_sync_schemas_are_not_imported", ReadBool(restored, "snapshotExcluded", false));
                Check("owned_import_removed_after_callback", !Exists(campaign) && !SchemaExists(campaign));
                using (var connection = ReignPostgreSqlStorage.OpenMetadataConnection())
                {
                    using var command = new NpgsqlCommand(DialogueStore.SelectSql2, connection);
                    command.Parameters.AddWithValue("key", "validation-native-archive:" + campaign);
                    bool released = command.ExecuteScalar() is true;
                    command.CommandText = DialogueStore.SelectSql3;
                    Check("advisory_lock_released_after_callback", released && command.ExecuteScalar() is true);
                }
                bool failed = false;
                NpgsqlConnection ownedBackend = null;
                int ownedBackendPid = 0;
                bool ownedBackendWasStopped = false;
                using var unownedBackend = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ReignPostgreSqlStorage.Options.BuildConnectionString())
                    { Pooling = false, ApplicationName = "reign_validation_archive_" + Guid.NewGuid().ToString("N") }.ConnectionString);
                unownedBackend.Open();
                try
                {
                    ReignPostgreSqlStorage.WithValidationCampaignArchive<bool>(campaign, archive, pin,
                        receipt =>
                        {
                            ownedBackend = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ReignPostgreSqlStorage.Options.BuildConnectionString())
                                { Pooling = false, ApplicationName = receipt.ToolApplicationName }.ConnectionString);
                            ownedBackend.Open();
                            using var pid = new NpgsqlCommand(DialogueStore.SelectSql4, ownedBackend);
                            ownedBackendPid = Convert.ToInt32(pid.ExecuteScalar(), CultureInfo.InvariantCulture);
                            throw new InvalidOperationException("injected_native_inspection_failure");
                        });
                }
                catch (InvalidOperationException ex) { failed = ex.Message == "injected_native_inspection_failure"; }
                finally
                {
                    try
                    {
                        // Check before client disposal, which would otherwise
                        // make this assertion pass without server quiescence.
                        using var connection = ReignPostgreSqlStorage.OpenMetadataConnection();
                        using var command = new NpgsqlCommand(DialogueStore.SelectPgStatActivitySql, connection);
                        command.Parameters.AddWithValue("pid", ownedBackendPid);
                        ownedBackendWasStopped = ownedBackendPid > 0 && command.ExecuteScalar() is true;
                    }
                    finally { ownedBackend?.Dispose(); }
                }
                Check("failed_inspection_cleans_only_owned_import", failed && !Exists(campaign) && Exists(control));
                Check("owned_restore_backend_quiesced_before_cleanup", ownedBackendWasStopped);
                using (var command = new NpgsqlCommand(DialogueStore.SelectSql5, unownedBackend))
                    Check("unowned_backend_is_preserved", Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1);
                string missing = "native_archive_missing_" + Guid.NewGuid().ToString("N");
                refused = false;
                try { ReignPostgreSqlStorage.WithValidationCampaignArchive(missing, archive, pin, _ => false); }
                catch (InvalidOperationException) { refused = true; }
                Check("missing_schema_restore_is_atomic", refused && !Exists(missing) && !SchemaExists(missing));
                refused = false;
                try
                {
                    ReignPostgreSqlStorage.WithValidationCampaignArchive(campaign, archive, pin, _ => false,
                        stage => { if (stage == "after_restore_before_registry") throw new InvalidOperationException("injected_before_registry"); });
                }
                catch (InvalidOperationException ex) { refused = ex.Message == "injected_before_registry"; }
                Check("failure_before_registry_cleans_stamped_owned_schema", refused && !Exists(campaign) && !SchemaExists(campaign));
                bool protectedForeign = false, primaryKept = false;
                try
                {
                    ReignPostgreSqlStorage.WithValidationCampaignArchive<bool>(campaign, archive, pin, _ =>
                    {
                        using var connection = ReignPostgreSqlStorage.OpenCampaignConnection(control);
                        Sql(connection, DialogueSchema.CreateSql2(schema));
                        throw new InvalidOperationException("injected_primary_failure_with_foreign_view");
                    });
                }
                catch (AggregateException ex)
                {
                    protectedForeign = ex.Flatten().InnerExceptions.Any(e => e.Message.Contains("foreign dependent", StringComparison.Ordinal));
                    primaryKept = ex.Flatten().InnerExceptions.Any(e => e.Message == "injected_primary_failure_with_foreign_view");
                }
                Check("primary_failure_survives_cleanup_refusal", primaryKept && protectedForeign);
                Check("foreign_schema_and_registry_preserved_after_refusal", protectedForeign && Exists(campaign) && SchemaExists(campaign));
                using (var connection = ReignPostgreSqlStorage.OpenCampaignConnection(control))
                {
                    using var command = new NpgsqlCommand(DialogueStore.CountForeignProbeSql, connection);
                    Check("foreign_dependency_prevents_cascade_cleanup", protectedForeign && Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1);
                    Sql(connection, DialogueSchema.DropSql);
                    using var sentinel = new NpgsqlCommand(DialogueStore.SelectSentinelSql, connection);
                    Check("unrelated_campaign_remains_unchanged", Convert.ToInt32(sentinel.ExecuteScalar(), CultureInfo.InvariantCulture) == 99);
                }
            }
            catch (Exception ex) { Check("integration_exception", false, DialogueService.LimitText(ex.ToString(), 2500)); }
            finally
            {
                // Both identities were generated by this exact test invocation.
                // No external archive or player campaign is used in this suite.
                ReignPostgreSqlStorage.DropCampaign(control);
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
            return results;
        }
    }
}
