using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private const string EditorJournalName = "active.json";
        private const string EditorRevisionPrefix = "editor_revision_";
        private static readonly Lazy<string[]> EditorJournalRoleList = new(() => EditorDocumentNames.Select(name => name + ".json")
            .Concat(EditorHistoryGroups.Select(group => "history/" + group + ".jsonl"))
            .Concat(new[] { "dynamic_characteristics.json", "editor_authority.json", "characters/index.json" }).ToArray());
        private static string[] EditorJournalRoles => EditorJournalRoleList.Value;

        private sealed record EditorJournalFile(string Role, [property: JsonRequired] string BeforeHash,
            [property: JsonRequired] int? BeforeMode, List<string> AfterHashes);
        private sealed record EditorFileJournal(int Version, string Campaign, string Hero, string Revision, string Generation,
            List<EditorJournalFile> Files);
        private sealed record EditorRecoveryFence(string Campaign, string JournalRoot, string Revision);

        private static readonly ConcurrentDictionary<string, EditorRecoveryFence> EditorRecoveryFences = new(StringComparer.Ordinal);
        [ThreadStatic] private static EditorRevisionFiles CurrentEditorRevisionFiles;
#if !REIGN_EXCLUDE_VERIFICATION
        [ThreadStatic] private static Action<string> EditorRevisionBoundaryForTests;
#endif

        private static void EditorRevisionBoundary(string boundary)
        {
#if !REIGN_EXCLUDE_VERIFICATION
            EditorRevisionBoundaryForTests?.Invoke(boundary);
#endif
        }

