using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private sealed record VerifiedForkRequest(string Campaign, string Timeline, long Sequence, string Head,
            string SavePoint, string LoadSession, string RestoreGeneration, Dictionary<string, object> LoadProof);

        private static VerifiedForkRequest ReadVerifiedForkRequest(Dictionary<string, object> payload)
        {
            if (!payload.TryGetValue("verifiedForkLoad", out var rawProof)
                || rawProof is not Dictionary<string, object> proof || proof.Count != 4
                || !proof.TryGetValue("schemaVersion", out var version) || version is not int number || number != 1
                || !payload.TryGetValue("savedSequence", out var sequenceValue))
                return null;
            long sequence = sequenceValue is int narrow ? narrow : sequenceValue is long wide ? wide : -1;
            string campaign = VerifiedForkString(payload, "campaignId"), timeline = VerifiedForkString(payload, "timelineId");
            string head = VerifiedForkString(payload, "savedHeadEventId");
            string point = VerifiedForkString(proof, "savePointId"), session = VerifiedForkString(proof, "loadSessionId");
            string generation = VerifiedForkString(proof, "restoreGeneration");
            return sequence > 0 && new[] { campaign, timeline, head, point, session, generation }
                .All(TerminalActionReportProtocol.IsTrustedIdentifier)
                ? new VerifiedForkRequest(campaign, timeline, sequence, head, point, session, generation, proof) : null;
        }

        private static string VerifiedForkString(Dictionary<string, object> values, string name) =>
            values?.TryGetValue(name, out var value) == true && value is string text ? text : string.Empty;

        private static Dictionary<string, object> UnprovenVerifiedFork() =>
            TestDict("ok", false, "error", "verified_fork_unproven");

        private static Dictionary<string, object> WorldHistoryOpenVerifiedFork(Dictionary<string, object> payload)
        {
            var request = ReadVerifiedForkRequest(payload);
            if (request == null) return UnprovenVerifiedFork();

            // The HTTP route already has the campaign read gate. A direct caller
            // uses the same ordering so Save Sync cannot replace the proof mid-open.
            bool takeReadGate = !Program.CampaignDataGate.IsReadLockHeld && !Program.CampaignDataGate.IsWriteLockHeld;
            if (takeReadGate) Program.CampaignDataGate.EnterReadLock();
            try
            {
                var opened = OpenVerifiedForkUnderCampaignGate(request);
                if (ReadBool(opened, "ok", false) && ReadBool(opened, "branched", false))
                    ReputationService.ResumePendingSocialWorldHistoryOutcomes(request.Campaign, ReadString(opened, "timelineId", ""));
                return opened;
            }
            finally
            {
                if (takeReadGate) Program.CampaignDataGate.ExitReadLock();
            }
        }

        private static Dictionary<string, object> OpenVerifiedForkUnderCampaignGate(VerifiedForkRequest request)
        {
            using var timelineScope = EnterWorldHistoryTimelineScope(request.Campaign);
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(request.Campaign,
                trackCampaignMutations: true, initializeInfrastructure: false);
            if (connection == null || !TableExists(connection, "world_history_timelines")
                || !TableExists(connection, "world_history_events") || !TableExists(connection, "memory_precision_state"))
                return UnprovenVerifiedFork();

            string currentGeneration = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
            if (DialogueService.TerminalReportLoadProofError(connection, currentGeneration, request.LoadProof,
                out var context).Length != 0) return UnprovenVerifiedFork();

            var ledger = PersistenceService.ReadSaveSyncLedger(request.Campaign);
            var loadReceipt = ReadDictionary(ledger, "lastLoadReceipt");
            var loadResult = ReadDictionary(loadReceipt, "response");
            if (VerifiedForkString(loadReceipt, "savePointId") != request.SavePoint
                || VerifiedForkString(loadReceipt, "loadSessionId") != request.LoadSession
                || !ReadBool(loadResult, "ok", false)
                || VerifiedForkString(loadResult, "result") is not ("no_op" or "rolled_back")
                || VerifiedForkString(loadResult, "campaignId") != request.Campaign
                || VerifiedForkString(loadResult, "savePointId") != request.SavePoint
                || VerifiedForkString(loadResult, "loadSessionId") != request.LoadSession
                || VerifiedForkString(loadResult, "restoreGeneration") != request.RestoreGeneration)
                return UnprovenVerifiedFork();

            var point = PersistenceService.SaveSyncPoint(ledger, request.SavePoint);
            double savedDay = ReadDouble(point, "campaignTimeDays", double.NaN);
            if (!double.IsFinite(savedDay) || savedDay < 0 || ReadDouble(context, "world_day", double.NaN) != savedDay
                || VerifiedForkString(point, "campaignId") != request.Campaign || VerifiedForkString(point, "savePointId") != request.SavePoint
                || VerifiedForkString(point, "savePointKind") != "native_save" || VerifiedForkString(point, "status") != "ready"
                || string.IsNullOrEmpty(VerifiedForkString(point, "saveFinalizedUtc"))
                || string.IsNullOrEmpty(VerifiedForkString(point, "snapshotFingerprint"))
                || VerifiedForkString(point, "timelineId") != request.Timeline
                || ReadLong(point, "worldHistorySequence", -1) != request.Sequence
                || VerifiedForkString(point, "worldHistoryHeadEventId") != request.Head
                || ReadLong(context, "head_sequence", -1) != request.Sequence
                || VerifiedForkString(context, "head_event_id") != request.Head
                || !VerifiedForkSnapshotMatches(connection, request, point))
                return UnprovenVerifiedFork();

            var source = WorldSimulationStore.SelectWorldHistoryTimelines(connection, TestDict("id", request.Timeline)).SingleOrDefault();
            long sourceHead = ReadLong(source, "head_sequence", -1);
            if (VerifiedForkString(source, "campaign_id") != request.Campaign || sourceHead < request.Sequence)
                return UnprovenVerifiedFork();

            string scopeTimeline = VerifiedForkString(context, "timeline_id");
            var active = WorldSimulationStore.SelectWorldHistoryTimelines2(connection).SingleOrDefault();
            if (scopeTimeline.Length == 0 || scopeTimeline != VerifiedForkString(active, "timeline_id"))
                return UnprovenVerifiedFork();
            double completeFrom = ReadDouble(source, "history_complete_from_day", savedDay);
            string child;
            // An unchanged registered head can be inherited immediately after
            // a fork. Reopening it leaves the already verified scope untouched.
            if (scopeTimeline == request.Timeline && sourceHead == request.Sequence)
                return VerifiedForkString(source, "head_event_id") == request.Head
                    ? VerifiedForkOpenResponse(request, request.Timeline, VerifiedForkString(source, "parent_timeline_id"),
                        sourceHead, completeFrom, null) : UnprovenVerifiedFork();

            // Only a directly retained event can authorize a changed timeline.
            var anchor = WorldSimulationStore.SelectWorldHistoryEvents4(connection,
                TestDict("id", request.Head, "timeline", request.Timeline)).SingleOrDefault();
            if (VerifiedForkString(anchor, "campaign_id") != request.Campaign
                || ReadLong(anchor, "sequence", -1) != request.Sequence)
                return UnprovenVerifiedFork();

            if (scopeTimeline == request.Timeline)
            {
                child = "verified_fork_" + Guid.NewGuid().ToString("N");
                string now = DateTime.UtcNow.ToString("o");
                using var transaction = connection.BeginTransaction();
                WorldSimulationStore.UpsertWorldHistoryTimelines2(connection,
                    TestDict("id", child, "campaign", request.Campaign, "parent", request.Timeline,
                        "fork", request.Head, "complete", completeFrom, "sequence", request.Sequence, "head", request.Head, "now", now),
                    transaction);
                WorldSimulationStore.UpdateWorldHistoryTimelines3(connection,
                    TestDict("id", child, "campaign", request.Campaign), transaction);
                // Commit the timeline and its checkpoint owner together. If
                // either write fails, a retry still sees the original context.
                DialogueStore.UpdateMemoryRestoredSourceContext3(connection,
                    TestDict("requested", request.Timeline, "recovered", child, "sequence", request.Sequence, "head", request.Head),
                    transaction);
                if (ReadVerifiedForkChild(connection, request, child, savedDay, transaction) == null)
                    return UnprovenVerifiedFork();
                transaction.Commit();
            }
            else
            {
                // A lost response must resolve the exact active direct child
                // already bound to this Save Sync load, never create a sibling.
                child = scopeTimeline;
            }

            var childRow = ReadVerifiedForkChild(connection, request, child, savedDay);
            if (childRow == null) return UnprovenVerifiedFork();

            // These existing projections can finish on a retry after the
            // branch commit. Repeating them cannot allocate another child.
            DialogueService.BindRecoveredLetterSources(connection, request.Timeline, child, request.Sequence, request.Head);
            DialogueService.BindRecoveredCommunications(connection, request.Timeline, child, request.Sequence, request.Head);
            RelationshipsService.BindRecoveredPregnancyKnowledge(connection, request.Timeline, child, request.Sequence, request.Head);
            DialogueService.BindRecoveredMemorySources(connection, request.Timeline, child, request.Sequence, request.Head);
            var verifiedFork = TestDict("schemaVersion", 1, "campaignId", request.Campaign, "savePointId", request.SavePoint,
                "loadSessionId", request.LoadSession, "restoreGeneration", request.RestoreGeneration,
                "sourceTimelineId", request.Timeline, "sourceSequence", request.Sequence, "sourceHeadEventId", request.Head,
                "checkpointDay", savedDay, "childTimelineId", child, "nativeMembershipVerified", false);
            return VerifiedForkOpenResponse(request, child, request.Timeline, sourceHead,
                ReadDouble(childRow, "history_complete_from_day", completeFrom), verifiedFork);
        }

        private static bool VerifiedForkSnapshotMatches(ReignDbConnection connection,
            VerifiedForkRequest request, Dictionary<string, object> point)
        {
            // The campaign read gate also keeps Save Sync storage rotation from
            // replacing the ledger-selected manifest or registered snapshot.
            string root = PersistenceService.SaveSyncSnapshotRoot(request.Campaign, point);
            if (!Directory.Exists(Path.Combine(root, "campaign"))) return false;
            var manifest = CampaignFileService.ReadJsonObject(Path.Combine(root, "manifest.json"));
            if (VerifiedForkString(manifest, "format") != "bannerlord-reign-save-sync-snapshot"
                || VerifiedForkString(manifest, "campaignId") != request.Campaign
                || VerifiedForkString(manifest, "databaseProvider") != "postgresql"
                || !string.Equals(VerifiedForkString(manifest, "fingerprint"), VerifiedForkString(point, "snapshotFingerprint"),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            string snapshot = PersistenceService.SaveSyncPostgreSqlSnapshotPointId(point);
            string schema = ReignPostgreSqlStorage.SnapshotSchemaName(request.Campaign, snapshot);
            return DialogueStore.SelectCreatedUtc(connection, TestDict("campaign", request.Campaign, "point", snapshot, "schema", schema)).Count == 1
                && ReadString(DialogueStore.SelectStatement8(connection,
                    TestDict("table", schema + ".world_history_timelines")).Single(), "name", "").Length > 0
                && DialogueStore.SelectWorldHistoryTimelines3(connection, schema,
                    TestDict("campaign", request.Campaign, "timeline", request.Timeline, "sequence", request.Sequence, "head", request.Head)).Count == 1;
        }

        private static Dictionary<string, object> ReadVerifiedForkChild(ReignDbConnection connection,
            VerifiedForkRequest request, string child, double savedDay, ReignDbTransaction transaction = null)
        {
            var active = WorldSimulationStore.SelectWorldHistoryTimelines2(connection, transaction: transaction).SingleOrDefault();
            var row = WorldSimulationStore.SelectWorldHistoryTimelines(connection, TestDict("id", child), transaction).SingleOrDefault();
            var bound = DialogueStore.SelectMemoryRestoredSourceContext3(connection,
                TestDict("timeline", child), transaction).SingleOrDefault();
            return VerifiedForkString(active, "timeline_id") == child
                && VerifiedForkString(row, "campaign_id") == request.Campaign
                && VerifiedForkString(row, "parent_timeline_id") == request.Timeline
                && VerifiedForkString(row, "fork_event_id") == request.Head
                && ReadLong(row, "head_sequence", -1) >= request.Sequence
                && VerifiedForkString(bound, "save_point_id") == request.SavePoint
                && VerifiedForkString(bound, "load_session_id") == request.LoadSession
                && VerifiedForkString(bound, "restore_generation") == request.RestoreGeneration
                && ReadDouble(bound, "world_day", double.NaN) == savedDay
                && ReadLong(bound, "head_sequence", -1) == request.Sequence
                && VerifiedForkString(bound, "head_event_id") == request.Head ? row : null;
        }

        private static Dictionary<string, object> VerifiedForkOpenResponse(VerifiedForkRequest request, string timeline,
            string parent, long sourceHead, double completeFrom, Dictionary<string, object> verifiedFork)
        {
            var response = TestDict("ok", true, "campaignId", request.Campaign, "timelineId", timeline,
                "branched", verifiedFork != null, "historyMissing", false, "sharedActivityGeneration", request.RestoreGeneration,
                "parentTimelineId", parent, "savedSequence", request.Sequence,
                "previousServerHeadSequence", sourceHead, "historyCompleteFromWorldDay", completeFrom);
            if (verifiedFork != null) response["verifiedFork"] = verifiedFork;
            return response;
        }
    }
}
