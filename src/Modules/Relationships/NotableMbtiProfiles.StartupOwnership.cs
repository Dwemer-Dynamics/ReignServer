using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private const string TypedNotableStartupField = "typedNotableStartupVersion";
        private const string StartupNativeSessionField = "startupNativeSessionId";
        private const string StartupNativeOwnerField = "startupNativeOwner";
        private const string DeferredNativeStartupStatus = "native_delivery_deferred";
        private const string StockNativeStartupPending = "startup_direct_pending";

        private sealed record NotableStartupReservation(bool Deferred, string Session);
        private sealed record NotableStartupObservation(bool Deferred, string Error);
        private sealed record NotableStartupScope(string Timeline, string Generation);
        private sealed record NotableStartupRequest(NotableStartupScope Scope, string Preparation);

        private static NotableStartupScope ReadNotableStartupScope(ReignDbConnection connection)
        {
            var rows = DialogueStore.SelectMemoryPrecisionState4(connection);
            if (rows.Count != 1 || !rows[0].TryGetValue("restore_generation", out object raw) || raw is not string generation
                || !Reign.Core.Contracts.Actions.TerminalActionReportProtocol.IsTrustedIdentifier(generation)) return null;
            return new NotableStartupScope(WorldSimulationService.ActiveWorldHistoryTimeline(connection), generation);
        }

        private static string ExactStartupReservationField(Dictionary<string, object> token, string field) =>
            token?.TryGetValue(field, out object raw) == true && raw is string text ? text : "";

        private static bool IsNotableStartupNonce(string value) =>
            Guid.TryParseExact(value, "N", out Guid nonce) && nonce.ToString("N") == value;

        private static NotableStartupRequest ReadNotableStartupRequest(Dictionary<string, object> payload)
        {
            if (payload == null || payload.Keys.Count(key => key.Equals(StartupNativeOwnerField, StringComparison.OrdinalIgnoreCase)) != 1
                || !payload.Keys.Contains(StartupNativeOwnerField, StringComparer.Ordinal)
                || !payload.TryGetValue(StartupNativeOwnerField, out object material)
                || material is not Dictionary<string, object> fields || fields.Count != 3) return null;
            string timeline = ExactStartupReservationField(fields, "timelineId");
            string generation = ExactStartupReservationField(fields, "restoreGeneration");
            string preparation = ExactStartupReservationField(fields, "preparationId");
            return fields.Keys.Contains("timelineId", StringComparer.Ordinal)
                && fields.Keys.Contains("restoreGeneration", StringComparer.Ordinal)
                && fields.Keys.Contains("preparationId", StringComparer.Ordinal)
                && Reign.Core.Contracts.Actions.TerminalActionReportProtocol.IsTrustedIdentifier(timeline)
                && Reign.Core.Contracts.Actions.TerminalActionReportProtocol.IsTrustedIdentifier(generation)
                && IsNotableStartupNonce(preparation)
                ? new NotableStartupRequest(new NotableStartupScope(timeline, generation), preparation) : null;
        }

        private static Dictionary<string, object> WriteNotableStartupRequest(NotableStartupRequest owner) =>
            TestDict("timelineId", owner.Scope.Timeline, "restoreGeneration", owner.Scope.Generation, "preparationId", owner.Preparation);

        private static ReignDbConnection OpenOwnedNotableStartupCampaign(string campaign) =>
            OwnsNativeNotableTimeline(campaign)
                ? ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                    trackCampaignMutations: true, initializeInfrastructure: false) : null;

        private static bool MatchesNotableStartupScope(ReignDbConnection connection, NotableStartupRequest owner) =>
            owner != null && ReadNotableStartupScope(connection) == owner.Scope;

        private static Dictionary<string, object> TypedNotableStartupOwnerStale() =>
            TestDict("ok", false, "error", "notable_startup_owner_stale");

        private static bool AcceptsTypedNotableStartup(Dictionary<string, object> payload) =>
            payload?.Keys.Count(key => key.Equals(TypedNotableStartupField, StringComparison.OrdinalIgnoreCase)) == 1
            && payload.Keys.Contains(TypedNotableStartupField, StringComparer.Ordinal)
            && payload.TryGetValue(TypedNotableStartupField, out object version) && (version is 1 || version is 1L);

        private static Dictionary<string, object> TypedNotableStartupRequired() =>
            TestDict("ok", false, "error", "typed_notable_startup_protocol_required");

        private static Dictionary<string, object> TypedNotableStartupSchemaNotReady() =>
            TestDict("ok", false, "error", "native_editor_campaign_schema_not_ready");

        private static bool OwnsNativeNotableTimeline(string campaign) =>
            (Program.CampaignDataGate.IsReadLockHeld || Program.CampaignDataGate.IsWriteLockHeld)
            && WorldSimulationService.OwnsWorldHistoryTimelineScope(campaign);

        private static string ExpectedNotableStartupCommandId(string hero) =>
            "notable_mbti_native_" + hero + "_v" + NotableMbtiTemplateVersion.ToString(CultureInfo.InvariantCulture);

        // An older schema can omit a marker. A present null, partial owner or nonempty token holds delivery.
        private static bool HasRetainedNotableMarker(Dictionary<string, object> row, string field) =>
            row?.TryGetValue(field, out object retained) == true && (retained is not string text || text.Length != 0);

        private static bool HasTypedNotableCommandOwner(Dictionary<string, object> command) =>
            HasRetainedNotableMarker(command, "automatic_origin_json")
            || HasRetainedNotableMarker(command, "manual_origin_json")
            || HasRetainedNotableMarker(command, "manual_origin_timeline_id")
            || HasRetainedNotableMarker(command, "manual_origin_lineage_id");

        private static bool HasNotableEditorAuthorityFile(string campaign, string hero) =>
            System.IO.Path.Exists(CampaignFileService.CharacterFile(campaign, hero, "editor_authority.json"));

        internal static bool StockNotableConstructionMustHold(string campaign, string hero, bool requestsNotable) =>
            !CharactersService.OwnedAutomaticNotableDeliveryReady
            && RetainedNotableDeliveryMustHold(campaign, hero, requestsNotable);

        internal static bool RetainedNotableDeliveryMustHold(string campaign, string hero, bool requestsNotable)
        {
            var persisted = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, hero, "profile.json"));
            // A stale request can ask for notable treatment, but cannot erase stored notable identity.
            bool knownNotable = requestsNotable || ReadBool(persisted, "isNotable", false);
            if (knownNotable && HasNotableEditorAuthorityFile(campaign, hero)) return true;

            // This read precedes the construction path's regular SQL/file initialization.
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                trackCampaignMutations: false, initializeInfrastructure: false);
            if (connection == null) return false;
            var row = WorldSimulationService.TableExists(connection, "notable_mbti_profiles")
                ? RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", hero)).SingleOrDefault() : null;
            if (!knownNotable && row == null) return false;
            return LegacyNotableDeliveryMustHold(connection, null, campaign, hero, row,
                canReadManualRevisions: WorldSimulationService.TableExists(connection, "character_editor_revisions"),
                canReadNativeCommands: WorldSimulationService.TableExists(connection, "character_editor_native_commands"));
        }

        private static bool LegacyNotableDeliveryMustHold(ReignDbConnection connection, ReignDbTransaction transaction,
            string campaign, string hero, Dictionary<string, object> profile,
            bool canReadManualRevisions = true, bool canReadNativeCommands = true)
        {
            if (HasRetainedNotableMarker(profile, "startup_native_session_id")
                || ReadString(profile, "native_sync_status", "").Equals(StockNativeStartupPending, StringComparison.OrdinalIgnoreCase)
                || HasNotableEditorAuthorityFile(campaign, hero)
                || canReadManualRevisions && CharactersStore.HasManualEditorRevisionForHero(connection,
                    TestDict("hero", hero), transaction))
                return true;
            if (!canReadNativeCommands) return false;

            string canonicalId = ExpectedNotableStartupCommandId(hero);
            var canonical = RelationshipsStore.SelectCharacterEditorNativeCommands(connection, TestDict("id", canonicalId), transaction)
                .SingleOrDefault();
            if (HasTypedNotableCommandOwner(canonical)) return true;

            string linkedId = ReadString(profile, "native_action_id", "");
            return linkedId.Length > 0 && linkedId != canonicalId
                && HasTypedNotableCommandOwner(RelationshipsStore.SelectCharacterEditorNativeCommands(connection,
                    TestDict("id", linkedId), transaction).SingleOrDefault());
        }

        private static bool HasOwnedOrHeldNotableDelivery(ReignDbConnection connection, ReignDbTransaction transaction,
            string campaign, string hero, Dictionary<string, object> profile) =>
            ReadString(profile, "native_action_id", "").Length > 0
            || RelationshipsStore.SelectCharacterEditorNativeCommands(connection,
                TestDict("id", ExpectedNotableStartupCommandId(hero)), transaction).Count != 0
            || System.IO.File.Exists(CampaignFileService.CharacterFile(campaign, hero, "editor_authority.json"))
            || CharactersStore.HasManualEditorRevisionForHero(connection, TestDict("hero", hero), transaction);

        private static bool ExistingNotableStartupDeliveryMustHold(ReignDbConnection connection,
            ReignDbTransaction transaction, string campaign, string hero)
        {
            var row = RelationshipsStore.LockAutomaticNotableProfile(connection, TestDict("hero", hero), transaction);
            return row == null || HasOwnedOrHeldNotableDelivery(connection, transaction, campaign, hero, row);
        }

        private static string ReadOwnedNotableStartupSession(Dictionary<string, object> row, NotableStartupRequest owner)
        {
            if (owner == null || ReadString(row, "native_sync_status", "") != StockNativeStartupPending
                || !row.TryGetValue("startup_native_session_id", out object value) || value is not string stored) return "";
            var retained = TryParseStrictJsonObject(stored);
            if (retained == null || retained.Count != 4
                || ExactStartupReservationField(retained, "timelineId") != owner.Scope.Timeline
                || ExactStartupReservationField(retained, "restoreGeneration") != owner.Scope.Generation
                || ExactStartupReservationField(retained, "preparationId") != owner.Preparation) return "";
            string session = ExactStartupReservationField(retained, "sessionId");
            return IsNotableStartupNonce(session) ? session : "";
        }

        private static NotableStartupReservation ReserveOrDeferNotableStartup(ReignDbConnection connection,
            ReignDbTransaction transaction, string campaign, string hero, NotableStartupRequest owner, bool inserted)
        {
            if (!OwnsNativeNotableTimeline(campaign)) return null;
            var row = RelationshipsStore.LockAutomaticNotableProfile(connection, TestDict("hero", hero), transaction);
            if (row == null) return null;
            if (HasOwnedOrHeldNotableDelivery(connection, transaction, campaign, hero, row))
                return new NotableStartupReservation(true, "");
            string expected = ReadString(row, "native_traits_json", "");
            if (ReadString(row, "source", "") != "notable_mbti_template"
                || ReadInt(row, "template_version", 0) != NotableMbtiTemplateVersion
                || !CharactersService.HasExactAutomaticNotableTraits(TryParseStrictJsonObject(expected)))
                return new NotableStartupReservation(true, "");

            if (!inserted)
            {
                string retained = ReadOwnedNotableStartupSession(row, owner);
                return new NotableStartupReservation(retained.Length == 0, retained);
            }

            string session = Guid.NewGuid().ToString("N");
            var witness = WriteNotableStartupRequest(owner);
            witness["sessionId"] = session;
            return RelationshipsStore.ReserveStockNotableNativeStartup(connection, TestDict("hero", hero,
                "version", NotableMbtiTemplateVersion, "expected", expected, "newSession", Json.Serialize(witness),
                "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), transaction)
                ? new NotableStartupReservation(false, session) : null;
        }

        private static NotableStartupObservation ConfirmOwnedNotableStartupObservation(ReignDbConnection connection,
            ReignDbTransaction transaction, string campaign, string hero, double day, NotableStartupRequest owner,
            Dictionary<string, object> observation)
        {
            if (!OwnsNativeNotableTimeline(campaign) || hero.Length == 0) return new NotableStartupObservation(false, "startup_scope_missing");
            var row = RelationshipsStore.LockAutomaticNotableProfile(connection, TestDict("hero", hero), transaction);
            if (row == null) return new NotableStartupObservation(false, "assignment_not_found");
            string kind = ReadString(observation, "status", "");
            string expected = ReadString(row, "native_traits_json", "");
            var desired = TryParseStrictJsonObject(expected);
            bool held = HasOwnedOrHeldNotableDelivery(connection, transaction, campaign, hero, row)
                || ReadString(row, "source", "") != "notable_mbti_template"
                || ReadInt(row, "template_version", 0) != NotableMbtiTemplateVersion
                || !CharactersService.HasExactAutomaticNotableTraits(desired);
            string reservation = held ? "" : ReadOwnedNotableStartupSession(row, owner);
            if (reservation.Length == 0)
                return kind == DeferredNativeStartupStatus && !observation.ContainsKey("traits") && !observation.ContainsKey(StartupNativeSessionField)
                    ? new NotableStartupObservation(true, "") : new NotableStartupObservation(false, "native_delivery_has_other_owner");

            string session = observation.Keys.Count(key => key.Equals(StartupNativeSessionField, StringComparison.OrdinalIgnoreCase)) == 1
                && observation.Keys.Contains(StartupNativeSessionField, StringComparer.Ordinal)
                && observation.TryGetValue(StartupNativeSessionField, out object token) && token is string exact ? exact : "";
            string stored = ReadString(row, "startup_native_session_id", "");
            if (kind == DeferredNativeStartupStatus || session != reservation)
                return new NotableStartupObservation(false, "startup_reservation_mismatch");

            bool obsolete = kind == "obsolete" && !observation.ContainsKey("traits");
            var observed = ReadDictionary(observation, "traits");
            bool complete = CharactersService.HasExactAutomaticNotableTraits(observed);
            bool matches = complete && CanonicalJson(observed) == CanonicalJson(desired);
            string status = obsolete ? "obsolete" : matches ? "synchronized" : complete ? "contradicted" : "awaiting_confirmation";
            if (!RelationshipsStore.ConsumeStockNotableNativeStartup(connection, TestDict("hero", hero, "session", stored,
                "version", NotableMbtiTemplateVersion, "expected", expected, "status", status, "day", day,
                "observed", Json.Serialize(observed), "hasObservation", complete,
                "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), transaction))
                return new NotableStartupObservation(false, "startup_reservation_replaced");
            return new NotableStartupObservation(false, obsolete || matches ? "" : complete
                ? "native_trait_mismatch" : "native_trait_observation_incomplete");
        }

        private static void ObserveOwnedNotableNativeTraits(ReignDbConnection connection, ReignDbTransaction transaction,
            string campaign, string hero, double day, Dictionary<string, object> native)
        {
            // A caller-owned SQL transaction must enter the history scope before beginning; never invert that order.
            if (!(Program.CampaignDataGate.IsReadLockHeld || Program.CampaignDataGate.IsWriteLockHeld)
                || transaction != null && !OwnsNativeNotableTimeline(campaign)) return;
            using var timeline = OwnsNativeNotableTimeline(campaign) ? null : WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
            using var suppressRetries = transaction == null ? new ReignDatabase.SqlStatementRetrySuppression(connection) : null;
            using var own = transaction == null ? connection.BeginTransaction() : null;
            transaction ??= own;
            if (!PersistenceService.IsNativeEditorOwnershipSchemaReady(connection, transaction)) return;
            var row = RelationshipsStore.LockAutomaticNotableProfile(connection, TestDict("hero", hero), transaction);
            if (row == null || ReadString(row, "startup_native_session_id", "").Length > 0
                || ReadString(row, "native_sync_status", "") == StockNativeStartupPending
                || HasOwnedOrHeldNotableDelivery(connection, transaction, campaign, hero, row)) return;

            var expected = TryParseStrictJsonObject(ReadString(row, "native_traits_json", ""));
            var observed = ReadDictionary(native, "traits");
            bool complete = CharactersService.HasExactAutomaticNotableTraits(observed);
            if (complete && CharactersService.HasExactAutomaticNotableTraits(expected) && CanonicalJson(expected) == CanonicalJson(observed))
            {
                RelationshipsStore.UpdateNotableMbtiProfiles5(connection, TestDict("hero", hero, "observed", Json.Serialize(observed),
                    "day", day, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), transaction);
            }
            else if (ReadBool(native, "isAlive", true))
            {
                CharactersService.QueueTypedAutomaticNotableTraits(connection, campaign, hero, day,
                    expected, observed ?? TestDict(), transaction);
            }
            own?.Commit();
        }

        internal sealed record RetainedNotableStartupObservation(string BasisSha256, string ExpectedTraits);

        internal static RetainedNotableStartupObservation ReadRetainedNotableStartupObservation(
            string campaign, string hero, string timelineId, string restoreGeneration)
        {
            if (!OwnsNativeNotableTimeline(campaign)) return null;
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                trackCampaignMutations: false, initializeInfrastructure: false);
            if (connection == null || !PersistenceService.IsNativeEditorOwnershipSchemaReady(connection)
                || ReadNotableStartupScope(connection) != new NotableStartupScope(timelineId, restoreGeneration))
                return null;

            var row = RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", hero)).SingleOrDefault();
            if (row == null || ReadString(row, "native_sync_status", "") != StockNativeStartupPending
                || ReadString(row, "source", "") != "notable_mbti_template"
                || ReadInt(row, "template_version", -1) != NotableMbtiTemplateVersion
                || HasOwnedOrHeldNotableDelivery(connection, null, campaign, hero, row))
                return null;

            string raw = ReadString(row, "startup_native_session_id", "");
            var token = TryParseStrictJsonObject(raw);
            string previousGeneration = ExactStartupReservationField(token, "restoreGeneration");
            if (token?.Count != 4 || !token.Keys.OrderBy(key => key, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "preparationId", "restoreGeneration", "sessionId", "timelineId" }, StringComparer.Ordinal)
                || ExactStartupReservationField(token, "timelineId") != timelineId
                || !Reign.Core.Contracts.Actions.TerminalActionReportProtocol.IsTrustedIdentifier(previousGeneration)
                || previousGeneration == restoreGeneration
                || !IsNotableStartupNonce(ExactStartupReservationField(token, "preparationId"))
                || !IsNotableStartupNonce(ExactStartupReservationField(token, "sessionId")))
                return null;

            string expected = ReadString(row, "native_traits_json", "");
            var desired = TryParseStrictJsonObject(expected);
            if (!CharactersService.HasExactAutomaticNotableTraits(desired)) return null;
            var bytes = System.Text.Encoding.UTF8.GetBytes(Json.Serialize(new[] { raw, expected }));
            string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            return new RetainedNotableStartupObservation(digest, CanonicalJson(desired));
        }

#if !REIGN_EXCLUDE_VERIFICATION
        [ThreadStatic] private static Action<string> TypedNotableMaterializationStartingForTests;

        private static void VerifyOwnedNotableStartupCoordination(Action<string, bool, string> add)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation)
                throw new InvalidOperationException("The notable startup fixture needs the isolated PostgreSQL database.");
            string campaign = "__notable_startup_owner_" + Guid.NewGuid().ToString("N");
            string campaignRoot = PersistenceService.StrictCampaignDirectory(campaign);
            string ledgerRoot = PersistenceService.SaveSyncCampaignRoot(campaign);
            using (var occupied = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                trackCampaignMutations: false, initializeInfrastructure: false))
                if (occupied != null || System.IO.Path.Exists(campaignRoot) || System.IO.Path.Exists(ledgerRoot))
                    throw new InvalidOperationException("The disposable notable startup fixture already exists.");

            const string direct = "stock_direct", queued = "owned_queue", legacy = "held_legacy",
                priorUnlinked = "unowned_legacy_synchronized";
            Dictionary<string, object> Hero(string id) =>
                TestDict("heroStringId", id, "name", id, "isNotable", true, "isAlive", true,
                    "traits", TestDict("valor", 0, "generosity", 0, "honor", 0, "mercy", 0, "calculating", 0));
            var heroes = new[] { Hero(direct), Hero(queued), Hero(legacy), Hero(priorUnlinked) };
            var initialize = TestDict("campaignId", campaign, "worldDay", 1d, "heroes", heroes, TypedNotableStartupField, 1);
            try
            {
                Program.CampaignDataGate.EnterReadLock();
                try
                {
                    var opened = WorldSimulationService.WorldHistoryOpenTimelineApi(TestDict("campaignId", campaign, "timelineId", "notable_startup_fixture"));
                    if (!ReadBool(opened, "ok", false)) throw new InvalidOperationException("The fixture timeline could not open.");
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    var scope = ReadNotableStartupScope(connection);
                    var owner = new NotableStartupRequest(scope, Guid.NewGuid().ToString("N"));
                    initialize[StartupNativeOwnerField] = WriteNotableStartupRequest(owner);
                    var existing = EnsureNotableMbtiProfile(connection, campaign, 1d, Hero(priorUnlinked), false,
                        queueNativeTraits: false);
                    var legacyObservation = NotableMbtiConfirmBatchCore(TestDict("campaignId", campaign, "worldDay", 1d,
                        "observations", new[] { TestDict("heroId", priorUnlinked, "traits", ReadDictionary(existing, "nativeTraits")) }),
                        typedStartup: false);
                    var queuedTemplate = EnsureNotableMbtiProfile(connection, campaign, 1d, Hero(queued), false, queueNativeTraits: false);
                    var expected = ReadDictionary(queuedTemplate, "nativeTraits");
                    var observed = ReadDictionary(Hero(queued), "traits");
                    string queuedProjection = CampaignFileService.CharacterFile(campaign, queued, "profile.json");
                    var queuedIdentity = Hero(queued);
                    queuedIdentity["campaignId"] = campaign;
                    CampaignFileService.WriteJsonObject(queuedProjection, queuedIdentity);
                    string queuedProjectionHash = PersistenceService.FileSha256(queuedProjection);
                    EnsureNotableMbtiProfile(connection, campaign, 1d, Hero(legacy), false, queueNativeTraits: false);
                    RelationshipsStore.InsertOrIgnoreCharacterEditorNativeCommands(connection,
                        TestDict("id", ExpectedNotableStartupCommandId(legacy), "revision", "notable_mbti_v" + NotableMbtiTemplateVersion,
                            "hero", legacy, "changes", "{}", "inverse", "{}", "ts", 1L));

                    Dictionary<string, object> Profile(string hero) =>
                        RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", hero)).Single();
                    Dictionary<string, object> Command(string hero) =>
                        RelationshipsStore.SelectCharacterEditorNativeCommands(connection, TestDict("id", ExpectedNotableStartupCommandId(hero))).SingleOrDefault();
                    bool wrongTimelineRefused;
                    using (WorldSimulationService.EnterWorldHistoryTimelineScope(campaign + "_wrong"))
                        wrongTimelineRefused = !CharactersService.QueueTypedAutomaticNotableTraits(connection, campaign, queued, 1d, expected, observed)
                            && Command(queued) == null;
                    using var competingWait = new System.Threading.ManualResetEventSlim();
                    using var competingEntry = new System.Threading.ManualResetEventSlim();
                    Exception competingError = null;
                    var competitor = new System.Threading.Thread(() =>
                    {
                        Program.CampaignDataGate.EnterReadLock();
                        try
                        {
                            PersistenceService.CampaignWorkLockWaitObserverForTests = _ => competingWait.Set();
                            using var other = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
                            competingEntry.Set();
                        }
                        catch (Exception ex) { competingError = ex; }
                        finally
                        {
                            PersistenceService.CampaignWorkLockWaitObserverForTests = null;
                            Program.CampaignDataGate.ExitReadLock();
                        }
                    }) { IsBackground = true };
                    bool heldThroughCommit = false;
                    try
                    {
                        using (WorldSimulationService.EnterWorldHistoryTimelineScope(campaign))
                        using (var producing = connection.BeginTransaction())
                        {
                            if (!CharactersService.QueueTypedAutomaticNotableTraits(connection, campaign, queued, 1d, expected, observed, producing)
                                || !OwnsNativeNotableTimeline(campaign))
                                throw new InvalidOperationException("The fixture could not publish owned background work under exact campaign and timeline scope.");
                            competitor.Start();
                            bool waited = competingWait.Wait(5000) && !competingEntry.IsSet;
                            producing.Commit();
                            heldThroughCommit = waited && OwnsNativeNotableTimeline(campaign) && !competingEntry.IsSet;
                        }
                    }
                    finally
                    {
                        if ((competitor.ThreadState & System.Threading.ThreadState.Unstarted) == 0 && !competitor.Join(10000))
                            throw new InvalidOperationException("The competing scoped fixture worker did not finish.");
                    }
                    add("typed_producer_timeline_switch_cannot_enter_before_outer_commit",
                        heldThroughCommit && competingEntry.IsSet && competingError == null,
                        "A second campaign reader owns no timeline while the typed producer holds its exact timeline through outer COMMIT.");
                    string typedRow = CanonicalJson(Command(queued));
                    string typedProfile = CanonicalJson(Profile(queued));
                    var noCapability = new Dictionary<string, object>(initialize);
                    noCapability.Remove(TypedNotableStartupField);
                    bool unnegotiated = !ReadBool(NotableMbtiInitializeCore(noCapability, typedStartup: true), "ok", false)
                        && CanonicalJson(Command(queued)) == typedRow;
                    const string unexpected = "wrong_owner_must_not_materialize";
                    var previous = new NotableStartupRequest(new NotableStartupScope(scope.Timeline, "old_generation"), owner.Preparation);
                    var staleRequest = new Dictionary<string, object>(initialize)
                    {
                        [StartupNativeOwnerField] = WriteNotableStartupRequest(previous),
                        ["heroes"] = new[] { Hero(unexpected) }
                    };
                    var staleInit = NotableMbtiInitializeCore(staleRequest, typedStartup: true);
                    add("typed_startup_rejects_stale_owner_before_sql_or_profile_writes",
                        ReadString(staleInit, "error", "") == "notable_startup_owner_stale"
                        && RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", unexpected)).Count == 0
                        && !System.IO.File.Exists(CampaignFileService.CharacterFile(campaign, unexpected, "profile.json")),
                        "A typed initialize pinned to an earlier restore generation never inserts a row or materializes files.");
                    bool heldAtMaterialization = false;
                    Dictionary<string, object> first;
                    var priorMaterialization = TypedNotableMaterializationStartingForTests;
                    try
                    {
                        TypedNotableMaterializationStartingForTests = candidate =>
                            heldAtMaterialization = candidate == campaign && OwnsNativeNotableTimeline(campaign);
                        first = NotableMbtiInitializeCore(initialize, typedStartup: true);
                    }
                    finally { TypedNotableMaterializationStartingForTests = priorMaterialization; }
                    add("typed_startup_retains_owner_through_real_materialization",
                        heldAtMaterialization && System.IO.File.Exists(CampaignFileService.CharacterFile(campaign, direct, "profile.json")),
                        "Typed initialization still holds its campaign timeline when it begins the actual post-COMMIT materialization.");
                    Dictionary<string, object> Assignment(Dictionary<string, object> batch, string hero) =>
                        ReadDictionaryList(batch, "assignments").Single(row => ReadString(row, "heroId", "") == hero);
                    var firstDirect = Assignment(first, direct);
                    string firstToken = ReadString(firstDirect, StartupNativeSessionField, "");
                    string firstReservation = ReadString(Profile(direct), "startup_native_session_id", "");
                    ObserveOwnedNotableNativeTraits(connection, null, campaign, direct, 2d, Hero(direct));
                    add("typed_startup_reserves_stock_without_double_dispatch_or_legacy_reauthorization",
                        wrongTimelineRefused && unnegotiated && ReadBool(first, "ok", false)
                        && ReadBool(Assignment(first, queued), "deferNativeTraitsToQueue", false)
                        && ReadBool(Assignment(first, legacy), "deferNativeTraitsToQueue", false)
                        && !Assignment(first, queued).ContainsKey(StartupNativeSessionField)
                        && firstToken.Length == 32 && firstReservation.Length > 0
                        && firstReservation == ReadString(Profile(direct), "startup_native_session_id", "")
                        && Command(direct) == null && CanonicalJson(Command(queued)) == typedRow
                        && ReadString(Command(legacy), "automatic_origin_json", "missing") == "",
                        "A scoped stock reservation excludes background queueing. Existing typed or lost-material legacy work is deferred without setting traits or minting ownership.");


                    var heldHeroes = new[] { Hero(direct), Hero(queued) };
                    foreach (var hero in heldHeroes) hero["name"] = "must_not_materialize";
                    string directHeld = CanonicalJson(Profile(direct));
                    string NativeFiles(string hero) => Json.Serialize(new[] { "profile.json", "traits.json", "constructed.json" }
                        .Select(file => CampaignFileService.CharacterFile(campaign, hero, file))
                        .Select(file => System.IO.File.Exists(file) ? PersistenceService.FileSha256(file) : "absent"));
                    string directFiles = NativeFiles(direct), queuedFiles = NativeFiles(queued);
                    string indexPath = System.IO.Path.Combine(campaignRoot, "characters", "index.json");
                    string indexBefore = PersistenceService.FileSha256(indexPath);
                    bool SameHeldFiles() => NativeFiles(direct) == directFiles && NativeFiles(queued) == queuedFiles;
                    bool SameHeldRows() => CanonicalJson(Profile(direct)) == directHeld && Command(direct) == null
                        && CanonicalJson(Command(queued)) == typedRow && CanonicalJson(Profile(queued)) == typedProfile;
                    bool HeldAssignment(Dictionary<string, object> batch, string hero)
                    {
                        var row = Assignment(batch, hero);
                        return row.Count == 2 && ReadBool(row, "deferNativeTraitsToQueue", false)
                            && !row.ContainsKey("nativeTraits") && !row.ContainsKey(StartupNativeSessionField);
                    }

                    var heldOnly = NotableMbtiInitializeApi(TestDict("campaignId", campaign, "worldDay", 2d, "heroes", heldHeroes));
                    foreach (var hero in heldHeroes)
                        EnsureNotableMbtiProfile(connection, campaign, 2d, hero, materialize: true);
                    var forgedStock = NotableMbtiConfirmBatchApi(TestDict("campaignId", campaign, "worldDay", 2d, "observations",
                        new[] { TestDict("heroId", direct, "status", "obsolete"), TestDict("heroId", queued, "traits", expected) }));
                    add("stock_notable_fallback_holds_reserved_and_typed_delivery_without_writes",
                        ReadBool(heldOnly, "ok", false) && HeldAssignment(heldOnly, direct) && HeldAssignment(heldOnly, queued)
                        && ReadInt(forgedStock, "confirmedCount", -1) == 0 && ReadInt(forgedStock, "failureCount", -1) == 2
                        && SameHeldFiles() && SameHeldRows() && PersistenceService.FileSha256(indexPath) == indexBefore,
                        "The inactive stock initialize, missing-file background materialization and forged confirmation leave both exact retained owners untouched.");

                    const string mixedLegacy = "stock_fallback_control";
                    var originalQueuedProfile = Profile(queued);
                    string queuedStatus = ReadString(originalQueuedProfile, "native_sync_status", "");
                    string queuedLink = ReadString(originalQueuedProfile, "native_action_id", "");
                    ReignDatabase.ExecuteSql(connection, "UPDATE notable_mbti_profiles SET native_sync_status='failed',native_action_id='' WHERE hero_id=$hero;",
                        TestDict("hero", queued));
                    try
                    {
                        string retainedFailed = CanonicalJson(Profile(queued));
                        var omitted = NotableMbtiInitializeApi(TestDict("campaignId", campaign, "worldDay", 2d,
                            "heroes", new[] { Hero(direct) }));
                        add("stock_notable_fallback_does_not_retire_typed_failed_profile",
                            ReadBool(omitted, "ok", false) && HeldAssignment(omitted, direct)
                            && CanonicalJson(Profile(queued)) == retainedFailed && CanonicalJson(Command(queued)) == typedRow
                            && SameHeldFiles() && PersistenceService.FileSha256(indexPath) == indexBefore,
                            "An omitted failed hero with a retained typed command keeps its exact profile row and command. No stale retirement is inferred.");
                    }
                    finally
                    {
                        ReignDatabase.ExecuteSql(connection, "UPDATE notable_mbti_profiles SET native_sync_status=$status,native_action_id=$link WHERE hero_id=$hero;",
                            TestDict("hero", queued, "status", queuedStatus, "link", queuedLink));
                    }

                    var mixed = NotableMbtiInitializeApi(TestDict("campaignId", campaign, "worldDay", 2d,
                        "heroes", heldHeroes.Append(Hero(mixedLegacy)).ToArray()));
                    var mixedStock = Assignment(mixed, mixedLegacy);
                    var mixedTraits = ReadDictionary(mixedStock, "nativeTraits");
                    var mixedConfirm = NotableMbtiConfirmBatchApi(TestDict("campaignId", campaign, "worldDay", 2d, "observations",
                        new[] { TestDict("heroId", direct, "status", DeferredNativeStartupStatus),
                            TestDict("heroId", queued, "status", DeferredNativeStartupStatus),
                            TestDict("heroId", mixedLegacy, "traits", mixedTraits) }));
                    var unmarkedDefer = NotableMbtiConfirmBatchApi(TestDict("campaignId", campaign, "worldDay", 2d, "observations",
                        new[] { TestDict("heroId", mixedLegacy, "status", DeferredNativeStartupStatus) }));
                    add("stock_notable_fallback_confirms_only_genuine_legacy_work_in_mixed_batch",
                        ReadBool(mixed, "ok", false) && HeldAssignment(mixed, direct) && HeldAssignment(mixed, queued)
                        && mixedTraits?.Count == 5 && !mixedStock.ContainsKey("deferNativeTraitsToQueue")
                        && ReadBool(mixedConfirm, "ok", false) && ReadInt(mixedConfirm, "confirmedCount", -1) == 1
                        && ReadInt(mixedConfirm, "deferredCount", -1) == 2
                        && !ReadBool(unmarkedDefer, "ok", true) && ReadString(Profile(mixedLegacy), "native_sync_status", "") == "synchronized"
                        && System.IO.File.Exists(CampaignFileService.CharacterFile(campaign, mixedLegacy, "profile.json"))
                        && SameHeldFiles() && SameHeldRows(),
                        "The mixed false-gate batch applies its ordinary legacy evidence and acknowledges two held rows without materializing or settling either retained owner.");

                    var constructedWhileReserved = CharactersService.ConstructCharacterFiles(campaign, direct, heldHeroes[0],
                        force: true, useLlm: false, source: "stock_fallback_fixture");
                    var constructedWhileTyped = CharactersService.ConstructCharacter(TestDict("campaignId", campaign,
                        "hero", heldHeroes[1], "force", true, "useLlm", false));
                    add("stock_notable_real_construction_holds_reservation_and_typed_owner",
                        ReadString(constructedWhileReserved, "status", "") == DeferredNativeStartupStatus
                        && ReadString(constructedWhileTyped, "status", "") == DeferredNativeStartupStatus
                        && SameHeldFiles() && SameHeldRows(),
                        "Actual direct and requested construction refuse reserved or typed work before the first profile or trait write.");

                    var second = NotableMbtiInitializeCore(initialize, typedStartup: true);
                    var secondDirect = Assignment(second, direct);
                    string secondToken = ReadString(secondDirect, StartupNativeSessionField, "");
                    var competitorOwner = new NotableStartupRequest(scope, Guid.NewGuid().ToString("N"));
                    var competitorRequest = new Dictionary<string, object>(initialize)
                        { [StartupNativeOwnerField] = WriteNotableStartupRequest(competitorOwner) };
                    var competitorInit = NotableMbtiInitializeCore(competitorRequest, typedStartup: true);
                    Dictionary<string, object> Confirm(NotableStartupRequest request, params Dictionary<string, object>[] report) =>
                        NotableMbtiConfirmBatchCore(TestDict("campaignId", campaign, "worldDay", 2d,
                            "observations", report, StartupNativeOwnerField, WriteNotableStartupRequest(request),
                            TypedNotableStartupField, 1), typedStartup: true);
                    var desired = ReadDictionary(secondDirect, "nativeTraits");
                    var stale = Confirm(competitorOwner, TestDict("heroId", direct, StartupNativeSessionField, firstToken, "traits", desired));
                    var forgery = Confirm(owner, TestDict("heroId", queued, "traits", expected));
                    add("typed_startup_reuses_only_the_exact_pending_owner_and_holds_unlinked_legacy",
                        firstToken.Length == 32 && firstToken == secondToken
                        && ReadBool(Assignment(competitorInit, direct), "deferNativeTraitsToQueue", false)
                        && !Assignment(competitorInit, direct).ContainsKey(StartupNativeSessionField)
                        && ReadBool(legacyObservation, "ok", false) && Command(priorUnlinked) == null
                        && ReadString(Profile(priorUnlinked), "native_sync_status", "") == "synchronized"
                        && ReadBool(Assignment(first, priorUnlinked), "deferNativeTraitsToQueue", false)
                        && !Assignment(first, priorUnlinked).ContainsKey(StartupNativeSessionField)
                        && firstReservation == ReadString(Profile(direct), "startup_native_session_id", ""),
                        "Only this transaction's actual new profile or that exact pending preparation can receive a direct grant.");
                    var acknowledged = Confirm(owner, TestDict("heroId", direct, StartupNativeSessionField, secondToken, "traits", desired),
                        TestDict("heroId", queued, "status", DeferredNativeStartupStatus),
                        TestDict("heroId", legacy, "status", DeferredNativeStartupStatus),
                        TestDict("heroId", priorUnlinked, "status", DeferredNativeStartupStatus));
                    var afterConfirm = NotableMbtiInitializeCore(initialize, typedStartup: true);
                    add("typed_confirm_separates_measured_stock_and_deferred_queue_without_overwrite",
                        firstToken == secondToken && !ReadBool(stale, "ok", false) && !ReadBool(forgery, "ok", false)
                        && ReadBool(acknowledged, "ok", false) && ReadInt(acknowledged, "confirmedCount", -1) == 1
                        && ReadInt(acknowledged, "deferredCount", -1) == 3
                        && ReadBool(Assignment(afterConfirm, direct), "deferNativeTraitsToQueue", false)
                        && ReadString(Profile(direct), "startup_native_session_id", "missing") == ""
                        && ReadString(Profile(direct), "native_sync_status", "") == "synchronized"
                        && CanonicalJson(Command(queued)) == typedRow && CanonicalJson(Profile(queued)) == typedProfile
                        && PersistenceService.FileSha256(queuedProjection) == queuedProjectionHash,
                        "Actual confirm-batch consumes the stable direct reservation and reports held work separately. Retry cannot mint a replacement.");

                    ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_native_commands SET status='completed' WHERE command_id=$id;",
                        TestDict("id", ExpectedNotableStartupCommandId(queued)));
                    ReignDatabase.ExecuteSql(connection, "UPDATE notable_mbti_profiles SET native_action_id='',native_sync_status='synchronized' WHERE hero_id=$hero;",
                        TestDict("hero", queued));
                    string terminal = CanonicalJson(Command(queued)), unlinked = CanonicalJson(Profile(queued));
                    ObserveOwnedNotableNativeTraits(connection, null, campaign, queued, 3d, Hero(queued));
                    add("typed_ordinary_observation_never_relinks_terminal_or_lost_legacy_command",
                        CanonicalJson(Command(queued)) == terminal && CanonicalJson(Profile(queued)) == unlinked,
                        "The actual guarded background observation respects an unlinked terminal command and cannot restore its previous link.");

                    var oldCall = NotableMbtiInitializeCore(TestDict("campaignId", campaign, "worldDay", 3d,
                        "heroes", new[] { Hero("old_protocol_stock") }), typedStartup: false);
                    var oldAssignment = Assignment(oldCall, "old_protocol_stock");
                    add("typed_startup_mode_disabled_preserves_original_stock_wire",
                        ReadBool(oldCall, "ok", false) && !oldCall.ContainsKey(TypedNotableStartupField)
                        && !oldAssignment.ContainsKey(StartupNativeSessionField) && !oldAssignment.ContainsKey("deferNativeTraitsToQueue")
                        && ReadDictionary(oldAssignment, "nativeTraits")?.Count == 5,
                        "The disabled feature retains the original startup assignment format and native trait material.");
                }
                finally { Program.CampaignDataGate.ExitReadLock(); }

                VerifyStockNotableManualConstruction(add, campaign);
                VerifyRetainedNotablePreactivationGuards(add, campaign);
            }
            finally
            {
                try { ReignPostgreSqlStorage.DropCampaign(campaign); }
                finally
                {
                    PersistenceService.TryDeleteDirectory(campaignRoot);
                    PersistenceService.TryDeleteDirectory(ledgerRoot);
                }
            }
        }

        private static void VerifyRetainedNotablePreactivationGuards(Action<string, bool, string> add, string campaign)
        {
            const string held = "retained_startup_stock", statusOnly = "retained_startup_status",
                ordinary = "unreserved_stock_queue", manual = "unreserved_manual_queue";
            Dictionary<string, object> Native(string id) => TestDict("heroStringId", id, "campaignId", campaign,
                "name", id, "isNotable", true, "isAlive", true, "narrativeVersion", 0,
                "traits", TestDict("valor", 0, "generosity", 0, "honor", 0, "mercy", 0, "calculating", 0));
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            Dictionary<string, object> Profile(string hero) =>
                RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", hero)).Single();
            string CommandId(string hero) => ExpectedNotableStartupCommandId(hero);
            Dictionary<string, object> Command(string id) =>
                RelationshipsStore.SelectCharacterEditorNativeCommands(connection, TestDict("id", id)).Single();
            foreach (string hero in new[] { held, statusOnly, ordinary })
            {
                EnsureNotableMbtiProfile(connection, campaign, 4d, Native(hero), false, queueNativeTraits: false);
                RelationshipsStore.InsertOrIgnoreCharacterEditorNativeCommands(connection,
                    TestDict("id", CommandId(hero), "revision", "notable_mbti_v" + NotableMbtiTemplateVersion,
                        "hero", hero, "changes", "{}", "inverse", "{}", "ts", 0L));
            }
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_editor_native_commands
(command_id,revision_id,hero_id,status,changes_json,inverse_json,dangerous,confirmation,created_ts)
VALUES($id,$id,$id,'pending','{}','{}',0,'',0);", TestDict("id", manual));
            // Reproduce mixed legacy state that a restored save can carry. Startup
            // and the stock queue must not independently act on the same notable.
            ReignDatabase.ExecuteSql(connection,
                "UPDATE notable_mbti_profiles SET startup_native_session_id=$session WHERE hero_id=$hero;",
                TestDict("hero", held, "session", Json.Serialize(TestDict("timelineId", "notable_startup_fixture",
                    "restoreGeneration", "retained_generation", "preparationId", Guid.NewGuid().ToString("N"),
                    "sessionId", Guid.NewGuid().ToString("N")))));
            ReignDatabase.ExecuteSql(connection,
                "UPDATE notable_mbti_profiles SET native_sync_status=$status WHERE hero_id=$hero;",
                TestDict("hero", statusOnly, "status", StockNativeStartupPending));
            string ProfileBytes(string hero) => CanonicalJson(Profile(hero));
            string heldProfile = ProfileBytes(held), statusProfile = ProfileBytes(statusOnly);
            string Id(Dictionary<string, object> row) => ReadString(row, "command_id", "");
            bool ReportVisible(string id) =>
                CharactersStore.ReadLegacyNativeEditorCommandForReport(connection, TestDict("id", id)) != null;
            var first = CharactersStore.ClaimCharacterEditorNativeCommands(connection,
                TestDict("ts", 1000L, "stale", 700L, "claim", "{}"));
            bool correctClaims = first.Any(row => Id(row) == CommandId(ordinary)) && first.Any(row => Id(row) == manual)
                && first.All(row => Id(row) != CommandId(held) && Id(row) != CommandId(statusOnly))
                && ReadString(Command(CommandId(held)), "status", "") == "pending"
                && ReadString(Command(CommandId(statusOnly)), "status", "") == "pending"
                && ReportVisible(CommandId(ordinary)) && ReportVisible(manual)
                && !ReportVisible(CommandId(held)) && !ReportVisible(CommandId(statusOnly));

            ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_native_commands SET status='claimed',claimed_ts=1 WHERE command_id=$id;",
                TestDict("id", CommandId(held)));
            string stale = CanonicalJson(Command(CommandId(held)));
            var retry = CharactersStore.ClaimCharacterEditorNativeCommands(connection,
                TestDict("ts", 1100L, "stale", 800L, "claim", "{}"));
            bool heldStaleClaim = retry.All(row => Id(row) != CommandId(held))
                && CanonicalJson(Command(CommandId(held))) == stale && !ReportVisible(CommandId(held));

            foreach (string id in new[] { CommandId(held), CommandId(ordinary), manual })
                ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_native_commands SET status='reconciling' WHERE command_id=$id;",
                    TestDict("id", id));
            string reconciling = CanonicalJson(Command(CommandId(held)));
            var replay = CharactersStore.ReadCharacterEditorNativeReconciliations(connection);
            add("stock_notable_legacy_delivery_holds_startup_reservations",
                correctClaims && heldStaleClaim && replay.All(row => Id(row) != CommandId(held))
                && replay.Any(row => Id(row) == CommandId(ordinary)) && replay.Any(row => Id(row) == manual)
                && !ReportVisible(CommandId(held)) && CanonicalJson(Command(CommandId(held))) == reconciling
                && ProfileBytes(held) == heldProfile && ProfileBytes(statusOnly) == statusProfile,
                "Legacy pending and stale claims, reconciliation, and reports preserve a retained startup owner; ordinary stock and manual work stay eligible.");

            // The profile is intentionally incomplete so only the retained-owner
            // check can stop the real worker before its identity upsert.
            const string ordinaryTown = "unreserved_town_prewarm";
            CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, held, "profile.json"), Native(held));
            var townHero = Native(held);
            townHero["name"] = "Unapplied prewarm identity";
            string profile = CampaignFileService.CharacterFile(campaign, held, "profile.json");
            string enrichment = CampaignFileService.CharacterFile(campaign, held, "enrichment.json");
            string index = System.IO.Path.Combine(CampaignFileService.CampaignDirectory(campaign), "characters", "index.json");
            string HashOrMissing(string path) => System.IO.File.Exists(path) ? PersistenceService.FileSha256(path) : "";
            string beforeProfile = HashOrMissing(profile), beforeIndex = HashOrMissing(index),
                beforeEnrichment = HashOrMissing(enrichment);
            long generation = System.Threading.Volatile.Read(ref PersistenceService.CampaignReplacementGeneration);
            int calls = 0;
            Dictionary<string, object> Author(string c, string h, Dictionary<string, object> request)
            {
                calls++;
                var ready = TestDict("ok", true, "status", "ready", "llmUsed", true);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(c, h, "constructed.json"), ready);
                return ready;
            }
            var protectedWork = new CharactersService.TownProfileWork
            {
                Campaign = campaign, HeroId = held, Hero = townHero, Generation = generation
            };
            bool skipped = CharactersService.VerifyTownNotableStartupPrewarm(protectedWork, Author);
            bool preserved = skipped && calls == 0 && HashOrMissing(profile) == beforeProfile
                && HashOrMissing(index) == beforeIndex && HashOrMissing(enrichment) == beforeEnrichment
                && ProfileBytes(held) == heldProfile && CanonicalJson(Command(CommandId(held))) == reconciling;
            var controlWork = new CharactersService.TownProfileWork
            {
                Campaign = campaign, HeroId = ordinaryTown, Hero = Native(ordinaryTown), Generation = generation
            };
            bool constructed = CharactersService.VerifyTownNotableStartupPrewarm(controlWork, Author);
            add("stock_notable_town_prewarm_holds_before_profile_upsert",
                preserved && constructed && calls == 1
                && System.IO.File.Exists(CampaignFileService.CharacterFile(campaign, ordinaryTown, "enrichment.json")),
                "Real town prewarm leaves the held row, profile, unrelated index and enrichment untouched, while ordinary unreserved work reaches its author.");
        }

        private static void VerifyStockNotableManualConstruction(Action<string, bool, string> add, string campaign)
        {
            const string hero = "retained_manual_stock", control = "unmarked_construction_stock";
            Dictionary<string, object> Native(string id) => TestDict("heroStringId", id, "campaignId", campaign,
                "name", id, "isNotable", true, "isAlive", true, "narrativeVersion", 0,
                "traits", TestDict("valor", 0, "generosity", 0, "honor", 0, "mercy", 0, "calculating", 0));
            var native = Native(hero);
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            var assigned = EnsureNotableMbtiProfile(connection, campaign, 3d, native, false, queueNativeTraits: false);
            var assignedNativeTraits = ReadDictionary(assigned, "nativeTraits");
            if (assignedNativeTraits?.Count != 5)
                throw new InvalidOperationException("The manual stock fixture requires five assigned native traits.");
            native["traits"] = assignedNativeTraits;
            string FileFor(string file) => CampaignFileService.CharacterFile(campaign, hero, file);
            CampaignFileService.WriteJsonObject(FileFor("profile.json"), native);

            Dictionary<string, object> SaveManual(string recipient)
            {
                var editor = CharactersService.CharacterEditorLoadApi(TestDict("campaignId", campaign, "heroStringId", recipient));
                var fields = TestDict("description", "Durable manual editor fixture");
                return CharactersService.CharacterEditorSaveApi(TestDict("campaignId", campaign, "heroStringId", recipient,
                    "section", "profile", "revisionToken", ReadString(editor, "revisionToken", ""),
                    "profileEdit", TestDict("basis", ReadString(editor, "profileEditBasis", ""), "fields", fields),
                    "nativeChanges", fields));
            }
            var saved = SaveManual(hero);
            string revision = ReadString(saved, "revisionId", "");
            string authority = FileFor("editor_authority.json");
            if (!ReadBool(saved, "ok", false) || !ReadBool(saved, "nativeQueued", false) || !System.IO.File.Exists(authority))
                throw new InvalidOperationException("The actual editor route did not produce its manual authority and revision.");

            Dictionary<string, object> Notable() =>
                RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", hero)).Single();
            Dictionary<string, object> ManualCommand() =>
                ReignDatabase.QuerySql(connection, "SELECT * FROM character_editor_native_commands WHERE hero_id=$hero AND revision_id=$revision;",
                    TestDict("hero", hero, "revision", revision)).Single();
            string Files() => Json.Serialize(new[] { "profile.json", "traits.json", "constructed.json", "editor_authority.json" }
                .Select(FileFor).Select(path => System.IO.File.Exists(path) ? PersistenceService.FileSha256(path) : "absent"));
            string notablesBefore = CanonicalJson(Notable());
            var manual = ManualCommand();
            string commandBefore = CanonicalJson(manual), filesBefore = Files();
            var incoming = new Dictionary<string, object>(native) { ["name"] = "must_not_overwrite_manual" };
            var startup = NotableMbtiInitializeApi(TestDict("campaignId", campaign, "worldDay", 3d, "heroes", new[] { incoming }));
            var assignment = ReadDictionaryList(startup, "assignments").Single();
            var confirm = NotableMbtiConfirmBatchApi(TestDict("campaignId", campaign, "worldDay", 3d, "observations",
                new[] { TestDict("heroId", hero, "status", DeferredNativeStartupStatus) }));
            var requested = CharactersService.ConstructCharacter(TestDict("campaignId", campaign, "hero", incoming, "force", true, "useLlm", false));
            var batch = CharactersService.ConstructCharacterBatch(TestDict("campaignId", campaign, "heroes", new[] { incoming },
                "force", true, "useLlm", false));
            var firstContact = CharactersService.EnsureCharacterConstructed(campaign, hero, incoming, "stock_fallback_fixture");
            var direct = CharactersService.ConstructCharacterFiles(campaign, hero, incoming, force: true, useLlm: false,
                source: "stock_fallback_fixture");
            var normal = CharactersService.ConstructCharacter(TestDict("campaignId", campaign, "hero", Native(control),
                "force", true, "useLlm", false));
            add("stock_notable_real_manual_save_holds_startup_and_construction",
                ReadString(manual, "command_id", "").StartsWith("editor_command_", StringComparison.Ordinal)
                && ReadString(Notable(), "native_action_id", "missing") == ""
                && ReadBool(startup, "ok", false) && assignment.Count == 2 && ReadBool(assignment, "deferNativeTraitsToQueue", false)
                && ReadBool(confirm, "ok", false) && ReadInt(confirm, "deferredCount", -1) == 1
                && ReadString(requested, "status", "") == DeferredNativeStartupStatus
                && ReadInt(batch, "failed", -1) == 1
                && (ReadString(firstContact, "status", "") == DeferredNativeStartupStatus || ReadBool(firstContact, "alreadyReady", false))
                && ReadString(direct, "status", "") == DeferredNativeStartupStatus
                && CanonicalJson(Notable()) == notablesBefore && CanonicalJson(ManualCommand()) == commandBefore && Files() == filesBefore
                && CharactersService.IsCharacterConstructionReady(normal)
                && System.IO.File.Exists(CampaignFileService.CharacterFile(campaign, control, "traits.json")),
                "An actual browser save creates an unlinked manual native command and authoritative files. Stock startup and all construction entry points preserve that hero while a marker-free control still constructs.");

            var staleRole = new Dictionary<string, object>(incoming) { ["isNotable"] = false };
            string indexPath = System.IO.Path.Combine(CampaignFileService.CampaignDirectory(campaign), "characters", "index.json");
            string indexBefore = PersistenceService.FileSha256(indexPath);
            bool Refused(Dictionary<string, object> result) => ReadString(result, "status", "") == DeferredNativeStartupStatus;
            Dictionary<string, object> RequestConstruction(Dictionary<string, object> request) =>
                CharactersService.ConstructCharacter(TestDict("campaignId", campaign, "hero", request, "force", true, "useLlm", false));
            var staleSingle = RequestConstruction(staleRole);
            var staleBatch = CharactersService.ConstructCharacterBatch(TestDict("campaignId", campaign, "heroes", new[] { staleRole },
                "force", true, "useLlm", false));
            var staleDirect = CharactersService.ConstructCharacterFiles(campaign, hero, staleRole, force: true, useLlm: false,
                source: "stock_fallback_fixture");
            var staleFirstContact = CharactersService.EnsureCharacterConstructed(campaign, hero, staleRole, "stock_fallback_fixture");
            bool protectedBefore = Refused(staleSingle)
                && Refused(ReadDictionary(ReadDictionaryList(staleBatch, "failures").SingleOrDefault(), "result"))
                && Refused(staleDirect)
                && (Refused(staleFirstContact) || ReadBool(staleFirstContact, "alreadyReady", false))
                && Files() == filesBefore && CanonicalJson(Notable()) == notablesBefore
                && CanonicalJson(ManualCommand()) == commandBefore;

            var persisted = CampaignFileService.ReadJsonObject(FileFor("profile.json"));
            persisted["isNotable"] = false;
            CampaignFileService.WriteJsonObject(FileFor("profile.json"), persisted);
            string sqlOnlyFiles = Files();
            bool sqlOnlyHeld = Refused(RequestConstruction(staleRole)) && Files() == sqlOnlyFiles
                && CanonicalJson(Notable()) == notablesBefore && CanonicalJson(ManualCommand()) == commandBefore;

            var readyTraits = CharactersService.BuildTraitDocument(native);
            if (!CharactersService.TraitDocumentReady(readyTraits))
                throw new InvalidOperationException("The retained-ready fixture requires complete traits from the ordinary builder.");
            CampaignFileService.WriteJsonObject(FileFor("traits.json"), readyTraits);
            CampaignFileService.WriteJsonObject(FileFor("constructed.json"), TestDict("ok", true, "status", "ready"));
            string readyFiles = Files();
            var readReady = CharactersService.EnsureCharacterConstructed(campaign, hero, staleRole, "stock_fallback_fixture");
            add("stock_notable_false_flag_existing_ready_is_read_only",
                CharactersService.IsCharacterConstructionReady(readReady) && ReadBool(readReady, "alreadyReady", false)
                && Files() == readyFiles && CanonicalJson(Notable()) == notablesBefore
                && CanonicalJson(ManualCommand()) == commandBefore && PersistenceService.FileSha256(indexPath) == indexBefore,
                "The actual first-contact route returns a retained complete profile without writing even when only the SQL row identifies the notable.");

            persisted["isNotable"] = true;
            CampaignFileService.WriteJsonObject(FileFor("profile.json"), persisted);
            ReignDatabase.ExecuteSql(connection, "DELETE FROM notable_mbti_profiles WHERE hero_id=$hero;", TestDict("hero", hero));
            string profileOnlyFiles = Files();
            bool profileOnlyHeld = Refused(RequestConstruction(staleRole)) && Files() == profileOnlyFiles
                && RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", hero)).Count == 0
                && CanonicalJson(ManualCommand()) == commandBefore;
            add("stock_notable_construction_uses_stored_profile_and_sql_identity",
                protectedBefore && sqlOnlyHeld && profileOnlyHeld && PersistenceService.FileSha256(indexPath) == indexBefore,
                "The real single, batch, direct and first-contact routes respect stored notable identity under a false request flag, including each stored identity source in isolation.");

            const string ordinaryHero = "real_nonnotable_construction_stock";
            var ordinary = Native(ordinaryHero);
            ordinary["isNotable"] = false;
            CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, ordinaryHero, "profile.json"), ordinary);
            var ordinarySave = SaveManual(ordinaryHero);
            var ordinaryConstruction = RequestConstruction(ordinary);
            add("stock_notable_stale_role_preserves_genuine_nonnotable_construction",
                ReadBool(ordinarySave, "ok", false)
                && System.IO.File.Exists(CampaignFileService.CharacterFile(campaign, ordinaryHero, "editor_authority.json"))
                && CharactersService.IsCharacterConstructionReady(ordinaryConstruction)
                && RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", ordinaryHero)).Count == 0,
                "A genuinely non-notable hero with its own actual manual authority keeps ordinary construction when no notable SQL identity exists.");
        }
#endif
    }
}
