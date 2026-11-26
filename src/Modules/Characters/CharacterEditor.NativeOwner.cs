using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private const string EditorNativeRevisionBasisKind = "editor-native-revision-v1";
        private const string EditorNativeCommandOriginSchema = "reign-editor-native-command-origin-v1";

        private sealed record EditorNativeBrowserAdmission(
            PersistenceService.RegisteredEditorNativeOwner Owner, string HeroId, string CurrentRevisionToken);

        private static byte[] SignEditorNativeRevisionBasis(PersistenceService.RegisteredEditorNativeOwner owner,
            string hero, string revision) =>
            SignEditorProfileBasis(owner.CampaignId, hero, revision, owner.RestoreGeneration,
                Json.Serialize(new[] { owner.TimelineId, owner.LoadPointId, owner.LoadSessionId, owner.LineageId }),
                EditorNativeRevisionBasisKind);

        private static string IssueEditorNativeRevisionBasis(PersistenceService.RegisteredEditorNativeOwner owner,
            string hero, string revision) =>
            owner == null || string.IsNullOrEmpty(hero) || string.IsNullOrEmpty(revision)
                ? "" : Convert.ToHexString(SignEditorNativeRevisionBasis(owner, hero, revision));

        private static bool MatchesEditorNativeRevisionBasis(PersistenceService.RegisteredEditorNativeOwner owner,
            string hero, string revision, string basis)
        {
            if (owner == null || string.IsNullOrEmpty(hero) || string.IsNullOrEmpty(revision) || basis?.Length != 64)
                return false;
            try
            {
                return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(basis),
                    SignEditorNativeRevisionBasis(owner, hero, revision));
            }
            catch (FormatException) { return false; }
        }

        private static string IssueCurrentEditorNativeRevisionBasis(string campaign, string hero, string revisionToken) =>
            IssueEditorNativeRevisionBasis(PersistenceService.ReadRegisteredEditorNativeOwner(campaign), hero, revisionToken);

        private static bool OwnsEditorNativeBrowserWriteScope(string campaign) =>
            Program.CampaignDataGate.IsWriteLockHeld && WorldSimulationService.OwnsWorldHistoryTimelineScope(campaign);

        // Own the exact campaign and timeline across validation, file staging and SQL COMMIT.
        private static Dictionary<string, object> RunInEditorNativeBrowserWriteScope(string campaign,
            Func<Dictionary<string, object>> mutation) =>
            OwnEditorCampaignWrite(() =>
            {
                using var timeline = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
                lock (NarrativeStateLock) return mutation();
            });

        // Call before performing any part of a mixed browser edit.
        private static EditorNativeBrowserAdmission AdmitEditorNativeBrowserWrite(
            string campaign, string hero, string revisionToken, string basis)
        {
            if (!OwnsEditorNativeBrowserWriteScope(campaign)) return null;
            var owner = PersistenceService.ReadRegisteredEditorNativeOwner(campaign);
            return MatchesEditorNativeRevisionBasis(owner, hero, revisionToken, basis)
                ? new EditorNativeBrowserAdmission(owner, hero, revisionToken) : null;
        }

        private static void CommitOwnedEditorNativeRevisionMetadata(ReignDbConnection connection, string campaign,
            EditorNativeBrowserAdmission admission, Dictionary<string, object> revision,
            Dictionary<string, object> command, Dictionary<string, object> head,
            bool isRollback = false, ReignDbTransaction transaction = null)
        {
            if (!OwnsEditorNativeBrowserWriteScope(campaign) || admission == null || admission.Owner.CampaignId != campaign
                || admission.Owner != PersistenceService.ReadRegisteredEditorNativeOwner(campaign))
                throw new InvalidOperationException("The Character Editor native admission is no longer current.");

            string revisionId = ReadString(revision, isRollback ? "id" : "revision", "");
            string revisionChanges = ReadString(revision, "native", "");
            if (revisionId.Length == 0 || ReadString(revision, "hero", "") != admission.HeroId
                || ReadString(head, "hero", "") != admission.HeroId || ReadString(head, "revision", "") != revisionId
                || TryParseStrictJsonObject(revisionChanges) is not { } edits)
                throw new InvalidOperationException("The native revision and Editor head do not match.");
            if (command == null)
            {
                // A confirmed rollback can retire a pending original without issuing an inverse.
                if (!isRollback || edits.Count != 0)
                    throw new InvalidOperationException("The owned native edit is missing its command.");
            }
            else if (edits.Count == 0 || ReadString(command, "id", "").Length == 0
                || ReadString(command, "hero", "") != admission.HeroId || ReadString(command, "revision", "") != revisionId
                || ReadString(command, "changes", "") != revisionChanges)
                throw new InvalidOperationException("The native command does not match its revision.");

            CommitEditorRevisionMetadata(connection, revision, command, head, isRollback, transaction, admission);
        }

        private static void RequireUnchangedEditorNativeRevisionHead(ReignDbConnection connection,
            ReignDbTransaction transaction, EditorNativeBrowserAdmission admission)
        {
            var schema = ReignDatabase.QuerySql(connection, "SELECT current_schema() AS schema_name;", null, transaction).Single();
            if (ReadString(schema, "schema_name", "") != ReignPostgreSqlStorage.CampaignSchemaName(admission.Owner.CampaignId))
                throw new InvalidOperationException("The Editor native transaction belongs to a different campaign.");
            var head = CharactersStore.SelectCharacterEditorHeads(connection, TestDict("id", admission.HeroId), transaction).SingleOrDefault();
            if (ReadString(head, "revision_token", "") != admission.CurrentRevisionToken)
                throw new InvalidOperationException("The Character Editor head changed after native admission.");
        }

        private static string EditorNativeOriginMaterialHash(Dictionary<string, object> row) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(new object[]
            {
                ReadString(row, "command_id", ""), ReadString(row, "revision_id", ""), ReadString(row, "hero_id", ""),
                ReadString(row, "changes_json", ""), ReadString(row, "inverse_json", ""),
                ReadInt(row, "dangerous", -1), ReadString(row, "confirmation", "")
            }))));

        private static void StampEditorNativeOrigin(ReignDbConnection connection, ReignDbTransaction transaction,
            EditorNativeBrowserAdmission admission, Dictionary<string, object> command)
        {
            string id = ReadString(command, "id", "");
            string revision = ReadString(command, "revision", "");
            var row = CharactersStore.SelectCharacterEditorNativeCommands5(connection, TestDict("id", id), transaction).Single();
            var owner = admission.Owner;
            var origin = TestDict("schema", EditorNativeCommandOriginSchema, "campaignId", owner.CampaignId,
                "timelineId", owner.TimelineId, "pointId", owner.LoadPointId, "sessionId", owner.LoadSessionId,
                "restoreGeneration", owner.RestoreGeneration, "lineageId", owner.LineageId,
                "heroId", admission.HeroId, "revisionId", revision, "commandId", id,
                "basisRevisionToken", admission.CurrentRevisionToken, "materialSha256", EditorNativeOriginMaterialHash(row));
            if (!CharactersStore.StampManualEditorCommandOrigin(connection,
                TestDict("id", id, "hero", admission.HeroId, "revision", revision, "timeline", owner.TimelineId,
                    "lineage", owner.LineageId, "origin", Json.Serialize(origin)), transaction))
                throw new InvalidOperationException("The Editor native command origin could not be committed.");
        }

        private static bool OwnsEditorNativeRollbackSource(ReignDbConnection connection, ReignDbTransaction transaction,
            EditorNativeBrowserAdmission admission, string originalRevision)
        {
            if (admission == null || !OwnsEditorNativeBrowserWriteScope(admission.Owner.CampaignId)) return false;
            var rows = CharactersStore.ReadEditorNativeCommandsForRevision(connection,
                TestDict("revision", originalRevision, "hero", admission.HeroId), transaction);
            return rows.Count == 1 && MatchesEditorNativeCommandOrigin(rows[0], admission.Owner);
        }

        private static bool MatchesEditorNativeCommandOrigin(Dictionary<string, object> row,
            PersistenceService.RegisteredEditorNativeOwner owner)
        {
            if (row == null || owner == null || ReadString(row, "manual_origin_timeline_id", "") != owner.TimelineId
                || ReadString(row, "manual_origin_lineage_id", "") != owner.LineageId)
                return false;
            var origin = TryParseStrictJsonObject(ReadString(row, "manual_origin_json", ""));
            string Field(string key) => origin?.TryGetValue(key, out object raw) == true && raw is string text ? text : "";
            string session = Field("sessionId");
            return Field("schema") == EditorNativeCommandOriginSchema
                && Field("campaignId") == owner.CampaignId && Field("timelineId") == owner.TimelineId
                && Field("lineageId") == owner.LineageId
                && Field("heroId") == ReadString(row, "hero_id", "") && Field("heroId").Length > 0
                && Field("revisionId") == ReadString(row, "revision_id", "") && Field("revisionId").Length > 0
                && Field("commandId") == ReadString(row, "command_id", "") && Field("commandId").Length > 0
                && TerminalActionReportProtocol.IsTrustedIdentifier(Field("pointId"))
                && Guid.TryParseExact(session, "N", out Guid sessionId) && sessionId.ToString("N") == session
                && TerminalActionReportProtocol.IsTrustedIdentifier(Field("restoreGeneration"))
                && TerminalActionReportProtocol.IsTrustedIdentifier(Field("basisRevisionToken"))
                && Field("materialSha256") == EditorNativeOriginMaterialHash(row);
        }