#if !REIGN_EXCLUDE_VERIFICATION
        private const string EditorRestartFixtureFlag = "--run-character-editor-restart-fixture";
        private const string EditorRestartHero = "fixture_editor_restart_hero";
        private const string EditorRestartName = "Restart fixture native name";
        private const int EditorRestartWriterExitCode = 86;
        private static string EditorRestartFixtureHome => Path.Combine(Program.TestsDir, "editor-restart-fixtures");
        private static string EditorRestartCampaign(string id) => "ce_restart_" + id;
        private static bool EditorRestartUsesValidation => OperatingSystem.IsLinux()
            && Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1"
            && Environment.GetEnvironmentVariable("REIGN_DB_NAME") == ReignPostgreSqlOptions.ValidationDatabaseName;

        internal static bool TryRunEditorRestartFixtureCommand(string[] args)
        {
            if (!args.Contains(EditorRestartFixtureFlag, StringComparer.Ordinal)) return false;
            if (!EditorRestartUsesValidation || args.Length != 3 || args[0] != EditorRestartFixtureFlag
                || !Guid.TryParseExact(args[1], "N", out Guid parsed) || args[1] != parsed.ToString("N")
                || args[2] is not ("before_sql_commit" or "after_sql_commit" or "recover"))
                throw new InvalidOperationException("A Character Editor restart fixture needs exact canonical validation ownership.");

            string id = args[1], phase = args[2];
            string fixture = Path.Combine(EditorRestartFixtureHome, id);
            string owner = Path.Combine(fixture, "fixture-owner");
            if (!EditorPhysicalPath(EditorRestartFixtureHome, owner, directory: false) || File.ReadAllText(owner) != id)
                throw new InvalidOperationException("The Character Editor restart fixture has no exact owner marker.");
            string campaigns = Path.Combine(fixture, "campaigns");
            string campaign = EditorRestartCampaign(id);
            if (!EditorPhysicalPath(fixture, Path.Combine(campaigns, campaign), directory: true))
                throw new InvalidOperationException("The Character Editor restart fixture has no exact campaign.");
            Program.CampaignsRootOverride.Value = campaigns;
            using var ownership = PortraitsService.AcquireOutfitRuntimeOwnership(fixture);
            if (phase == "recover")
            {
                DiscoverEditorPublicationsAtStartup();
                var found = EditorRecoveryStatus(campaign);
                if (ReadString(found, "journalKind", "") != "character_editor")
                    throw new InvalidDataException("Restart did not discover the owned Character Editor journal.");
                try
                {
                    CampaignFileService.CharacterDirectory(campaign, EditorRestartHero);
                    throw new InvalidDataException("The discovered Character Editor journal did not fence campaign access.");
                }
                catch (PersistenceService.SaveSyncCampaignFencedException ex)
                    when (ReadString(ex.Status, "operationId", "") == ReadString(found, "operationId", "")) { }
                RecoverEditorPublicationsAtStartup();
                DiscoverEditorPublicationsAtStartup();
                RecoverEditorPublicationsAtStartup();
                if (EditorRecoveryStatus(campaign) != null
                    || File.Exists(Path.Combine(EditorJournalRoot(campaigns, campaign), EditorJournalName)))
                    throw new InvalidDataException("Character Editor restart did not settle and retire the owned intent.");
                Environment.ExitCode = 0;
                return true;
            }

            string token;
            using (var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign))
            {
                if (connection == null) throw new InvalidOperationException("The parent fixture did not register its campaign.");
                token = ReadString(CharactersStore.SelectCharacterEditorHeads(connection, TestDict("id", EditorRestartHero))
                    .SingleOrDefault(), "revision_token", "");
            }
            if (token.Length == 0) throw new InvalidOperationException("The parent fixture did not prepare an editor token.");
            EditorRevisionBoundaryForTests = reached =>
            {
                if (reached == phase) Environment.Exit(EditorRestartWriterExitCode);
            };
            CharacterEditorSaveApi(TestDict("campaignId", campaign, "heroStringId", EditorRestartHero,
                "section", "state", "revisionToken", token, "documents", TestDict("state", TestDict("value", "after")),
                "nativeChanges", TestDict("name", EditorRestartName)));
            throw new InvalidOperationException("The Character Editor writer did not reach its hard-exit boundary.");
        }

        private static void RunEditorRestartFixtureChild(string id, string phase, int expectedExit)
        {
            string executable = Environment.ProcessPath;
            var start = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(CharactersService).Assembly.Location);
            start.ArgumentList.Add(EditorRestartFixtureFlag);
            start.ArgumentList.Add(id);
            start.ArgumentList.Add(phase);
            using var child = System.Diagnostics.Process.Start(start);
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(60000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
                throw new IOException("The isolated Character Editor restart child did not exit.");
            }
            if (child.ExitCode != expectedExit)
                throw new IOException("Character Editor restart child returned " + child.ExitCode + ": " + output.Result + error.Result);
        }

        private static void VerifyEditorHardExit(Action<string, bool, string> add, bool committed)
        {
            string caseId = committed ? "mixed_recovery_hard_exit_after_commit" : "mixed_recovery_hard_exit_before_commit";
            string id = Guid.NewGuid().ToString("N"), campaign = EditorRestartCampaign(id);
            string fixture = Path.Combine(EditorRestartFixtureHome, id);
            string campaigns = Path.Combine(fixture, "campaigns");
            string previousRoot = Program.CampaignsRootOverride.Value;
            Program.CampaignsRootOverride.Value = campaigns;
            bool passed = false;
            try
            {
                PersistenceService.WriteRecoveryFile(Path.Combine(fixture, "fixture-owner"), System.Text.Encoding.UTF8.GetBytes(id), replace: false);
                string state = CampaignFileService.CharacterFile(campaign, EditorRestartHero, "state.json");
                CampaignFileService.WriteJsonObject(state, TestDict("value", "before"));
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, EditorRestartHero, "profile.json"),
                    TestDict("heroStringId", EditorRestartHero, "name", "Restart fixture before"));
                var loaded = CharacterEditorLoadApi(TestDict("campaignId", campaign, "heroStringId", EditorRestartHero));
                string originalToken = ReadString(loaded, "revisionToken", "");
                if (!ReadBool(loaded, "ok", false) || originalToken.Length == 0)
                    throw new InvalidDataException("The Character Editor restart fixture could not load its token.");
                byte[] before = File.ReadAllBytes(state);
                RunEditorRestartFixtureChild(id, committed ? "after_sql_commit" : "before_sql_commit", EditorRestartWriterExitCode);

                string journalRoot = EditorJournalRoot(campaigns, campaign);
                var journal = ReadEditorJournal(campaigns, journalRoot, campaign);
                byte[] published = File.ReadAllBytes(state);
                var entry = journal.Files.Single(item => item.Role == "state.json");
                if (entry.BeforeHash != EditorFileHash(before) || entry.AfterHashes[^1] != EditorFileHash(published)
                    || ReadString(TryParseJsonObject(System.Text.Encoding.UTF8.GetString(published)), "value", "") != "after")
                    throw new InvalidDataException("The interrupted writer did not leave the exact file intent and postimage.");
                RunEditorRestartFixtureChild(id, "recover", 0);

                using var reader = ReignDatabase.OpenCampaignConnection(campaign);
                var revisions = ReignDatabase.QuerySql(reader, "SELECT revision_id,hero_id FROM character_editor_revisions;");
                var commands = ReignDatabase.QuerySql(reader,
                    "SELECT revision_id,hero_id,status,changes_json FROM character_editor_native_commands;");
                var head = CharactersStore.SelectCharacterEditorHeads(reader, TestDict("id", EditorRestartHero)).Single();
                bool tokenChanged = ReadString(head, "revision_token", "") != originalToken;
                bool correctDatabase = committed
                    ? revisions.Count == 1 && commands.Count == 1 && tokenChanged
                        && ReadString(head, "latest_revision_id", "") == journal.Revision
                        && ReadString(revisions[0], "revision_id", "") == journal.Revision
                        && ReadString(revisions[0], "hero_id", "") == EditorRestartHero
                        && ReadString(commands[0], "revision_id", "") == journal.Revision
                        && ReadString(commands[0], "hero_id", "") == EditorRestartHero
                        && ReadString(commands[0], "status", "") == "pending"
                        && ReadString(TryParseJsonObject(ReadString(commands[0], "changes_json", "")), "name", "") == EditorRestartName
                    : revisions.Count == 0 && commands.Count == 0 && !tokenChanged;
                passed = correctDatabase && journal.Hero == EditorRestartHero
                    && EditorProfileRestoreGeneration(reader) == journal.Generation
                    && !File.Exists(Path.Combine(journalRoot, EditorJournalName))
                    && File.ReadAllBytes(state).SequenceEqual(committed ? published : before);
                add(caseId, passed, committed
                    ? "A real exit after COMMIT retains the exact file bytes and one revision and native command across restart and replay."
                    : "A real exit before COMMIT restores the exact original file bytes and leaves no revision or native command after restart.");
            }
            catch (Exception ex) { add(caseId, false, ex.Message); }
            finally
            {
                try
                {
                    ReignPostgreSqlStorage.DropCampaign(campaign);
                    if (passed) Directory.Delete(fixture, recursive: true);
                }
                finally { Program.CampaignsRootOverride.Value = previousRoot; }
            }
        }

        private static void VerifyEditorRevisionRecovery(Action<string, bool, string> add)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation) return;
            if (EditorRestartUsesValidation && Environment.GetCommandLineArgs().Contains("--run-verification", StringComparer.Ordinal))
            {
                VerifyEditorHardExit(add, committed: false);
                VerifyEditorHardExit(add, committed: true);
            }
            VerifyEditorRevisionWitness(add);
            add("mixed_recovery_catalog_projection_does_not_publish", ReputationService.VerifySocialCatalogProjectionReadOnly(),
                "The revision snapshot expands a missing or legacy global social catalog in memory without creating or rewriting it.");
            string campaign = "ce_recovery_" + Guid.NewGuid().ToString("N");
            const string hero = "fixture_hero";
            const string failedSave = "mixed_recovery_aborts_file_and_sql_save";
            const string lostReply = "mixed_recovery_retains_committed_file_and_sql_save";
            const string failedRollback = "mixed_recovery_aborts_rollback_and_preserves_later_fields";
            const string lateFile = "mixed_recovery_fences_unexpected_file_and_replays_durable_intent";
            var remaining = new HashSet<string>(new[] { failedSave, lostReply, failedRollback, lateFile });
            void Record(string id, bool passed, string summary)
            {
                remaining.Remove(id);
                add(id, passed, summary);
            }
            string campaigns = CampaignFileService.CampaignsRoot();
            string journalRoot = EditorJournalRoot(campaigns, campaign);
            try
            {
                string state = CampaignFileService.CharacterFile(campaign, hero, "state.json");
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, hero, "profile.json"),
                    TestDict("heroStringId", hero, "name", "Fixture before"));
                CampaignFileService.WriteJsonObject(state, TestDict("edited", "before", "independent", "keep", "competing", "original"));
                byte[] pristine = File.ReadAllBytes(state);
                var loaded = CharacterEditorLoadApi(TestDict("campaignId", campaign, "heroStringId", hero));
                var request = TestDict("campaignId", campaign, "heroStringId", hero, "section", "state",
                    "revisionToken", ReadString(loaded, "revisionToken", ""), "documents",
                    TestDict("state", TestDict("edited", "after", "independent", "keep", "competing", "saved")),
                    "nativeChanges", TestDict("name", "Fixture after"));
                int RevisionCount()
                {
                    using var reader = ReignDatabase.OpenCampaignConnection(campaign);
                    return ReignDatabase.QuerySql(reader, "SELECT revision_id FROM character_editor_revisions;").Count;
                }
                string[] NativeStatuses()
                {
                    using var reader = ReignDatabase.OpenCampaignConnection(campaign);
                    return ReignDatabase.QuerySql(reader, "SELECT status FROM character_editor_native_commands;")
                        .Select(row => ReadString(row, "status", "")).ToArray();
                }
                bool JournalCleared() => EditorRecoveryStatus(campaign) == null
                    && !File.Exists(Path.Combine(journalRoot, EditorJournalName));
                bool RejectAt(string boundary, Func<Dictionary<string, object>> operation)
                {
                    EditorRevisionBoundaryForTests = reached =>
                    {
                        if (reached == boundary) throw new IOException("Injected Character Editor publication interruption.");
                    };
                    try { operation(); }
                    catch (IOException ex) when (ex.Message == "Injected Character Editor publication interruption.") { return true; }
                    finally { EditorRevisionBoundaryForTests = null; }
                    return false;
                }
                bool rejectedFile = RejectAt("after_file_replace", () => CharacterEditorSaveApi(request));
                bool restoredFile = File.ReadAllBytes(state).SequenceEqual(pristine) && RevisionCount() == 0 && JournalCleared();
                bool rejectedCommit = RejectAt("before_sql_commit", () => CharacterEditorSaveApi(request));
                Record(failedSave, rejectedFile && restoredFile && rejectedCommit && File.ReadAllBytes(state).SequenceEqual(pristine)
                    && RevisionCount() == 0 && NativeStatuses().Length == 0 && JournalCleared(),
                    "Both a file-publication interruption and a pre-COMMIT interruption restore the original bytes without a revision or native command.");

                EditorRevisionBoundaryForTests = reached =>
                {
                    if (reached == "after_sql_commit") throw new IOException("Injected loss of the Character Editor COMMIT response.");
                };
                Dictionary<string, object> saved;
                try { saved = CharacterEditorSaveApi(request); }
                finally { EditorRevisionBoundaryForTests = null; }
                Record(lostReply, ReadBool(saved, "ok", false) && RevisionCount() == 1 && JournalCleared()
                    && NativeStatuses().SequenceEqual(new[] { "pending" })
                    && ReadString(CampaignFileService.ReadJsonObject(state), "edited", "") == "after",
                    "The exact PostgreSQL witness retains the committed file postimage after a lost COMMIT response.");

                CampaignFileService.WriteJsonObject(state, TestDict("edited", "after", "independent", "newer", "competing", "later"));
                byte[] later = File.ReadAllBytes(state);
                var rollback = TestDict("campaignId", campaign, "heroStringId", hero, "revisionId", ReadString(saved, "revisionId", ""));
                bool rejectedRollback = RejectAt("before_sql_commit", () => CharacterEditorRollbackApi(rollback));
                bool restoredRollback = File.ReadAllBytes(state).SequenceEqual(later) && RevisionCount() == 1 && JournalCleared()
                    && NativeStatuses().SequenceEqual(new[] { "pending" });
                var undone = CharacterEditorRollbackApi(rollback);
                var current = CampaignFileService.ReadJsonObject(state);
                Record(failedRollback, rejectedRollback && restoredRollback && ReadBool(undone, "ok", false)
                    && ReadString(current, "edited", "") == "before" && ReadString(current, "independent", "") == "newer"
                    && ReadString(current, "competing", "") == "later"
                    && ReadDictionaryList(undone, "recordEditIssues").Any(row => ReadString(row, "column", "") == "competing")
                    && RevisionCount() == 2 && JournalCleared() && NativeStatuses().SequenceEqual(new[] { "superseded" }),
                    "An interrupted rollback keeps its own preimage. Successful rollback updates only the original still-owned field.");

                byte[] previousBytes = File.ReadAllBytes(state);
                byte[] publishedBytes = null;
                const string unexpected = "{\"edited\":\"unexpected later bytes\"}";
                request["revisionToken"] = ReadString(undone, "revisionToken", "");
                EditorRevisionBoundaryForTests = reached =>
                {
                    if (reached != "after_file_replace") return;
                    publishedBytes = File.ReadAllBytes(state);
                    File.WriteAllText(state, unexpected);
                    throw new IOException("Injected interrupted publication with an unexpected file target.");
                };
                bool fenced = false;
                try { CharacterEditorSaveApi(request); }
                catch (PersistenceService.SaveSyncCampaignFencedException)
                {
                    fenced = EditorRecoveryStatus(campaign) != null && File.ReadAllText(state) == unexpected
                        && File.Exists(Path.Combine(journalRoot, EditorJournalName));
                }
                finally { EditorRevisionBoundaryForTests = null; }
                if (fenced)
                {
                    File.WriteAllBytes(state, publishedBytes);
                    EditorRecoveryFences.TryRemove(journalRoot, out _);
                    DiscoverEditorPublicationsAtStartup();
                    RecoverEditorPublicationsAtStartup();
                }
                Record(lateFile, fenced && JournalCleared() && RevisionCount() == 2
                    && File.ReadAllBytes(state).SequenceEqual(previousBytes),
                    "Unrecognized bytes remain intact behind the campaign fence. Startup discovers the durable intent and restores the exact original when its known postimage returns.");
            }
            catch (Exception ex)
            {
                foreach (string id in remaining)
                    add(id, false, ex.Message);
            }
            finally
            {
                EditorRevisionBoundaryForTests = null;
                if (EditorRecoveryStatus(campaign) == null)
                {
                    ReignPostgreSqlStorage.DropCampaign(campaign);
                    string directory = CampaignFileService.CampaignDirectory(campaign);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
            }
        }

        private static void VerifyEditorRevisionWitness(Action<string, bool, string> add)
        {
            const string hero = "fixture_witness_hero";
            const string wrongHero = "mixed_recovery_wrong_hero_witness_is_unknown";
            const string missingHash = "mixed_recovery_missing_before_hash_is_not_absence";
            const string replacedManifest = "mixed_recovery_replaced_manifest_blocks_commit_and_cleanup";
            string campaign = "ce_witness_" + Guid.NewGuid().ToString("N");
            var remaining = new HashSet<string>(new[] { wrongHero, missingHash, replacedManifest });
            void Record(string id, bool passed, string summary)
            {
                remaining.Remove(id);
                add(id, passed, summary);
            }
            var before = System.Text.Encoding.UTF8.GetBytes("{\"basis\":\"original\"}");
            var after = System.Text.Encoding.UTF8.GetBytes("{\"basis\":\"published\"}");
            try
            {
                using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                string state = CampaignFileService.CharacterFile(campaign, hero, "state.json");
                PersistenceService.WriteRecoveryFile(state, before);
                var scope = new EditorRevisionFiles(campaign, hero, EditorRevisionPrefix + Guid.NewGuid().ToString("N"),
                    EditorProfileRestoreGeneration(connection));
                lock (Program.FileLock) scope.Write(state, after);
                string active = Path.Combine(scope.Root, EditorJournalName);
                var fence = new EditorRecoveryFence(campaign, scope.Root, scope.Journal.Revision);
                var originalIntent = File.ReadAllBytes(active);
                bool PublicationRetained() => File.Exists(active) && File.ReadAllBytes(state).SequenceEqual(after)
                    && EditorRecoveryStatus(campaign) != null;

                ReignDatabase.ExecuteSql(connection,
                    "INSERT INTO character_editor_revisions(revision_id,hero_id,created_ts) VALUES($revision,'other_hero',1);",
                    TestDict("revision", scope.Journal.Revision));
                EditorRecoveryFences[fence.JournalRoot] = fence;
                Record(wrongHero, TrySettleEditorRevision(fence) == null && PublicationRetained(),
                    "A revision UUID belonging to another hero retains the journal and postimage instead of authorizing compensation.");
                using (var cleanup = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign, allowEditorRecoveryWitness: true))
                    ReignDatabase.ExecuteSql(cleanup, "DELETE FROM character_editor_revisions WHERE revision_id=$revision;",
                        TestDict("revision", scope.Journal.Revision));

                var omitted = System.Text.Json.Nodes.JsonNode.Parse(originalIntent);
                var entry = omitted["Files"][0].AsObject();
                entry.Remove("BeforeHash");
                entry["BeforeMode"] = null;
                File.WriteAllText(active, omitted.ToJsonString());
                Record(missingHash, TrySettleEditorRevision(fence) == null && PublicationRetained(),
                    "An omitted before hash cannot be treated as an explicitly absent original.");

                var replacement = System.Text.Json.Nodes.JsonNode.Parse(originalIntent);
                replacement["Files"] = new System.Text.Json.Nodes.JsonArray();
                File.WriteAllText(active, replacement.ToJsonString());
                bool commitRejected = false;
                try { scope.PrepareCommit(); }
                catch (InvalidDataException ex) when (ex.Message == "Character Editor journal bytes changed during publication.")
                { commitRejected = true; }
                bool retained = commitRejected && TrySettleEditorRevision(fence, scope.PublishedIntent) == null && PublicationRetained();
                File.WriteAllBytes(active, originalIntent);
                Record(replacedManifest, retained && TrySettleEditorRevision(fence) == false
                    && !File.Exists(active) && File.ReadAllBytes(state).SequenceEqual(before),
                    "A syntactically valid replaced manifest cannot commit or settle. The exact original journal still restores the preimage.");
            }
            catch (Exception ex)
            {
                foreach (string id in remaining) add(id, false, ex.Message);
            }
            finally
            {
                if (EditorRecoveryStatus(campaign) == null)
                {
                    ReignPostgreSqlStorage.DropCampaign(campaign);
                    string directory = CampaignFileService.CampaignDirectory(campaign);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
            }
        }
