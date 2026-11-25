using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private const string ManualEditorSessionField = "editorNativeLoadSessionId";
        private const string ManualEditorLineageField = "manualOriginLineageId";
        private const string ManualEditorMaterialField = "manualOriginMaterialSha256";
        private const string ManualEditorReceiptOnlyField = "nativeReceiptOnly";

        private static string ExactNativeEditorField(Dictionary<string, object> fields, string key) =>
            fields?.Keys.Count(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) == 1
            && fields.Keys.Contains(key, StringComparer.Ordinal) && fields.TryGetValue(key, out object raw)
            && raw is string value ? value : "";

        private static bool ExactNativeEditorFlag(Dictionary<string, object> fields, string key, out bool value)
        {
            value = false;
            if (fields?.Keys.Count(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) != 1
                || !fields.Keys.Contains(key, StringComparer.Ordinal) || !fields.TryGetValue(key, out object raw)
                || raw is not bool enabled) return false;
            value = enabled;
            return true;
        }

        private static bool EditorNativeWireNonce(string value) =>
            Guid.TryParseExact(value, "N", out Guid parsed) && parsed.ToString("N") == value;

        private static bool HasEditorNativeCampaignGate =>
            Program.CampaignDataGate.IsReadLockHeld || Program.CampaignDataGate.IsWriteLockHeld;

        private static bool IsExactEditorNativeCampaign(string campaign) =>
            Regex.IsMatch(campaign, @"\A[A-Za-z0-9_-]{1,180}\z");

        private static bool MatchesManualEditorTransport(Dictionary<string, object> wire,
            PersistenceService.RegisteredEditorNativeOwner owner) =>
            owner != null && ExactNativeEditorField(wire, "campaignId") == owner.CampaignId
            && ExactNativeEditorField(wire, "timelineId") == owner.TimelineId
            && ExactNativeEditorField(wire, "sharedActivityGeneration") == owner.RestoreGeneration
            && ExactNativeEditorField(wire, ManualEditorSessionField) == owner.LoadSessionId;

        private static bool MatchesManualEditorCommandIdentity(Dictionary<string, object> wire, Dictionary<string, object> row) =>
            ExactNativeEditorField(wire, "commandId") == ReadString(row, "command_id", "")
            && ExactNativeEditorField(wire, ManualEditorLineageField) == ReadString(row, "manual_origin_lineage_id", "")
            && ExactNativeEditorField(wire, ManualEditorMaterialField) == EditorNativeOriginMaterialHash(row)
            && EditorNativeWireNonce(ExactNativeEditorField(wire, "claimId"))
            && EditorNativeWireNonce(ExactNativeEditorField(wire, ManualEditorSessionField))
            && ExactNativeEditorFlag(wire, ManualEditorReceiptOnlyField, out _);

        private static bool MatchesHistoricalManualEditorIdentity(Dictionary<string, object> wire, Dictionary<string, object> row)
        {
            var retainedScope = new PersistenceService.RegisteredEditorNativeOwner(
                ExactNativeEditorField(wire, "campaignId"), ExactNativeEditorField(wire, "timelineId"), "",
                ExactNativeEditorField(wire, ManualEditorSessionField), ExactNativeEditorField(wire, "sharedActivityGeneration"),
                ExactNativeEditorField(wire, ManualEditorLineageField));
            return IsExactEditorNativeCampaign(retainedScope.CampaignId)
                && TerminalActionReportProtocol.IsTrustedIdentifier(retainedScope.TimelineId)
                && TerminalActionReportProtocol.IsTrustedIdentifier(retainedScope.RestoreGeneration)
                && EditorNativeWireNonce(retainedScope.LineageId)
                && MatchesEditorNativeCommandOrigin(row, retainedScope) && MatchesManualEditorCommandIdentity(wire, row);
        }

        private static Dictionary<string, object> NewManualEditorClaim(Dictionary<string, object> row,
            PersistenceService.RegisteredEditorNativeOwner owner, bool receiptOnly) =>
            TestDict("commandId", ReadString(row, "command_id", ""), "claimId", Guid.NewGuid().ToString("N"),
                "campaignId", owner.CampaignId, "timelineId", owner.TimelineId,
                "sharedActivityGeneration", owner.RestoreGeneration, ManualEditorSessionField, owner.LoadSessionId,
                ManualEditorLineageField, owner.LineageId, ManualEditorMaterialField, EditorNativeOriginMaterialHash(row),
                ManualEditorReceiptOnlyField, receiptOnly);

        private static bool TryReadManualEditorResult(Dictionary<string, object> payload, out string status)
        {
            status = ExactNativeEditorField(payload, "status");
            bool StringArray(string key, out int count)
            {
                count = 0;
                if (payload?.Keys.Count(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) != 1
                    || !payload.Keys.Contains(key, StringComparer.Ordinal) || !payload.TryGetValue(key, out object raw)
                    || raw is string || raw is IDictionary || raw is not IEnumerable entries)
                    return false;
                foreach (object item in entries)
                {
                    if (item is not string) return false;
                    count++;
                }
                return true;
            }
            return StringArray("applied", out int applied) && StringArray("errors", out int errors)
                && status == (errors == 0 ? "completed" : applied == 0 ? "failed" : "partially_applied");
        }

        private static bool ManualEditorOutcomesMatch(Dictionary<string, object> accepted, Dictionary<string, object> actual) =>
            TryReadManualEditorResult(accepted, out string original) && TryReadManualEditorResult(actual, out string reported)
            && original == reported && CanonicalJson(accepted["applied"]) == CanonicalJson(actual["applied"])
            && CanonicalJson(accepted["errors"]) == CanonicalJson(actual["errors"]);

        private static bool HasCoherentManualEditorReconciliation(Dictionary<string, object> journal, Dictionary<string, object> row)
        {
            var report = ReadDictionary(journal, "report");
            var fields = ReadDictionary(journal, "fields");
            var basis = ReadDictionary(journal, "basis");
            if (!MatchesHistoricalManualEditorIdentity(report, row) || !TryReadManualEditorResult(report, out _)
                || fields == null || basis == null || fields.Count != basis.Count || ReadLong(journal, "settledAt", 0) <= 0
                || !ExactNativeEditorFlag(journal, "notableTraits", out bool notable) || notable)
                return false;
            var applied = ReadStringList(report, "applied");
            var changes = TryParseStrictJsonObject(ReadString(row, "changes_json", ""));
            return changes != null && fields.All(field =>
                applied.Contains(field.Key, StringComparer.Ordinal) && !EditorProfileManagedFields.Contains(field.Key)
                && !field.Key.Equals("wealth", StringComparison.OrdinalIgnoreCase)
                && changes.TryGetValue(field.Key, out object requested) && CanonicalJson(requested) == CanonicalJson(field.Value)
                && basis.TryGetValue(field.Key, out object expected)
                && (expected == null || expected is string digest && digest.Length == 64 && digest.All(Uri.IsHexDigit)));
        }

        private static bool ManualEditorClaimMatchesReport(Dictionary<string, object> claim, Dictionary<string, object> report) =>
            ExactNativeEditorField(claim, "claimId") == ExactNativeEditorField(report, "claimId")
            && ExactNativeEditorFlag(claim, ManualEditorReceiptOnlyField, out bool receiptOnly)
            && ExactNativeEditorFlag(report, ManualEditorReceiptOnlyField, out bool reportedReceiptOnly)
            && receiptOnly == reportedReceiptOnly;

        private static Dictionary<string, object> PollOwnedEditorNativeCommands(Dictionary<string, object> payload)
        {
            string campaign = ExactNativeEditorField(payload, "campaignId");
            if (!HasEditorNativeCampaignGate || !IsExactEditorNativeCampaign(campaign))
                return NativeEditorReportResult("", "ownership_mismatch", false);
            using var timeline = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
            lock (NarrativeStateLock)
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                    trackCampaignMutations: true, initializeInfrastructure: false);
                if (connection == null) return NativeEditorReportResult("", "ownership_mismatch", false);
                if (!PersistenceService.IsNativeEditorOwnershipSchemaReady(connection))
                    return NativeEditorReportResult("", "schema_not_ready", false);
                var scope = ReadAutomaticNotableScope(connection, campaign, payload);
                bool asksForManual = payload.Keys.Any(key => key.Equals(ManualEditorSessionField, StringComparison.OrdinalIgnoreCase));
                var owner = asksForManual ? PersistenceService.ReadRegisteredEditorNativeOwner(campaign) : null;
                if (scope == null || asksForManual && !MatchesManualEditorTransport(payload, owner))
                    return NativeEditorReportResult("", "ownership_mismatch", false);

                var commands = new List<Dictionary<string, object>>();
                var heroes = new HashSet<string>(StringComparer.Ordinal);
                if (asksForManual)
                {
                    var due = CharactersStore.ReadScopedManualEditorNativeQueue(connection,
                        TestDict("timeline", owner.TimelineId, "lineage", owner.LineageId))
                        .Where(row => MatchesEditorNativeCommandOrigin(row, owner))
                        .GroupBy(row => ReadString(row, "hero_id", ""), StringComparer.Ordinal)
                        // A retained reconciliation owns this hero before a later native effect.
                        .Select(group => group.FirstOrDefault(row => ReadString(row, "status", "") == "reconciling") ?? group.First());
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    foreach (var row in due)
                    {
                        heroes.Add(ReadString(row, "hero_id", ""));
                        var command = TryIssueManualEditorClaim(connection, row, owner, now);
                        if (command != null) commands.Add(command);
                        if (commands.Count == 25) break;
                    }
                }
                AppendAutomaticNotableClaims(connection, scope, commands, heroes);
                return TestDict("ok", true, "commands", commands);
            }
        }

        private static Dictionary<string, object> TryIssueManualEditorClaim(ReignDbConnection connection,
            Dictionary<string, object> row, PersistenceService.RegisteredEditorNativeOwner owner, long now)
        {
            string status = ReadString(row, "status", "");
            string priorJson = ReadString(row, "result_json", "");
            long priorTimestamp = ReadLong(row, "claimed_ts", 0);
            var changes = TryParseStrictJsonObject(ReadString(row, "changes_json", ""));
            if (changes is not { Count: > 0 }) return null;
            var prior = TryParseStrictJsonObject(priorJson);
            bool reconciling = status == "reconciling";
            if (reconciling)
            {
                var accepted = ReadDictionary(prior, "report");
                if (!HasCoherentManualEditorReconciliation(prior, row)) return null;
                var reclaim = ReadDictionary(prior, "manualReclaim");
                if (reclaim == null && MatchesManualEditorTransport(accepted, owner))
                {
                    FinishNativeEditorReport(connection, owner.CampaignId, row, manualOrigin: true);
                    return null;
                }
                if (MatchesManualEditorTransport(reclaim, owner) && priorTimestamp >= now - 300) return null;
            }
            else if (status == "claimed")
            {
                if (!MatchesHistoricalManualEditorIdentity(prior, row) || priorTimestamp >= now - 300) return null;
            }
            else if (status != "pending" || prior is not { Count: 0 } || priorTimestamp != 0
                || ReadLong(row, "resolved_ts", 0) != 0) return null;

            // Only a first claim from pending authorizes a native effect. Every reissue reads a saved receipt only.
            var claim = NewManualEditorClaim(row, owner, status != "pending");
            if (reconciling) prior["manualReclaim"] = claim;
            if (!CharactersStore.PublishScopedManualEditorNativeClaim(connection, TestDict(
                "id", ReadString(row, "command_id", ""), "previous", status, "previousTs", priorTimestamp,
                "status", reconciling ? "reconciling" : "claimed", "basis", priorJson, "ts", now,
                "result", Json.Serialize(reconciling ? prior : claim), "timeline", owner.TimelineId, "lineage", owner.LineageId)))
                return null;

            PersistenceService.MarkSaveSyncActiveStateDirty(owner.CampaignId);
            return TestDict("command_id", ReadString(row, "command_id", ""),
                "claimId", ExactNativeEditorField(claim, "claimId"), "hero_id", ReadString(row, "hero_id", ""),
                "dangerous", ReadInt(row, "dangerous", 0), "confirmation", ReadString(row, "confirmation", ""),
                "changes", changes, ManualEditorLineageField, owner.LineageId,
                ManualEditorMaterialField, ExactNativeEditorField(claim, ManualEditorMaterialField),
                ManualEditorSessionField, owner.LoadSessionId, ManualEditorReceiptOnlyField, status != "pending");
        }

        private static Dictionary<string, object> ReportOwnedEditorNativeCommand(Dictionary<string, object> payload)
        {
            string campaign = ExactNativeEditorField(payload, "campaignId");
            string id = ExactNativeEditorField(payload, "commandId");
            if (!HasEditorNativeCampaignGate || !IsExactEditorNativeCampaign(campaign) || id.Length == 0
                || !TryReadManualEditorResult(payload, out string reported))
                return NativeEditorReportResult(id, "invalid_report", false);

            using var timeline = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
            lock (NarrativeStateLock)
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                    trackCampaignMutations: true, initializeInfrastructure: false);
                if (connection == null) return NativeEditorReportResult(id, "ownership_mismatch", false);
                if (!PersistenceService.IsNativeEditorOwnershipSchemaReady(connection))
                    return NativeEditorReportResult(id, "schema_not_ready", false);
                var row = CharactersStore.SelectCharacterEditorNativeCommands5(connection, TestDict("id", id)).SingleOrDefault();
                if (row == null) return NativeEditorReportResult(id, "unknown_command", false);
                if (ReadString(row, "automatic_origin_json", "").Length > 0 || payload.Keys.Any(key =>
                    key.Equals(AutomaticNotableOriginField, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(AutomaticNotableMaterialField, StringComparison.OrdinalIgnoreCase)))
                    return ReportTypedAutomaticNotable(connection, campaign, row, payload);
                string prior = ReadString(row, "status", "");
                var journal = TryParseStrictJsonObject(ReadString(row, "result_json", ""));
                if (prior is "completed" or "partially_applied" or "failed" or "projection_conflict" or "superseded")
                    return ReadHistoricalManualEditorResult(payload, row, journal, prior);

                var owner = PersistenceService.ReadRegisteredEditorNativeOwner(campaign);
                if (!MatchesManualEditorTransport(payload, owner) || !MatchesEditorNativeCommandOrigin(row, owner)
                    || !MatchesManualEditorCommandIdentity(payload, row))
                    return NativeEditorReportResult(id, "ownership_mismatch", false);
                if (prior == "claimed")
                {
                    if (!MatchesManualEditorTransport(journal, owner) || !MatchesManualEditorCommandIdentity(journal, row)
                        || !ManualEditorClaimMatchesReport(journal, payload))
                        return NativeEditorReportResult(id, "claim_mismatch", false);
                    return AdmitEditorNativeReport(connection, campaign, row, id, reported, payload, manualOrigin: true);
                }
                if (prior != "reconciling") return NativeEditorReportResult(id, "claim_mismatch", false);

                return ReportManualEditorReconciliation(connection, owner, row, journal, payload);
            }
        }

        private static Dictionary<string, object> ReportManualEditorReconciliation(ReignDbConnection connection,
            PersistenceService.RegisteredEditorNativeOwner owner, Dictionary<string, object> row,
            Dictionary<string, object> journal, Dictionary<string, object> payload)
        {
            string id = ReadString(row, "command_id", "");
            var accepted = ReadDictionary(journal, "report");
            if (!HasCoherentManualEditorReconciliation(journal, row)) return NativeEditorReportResult(id, "report_conflict", false);
            var reclaim = ReadDictionary(journal, "manualReclaim");
            if (reclaim == null && MatchesManualEditorTransport(accepted, owner) && CanonicalJson(accepted) == CanonicalJson(payload))
                return FinishNativeEditorReport(connection, owner.CampaignId, row, manualOrigin: true);

            if (!MatchesManualEditorTransport(reclaim, owner) || !MatchesManualEditorCommandIdentity(reclaim, row)
                || !ManualEditorClaimMatchesReport(reclaim, payload) || !ExactNativeEditorFlag(payload, ManualEditorReceiptOnlyField, out bool receiptOnly)
                || !receiptOnly || !ManualEditorOutcomesMatch(accepted, payload))
                return NativeEditorReportResult(id, "report_conflict", false);

            // Keep the original accepted report and projection basis. Only transfer the accepted native receipt's transport.
            if (!journal.ContainsKey("originalManualReport")) journal["originalManualReport"] = accepted;
            journal["report"] = payload;
            journal.Remove("manualReclaim");
            string rebound = Json.Serialize(journal);
            if (!TransitionNativeEditorReport(connection, owner.CampaignId, row, "reconciling", rebound, 0))
                return NativeEditorReportResult(id, "claim_mismatch", false);
            row["result_json"] = rebound;
            return FinishNativeEditorReport(connection, owner.CampaignId, row, manualOrigin: true);
        }

        private static Dictionary<string, object> ReadHistoricalManualEditorResult(Dictionary<string, object> payload,
            Dictionary<string, object> row, Dictionary<string, object> journal, string status)
        {
            string id = ReadString(row, "command_id", "");
            // Only a retained exact report can answer through this read-only path.
            if (!MatchesHistoricalManualEditorIdentity(payload, row))
                return NativeEditorReportResult(id, "ownership_mismatch", false);
            string supplied = CanonicalJson(payload);
            return supplied == CanonicalJson(ReadDictionary(journal, "report"))
                || supplied == CanonicalJson(ReadDictionary(journal, "originalManualReport"))
                ? NativeEditorReportResult(id, status, true) : NativeEditorReportResult(id, "report_conflict", false);
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static void VerifyScopedManualEditorNativeDelivery(Action<string, bool, string> add, string campaign, string ledgerPath)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation || !CampaignFileService.IsInternalCampaignId(campaign))
                throw new InvalidOperationException("The scoped native Editor fixture needs an isolated validation campaign.");
            RunInEditorNativeBrowserWriteScope(campaign, () =>
            {
                var owner = PersistenceService.ReadRegisteredEditorNativeOwner(campaign);
                if (owner == null) throw new InvalidOperationException("The fixture requires its real registered Save Sync owner.");
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                    trackCampaignMutations: true, initializeInfrastructure: false);
                if (connection == null) throw new InvalidOperationException("The disposable campaign connection is unavailable.");

                CharactersSchema.EnsureAutomaticNotableOriginColumns(connection);
                string ledgerSha = PersistenceService.FileSha256(ledgerPath);
                const string originalId = EditorOriginFixtureRevision + "_command";
                const string rollbackId = "editor_origin_fixture_rollback_command";
                const string legacyId = "editor_origin_fixture_legacy_command";
                var scope = TestDict("campaignId", campaign, "timelineId", owner.TimelineId,
                    "sharedActivityGeneration", owner.RestoreGeneration, ManualEditorSessionField, owner.LoadSessionId);
                Dictionary<string, object> Row(string id) =>
                    CharactersStore.SelectCharacterEditorNativeCommands5(connection, TestDict("id", id)).Single();
                Dictionary<string, object> Claim(string id) =>
                    ReadDictionaryList(PollOwnedEditorNativeCommands(scope), "commands")
                        .Single(row => ReadString(row, "command_id", "") == id);
                Dictionary<string, object> Report(Dictionary<string, object> claim, string error)
                {
                    return TestDict("campaignId", campaign, "timelineId", owner.TimelineId, "sharedActivityGeneration", owner.RestoreGeneration,
                        ManualEditorSessionField, ExactNativeEditorField(claim, ManualEditorSessionField), "commandId", ReadString(claim, "command_id", ""),
                        "claimId", ExactNativeEditorField(claim, "claimId"), ManualEditorLineageField, ExactNativeEditorField(claim, ManualEditorLineageField),
                        ManualEditorMaterialField, ExactNativeEditorField(claim, ManualEditorMaterialField),
                        ManualEditorReceiptOnlyField, ReadBool(claim, ManualEditorReceiptOnlyField, false),
                        "status", "failed", "applied", Array.Empty<string>(), "errors", new[] { error });
                }

                string previous = ReadString(Row(originalId), "result_json", "");
                var wrongScope = new Dictionary<string, object>(scope) { [ManualEditorSessionField] = new string('d', 32) };
                var refused = PollOwnedEditorNativeCommands(wrongScope);
                bool untouchedOnWrongSession = !ReadBool(refused, "ok", false)
                    && ReadString(Row(originalId), "result_json", "") == previous;
                var first = Claim(originalId);
                add("native_editor_current_registered_claim_ignores_legacy_earlier_rows",
                    untouchedOnWrongSession && !ReadBool(first, ManualEditorReceiptOnlyField, true)
                    && ReadString(Row(legacyId), "status", "") == "pending" && ReadString(Row(originalId), "status", "") == "claimed",
                    "The real registered owner can claim an owned pending command. Earlier unknown work and the wrong session receive no claim.");

                ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_native_commands SET claimed_ts=1 WHERE command_id=$id;",
                    TestDict("id", originalId));
                var expiredClaim = Claim(originalId);
                var retained = Report(expiredClaim, "The fixture retains this previously measured failure.");
                string lastClaim = ReadString(Row(originalId), "result_json", "");
                var forgedMode = new Dictionary<string, object>(retained) { [ManualEditorReceiptOnlyField] = false };
                bool refusedMode = !ReadBool(ReportOwnedEditorNativeCommand(forgedMode), "ok", false)
                    && ReadString(Row(originalId), "result_json", "") == lastClaim;
                var wrongReporter = new Dictionary<string, object>(retained) { [ManualEditorSessionField] = new string('c', 32) };
                bool refusedStaleReporter = !ReadBool(ReportOwnedEditorNativeCommand(wrongReporter), "ok", false)
                    && ReadString(Row(originalId), "result_json", "") == lastClaim;
                bool acceptedOnce = ReadBool(ReportOwnedEditorNativeCommand(retained), "ok", false);
                add("native_editor_stale_claim_reissues_only_saved_receipt_authority",
                    acceptedOnce && refusedMode && refusedStaleReporter && ReadBool(expiredClaim, ManualEditorReceiptOnlyField, false)
                    && ReadString(first, "claimId", "") != ReadString(expiredClaim, "claimId", "")
                    && ReadString(Row(originalId), "status", "") == "failed",
                    "A stale claim receives different receipt-only authority. It cannot use the first-effect report mode.");

                var next = Claim(rollbackId);
                // This is a preserved accepted-report fixture, not a native game process.
                var oldAccepted = Report(next, "The exact accepted outcome must survive rebind.");
                oldAccepted[ManualEditorSessionField] = new string('e', 32);
                oldAccepted["sharedActivityGeneration"] = "older_retained_fixture_generation";
                var before = Row(rollbackId);
                var acceptedJournal = TestDict("report", oldAccepted, "fields", TestDict(), "basis", TestDict(),
                    "settledAt", 1L, "notableTraits", false);
                if (!TransitionNativeEditorReport(connection, campaign, before, "reconciling", Json.Serialize(acceptedJournal), 0))
                    throw new InvalidOperationException("Could not establish the isolated older accepted report.");

                var recovered = Claim(rollbackId);
                string recoveryJournal = ReadString(Row(rollbackId), "result_json", "");
                var wrongOutcome = Report(recovered, "Different result");
                bool refusedResult = !ReadBool(ReportOwnedEditorNativeCommand(wrongOutcome), "ok", false)
                    && ReadString(Row(rollbackId), "result_json", "") == recoveryJournal;
                var exactOutcome = Report(recovered, "The exact accepted outcome must survive rebind.");
                bool settled = ReadBool(ReportOwnedEditorNativeCommand(exactOutcome), "ok", false);
                var terminal = Row(rollbackId);
                var persisted = TryParseStrictJsonObject(ReadString(terminal, "result_json", ""));
                add("native_editor_reconciling_reclaims_exact_outcome_without_discarding_accepted_report",
                    refusedResult && settled && ReadBool(recovered, ManualEditorReceiptOnlyField, false)
                    && ReadString(terminal, "status", "") == "failed"
                    && CanonicalJson(ReadDictionary(persisted, "originalManualReport")) == CanonicalJson(oldAccepted)
                    && CanonicalJson(ReadDictionary(persisted, "report")) == CanonicalJson(exactOutcome),
                    "The actual server reclaim path preserves the old accepted report and binds transport only after an exact saved-outcome match.");

                string terminalBytes = ReadString(terminal, "result_json", "");
                long terminalTs = ReadLong(terminal, "resolved_ts", -1);
                bool staleReplay = ReadBool(ReportOwnedEditorNativeCommand(oldAccepted), "ok", false)
                    && ReadBool(ReportOwnedEditorNativeCommand(exactOutcome), "ok", false);
                var changedOld = new Dictionary<string, object>(oldAccepted) { ["errors"] = new[] { "Different stale outcome" } };
                var after = Row(rollbackId);
                add("native_editor_terminal_stale_replay_has_no_state_effect",
                    staleReplay && !ReadBool(ReportOwnedEditorNativeCommand(changedOld), "ok", false)
                    && ReadString(after, "result_json", "") == terminalBytes && ReadLong(after, "resolved_ts", -2) == terminalTs
                    && PersistenceService.FileSha256(ledgerPath) == ledgerSha,
                    "An exact retained old or current terminal receipt is readable without writing queue or Save Sync ledger state.");
                return TestDict("ok", true);
            });
        }
#endif
    }
}