#if !REIGN_EXCLUDE_VERIFICATION
        private const string EditorOriginFixtureHero = "editor_origin_fixture_hero";
        private const string EditorOriginFixtureRevision = "editor_origin_fixture_original";

        internal static string VerifyEditorManualCommandOrigin(Action<string, bool, string> add, string campaign, string ledgerPath)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation || !CampaignFileService.IsInternalCampaignId(campaign))
                throw new InvalidOperationException("The manual Editor command origin fixture needs an isolated validation campaign.");

            const string hero = EditorOriginFixtureHero, legacy = "editor_origin_fixture_legacy";
            const string originalRevision = EditorOriginFixtureRevision, rollbackRevision = "editor_origin_fixture_rollback";
            const string legacyToken = "editor_origin_fixture_legacy_token", currentToken = "editor_origin_fixture_current_token";
            const string nativeChanges = "{\"name\":\"Ledger-bound test edit\"}";

            Dictionary<string, object> Revision(string id, string rollback = null)
            {
                var data = TestDict(rollback == null ? "revision" : "id", id, "hero", hero,
                    "description", "Owned metadata fixture", "before", "{}", "after", "{}",
                    "native", nativeChanges, "reconcile", "{}", "ts", 1L);
                if (rollback == null) data["section"] = "profile";
                else data["rollback"] = rollback;
                return data;
            }

            Dictionary<string, object> Command(string id) => TestDict("id", id + "_command", "revision", id,
                "hero", hero, "changes", nativeChanges, "inverse", "{}", "dangerous", 0, "confirmation", "", "ts", 1L);
            Dictionary<string, object> Head(string id, string token) =>
                TestDict("hero", hero, "revision", id, "token", token, "ts", 1L);

            var scopeProbe = RunInEditorNativeBrowserWriteScope(campaign, () =>
                TestDict("basis", IssueCurrentEditorNativeRevisionBasis(campaign, hero, legacyToken)));
            bool unownedDenied = ReadString(scopeProbe, "basis", "").Length > 0 && ReadBool(OwnEditorCampaignWrite(() =>
            {
                using var other = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign + "_other");
                return TestDict("held", AdmitEditorNativeBrowserWrite(campaign, hero, legacyToken, ReadString(scopeProbe, "basis", "")) == null);
            }), "held", false);

            var outcome = RunInEditorNativeBrowserWriteScope(campaign, () =>
            {
                using var writer = ReignDatabase.OpenCampaignConnection(campaign);
                EnsureCharacterEditorSchema(writer);
                CharactersSchema.EnsureManualEditorCommandOriginColumns(writer);
                CommitEditorRevisionMetadata(writer, Revision(legacy), Command(legacy), Head(legacy, legacyToken));
                using var reader = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                    trackCampaignMutations: false, initializeInfrastructure: false);
                if (reader == null) throw new InvalidOperationException("The editor fixture campaign is not registered.");

                Dictionary<string, object> ReadCommand(string id) =>
                    CharactersStore.SelectCharacterEditorNativeCommands5(reader, TestDict("id", id + "_command")).SingleOrDefault();
                Dictionary<string, object> ReadRevision(string id) =>
                    CharactersStore.SelectCharacterEditorRevisions2(reader, TestDict("revision", id, "hero", hero)).SingleOrDefault();
                string ReadHead() =>
                    ReadString(CharactersStore.SelectCharacterEditorHeads(reader, TestDict("id", hero)).SingleOrDefault(),
                        "latest_revision_id", "");
                bool Absent(string id) => ReadCommand(id) == null && ReadRevision(id) == null;

                var owner = PersistenceService.ReadRegisteredEditorNativeOwner(campaign);
                string ledgerSha = PersistenceService.FileSha256(ledgerPath);
                string basis = IssueCurrentEditorNativeRevisionBasis(campaign, hero, legacyToken);
                var admission = AdmitEditorNativeBrowserWrite(campaign, hero, legacyToken, basis);
                if (owner == null || admission == null)
                    throw new InvalidOperationException("The exact registered owner did not issue an Editor basis.");

                bool held = false;
                try
                {
                    CommitOwnedEditorNativeRevisionMetadata(writer, campaign, null,
                        Revision("unscoped"), Command("unscoped"), Head("unscoped", "held"));
                }
                catch (InvalidOperationException) { held = true; }

                bool heldLegacyRollback = false;
                try
                {
                    CommitOwnedEditorNativeRevisionMetadata(writer, campaign, admission,
                        Revision("legacy_rollback", legacy), Command("legacy_rollback"),
                        Head("legacy_rollback", "held"), isRollback: true);
                }
                catch (InvalidOperationException) { heldLegacyRollback = true; }
                add("native_editor_unscoped_write_and_legacy_native_rollback_leave_ledger_and_rows_unchanged",
                    held && heldLegacyRollback && Absent("unscoped") && Absent("legacy_rollback") && ReadHead() == legacy
                    && ReadString(ReadCommand(legacy), "manual_origin_json", "") == ""
                    && AdmitEditorNativeBrowserWrite(campaign, hero, legacyToken, "") == null
                    && AdmitEditorNativeBrowserWrite(campaign, "wrong_hero", legacyToken, basis) == null
                    && PersistenceService.FileSha256(ledgerPath) == ledgerSha,
                    "Denied native writes and rollback of unproven legacy work leave the real Save Sync ledger and existing command/head unchanged.");

                bool headRejected = false;
                try
                {
                    CommitOwnedEditorNativeRevisionMetadata(writer, campaign, admission, Revision("head_rejection"),
                        Command("head_rejection"), Head("head_rejection", null));
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.NotNullViolation
                    && ex.TableName == "character_editor_heads" && ex.ColumnName == "revision_token")
                { headRejected = true; }
                add("native_editor_origin_rolls_back_after_actual_head_failure",
                    headRejected && Absent("head_rejection") && ReadHead() == legacy
                    && PersistenceService.FileSha256(ledgerPath) == ledgerSha,
                    "PostgreSQL rejection of the head undoes the staged revision, native command and native origin.");

                using (var transaction = writer.BeginTransaction())
                {
                    CommitOwnedEditorNativeRevisionMetadata(writer, campaign, admission, Revision(originalRevision),
                        Command(originalRevision), Head(originalRevision, currentToken), transaction: transaction);
                    if (!OwnsEditorNativeBrowserWriteScope(campaign) || !Absent(originalRevision) || ReadHead() != legacy)
                        throw new InvalidOperationException("A second connection could see uncommitted Editor metadata.");
                    transaction.Commit();
                }

                var stored = ReadCommand(originalRevision);
                var origin = TryParseStrictJsonObject(ReadString(stored, "manual_origin_json", ""));
                var altered = new Dictionary<string, object>(stored) { ["changes_json"] = "{\"name\":\"Other\"}" };
                bool WrongScopeField(string field, object value)
                {
                    var malformed = new Dictionary<string, object>(origin) { [field] = value };
                    var unscoped = new Dictionary<string, object>(stored) { ["manual_origin_json"] = Json.Serialize(malformed) };
                    return !MatchesEditorNativeCommandOrigin(unscoped, owner);
                }
                add("native_editor_origin_requires_exact_timeline_scope_and_typed_original_fields",
                    unownedDenied && WrongScopeField("pointId", 123) && WrongScopeField("sessionId", true)
                    && WrongScopeField("sessionId", new string('A', 32))
                    && WrongScopeField("restoreGeneration", TestDict("generation", "fake"))
                    && WrongScopeField("basisRevisionToken", new[] { "wrong" }),
                    "A different timeline lock and coercible or malformed persisted owner fields never confer manual Editor authority.");
                add("native_editor_commit_publishes_ledger_bound_origin_payload_and_head_together",
                    MatchesEditorNativeCommandOrigin(stored, owner) && !MatchesEditorNativeCommandOrigin(altered, owner)
                    && !MatchesEditorNativeCommandOrigin(stored, owner with { LineageId = "foreign_lineage" })
                    && ReadString(origin, "pointId", "") == owner.LoadPointId
                    && ReadString(origin, "sessionId", "") == owner.LoadSessionId
                    && ReadString(origin, "restoreGeneration", "") == owner.RestoreGeneration
                    && ReadString(origin, "basisRevisionToken", "") == legacyToken
                    && ReadString(ReadRevision(originalRevision), "native_changes_json", "") == nativeChanges
                    && ReadHead() == originalRevision && PersistenceService.ReadRegisteredEditorNativeOwner(campaign) == owner
                    && PersistenceService.FileSha256(ledgerPath) == ledgerSha,
                    "A second database connection sees the exact accepted origin, original load witness, native material, revision and head after COMMIT.");

                bool staleRejected = false;
                try
                {
                    CommitOwnedEditorNativeRevisionMetadata(writer, campaign, admission,
                        Revision("stale_basis"), Command("stale_basis"), Head("stale_basis", "held"));
                }
                catch (InvalidOperationException) { staleRejected = true; }
                var updatedAdmission = AdmitEditorNativeBrowserWrite(campaign, hero, currentToken,
                    IssueCurrentEditorNativeRevisionBasis(campaign, hero, currentToken));
                CommitOwnedEditorNativeRevisionMetadata(writer, campaign, updatedAdmission,
                    Revision(rollbackRevision, originalRevision), Command(rollbackRevision),
                    Head(rollbackRevision, "editor_origin_fixture_rollback_token"), isRollback: true);
                add("native_editor_native_rollback_requires_current_basis_and_owned_original",
                    staleRejected && Absent("stale_basis") && ReadHead() == rollbackRevision
                    && MatchesEditorNativeCommandOrigin(ReadCommand(rollbackRevision), owner)
                    && ReadString(ReadRevision(rollbackRevision), "rollback_of", "") == originalRevision
                    && PersistenceService.FileSha256(ledgerPath) == ledgerSha,
                    "After the Editor head advances, the old basis holds and a current signed basis can commit a rollback of the exact owned command.");
                return TestDict("retainedOrigin", ReadString(stored, "manual_origin_json", ""));
            });
            return ReadString(outcome, "retainedOrigin", "");
        }

        internal static void VerifyPersistedEditorCommandOriginWithoutCurrentOwner(Action<string, bool, string> add,
            string campaign, string ledgerPath, string retainedOrigin)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation || !CampaignFileService.IsInternalCampaignId(campaign))
                throw new InvalidOperationException("The manual Editor command origin fixture needs an isolated validation campaign.");
            Program.CampaignDataGate.EnterWriteLock();
            try
            {
                using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
                using var reader = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                    trackCampaignMutations: false, initializeInfrastructure: false);
                var row = reader == null ? null : CharactersStore.SelectCharacterEditorNativeCommands5(reader,
                    TestDict("id", EditorOriginFixtureRevision + "_command")).SingleOrDefault();
                string ledgerSha = PersistenceService.FileSha256(ledgerPath);
                add("native_editor_degraded_load_retains_pending_origin_but_holds_new_manual_authority",
                    retainedOrigin.Length > 0 && ReadString(row, "manual_origin_json", "") == retainedOrigin
                    && ReadString(row, "status", "") == "pending"
                    && IssueCurrentEditorNativeRevisionBasis(campaign, EditorOriginFixtureHero, "editor_origin_fixture_rollback_token").Length == 0
                    && PersistenceService.ReadRegisteredEditorNativeOwner(campaign) == null
                    && PersistenceService.FileSha256(ledgerPath) == ledgerSha,
                    "A real degraded load preserves the command and immutable origin, while the retired owner cannot issue a new native Editor basis.");
            }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
        }

        private static void VerifyEditorNativeRevisionBasis(Action<string, bool, string> add)
        {
            const string hero = "editor_hero", revision = "editor_head";
            var owner = new PersistenceService.RegisteredEditorNativeOwner("editor_campaign", "editor_timeline",
                "editor_point", "editor_session", "editor_generation", "editor_lineage");
            string basis = IssueEditorNativeRevisionBasis(owner, hero, revision);
            add("native_revision_basis_binds_registered_owner_and_editor_head",
                basis.Length == 64 && MatchesEditorNativeRevisionBasis(owner, hero, revision, basis)
                && !MatchesEditorNativeRevisionBasis(owner, hero, "newer_head", basis)
                && !MatchesEditorNativeRevisionBasis(owner, "different_hero", revision, basis)
                && !MatchesEditorNativeRevisionBasis(owner with { CampaignId = "new_campaign" }, hero, revision, basis)
                && !MatchesEditorNativeRevisionBasis(owner with { TimelineId = "fork" }, hero, revision, basis)
                && !MatchesEditorNativeRevisionBasis(owner with { LoadPointId = "new_point" }, hero, revision, basis)
                && !MatchesEditorNativeRevisionBasis(owner with { LoadSessionId = "new_session" }, hero, revision, basis)
                && !MatchesEditorNativeRevisionBasis(owner with { RestoreGeneration = "new_generation" }, hero, revision, basis)
                && !MatchesEditorNativeRevisionBasis(owner with { LineageId = "new_lineage" }, hero, revision, basis),
                "The opaque native edit basis is scoped to the exact campaign, hero, revision and registered native owner.");
            add("native_revision_basis_refuses_unbound_or_malformed_input",
                IssueEditorNativeRevisionBasis(null, hero, revision).Length == 0
                && !MatchesEditorNativeRevisionBasis(null, hero, revision, basis)
                && !MatchesEditorNativeRevisionBasis(owner, hero, revision, new string('z', 64))
                && !MatchesEditorNativeRevisionBasis(owner, hero, revision, basis + "0"),
                "No native edit basis can issue or compare without an owner and an exact signature.");
        }
#endif
    }
}