#endif

        internal static Dictionary<string, object> OwnEditorCampaignWrite(Func<Dictionary<string, object>> action)
        {
            if (Program.CampaignDataGate.IsWriteLockHeld) return action();
            Program.CampaignDataGate.EnterWriteLock();
            try { return action(); }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> RunEditorRevisionMutation(ReignDbConnection connection, string campaign, string hero,
            Func<ReignDbTransaction, string, Dictionary<string, object>> apply)
        {
            PrepareEditorSnapshotSchemas(connection, publishCatalog: false);
            string generation = EditorProfileRestoreGeneration(connection);
            if (string.IsNullOrEmpty(generation)) throw new InvalidDataException("The campaign restore generation is unavailable.");
            var scope = new EditorRevisionFiles(campaign, hero, EditorRevisionPrefix + Guid.NewGuid().ToString("N"), generation);
            Dictionary<string, object> response = null;
            Exception failure = null;
            bool commitAttempted = false;
            try
            {
                using var suppressRetries = new ReignDatabase.SqlStatementRetrySuppression(connection);
                using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
                AcquireEditorRevisionWitnessLock(connection, transaction, scope.Journal.Revision);
                CurrentEditorRevisionFiles = scope;
                response = apply(transaction, scope.Journal.Revision);
                if (ReadBool(response, "ok", false))
                {
                    scope.PrepareCommit();
                    EditorRevisionBoundary("before_sql_commit");
                    commitAttempted = true;
                    transaction.Commit();
                    EditorRevisionBoundary("after_sql_commit");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { CurrentEditorRevisionFiles = null; }

            if (scope.HasJournal)
            {
                // The successful transport return is not the commit witness. In particular,
                // a lost reply to COMMIT cannot justify subtracting the file side.
                var fence = new EditorRecoveryFence(campaign, scope.Root, scope.Journal.Revision);
                EditorRecoveryFences[scope.Root] = fence;
                bool? committed = TrySettleEditorRevision(fence, scope.PublishedIntent);
                if (!committed.HasValue || (committed.Value && !ReadBool(response, "ok", false)))
                    throw new PersistenceService.SaveSyncCampaignFencedException(EditorRecoveryStatus(campaign)
                        ?? EditorRecoveryError(fence));
                if (committed.Value)
                {
                    DialogueService.InvalidatePromptRuntimeCache();
                    return response;
                }
                if (commitAttempted && failure == null)
                    throw new IOException("Character Editor COMMIT did not produce its exact revision witness.");
            }

            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return response;
        }

        internal static bool TryWriteEditorRevisionFile(string path, Func<byte[]> bytes)
        {
            var scope = CurrentEditorRevisionFiles;
            if (scope == null) return false;
            scope.Write(path, bytes());
            return true;
        }

        private sealed class EditorRevisionFiles
        {
            internal readonly string Root;
            internal readonly EditorFileJournal Journal;
            internal bool HasJournal;
            internal string StagingDirectory => Path.Combine(Root, Journal.Revision);
            internal byte[] PublishedIntent { get; private set; }

            internal EditorRevisionFiles(string campaign, string hero, string revision, string generation)
            {
                if (!IsEditorStorageId(campaign) || !IsEditorStorageId(hero))
                    throw new InvalidDataException("Character Editor recovery requires exact campaign and character IDs.");
                Root = EditorJournalRoot(CampaignFileService.CampaignsRoot(), campaign);
                Journal = new EditorFileJournal(1, campaign, hero, revision, generation, new());
            }

            internal void Write(string path, byte[] bytes)
            {
                if (!System.Threading.Monitor.IsEntered(Program.FileLock))
                    throw new InvalidOperationException("Character Editor file publication requires FileLock.");
                string campaigns = CampaignFileService.CampaignsRoot();
                string fullPath = Path.GetFullPath(path);
                string role = EditorJournalRoles.SingleOrDefault(candidate => EditorRolePath(campaigns, Journal, candidate) == fullPath);
                if (role == null) throw new InvalidDataException("An editor revision attempted an unowned file write.");

                VerifyOwnedIntent(campaigns);
                byte[] current = EditorReadFile(campaigns, path);
                string currentHash = EditorFileHash(current);
                string afterHash = EditorFileHash(bytes);
                int index = Journal.Files.FindIndex(entry => entry.Role == role);
                var entry = index < 0 ? null : Journal.Files[index];
                if (entry != null && currentHash != entry.AfterHashes[^1])
                    throw new IOException("Character Editor file changed during revision publication.");
                if (currentHash == afterHash) return;

                // A second write to the same file may be interrupted after publication
                // of its intent but before replacement. Retain each permitted intermediate.
                if (entry == null)
                {
                    int? beforeMode = current != null && OperatingSystem.IsLinux() ? (int)File.GetUnixFileMode(path) : null;
                    index = Journal.Files.Count;
                    entry = new EditorJournalFile(role, currentHash, beforeMode, new());
                    if (current != null)
                    {
                        string backup = EditorPreimagePath(Root, Journal.Revision, index);
                        if (EditorPhysicalPath(campaigns, backup, directory: false))
                            throw new IOException("Reused Character Editor recovery preimage.");
                        PersistenceService.WriteRecoveryFile(backup, current, replace: false);
                    }
                    Journal.Files.Add(entry);
                }
                entry.AfterHashes.Add(afterHash);
                PublishIntent();
                EditorRevisionBoundary("before_file_replace");
                if (bytes == null) File.Delete(path);
                else PersistenceService.WriteRecoveryFile(path, bytes, mode: EditorUnixMode(entry.BeforeMode),
                    temporaryDirectory: StagingDirectory);
                EditorRevisionBoundary("after_file_replace");
            }

            private void PublishIntent()
            {
                byte[] intent = JsonSerializer.SerializeToUtf8Bytes(Journal);
                PersistenceService.WriteRecoveryFile(Path.Combine(Root, EditorJournalName), intent, replace: HasJournal);
                PublishedIntent = intent;
                HasJournal = true;
            }

            private void VerifyOwnedIntent(string campaigns)
            {
                if (HasJournal != EditorPhysicalPath(campaigns, Path.Combine(Root, EditorJournalName), directory: false))
                    throw new IOException("Character Editor journal ownership changed during publication.");
                if (HasJournal) ReadEditorJournal(campaigns, Root, Journal.Campaign, PublishedIntent);
            }

            internal void PrepareCommit()
            {
                lock (Program.FileLock)
                {
                    string campaigns = CampaignFileService.CampaignsRoot();
                    if (Journal.Files.Any(entry => EditorFileHash(EditorReadFile(campaigns,
                        EditorRolePath(campaigns, Journal, entry.Role))) != entry.AfterHashes[^1]))
                        throw new IOException("Character Editor file replacement could not be verified before COMMIT.");
                    VerifyOwnedIntent(campaigns);
                    if (!HasJournal) PublishIntent();
                }
            }
        }

        private static void AcquireEditorRevisionWitnessLock(ReignDbConnection connection, ReignDbTransaction transaction, string revision)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@revision, 0));";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "revision";
            parameter.Value = revision;
            command.Parameters.Add(parameter);
            command.ExecuteNonQuery();
        }

        private static string EditorRecoveryRoot(string campaigns) => Path.Combine(campaigns, ".save-sync-work", "character-editor");
        private static string EditorJournalRoot(string campaigns, string campaign) => Path.Combine(EditorRecoveryRoot(campaigns), campaign);
        private static string EditorPreimagePath(string journalRoot, string revision, int index) =>
            Path.Combine(journalRoot, revision, "before-" + index.ToString(CultureInfo.InvariantCulture));

        private static string EditorRolePath(string campaigns, EditorFileJournal journal, string role) =>
            role == "characters/index.json"
                ? Path.Combine(campaigns, journal.Campaign, "characters", "index.json")
                : Path.Combine(campaigns, journal.Campaign, "characters", journal.Hero, role.Replace('/', Path.DirectorySeparatorChar));

        private static bool IsEditorStorageId(string value) => value is { Length: > 0 and <= 180 }
            && value != "." && value != ".." && !value.Equals(".save-sync-work", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+\z");

        private static void RequireEditorStorageIdentity(string campaign, string hero)
        {
            if (!IsEditorStorageId(campaign) || !IsEditorStorageId(hero))
                throw new InvalidDataException("Character Editor recovery requires exact campaign and character IDs.");
            string campaigns = CampaignFileService.CampaignsRoot();
            EditorPhysicalPath(campaigns, Path.Combine(campaigns, campaign, "characters", hero), directory: true);
        }

        private static bool IsEditorRevisionId(string value) => value != null && value.StartsWith(EditorRevisionPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value[EditorRevisionPrefix.Length..], "N", out Guid parsed)
            && value == EditorRevisionPrefix + parsed.ToString("N");

        private static string EditorFileHash(byte[] bytes) => bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes));
        private static bool IsEditorFileHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        private static UnixFileMode? EditorUnixMode(int? mode) => mode.HasValue ? (UnixFileMode)mode.Value : null;

        private static bool EditorPhysicalPath(string root, string path, bool directory)
        {
            string relative = Path.GetRelativePath(root, path);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || parts.Any(part => part is "." or ".."))
                throw new InvalidDataException("Character Editor recovery path escaped the managed campaign root.");
            const FileAttributes kind = FileAttributes.Directory | FileAttributes.ReparsePoint;
            try
            {
                if ((File.GetAttributes(root) & kind) != FileAttributes.Directory)
                    throw new InvalidDataException("Character Editor campaign root is not an ordinary directory.");
                for (int i = 0; i < parts.Length; i++)
                {
                    string candidate = Path.Combine(root, parts[i]);
                    FileAttributes expected = i < parts.Length - 1 || directory ? FileAttributes.Directory : 0;
                    if ((File.GetAttributes(candidate) & kind) != expected
                        || !Directory.EnumerateFileSystemEntries(root).Any(entry => Path.GetFileName(entry) == parts[i]))
                        throw new InvalidDataException("Character Editor recovery path is redirected or uses a different physical name.");
                    root = candidate;
                }
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static byte[] EditorReadFile(string campaigns, string path) =>
            EditorPhysicalPath(campaigns, path, directory: false) ? File.ReadAllBytes(path) : null;

        private static EditorFileJournal ReadEditorJournal(string campaigns, string root, string campaign, byte[] expectedIntent = null)
        {
            string path = Path.Combine(root, EditorJournalName);
            if (!EditorPhysicalPath(campaigns, path, directory: false) || new FileInfo(path).Length > 256 * 1024)
                throw new InvalidDataException("Character Editor journal is missing or has an invalid size.");
            byte[] intent = File.ReadAllBytes(path);
            if (expectedIntent != null && !intent.SequenceEqual(expectedIntent))
                throw new InvalidDataException("Character Editor journal bytes changed during publication.");
            var journal = JsonSerializer.Deserialize<EditorFileJournal>(intent);
            if (journal == null || journal.Version != 1 || journal.Campaign != campaign || !IsEditorStorageId(journal.Hero)
                || !IsEditorRevisionId(journal.Revision) || string.IsNullOrEmpty(journal.Generation)
                || journal.Files == null || journal.Files.Count > EditorJournalRoles.Length
                || journal.Files.Any(entry => entry == null || !EditorJournalRoles.Contains(entry.Role)
                    || (entry.BeforeHash != null && !IsEditorFileHash(entry.BeforeHash))
                    || (entry.BeforeMode.HasValue && (entry.BeforeHash == null || entry.BeforeMode.Value < 0 || entry.BeforeMode.Value > 4095))
                    || entry.AfterHashes == null || entry.AfterHashes.Count == 0
                    || entry.AfterHashes.Any(hash => hash != null && !IsEditorFileHash(hash)))
                || journal.Files.Select(entry => entry.Role).Distinct().Count() != journal.Files.Count)
                throw new InvalidDataException("Character Editor recovery journal is invalid.");
            return journal;
        }

        private static bool? ReadEditorRevisionCommit(EditorFileJournal journal)
        {
            using var witness = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(journal.Campaign,
                allowEditorRecoveryWitness: true);
            if (witness == null) return null;
            using var transaction = witness.BeginTransaction(IsolationLevel.ReadCommitted);
            // A dropped connection can leave its COMMIT in flight. Wait for the
            // original transaction's lock before taking the witness read snapshot.
            AcquireEditorRevisionWitnessLock(witness, transaction, journal.Revision);
            var row = ReignDatabase.QuerySql(witness, @"
SELECT state.restore_generation, revision.revision_id, revision.hero_id
FROM memory_precision_state AS state
LEFT JOIN character_editor_revisions AS revision ON revision.revision_id=$revision
WHERE state.state_id='current';", TestDict("revision", journal.Revision), transaction).SingleOrDefault();
            if (ReadString(row, "restore_generation", "") != journal.Generation) return null;
            if (ReadString(row, "revision_id", "").Length == 0) return false;
            return ReadString(row, "hero_id", "") == journal.Hero ? true : null;
        }

        private static bool? TrySettleEditorRevision(EditorRecoveryFence fence, byte[] expectedIntent = null)
        {
            string campaigns = CampaignFileService.CampaignsRoot();
            lock (Program.FileLock)
            {
                try
                {
                    var journal = ReadEditorJournal(campaigns, fence.JournalRoot, fence.Campaign, expectedIntent);
                    if (journal.Revision != fence.Revision || PersistenceService.HasSaveSyncJournalFence(journal.Campaign))
                        return null;
                    bool? commitment = ReadEditorRevisionCommit(journal);
                    if (!commitment.HasValue) return null;
                    bool committed = commitment.Value;
                    var preimages = new List<byte[]>();
                    for (int i = 0; i < journal.Files.Count; i++)
                    {
                        var entry = journal.Files[i];
                        string liveHash = EditorFileHash(EditorReadFile(campaigns, EditorRolePath(campaigns, journal, entry.Role)));
                        if (committed ? liveHash != entry.AfterHashes[^1]
                            : liveHash != entry.BeforeHash && !entry.AfterHashes.Contains(liveHash))
                            throw new IOException("Character Editor recovery will not overwrite unexpected file bytes.");
                        if (committed) continue;
                        var before = entry.BeforeHash == null ? null : EditorReadFile(campaigns, EditorPreimagePath(fence.JournalRoot, journal.Revision, i));
                        if (EditorFileHash(before) != entry.BeforeHash) throw new IOException("Character Editor recovery preimage changed.");
                        preimages.Add(before);
                    }
                    if (!committed)
                    {
                        for (int i = journal.Files.Count - 1; i >= 0; i--)
                        {
                            var entry = journal.Files[i];
                            string path = EditorRolePath(campaigns, journal, entry.Role);
                            string current = EditorFileHash(EditorReadFile(campaigns, path));
                            if (current == entry.BeforeHash) continue;
                            if (!entry.AfterHashes.Contains(current)) throw new IOException("Character Editor recovery target changed.");
                            if (preimages[i] == null) File.Delete(path);
                            else PersistenceService.WriteRecoveryFile(path, preimages[i], mode: EditorUnixMode(entry.BeforeMode),
                                temporaryDirectory: Path.Combine(fence.JournalRoot, journal.Revision));
                        }
                        if (journal.Files.Any(entry => EditorFileHash(EditorReadFile(campaigns,
                                EditorRolePath(campaigns, journal, entry.Role))) != entry.BeforeHash))
                            throw new IOException("Character Editor compensation could not verify the original bytes.");
                    }
                    PersistenceService.MarkSaveSyncActiveStateDirty(journal.Campaign);
                    File.Delete(Path.Combine(fence.JournalRoot, EditorJournalName));
                    EditorRecoveryFences.TryRemove(new KeyValuePair<string, EditorRecoveryFence>(fence.JournalRoot, fence));
                    for (int i = 0; i < journal.Files.Count; i++)
                        if (journal.Files[i].BeforeHash != null)
                            PersistenceService.TryDeleteFile(EditorPreimagePath(fence.JournalRoot, journal.Revision, i));
                    try { Directory.Delete(Path.Combine(fence.JournalRoot, journal.Revision), recursive: false); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    return committed;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Npgsql.NpgsqlException
                    or InvalidOperationException or InvalidDataException)
                {
                    OperationsService.LogOperational("character_editor.recovery_pending", TestDict(
                        "campaignId", fence.Campaign, "revisionId", fence.Revision, "error", ex.Message));
                    return null;
                }
            }
        }

        internal static bool HasEditorRecoveryFences => !EditorRecoveryFences.IsEmpty;

        internal static IEnumerable<string> FencedEditorCampaigns() => EditorRecoveryFences.Values
            .Where(fence => Path.GetDirectoryName(fence.JournalRoot) == EditorRecoveryRoot(CampaignFileService.CampaignsRoot()))
            .Select(fence => fence.Campaign);

        internal static Dictionary<string, object> EditorRecoveryStatus(string campaign)
        {
            if (!IsEditorStorageId(campaign)) return null;
            string campaigns = CampaignFileService.CampaignsRoot();
            if (EditorRecoveryFences.TryGetValue(EditorJournalRoot(campaigns, campaign), out var exact))
                return EditorRecoveryError(exact);
            var folded = EditorRecoveryFences.Values.FirstOrDefault(fence => Path.GetDirectoryName(fence.JournalRoot) == EditorRecoveryRoot(campaigns)
                && fence.Campaign.Equals(campaign, StringComparison.OrdinalIgnoreCase));
            if (folded == null) return null;
            try { EditorPhysicalPath(campaigns, Path.Combine(campaigns, campaign), directory: true); }
            catch (InvalidDataException) { return EditorRecoveryError(folded); }
            return null;
        }

        private static Dictionary<string, object> EditorRecoveryError(EditorRecoveryFence fence) =>
            TestDict("ok", false, "result", "recovery_required", "outcome", "recovery_required", "status", "recovery_required",
                "storageCategory", "recovery_required", "eligibleForPermanentDeletion", false, "campaignId", fence.Campaign,
                "operationId", fence.Revision, "backupId", fence.Revision, "journalKind", "character_editor",
                "journalPath", Path.Combine(fence.JournalRoot, EditorJournalName),
                "error", "An interrupted Character Editor revision needs recovery before this campaign can be changed.");

        private static IEnumerable<string> EditorJournalDirectories(string campaigns)
        {
            string root = EditorRecoveryRoot(campaigns);
            return EditorPhysicalPath(campaigns, root, directory: true) ? Directory.EnumerateFileSystemEntries(root) : Array.Empty<string>();
        }

        internal static void DiscoverEditorPublicationsAtStartup()
        {
            string campaigns = CampaignFileService.CampaignsRoot();
            foreach (string root in EditorJournalDirectories(campaigns))
            {
                string campaign = Path.GetFileName(root);
                if (!IsEditorStorageId(campaign)) throw new InvalidDataException("Character Editor journal campaign identity is invalid.");
                string revision;
                try
                {
                    if (!EditorPhysicalPath(campaigns, Path.Combine(root, EditorJournalName), directory: false)) continue;
                    revision = ReadEditorJournal(campaigns, root, campaign).Revision;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                { revision = "unreadable"; }
                EditorRecoveryFences[root] = new EditorRecoveryFence(campaign, root, revision);
            }
        }

        internal static void RecoverEditorPublicationsAtStartup()
        {
            foreach (var campaign in FencedEditorCampaigns().ToArray())
                if (EditorRecoveryFences.TryGetValue(EditorJournalRoot(CampaignFileService.CampaignsRoot(), campaign), out var fence)
                    && IsEditorRevisionId(fence.Revision))
                    TrySettleEditorRevision(fence);
        }

        internal static void RequireOfflineEditorCampaignAvailable(string campaigns, string campaign)
        {
            EditorPhysicalPath(campaigns, Path.Combine(campaigns, campaign), directory: true);
            if (EditorPhysicalPath(campaigns, Path.Combine(EditorJournalRoot(campaigns, campaign), EditorJournalName), directory: false))
                throw new InvalidOperationException("Character Editor recovery is pending for campaign " + campaign + ".");
        }

        internal static bool AnyOfflineEditorPublication(string campaigns) => EditorJournalDirectories(campaigns)
            .Any(root => EditorPhysicalPath(campaigns, Path.Combine(root, EditorJournalName), directory: false));
    }
}
