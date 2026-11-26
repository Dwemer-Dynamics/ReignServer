using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class PersistenceService
    {
        private const string EditorNativeOwnerLedgerKey = "editorNativeOwner";
        private const string EditorNativePointLineageKey = "editorNativeLineage";

        internal sealed record RegisteredEditorNativeOwner(
            string CampaignId, string TimelineId, string LoadPointId, string LoadSessionId,
            string RestoreGeneration, string LineageId);

        // Consumers hold the campaign gate across this read and admission.
        // The timeline scope inside the proof keeps the SQL owner consistent.
        internal static RegisteredEditorNativeOwner ReadRegisteredEditorNativeOwner(string campaign)
        {
            if (!(Program.CampaignDataGate.IsReadLockHeld || Program.CampaignDataGate.IsWriteLockHeld)
                || !Regex.IsMatch(campaign ?? "", @"\A[A-Za-z0-9_-]{1,180}\z") || !IsSaveSyncEnabled())
                return null;
            var active = ReadDictionary(ReadSaveSyncLedger(campaign), EditorNativeOwnerLedgerKey);
            string timeline = ReadString(active, "timelineId", "");
            string point = ReadString(active, "loadPointId", "");
            string session = ReadString(active, "loadSessionId", "");
            string restore = ReadString(active, "restoreGeneration", "");
            string lineage = ReadString(active, "lineageId", "");
            if (ReadString(active, "kind", "") != "registered_load"
                || !IsEditorNativeNonce(session) || !IsEditorNativeNonce(lineage)
                || !TerminalActionReportProtocol.IsTrustedIdentifier(point)
                || !TerminalActionReportProtocol.IsTrustedIdentifier(timeline)
                || !TerminalActionReportProtocol.IsTrustedIdentifier(restore)
                || !EditorNativeOwnerStillVerified(campaign, timeline, point, session, restore))
                return null;
            return new RegisteredEditorNativeOwner(campaign, timeline, point, session, restore, lineage);
        }

        // Called under the Save Sync write gate before any possible no-replacement result.
        // An unverified continuation must not keep the preceding native session's authority.
        private static bool BeginEditorNativeOwnerLoad(Dictionary<string, object> ledger,
            Dictionary<string, object> request, string point, string session)
        {
            bool optedIn = request.TryGetValue("editorNativeOwnerVersion", out object version)
                && (version is 1 || version is 1L);
            // Missing/legacy session IDs are valid Save Sync inputs. They cannot
            // mint Editor authority and must not leave a prior native owner active.
            if (!optedIn || !IsEditorNativeNonce(session)) return RetireExistingEditorNativeOwner(ledger);

            var active = ReadDictionary(ledger, EditorNativeOwnerLedgerKey);
            if (ReadString(active, "loadSessionId", "") == session && ReadString(active, "loadPointId", "") == point)
                return false;

            if (active != null) ledger["editorNativePriorOwnerRetired"] = true;
            ledger[EditorNativeOwnerLedgerKey] = TestDict("kind", "loading", "loadSessionId", session, "loadPointId", point);
            // Write an early fence only to retire an existing owner. An absent or
            // unregistered campaign must not get a new ledger merely from a load probe.
            return active != null;
        }

        // Save Sync has already verified and bound this exact registered load proof.
        private static void CompleteEditorNativeOwnerLoad(Dictionary<string, object> ledger,
            Dictionary<string, object> point, string session, string restore)
        {
            string pointId = ReadString(point, "savePointId", "");
            string timeline = ReadString(point, "timelineId", "");
            var active = ReadDictionary(ledger, EditorNativeOwnerLedgerKey);
            if (ReadString(active, "loadSessionId", "") != session || !IsEditorNativeNonce(session)
                || ReadString(active, "loadPointId", "") != pointId || timeline.Length == 0
                || ReadString(point, "status", "") != "ready" || ReadString(point, "savePointKind", "") != "native_save"
                || !TerminalActionReportProtocol.IsTrustedIdentifier(restore))
                return;

            string lineage = ReadString(active, "lineageId", "");
            if (!IsEditorNativeNonce(lineage))
            {
                var saved = ReadDictionary(point, EditorNativePointLineageKey);
                lineage = ReadString(saved, "lineageId", "");
                if (!IsEditorNativeNonce(lineage) || ReadString(saved, "timelineId", "") != timeline
                    || ReadString(saved, "source", "") != "verified_registered_load")
                    lineage = Guid.NewGuid().ToString("N");
            }

            ledger[EditorNativeOwnerLedgerKey] = TestDict("kind", "registered_load", "loadSessionId", session, "loadPointId", pointId,
                "restoreGeneration", restore, "timelineId", timeline, "lineageId", lineage);
        }

        private static bool EditorNativeOwnerStillVerified(string campaign, string timeline,
            string point, string session, string restore)
        {
            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
            try
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                    trackCampaignMutations: false, initializeInfrastructure: false);
                return connection != null && WorldSimulationService.NoticeAcknowledgementOwnerMatches(connection, null,
                    TestDict("campaignId", campaign, "timelineId", timeline, "sharedActivityGeneration", restore),
                    campaign, timeline)
                    && DialogueService.TerminalReportLoadProofError(connection, restore,
                        TestDict("loadSessionId", session, "savePointId", point, "restoreGeneration", restore), out _).Length == 0;
            }
            catch (System.Data.Common.DbException)
            {
                // The optional provenance read cannot make an ordinary registered save fail.
                return false;
            }
        }

        // Stamp both ordinary and shared-content registrations from the live native load,
        // never from inherited SQL rows, active point names or prior checkpoint metadata.
        private static void CaptureEditorNativePointLineage(Dictionary<string, object> ledger,
            Dictionary<string, object> point, Dictionary<string, object> request,
            Func<string, string, string, bool> stillVerified)
        {
            if (!request.TryGetValue("editorNativeOwnerSessionId", out object requested)
                || requested is not string session || !IsEditorNativeNonce(session))
                return;
            var active = ReadDictionary(ledger, EditorNativeOwnerLedgerKey);
            string timeline = ReadString(point, "timelineId", "");
            string lineage = ReadString(active, "lineageId", "");
            if (ReadString(active, "kind", "") == "registered_load"
                && ReadString(active, "loadSessionId", "") == session
                && ReadString(active, "timelineId", "") == timeline && timeline.Length > 0
                && ReadString(point, "savePointKind", "") == "native_save"
                && IsEditorNativeNonce(lineage)
                && stillVerified(ReadString(active, "loadPointId", ""), session, ReadString(active, "restoreGeneration", "")))
                point[EditorNativePointLineageKey] = TestDict("timelineId", timeline, "lineageId", lineage,
                    "source", "verified_registered_load");
        }

        private static bool RetireExistingEditorNativeOwner(Dictionary<string, object> ledger)
        {
            if (!ledger.Remove(EditorNativeOwnerLedgerKey, out object previous)) return false;
            var active = previous as Dictionary<string, object>;
            if (!IsEditorNativeNonce(ReadString(active, "lineageId", ""))
                && !ReadBool(ledger, "editorNativePriorOwnerRetired", false))
                return false; // An in-memory attempt without an earlier owner needs no ledger.
            ledger["editorNativePriorOwnerRetired"] = true;
            return true;
        }

        private static bool IsEditorNativeNonce(string id)
            => Guid.TryParseExact(id, "N", out Guid value) && value.ToString("N") == id;
    }
}
