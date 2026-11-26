using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private const string AutomaticNotableOriginSchema = "reign-automatic-notable-origin-v1";
        private const string AutomaticNotableOriginField = "automaticOriginId";
        private const string AutomaticNotableMaterialField = "automaticOriginMaterialSha256";
        private static readonly string[] AutomaticNotableTraitNames = { "valor", "generosity", "honor", "mercy", "calculating" };
        internal static bool OwnedAutomaticNotableDeliveryReady => OwnedEditorBrowserDeliveryReady;

        private sealed record AutomaticNotableScope(string Campaign, string Timeline, string Generation);
        private sealed record AutomaticNotableOrigin(string Id, string Campaign, string Timeline,
            string Generation, string Hero, string Command, string Material);
        private static string AutomaticNotableVersion => RelationshipsService.NotableMbtiTemplateVersion.ToString(CultureInfo.InvariantCulture);
        private static string AutomaticNotableCommandId(string hero) => "notable_mbti_native_" + hero + "_v" + AutomaticNotableVersion;

        internal static bool HasExactAutomaticNotableTraits(Dictionary<string, object> traits) =>
            traits?.Count == AutomaticNotableTraitNames.Length && AutomaticNotableTraitNames.All(key =>
                traits.TryGetValue(key, out object value) && (value is int level && level is >= -2 and <= 2
                    || value is long wide && wide is >= -2L and <= 2L));

        private static AutomaticNotableScope ReadAutomaticNotableScope(ReignDbConnection connection, string campaign,
            Dictionary<string, object> wire)
        {
            string timeline = ExactNativeEditorField(wire, "timelineId");
            string generation = ExactNativeEditorField(wire, "sharedActivityGeneration");
            if (!IsExactEditorNativeCampaign(campaign) || !TerminalActionReportProtocol.IsTrustedIdentifier(timeline)
                || !TerminalActionReportProtocol.IsTrustedIdentifier(generation) || !HasEditorNativeOwner(wire)
                || !EditorNativeOwnerMatches(connection, wire, campaign)) return null;
            return new AutomaticNotableScope(campaign, timeline, generation);
        }

        internal static bool QueueTypedAutomaticNotableTraits(ReignDbConnection connection, string campaign, string hero,
            double worldDay, Dictionary<string, object> expected, Dictionary<string, object> observed,
            ReignDbTransaction transaction = null)
        {
            if (!HasEditorNativeCampaignGate || !IsExactEditorNativeCampaign(campaign) || !IsEditorStorageId(hero)
                || !WorldSimulationService.OwnsWorldHistoryTimelineScope(campaign)
                || !HasExactAutomaticNotableTraits(expected)
                || ReadAutomaticNotableProjection(campaign, hero) == null) return false;

            using var suppressRetries = transaction == null ? new ReignDatabase.SqlStatementRetrySuppression(connection) : null;
            using var ownedTransaction = transaction == null ? connection.BeginTransaction() : null;
            transaction ??= ownedTransaction;
            if (!PersistenceService.IsNativeEditorOwnershipSchemaReady(connection, transaction)) return false;
            var schema = ReignDatabase.QuerySql(connection, "SELECT current_schema() AS schema_name;", null, transaction).Single();
            if (ReadString(schema, "schema_name", "") != ReignPostgreSqlStorage.CampaignSchemaName(campaign)
                || AutomaticNotableHasManualOverride(connection, campaign, hero, transaction)) return false;
            if (!WorldSimulationService.TableExists(connection, "world_history_timelines")
                || !WorldSimulationService.TableExists(connection, "memory_precision_state")) return false;
            string timeline = WorldSimulationService.ActiveWorldHistoryTimeline(connection);
            var state = DialogueStore.SelectMemoryPrecisionState4(connection);
            string generation = state.Count == 1 ? ExactNativeEditorField(state[0], "restore_generation") : "";
            if (ReadAutomaticNotableScope(connection, campaign, TestDict("campaignId", campaign,
                "timelineId", timeline, "sharedActivityGeneration", generation)) == null) return false;

            string commandId = AutomaticNotableCommandId(hero);
            string revision = "notable_mbti_v" + AutomaticNotableVersion;
            var profile = RelationshipsStore.LockAutomaticNotableProfile(connection, TestDict("hero", hero), transaction);
            string expectedJson = ReadString(profile, "native_traits_json", "");
            if (!IsCurrentAutomaticNotableProfile(profile, hero, expected) || ReadString(profile, "native_action_id", "").Length != 0
                || ReadString(profile, "startup_native_session_id", "").Length != 0
                || CharactersStore.LockTypedAutomaticNotableCommand(connection, TestDict("id", commandId), transaction) != null)
                return false;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var insert = TestDict("id", commandId, "revision", revision, "hero", hero,
                "changes", Json.Serialize(TestDict("traits", expected)), "inverse", Json.Serialize(TestDict("traits", observed)),
                "ts", now);
            // Only this transaction's actual insert can confer producer provenance.
            if (!RelationshipsStore.InsertTypedAutomaticNotableCommand(connection, insert, transaction)) return false;
            var row = CharactersStore.LockTypedAutomaticNotableCommand(connection, TestDict("id", commandId), transaction);
            if (row == null) throw new InvalidOperationException("The exact automatic notable command did not persist.");
            var origin = TestDict("schema", AutomaticNotableOriginSchema, "originId", Guid.NewGuid().ToString("N"),
                "campaignId", campaign, "timelineId", timeline, "generation", generation, "heroId", hero,
                "commandId", commandId, "templateVersion", AutomaticNotableVersion, "materialSha256", EditorNativeOriginMaterialHash(row));
            if (!RelationshipsStore.StampTypedAutomaticNotableOrigin(connection,
                    TestDict("id", commandId, "hero", hero, "revision", revision, "origin", Json.Serialize(origin)), transaction)
                || !RelationshipsStore.LinkTypedAutomaticNotableCommand(connection,
                    TestDict("id", commandId, "hero", hero, "version", RelationshipsService.NotableMbtiTemplateVersion,
                        "expected", expectedJson, "day", worldDay, "ts", now), transaction))
                throw new InvalidOperationException("The automatic notable command and its exact profile link could not be committed together.");
            ownedTransaction?.Commit();
            return true;
        }

        private static bool IsCurrentAutomaticNotableProfile(Dictionary<string, object> profile, string hero,
            Dictionary<string, object> traits) =>
            ReadString(profile, "hero_id", "") == hero && ReadString(profile, "source", "") == "notable_mbti_template"
            && ReadInt(profile, "template_version", 0) == RelationshipsService.NotableMbtiTemplateVersion
            && ReadString(profile, "native_sync_status", "") is "pending" or "claimed" or "awaiting_confirmation"
            && HasExactAutomaticNotableTraits(traits)
            && CanonicalJson(TryParseStrictJsonObject(ReadString(profile, "native_traits_json", ""))) == CanonicalJson(traits);

        private static AutomaticNotableOrigin ReadTypedAutomaticNotableOrigin(Dictionary<string, object> row)
        {
            var marker = TryParseStrictJsonObject(ReadString(row, "automatic_origin_json", ""));
            string hero = ExactNativeEditorField(marker, "heroId");
            string id = ExactNativeEditorField(marker, "originId");
            string campaign = ExactNativeEditorField(marker, "campaignId");
            string timeline = ExactNativeEditorField(marker, "timelineId");
            string generation = ExactNativeEditorField(marker, "generation");
            string command = ExactNativeEditorField(marker, "commandId");
            string material = ExactNativeEditorField(marker, "materialSha256");
            if (ExactNativeEditorField(marker, "schema") != AutomaticNotableOriginSchema
                || !EditorNativeWireNonce(id) || !IsExactEditorNativeCampaign(campaign) || !IsEditorStorageId(hero)
                || !TerminalActionReportProtocol.IsTrustedIdentifier(timeline) || !TerminalActionReportProtocol.IsTrustedIdentifier(generation)
                || command != AutomaticNotableCommandId(hero) || command != ReadString(row, "command_id", "")
                || hero != ReadString(row, "hero_id", "") || ReadString(row, "revision_id", "") != "notable_mbti_v" + AutomaticNotableVersion
                || ReadString(row, "confirmation", "") != "automatic_notable_mbti_sync" || ReadInt(row, "dangerous", -1) != 0
                || ReadString(row, "manual_origin_json", "").Length != 0
                || ExactNativeEditorField(marker, "templateVersion") != AutomaticNotableVersion
                || material != EditorNativeOriginMaterialHash(row)) return null;
            return new AutomaticNotableOrigin(id, campaign, timeline, generation, hero, command, material);
        }

        private static Dictionary<string, object> AutomaticNotableTraits(Dictionary<string, object> row)
        {
            var changes = TryParseStrictJsonObject(ReadString(row, "changes_json", ""));
            return changes?.Count == 1 ? ReadDictionary(changes, "traits") : null;
        }

        private static bool IsAutomaticNotableProjection(Dictionary<string, object> profile, string campaign, string hero) =>
            profile != null && ExactNativeEditorField(profile, "campaignId") == campaign
            && ExactNativeEditorField(profile, "heroStringId") == hero
            && profile.Keys.Count(key => key.Equals("traits", StringComparison.OrdinalIgnoreCase)) == 1
            && profile.Keys.Contains("traits", StringComparer.Ordinal)
            && profile["traits"] is Dictionary<string, object>;

        private static Dictionary<string, object> ReadAutomaticNotableProjection(string campaign, string hero)
        {
            // This stock profile is atomically replaced. Avoid taking the global file lock
            // while the producer may hold a caller's PostgreSQL profile-row transaction.
            var profile = CampaignFileService.ReadAtomicallyReplacedJsonObject(
                CampaignFileService.CharacterFile(campaign, hero, "profile.json"));
            return IsAutomaticNotableProjection(profile, campaign, hero) ? profile : null;
        }

        private static bool AutomaticNotableHasManualOverride(ReignDbConnection connection, string campaign, string hero,
            ReignDbTransaction transaction = null) =>
            System.IO.File.Exists(CampaignFileService.CharacterFile(campaign, hero, "editor_authority.json"))
            || CharactersStore.HasManualEditorRevisionForHero(connection, TestDict("hero", hero), transaction);

        private static bool AutomaticLinkStillMatches(ReignDbConnection connection, Dictionary<string, object> profile,
            Dictionary<string, object> row, AutomaticNotableOrigin origin, ReignDbTransaction transaction = null) =>
            IsCurrentAutomaticNotableProfile(profile, origin.Hero, AutomaticNotableTraits(row))
            && ReadString(profile, "native_action_id", "") == origin.Command
            && ReadString(profile, "startup_native_session_id", "").Length == 0
            && !AutomaticNotableHasManualOverride(connection, origin.Campaign, origin.Hero, transaction);

        private static bool AutomaticWireMatchesOrigin(Dictionary<string, object> wire, AutomaticNotableOrigin origin) =>
            wire != null && !wire.Keys.Any(key => key.Equals(ManualEditorSessionField, StringComparison.OrdinalIgnoreCase)
                || key.Equals(ManualEditorLineageField, StringComparison.OrdinalIgnoreCase)
                || key.Equals(ManualEditorMaterialField, StringComparison.OrdinalIgnoreCase))
            && ExactNativeEditorField(wire, "campaignId") == origin.Campaign && ExactNativeEditorField(wire, "timelineId") == origin.Timeline
            && ExactNativeEditorField(wire, "commandId") == origin.Command && ExactNativeEditorField(wire, AutomaticNotableOriginField) == origin.Id
            && ExactNativeEditorField(wire, AutomaticNotableMaterialField) == origin.Material
            && TerminalActionReportProtocol.IsTrustedIdentifier(ExactNativeEditorField(wire, "sharedActivityGeneration"))
            && EditorNativeWireNonce(ExactNativeEditorField(wire, "claimId"))
            && ExactNativeEditorFlag(wire, ManualEditorReceiptOnlyField, out _);

        private static bool ReadAutomaticNotableReport(Dictionary<string, object> wire, out string status)
        {
            if (!TryReadManualEditorResult(wire, out status)) return false;
            var applied = ReadStringList(wire, "applied");
            return status == "completed" && applied.SequenceEqual(new[] { "traits" }) || status == "failed" && applied.Count == 0;
        }

        private static void AppendAutomaticNotableClaims(ReignDbConnection connection, AutomaticNotableScope scope,
            List<Dictionary<string, object>> commands, HashSet<string> reservedHeroes)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var row in CharactersStore.ReadTypedAutomaticNotableQueue(connection))
            {
                if (commands.Count == 25) break;
                var origin = ReadTypedAutomaticNotableOrigin(row);
                if (origin == null || origin.Campaign != scope.Campaign || origin.Timeline != scope.Timeline
                    || !reservedHeroes.Add(origin.Hero)) continue;
                var profile = RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", origin.Hero)).SingleOrDefault();
                if (!AutomaticLinkStillMatches(connection, profile, row, origin)) continue;
                var command = TryIssueAutomaticNotableClaim(connection, row, origin, scope, now);
                if (command != null) commands.Add(command);
            }
        }

        private static Dictionary<string, object> TryIssueAutomaticNotableClaim(ReignDbConnection connection,
            Dictionary<string, object> row, AutomaticNotableOrigin origin, AutomaticNotableScope scope, long now)
        {
            string status = ReadString(row, "status", "");
            string priorJson = ReadString(row, "result_json", "");
            long priorTime = ReadLong(row, "claimed_ts", 0);
            var retained = TryParseStrictJsonObject(priorJson);
            bool reconciling = status == "reconciling";
            if (reconciling)
            {
                var report = ReadDictionary(retained, "report");
                if (!AutomaticWireMatchesOrigin(report, origin) || !ReadAutomaticNotableReport(report, out _)) return null;
                var reclaim = ReadDictionary(retained, "automaticReclaim");
                if (reclaim == null && ExactNativeEditorField(report, "sharedActivityGeneration") == scope.Generation)
                {
                    FinishTypedAutomaticNotable(connection, row, origin);
                    return null;
                }
                if (ExactNativeEditorField(reclaim, "sharedActivityGeneration") == scope.Generation && priorTime >= now - 300)
                    return null;
            }
            else if (status == "claimed")
            {
                if (!AutomaticWireMatchesOrigin(retained, origin) || priorTime >= now - 300) return null;
            }
            else if (status != "pending" || origin.Generation != scope.Generation || retained is not { Count: 0 }
                || priorTime != 0 || ReadLong(row, "resolved_ts", 0) != 0
                || ReadAutomaticNotableProjection(origin.Campaign, origin.Hero) == null) return null;

            bool receiptOnly = status != "pending";
            var claim = TestDict("campaignId", origin.Campaign, "timelineId", origin.Timeline, "sharedActivityGeneration", scope.Generation,
                "commandId", origin.Command, "claimId", Guid.NewGuid().ToString("N"), AutomaticNotableOriginField, origin.Id,
                AutomaticNotableMaterialField, origin.Material, ManualEditorReceiptOnlyField, receiptOnly);
            if (reconciling) retained["automaticReclaim"] = claim;
            if (!CharactersStore.PublishTypedAutomaticNotableClaim(connection, TestDict(
                "id", origin.Command, "previous", status, "previousTs", priorTime, "basis", priorJson,
                "origin", ReadString(row, "automatic_origin_json", ""), "status", reconciling ? "reconciling" : "claimed",
                "ts", now, "result", Json.Serialize(reconciling ? retained : claim)))) return null;

            PersistenceService.MarkSaveSyncActiveStateDirty(origin.Campaign);
            return TestDict("command_id", origin.Command, "claimId", ExactNativeEditorField(claim, "claimId"),
                "hero_id", origin.Hero, "dangerous", 0, "confirmation", "automatic_notable_mbti_sync",
                "changes", TestDict("traits", AutomaticNotableTraits(row)), AutomaticNotableOriginField, origin.Id,
                AutomaticNotableMaterialField, origin.Material, ManualEditorReceiptOnlyField, receiptOnly);
        }

        private static Dictionary<string, object> ReportTypedAutomaticNotable(ReignDbConnection connection, string campaign,
            Dictionary<string, object> row, Dictionary<string, object> payload)
        {
            string id = ReadString(row, "command_id", "");
            var origin = ReadTypedAutomaticNotableOrigin(row);
            if (origin == null || origin.Campaign != campaign || !AutomaticWireMatchesOrigin(payload, origin)
                || !ReadAutomaticNotableReport(payload, out string reported))
                return NativeEditorReportResult(id, "ownership_mismatch", false);
            string prior = ReadString(row, "status", "");
            var journal = TryParseStrictJsonObject(ReadString(row, "result_json", ""));
            if (prior is "completed" or "failed")
            {
                string exact = CanonicalJson(payload);
                bool retained = exact == CanonicalJson(ReadDictionary(journal, "report"))
                    || exact == CanonicalJson(ReadDictionary(journal, "originalAutomaticReport"));
                return NativeEditorReportResult(id, retained ? prior : "report_conflict", retained);
            }

            var scope = ReadAutomaticNotableScope(connection, campaign, payload);
            var profile = RelationshipsStore.SelectNotableMbtiProfiles4(connection, TestDict("id", origin.Hero)).SingleOrDefault();
            if (scope == null || !AutomaticLinkStillMatches(connection, profile, row, origin))
                return NativeEditorReportResult(id, "ownership_mismatch", false);
            if (prior == "claimed")
            {
                if (!AutomaticWireMatchesOrigin(journal, origin)
                    || ExactNativeEditorField(journal, "sharedActivityGeneration") != scope.Generation
                    || !ManualEditorClaimMatchesReport(journal, payload))
                    return NativeEditorReportResult(id, "claim_mismatch", false);
                var currentProfile = ReadAutomaticNotableProjection(campaign, origin.Hero);
                if (reported == "completed" && currentProfile == null)
                    return NativeEditorReportResult(id, "projection_conflict", false);
                string beforeHash = currentProfile == null ? "" : EditorProfileValueHash(currentProfile["traits"]);
                journal = TestDict("report", payload, "beforeTraitsHash", beforeHash, "settledAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            else if (prior == "reconciling")
            {
                var accepted = ReadDictionary(journal, "report");
                if (!AutomaticWireMatchesOrigin(accepted, origin) || !ReadAutomaticNotableReport(accepted, out _))
                    return NativeEditorReportResult(id, "report_conflict", false);
                var claim = ReadDictionary(journal, "automaticReclaim");
                if (claim == null && CanonicalJson(accepted) == CanonicalJson(payload))
                    return FinishTypedAutomaticNotable(connection, row, origin);
                if (!AutomaticWireMatchesOrigin(claim, origin)
                    || ExactNativeEditorField(claim, "sharedActivityGeneration") != scope.Generation
                    || !ManualEditorClaimMatchesReport(claim, payload) || !ExactNativeEditorFlag(payload, ManualEditorReceiptOnlyField, out bool receiptOnly)
                    || !receiptOnly || !ManualEditorOutcomesMatch(accepted, payload))
                    return NativeEditorReportResult(id, "report_conflict", false);
                if (!journal.ContainsKey("originalAutomaticReport")) journal["originalAutomaticReport"] = accepted;
                journal["report"] = payload;
                journal.Remove("automaticReclaim");
            }
            else return NativeEditorReportResult(id, "claim_mismatch", false);

            string acceptedJson = Json.Serialize(journal);
            if (!TransitionNativeEditorReport(connection, campaign, row, "reconciling", acceptedJson, 0))
                return NativeEditorReportResult(id, "claim_mismatch", false);
            row["status"] = "reconciling";
            row["result_json"] = acceptedJson;
            return FinishTypedAutomaticNotable(connection, row, origin);
        }

        private static Dictionary<string, object> FinishTypedAutomaticNotable(ReignDbConnection connection,
            Dictionary<string, object> expectedRow, AutomaticNotableOrigin origin)
        {
            var journal = TryParseStrictJsonObject(ReadString(expectedRow, "result_json", ""));
            var report = ReadDictionary(journal, "report");
            long settled = ReadLong(journal, "settledAt", 0);
            if (settled <= 0 || ReadDictionary(journal, "automaticReclaim") != null || !AutomaticWireMatchesOrigin(report, origin)
                || !ReadAutomaticNotableReport(report, out string status) || ReadAutomaticNotableScope(connection, origin.Campaign, report) == null
                || !journal.TryGetValue("beforeTraitsHash", out object field) || field is not string before
                || !(before.Length == 0 || before.Length == 64 && before.All(Uri.IsHexDigit)))
                return NativeEditorReportResult(origin.Command, "reconciling", false);

            using var suppressRetries = new ReignDatabase.SqlStatementRetrySuppression(connection);
            using var transaction = connection.BeginTransaction();
            // Producer and settlement both take profile before command.
            var profile = RelationshipsStore.LockAutomaticNotableProfile(connection, TestDict("hero", origin.Hero), transaction);
            var row = CharactersStore.LockTypedAutomaticNotableCommand(connection, TestDict("id", origin.Command), transaction);
            if (ReadTypedAutomaticNotableOrigin(row) != origin || ReadString(row, "status", "") != "reconciling"
                || ReadString(row, "result_json", "") != ReadString(expectedRow, "result_json", "")
                || !AutomaticLinkStillMatches(connection, profile, row, origin, transaction))
                return NativeEditorReportResult(origin.Command, "reconciling", false);
            bool success = status == "completed";
            if (success && !ProjectTypedAutomaticNotableTraits(origin, AutomaticNotableTraits(row), before, settled))
                return NativeEditorReportResult(origin.Command, "projection_conflict", false);

            if (!RelationshipsStore.ClearTypedAutomaticNotableCommandLink(connection,
                    TestDict("id", origin.Command, "hero", origin.Hero, "version", RelationshipsService.NotableMbtiTemplateVersion,
                        "expected", ReadString(profile, "native_traits_json", ""), "profileStatus", success ? "synchronized" : "failed",
                        "success", success, "ts", settled), transaction)
                || !CharactersStore.CompleteTypedAutomaticNotableCommand(connection,
                    TestDict("id", origin.Command, "status", status, "ts", settled,
                        "basis", ReadString(row, "result_json", ""), "origin", ReadString(row, "automatic_origin_json", "")), transaction))
                return NativeEditorReportResult(origin.Command, "reconciling", false);

            transaction.Commit();
            PersistenceService.MarkSaveSyncActiveStateDirty(origin.Campaign);
            return NativeEditorReportResult(origin.Command, status, true);
        }

        private static bool ProjectTypedAutomaticNotableTraits(AutomaticNotableOrigin origin,
            Dictionary<string, object> traits, string before, long settled)
        {
            lock (Program.FileLock)
            {
                string path = CampaignFileService.CharacterFile(origin.Campaign, origin.Hero, "profile.json");
                string indexPath = Path.Combine(CampaignFileService.CampaignDirectory(origin.Campaign), "characters", "index.json");
                try
                {
                    // Only an existing coherent index can be repaired here. A missing or
                    // unreadable index says nothing about what other characters it held.
                    var attributes = File.GetAttributes(indexPath);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
                    using var index = JsonDocument.Parse(File.ReadAllBytes(indexPath),
                        new JsonDocumentOptions { MaxDepth = Json.RecursionLimit });
                    if (!TryFindAutomaticNotableIndexRow(index.RootElement, origin.Hero, out int targetRow)) return false;

                    string desired = EditorProfileValueHash(traits);
                    string updatedUtc = DateTimeOffset.FromUnixTimeSeconds(settled).UtcDateTime.ToString("o");
                    Dictionary<string, object> projected = null;
                    CampaignFileService.TryUpdateJsonObject(path, profile =>
                    {
                        if (!IsAutomaticNotableProjection(profile, origin.Campaign, origin.Hero)) return null;
                        string current = EditorProfileValueHash(profile["traits"]);
                        if (current == desired)
                        {
                            projected = profile;
                            return null;
                        }
                        if (current != before) return null;
                        profile["traits"] = traits;
                        profile["updatedUtc"] = updatedUtc;
                        return projected = profile;
                    });
                    if (projected == null) return false;
                    // The profile can already hold these traits after a lost settlement.
                    // Use the validated index snapshot, never the lenient ordinary writer.
                    WriteAutomaticNotableIndexRow(indexPath, index.RootElement, targetRow, origin.Hero, projected, updatedUtc);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    return false;
                }
            }
        }

        private static bool TryFindAutomaticNotableIndexRow(JsonElement index, string hero, out int targetRow)
        {
            targetRow = -1;
            if (index.ValueKind != JsonValueKind.Array) return false;
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int position = 0;
            foreach (var row in index.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) return false;
                var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (row.EnumerateObject().Any(field => !properties.Add(field.Name))
                    || !row.TryGetProperty("heroStringId", out var identity) || identity.ValueKind != JsonValueKind.String
                    || identity.GetString() is not { Length: > 0 } id || id != id.Trim() || !identities.Add(id)) return false;
                if (id.Equals(hero, StringComparison.OrdinalIgnoreCase))
                {
                    if (id != hero || row.EnumerateObject().Any(IsAmbiguousAutomaticNotableIndexField)) return false;
                    targetRow = position;
                }
                position++;
            }
            return true;
        }

        private static bool IsAmbiguousAutomaticNotableIndexField(JsonProperty field)
        {
            foreach (string known in new[] { "name", "clanId", "kingdomId", "updatedUtc" })
            {
                if (field.Name.Equals(known, StringComparison.OrdinalIgnoreCase))
                    return field.Name != known || field.Value.ValueKind != JsonValueKind.String;
            }
            return false;
        }

        private static void WriteAutomaticNotableIndexRow(string path, JsonElement index, int targetRow, string hero,
            Dictionary<string, object> profile, string updatedUtc)
        {
            static string ReadIndexField(JsonElement row, string key) =>
                row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) ? value.GetString() : "";
            string name = ReadString(profile, "name", hero);
            string clan = ReadString(profile, "clanId", "");
            string kingdom = ReadString(profile, "kingdomId", "");
            var prior = targetRow < 0 ? default : index[targetRow];
            if (targetRow >= 0 && ReadIndexField(prior, "name") == name
                && ReadIndexField(prior, "clanId") == clan && ReadIndexField(prior, "kingdomId") == kingdom) return;
            var replacement = targetRow < 0 ? new Dictionary<string, object>()
                : prior.EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value, StringComparer.Ordinal);
            replacement["heroStringId"] = hero;
            replacement["name"] = name;
            replacement["clanId"] = clan;
            replacement["kingdomId"] = kingdom;
            replacement["updatedUtc"] = updatedUtc;
            var rows = index.EnumerateArray().Select(row => (object)row).ToList();
            if (targetRow < 0) rows.Add(replacement);
            else rows[targetRow] = replacement;
            CampaignFileService.WriteJsonObject(path, rows);
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static void VerifyTypedAutomaticNotableDelivery(Action<string, bool, string> add)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation)
                throw new InvalidOperationException("Typed notable verification requires the isolated PostgreSQL validation database.");
            string campaign = "__editor_notable_delivery_" + Guid.NewGuid().ToString("N");
            string campaignRoot = PersistenceService.StrictCampaignDirectory(campaign);
            string saveSyncRoot = PersistenceService.SaveSyncCampaignRoot(campaign);
            using (var existing = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                trackCampaignMutations: false, initializeInfrastructure: false))
                if (!CampaignFileService.IsInternalCampaignId(campaign) || existing != null
                    || System.IO.Path.Exists(campaignRoot) || System.IO.Path.Exists(saveSyncRoot))
                    throw new InvalidOperationException("The disposable typed notable fixture is already occupied.");

            try
            {
                RunInEditorNativeBrowserWriteScope(campaign, () =>
                {
                    const string hero = "typed_notable", legacy = "legacy_notable", contradicted = "contradicted_notable";
                    const string timeline = "typed_notable_fixture_timeline";
                    var onOpen = WorldSimulationService.WorldHistoryOpenTimelineApi(TestDict("campaignId", campaign, "timelineId", timeline));
                    if (!ReadBool(onOpen, "ok", false)) throw new InvalidOperationException("The disposable notable timeline could not be opened.");
                    var expected = TestDict("valor", 1, "generosity", -1, "honor", 1, "mercy", 0, "calculating", -1);
                    var observed = TestDict("valor", 0, "generosity", 0, "honor", 0, "mercy", 0, "calculating", 0);
                    using var writer = ReignDatabase.OpenCampaignConnection(campaign);
                    RelationshipsService.EnsureNotableMbtiSchema(writer);
                    CharactersSchema.EnsureManualEditorCommandOriginColumns(writer);
                    CharactersSchema.EnsureAutomaticNotableOriginColumns(writer);
                    using var reader = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                        trackCampaignMutations: false, initializeInfrastructure: false);
                    if (reader == null) throw new InvalidOperationException("The disposable notable schema is unavailable.");
                    var request = TestDict("campaignId", campaign, "timelineId", timeline,
                        "sharedActivityGeneration", EditorProfileRestoreGeneration(writer));
                    string file = CampaignFileService.CharacterFile(campaign, hero, "profile.json");
                    string indexPath = Path.Combine(campaignRoot, "characters", "index.json");
                    var neighbor = TestDict("heroStringId", "stock_neighbor", "name", "Stock neighbor",
                        "unknownIndexMetadata", TestDict("retained", true, "terms", new[] { "one", "two" }));
                    var currentHero = TestDict("heroStringId", hero, "name", "Old name", "customTargetData", "preserve");
                    void WriteProfile(string id, string profileHero, string profileCampaign) =>
                        CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, id, "profile.json"),
                            TestDict("heroStringId", profileHero, "campaignId", profileCampaign, "name", "Typed fixture", "traits", observed));

                    void SeedProfile(string id) => RelationshipsStore.InsertOrIgnoreNotableMbtiProfiles(writer,
                        TestDict("id", id, "type", "INTJ", "title", "Fixture", "description", "Fixture", "foundation", "{}",
                            "percentages", "{}", "native", Json.Serialize(expected), "version", RelationshipsService.NotableMbtiTemplateVersion,
                            "day", 1d, "ts", 1L));
                    Dictionary<string, object> CommandRow(string id) =>
                        CharactersStore.SelectCharacterEditorNativeCommands5(reader, TestDict("id", AutomaticNotableCommandId(id))).SingleOrDefault();
                    Dictionary<string, object> ProfileRow(string id) =>
                        RelationshipsStore.SelectNotableMbtiProfiles4(reader, TestDict("id", id)).Single();
                    List<Dictionary<string, object>> Poll() =>
                        ReadDictionaryList(PollOwnedEditorNativeCommands(request), "commands");
                    Dictionary<string, object> Report(Dictionary<string, object> claim) =>
                        TestDict("campaignId", campaign, "timelineId", timeline, "sharedActivityGeneration", ReadString(request, "sharedActivityGeneration", ""),
                            "commandId", ReadString(claim, "command_id", ""), "claimId", ExactNativeEditorField(claim, "claimId"),
                            AutomaticNotableOriginField, ExactNativeEditorField(claim, AutomaticNotableOriginField),
                            AutomaticNotableMaterialField, ExactNativeEditorField(claim, AutomaticNotableMaterialField),
                            ManualEditorReceiptOnlyField, ReadBool(claim, ManualEditorReceiptOnlyField, false),
                            "status", "completed", "applied", new[] { "traits" }, "errors", Array.Empty<string>());

                    SeedProfile(hero);
                    bool absentHeld = !QueueTypedAutomaticNotableTraits(writer, campaign, hero, 1d, expected, observed)
                        && CommandRow(hero) == null && ReadString(ProfileRow(hero), "native_action_id", "") == "";
                    WriteProfile(hero, "unrelated_hero", campaign);
                    string unrelatedHash = PersistenceService.FileSha256(file);
                    bool wrongHeroHeld = !QueueTypedAutomaticNotableTraits(writer, campaign, hero, 1d, expected, observed)
                        && CommandRow(hero) == null && PersistenceService.FileSha256(file) == unrelatedHash;
                    WriteProfile(hero, hero, campaign + "_foreign");
                    bool wrongCampaignHeld = !QueueTypedAutomaticNotableTraits(writer, campaign, hero, 1d, expected, observed)
                        && CommandRow(hero) == null && ReadString(ProfileRow(hero), "native_action_id", "") == "";
                    File.WriteAllText(file, "{malformed");
                    string malformedProfile = PersistenceService.FileSha256(file);
                    bool malformedHeld = !QueueTypedAutomaticNotableTraits(writer, campaign, hero, 1d, expected, observed)
                        && CommandRow(hero) == null && PersistenceService.FileSha256(file) == malformedProfile;
                    WriteProfile(hero, hero, campaign);
                    CampaignFileService.WriteJsonObject(indexPath, new[] { neighbor, currentHero });
                    add("typed_automatic_missing_or_misowned_profile_never_gets_effect_authority",
                        absentHeld && wrongHeroHeld && wrongCampaignHeld && malformedHeld,
                        "Missing, malformed or misowned profile bytes admit neither a typed command nor its link. The exact profile can still admit and settle the command.");
                    var outOfRange = new Dictionary<string, object>(expected) { ["valor"] = 3 };
                    bool invalidTraitHeld = !HasExactAutomaticNotableTraits(outOfRange)
                        && !QueueTypedAutomaticNotableTraits(writer, campaign, hero, 1d, outOfRange, observed)
                        && CommandRow(hero) == null;
                    bool invisible;
                    using (var transaction = writer.BeginTransaction())
                    {
                        if (!QueueTypedAutomaticNotableTraits(writer, campaign, hero, 1d, expected, observed, transaction))
                            throw new InvalidOperationException("The typed producer could not stage its actual row and profile link.");
                        invisible = CommandRow(hero) == null && ReadString(ProfileRow(hero), "native_action_id", "") == "";
                        transaction.Commit();
                    }
                    var origin = ReadTypedAutomaticNotableOrigin(CommandRow(hero));
                    add("typed_origin_and_exact_profile_link_publish_in_one_transaction",
                        invalidTraitHeld && invisible && origin != null && origin.Generation == ReadString(request, "sharedActivityGeneration", "")
                        && ReadString(ProfileRow(hero), "native_action_id", "") == origin.Command,
                        "A second PostgreSQL connection sees the original typed material and producer link only after COMMIT.");

                    SeedProfile(legacy);
                    RelationshipsStore.InsertOrIgnoreCharacterEditorNativeCommands(writer,
                        TestDict("id", AutomaticNotableCommandId(legacy), "hero", legacy, "revision", "notable_mbti_v" + AutomaticNotableVersion,
                            "changes", Json.Serialize(TestDict("traits", expected)), "inverse", Json.Serialize(TestDict("traits", observed)), "ts", 1L));
                    ReignDatabase.ExecuteSql(writer, "UPDATE notable_mbti_profiles SET native_action_id=$id WHERE hero_id=$hero;",
                        TestDict("id", AutomaticNotableCommandId(legacy), "hero", legacy));
                    SeedProfile(contradicted);
                    WriteProfile(contradicted, contradicted, campaign);
                    if (!QueueTypedAutomaticNotableTraits(writer, campaign, contradicted, 1d, expected, observed))
                        throw new InvalidOperationException("The contradicted typed notable fixture could not queue.");
                    ReignDatabase.ExecuteSql(writer, "UPDATE notable_mbti_profiles SET native_sync_status='contradicted' WHERE hero_id=$hero;",
                        TestDict("hero", contradicted));
                    string displacedFile = file + ".owned_fixture_displaced";
                    string originalProjection = PersistenceService.FileSha256(file);
                    bool missingBeforeClaim;
                    System.IO.File.Move(file, displacedFile);
                    try
                    {
                        missingBeforeClaim = Poll().Count == 0 && ReadString(CommandRow(hero), "status", "") == "pending";
                    }
                    finally { System.IO.File.Move(displacedFile, file); }
                    add("typed_automatic_missing_profile_before_claim_preserves_queued_work",
                        missingBeforeClaim && PersistenceService.FileSha256(file) == originalProjection,
                        "Losing the real projection after queueing holds a pending command until the exact original profile returns.");
                    var first = Poll().Single();
                    add("typed_delivery_holds_linked_legacy_and_contradicted_rows",
                        !QueueTypedAutomaticNotableTraits(writer, campaign, legacy, 1d, expected, observed)
                        && ReadTypedAutomaticNotableOrigin(CommandRow(legacy)) == null
                        && ReadString(CommandRow(legacy), "status", "") == "pending"
                        && ReadString(CommandRow(contradicted), "status", "") == "pending"
                        && ReadString(first, "command_id", "") == AutomaticNotableCommandId(hero)
                        && !ReadBool(first, ManualEditorReceiptOnlyField, true),
                        "A confirmation string, deterministic legacy ID, or contradicted current profile never grants a typed effect.");

                    ReignDatabase.ExecuteSql(writer, "UPDATE character_editor_native_commands SET claimed_ts=1 WHERE command_id=$id;",
                        TestDict("id", origin.Command));
                    var expired = Poll().Single();
                    var accepted = Report(expired);
                    string expiredBasis = ReadString(CommandRow(hero), "result_json", "");
                    var presentedAsNew = new Dictionary<string, object>(accepted) { [ManualEditorReceiptOnlyField] = false };
                    add("typed_expired_claim_accepts_only_restored_receipt_mode",
                        ReadBool(expired, ManualEditorReceiptOnlyField, false)
                        && !ReadBool(ReportOwnedEditorNativeCommand(presentedAsNew), "ok", false)
                        && ReadString(CommandRow(hero), "result_json", "") == expiredBasis,
                        "The expired typed action can only read a corresponding saved native result, and cannot regain new effect authority.");

                    ReignDatabase.ExecuteSql(writer, "UPDATE notable_mbti_profiles SET native_traits_json=$changed WHERE hero_id=$hero;",
                        TestDict("hero", hero, "changed", Json.Serialize(observed)));
                    bool changedProfileHeld = !ReadBool(ReportOwnedEditorNativeCommand(accepted), "ok", false)
                        && ReadString(CommandRow(hero), "result_json", "") == expiredBasis;
                    ReignDatabase.ExecuteSql(writer, "UPDATE notable_mbti_profiles SET native_traits_json=$expected WHERE hero_id=$hero;",
                        TestDict("hero", hero, "expected", Json.Serialize(expected)));
                    add("typed_report_refuses_changed_authoritative_profile_material", changedProfileHeld,
                        "A current claimant cannot publish once the persisted profile material no longer matches its original action.");

                    bool missingAfterClaim;
                    System.IO.File.Move(file, displacedFile);
                    try
                    {
                        var held = ReportOwnedEditorNativeCommand(accepted);
                        missingAfterClaim = ReadString(held, "status", "") == "projection_conflict"
                            && ReadString(CommandRow(hero), "result_json", "") == expiredBasis
                            && ReadString(ProfileRow(hero), "native_action_id", "") == origin.Command
                            && !System.IO.File.Exists(file);
                    }
                    finally { System.IO.File.Move(displacedFile, file); }
                    add("typed_automatic_missing_profile_after_claim_retains_report_for_valid_retry",
                        missingAfterClaim && PersistenceService.FileSha256(file) == originalProjection,
                        "An exact native report cannot publish into a missing profile. Its claim and original link remain available to the real terminal retry below.");

                    byte[] existingIndex = File.ReadAllBytes(indexPath);
                    string heldIndex = indexPath + ".owned_fixture_displaced";
                    string acceptedBasis = null;
                    bool IndexHeld()
                    {
                        var result = ReportOwnedEditorNativeCommand(accepted);
                        var retainedRow = CommandRow(hero);
                        string basis = ReadString(retainedRow, "result_json", "");
                        acceptedBasis ??= basis;
                        return ReadString(result, "status", "") == "projection_conflict"
                            && ReadString(retainedRow, "status", "") == "reconciling"
                            && basis == acceptedBasis && ReadString(ProfileRow(hero), "native_action_id", "") == origin.Command
                            && PersistenceService.FileSha256(file) == originalProjection;
                    }

                    bool missingIndexHeld, unreadableIndexHeld, malformedIndexHeld;
                    bool ambiguousIndexHeld;
                    File.Move(indexPath, heldIndex);
                    try
                    {
                        missingIndexHeld = IndexHeld() && !File.Exists(indexPath);
                        Directory.CreateDirectory(indexPath);
                        string sentinel = Path.Combine(indexPath, "keep");
                        File.WriteAllText(sentinel, "unreadable index path fixture");
                        try { unreadableIndexHeld = IndexHeld() && File.ReadAllText(sentinel) == "unreadable index path fixture"; }
                        finally { File.Delete(sentinel); Directory.Delete(indexPath); }

                        File.WriteAllText(indexPath, "[{");
                        malformedIndexHeld = IndexHeld() && File.ReadAllText(indexPath) == "[{";
                        var alias = TestDict("heroStringId", hero.ToUpperInvariant(), "name", "Ambiguous old row");
                        CampaignFileService.WriteJsonObject(indexPath, new[] { neighbor, alias });
                        string ambiguousHash = PersistenceService.FileSha256(indexPath);
                        ambiguousIndexHeld = IndexHeld() && PersistenceService.FileSha256(indexPath) == ambiguousHash;
                    }
                    finally { File.Move(heldIndex, indexPath, overwrite: true); }

                    add("typed_projection_holds_missing_unreadable_or_malformed_index_without_writes",
                        missingIndexHeld && unreadableIndexHeld && malformedIndexHeld
                        && File.ReadAllBytes(indexPath).SequenceEqual(existingIndex)
                        && PersistenceService.FileSha256(file) == originalProjection,
                        "The exact accepted report stays linked and recoverable. An absent, non-file or malformed index cannot write the profile or create a singleton index.");
                    add("typed_projection_holds_ambiguous_index_identity_without_writes", ambiguousIndexHeld,
                        "A case-ambiguous existing hero row is preserved and cannot be silently replaced.");

                    bool HoldsProjection()
                    {
                        var result = ReportOwnedEditorNativeCommand(accepted);
                        return ReadString(result, "status", "") == "projection_conflict"
                            && ReadString(CommandRow(hero), "result_json", "") == acceptedBasis
                            && ReadString(ProfileRow(hero), "native_action_id", "") == origin.Command
                            && File.ReadAllBytes(indexPath).SequenceEqual(existingIndex);
                    }

                    bool wrongProfileHeld, malformedProjectionHeld, unreadableProjectionHeld;
                    File.Move(file, displacedFile);
                    try
                    {
                        WriteProfile(hero, "different_hero", campaign);
                        string wrongProfileHash = PersistenceService.FileSha256(file);
                        wrongProfileHeld = HoldsProjection() && PersistenceService.FileSha256(file) == wrongProfileHash;
                        File.WriteAllText(file, "{malformed");
                        malformedProjectionHeld = HoldsProjection() && File.ReadAllText(file) == "{malformed";
                        File.Delete(file);
                        Directory.CreateDirectory(file);
                        try { unreadableProjectionHeld = HoldsProjection() && Directory.Exists(file); }
                        finally { Directory.Delete(file); }
                    }
                    finally { File.Move(displacedFile, file, overwrite: true); }
                    add("typed_projection_preserves_valid_index_when_profile_is_misowned_or_unreadable",
                        wrongProfileHeld && malformedProjectionHeld && unreadableProjectionHeld
                        && PersistenceService.FileSha256(file) == originalProjection,
                        "Misowned, malformed and non-file profiles leave the valid index, accepted report and command link unchanged.");

                    ReignDatabase.ExecuteSql(writer, "ALTER TABLE character_editor_native_commands "
                        + "ADD CONSTRAINT typed_notable_terminal_fixture CHECK (status<>'completed');");
                    bool rejected = false;
                    try { ReportOwnedEditorNativeCommand(accepted); }
                    catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.CheckViolation
                        && ex.ConstraintName == "typed_notable_terminal_fixture") { rejected = true; }
                    finally
                    {
                        ReignDatabase.ExecuteSql(writer, "ALTER TABLE character_editor_native_commands DROP CONSTRAINT typed_notable_terminal_fixture;");
                    }
                    var interrupted = CommandRow(hero);
                    var retained = TryParseStrictJsonObject(ReadString(interrupted, "result_json", ""));
                    var oldReport = ReadDictionary(retained, "report");
                    using (var repaired = JsonDocument.Parse(File.ReadAllBytes(indexPath)))
                    {
                        bool preserved = repaired.RootElement.GetArrayLength() == 2
                            && repaired.RootElement[0].GetRawText() == Json.Serialize(neighbor)
                            && repaired.RootElement[1].GetProperty("heroStringId").GetString() == hero
                            && repaired.RootElement[1].GetProperty("name").GetString() == "Typed fixture"
                            && repaired.RootElement[1].GetProperty("customTargetData").GetString() == "preserve";
                        add("typed_projection_repairs_in_place_and_preserves_other_rows", preserved,
                            "Repair updates the targeted name in place and preserves the other entry, nested values and the target's unrelated metadata.");
                    }
                    add("typed_terminal_failure_rolls_back_link_and_keeps_accepted_projection_retryable",
                        rejected && ReadString(ProfileRow(hero), "native_action_id", "") == origin.Command
                        && ReadString(interrupted, "status", "") == "reconciling"
                        && CanonicalJson(oldReport) == CanonicalJson(accepted)
                        && CanonicalJson(ReadDictionary(CampaignFileService.ReadJsonObject(file), "traits")) == CanonicalJson(expected),
                        "A real PostgreSQL terminal failure rolls back the prior profile unlink, retaining the accepted report and already measured projection.");

                    // Simulate a restored older accepted transport in isolation. The saved outcome remains exact.
                    oldReport["sharedActivityGeneration"] = "previous_notable_fixture_generation";
                    if (!TransitionNativeEditorReport(writer, campaign, interrupted, "reconciling", Json.Serialize(retained), 0))
                        throw new InvalidOperationException("The stale retained notable report could not be established.");
                    string projectedSha = PersistenceService.FileSha256(file);
                    CampaignFileService.WriteJsonObject(indexPath, new[] { neighbor });
                    long projectionTimestamp = ReadLong(retained, "settledAt", 0);
                    bool repairedIndex = ProjectTypedAutomaticNotableTraits(origin, expected, ReadString(retained, "beforeTraitsHash", ""), projectionTimestamp);
                    using (var repaired = JsonDocument.Parse(File.ReadAllBytes(indexPath)))
                    {
                        repairedIndex &= repaired.RootElement.GetArrayLength() == 2
                            && repaired.RootElement[0].GetRawText() == Json.Serialize(neighbor)
                            && repaired.RootElement[1].GetProperty("heroStringId").GetString() == hero
                            && repaired.RootElement[1].GetProperty("name").GetString() == "Typed fixture";
                    }
                    add("typed_projection_retry_repairs_missing_index_without_rewriting_profile",
                        repairedIndex && PersistenceService.FileSha256(file) == projectedSha,
                        "A retry inserts the missing target row into the valid index without replacing unrelated entries or rewriting the accepted profile.");
                    string projectedIndexSha = PersistenceService.FileSha256(indexPath);
                    var recovery = Poll().Single();
                    var exact = Report(recovery);
                    string recoveryBytes = ReadString(CommandRow(hero), "result_json", "");
                    var changed = new Dictionary<string, object>(exact) { ["status"] = "failed", ["applied"] = Array.Empty<string>(),
                        ["errors"] = new[] { "Contradictory retained outcome." } };
                    bool denied = !ReadBool(ReportOwnedEditorNativeCommand(changed), "ok", false)
                        && ReadString(CommandRow(hero), "result_json", "") == recoveryBytes;
                    bool settled = ReadBool(ReportOwnedEditorNativeCommand(exact), "ok", false);
                    string terminal = ReadString(CommandRow(hero), "result_json", "");
                    bool oldReplay = ReadBool(ReportOwnedEditorNativeCommand(oldReport), "ok", false);
                    add("typed_accepted_recovery_matches_saved_outcome_and_settles_atomically_once",
                        denied && settled && oldReplay && ReadBool(recovery, ManualEditorReceiptOnlyField, false)
                        && ReadString(CommandRow(hero), "status", "") == "completed"
                        && ReadString(ProfileRow(hero), "native_sync_status", "") == "synchronized"
                        && ReadString(ProfileRow(hero), "native_action_id", "") == ""
                        && ReadString(CommandRow(hero), "result_json", "") == terminal && PersistenceService.FileSha256(file) == projectedSha
                        && PersistenceService.FileSha256(indexPath) == projectedIndexSha,
                        "An exact saved result rebinds an accepted old attempt and clears the link with terminal status, without another projection or stale replay write.");
                    return TestDict("ok", true);
                });
            }
            finally
            {
                try { ReignPostgreSqlStorage.DropCampaign(campaign); }
                finally
                {
                    PersistenceService.TryDeleteDirectory(campaignRoot);
                    PersistenceService.TryDeleteDirectory(saveSyncRoot);
                }
            }
        }
#endif
    }
}
