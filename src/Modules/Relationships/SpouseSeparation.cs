using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        internal static bool IsSpouseLetterPayload(Dictionary<string,object> payload)
            => ReadString(payload,"source","")==ReignSpouseSeparation.Source;

        // Runs inside the history-ingest transaction, against its immutable stored event.
        // The letter doubles as the once-only projection receipt. Failed ingestion retries all effects together.
        internal static void ProjectSpouseSeparation(ReignDbConnection connection, string campaignId,
            string timelineId, Dictionary<string, object> evt)
        {
            if (ReadString(evt, "event_type", "") != "divorce" || ReadString(evt, "source", "") != "reign_system"
                || ReadString(evt, "phase", "") != "completed") return;
            var data = TryParseJsonObject(ReadString(evt, "payload_json", "{}"));
            if (!IsSpouseLetterPayload(data)) return;
            string operation = ReadString(data, "operationId", ""), actor = ReadString(data, "actorId", ""),
                spouse = ReadString(data, "spouseId", "");
            if (operation.Length == 0 || actor.Length == 0 || spouse.Length == 0 || actor == spouse
                || ReadString(evt, "correlation_id", "") != operation || ReadString(data, "termsHash", "").Length != 64)
                throw new InvalidOperationException("The completed spouse-letter action lacks its exact native identity.");
            string letterId = "spouse_notice_" + Sha256Hex(Encoding.UTF8.GetBytes(timelineId + "|" + operation));
            if (RelationshipsStore.SelectLetters14(connection, TestDict("id", letterId)).Any()) return;
            var relations = ReadDictionaryList(data, "relations");
            var ids = relations.Select(r => ReadString(r, "heroId", "")).ToList();
            if (!ids.Contains(spouse) || ids.Any(id => id.Length == 0 || id == actor) || ids.Distinct().Count() != ids.Count
                || relations.Any(r => ReadInt(r, "after", 0) != ReignSpouseSeparation.RelationAfter(ReadInt(r, "before", 0))))
                throw new InvalidOperationException("The spouse-letter relationship receipt is invalid.");
            double day = ReadDouble(evt, "world_day", 0);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (string target in ids)
            {
                ApplyAuthoritativeRelationshipDelta(connection, campaignId, actor, target,
                    -ReignSpouseSeparation.RelationLoss, day, ReignSpouseSeparation.Source, timelineId);
                ApplyAuthoritativeRelationshipDelta(connection, campaignId, target, actor,
                    -ReignSpouseSeparation.RelationLoss, day, ReignSpouseSeparation.Source, timelineId);
            }
            string pair = AmbientPairKey(actor, spouse);
            RelationshipsStore.UpsertRelationshipPairLifecycle2(connection, TestDict("pair", pair, "a", pair.Split('|')[0], "b", pair.Split('|')[1], "day", day, "ts", ts));
            RelationshipsStore.UpdateRelationshipPairChemistry16(connection, TestDict("ts", ts, "pair", pair));
            // Ordinary private correspondence; no generated reply, invented courtship or movement.
            string first = string.Compare(actor, spouse, StringComparison.OrdinalIgnoreCase) <= 0 ? actor : spouse;
            string second = first == actor ? spouse : actor;
            string thread = ReadString(RelationshipsStore.SelectCorrespondenceThreads2(connection, TestDict("a", first, "b", second)).FirstOrDefault(), "thread_id", "");
            if (thread.Length == 0)
            {
                thread = "thread_" + Guid.NewGuid().ToString("N");
                RelationshipsStore.InsertCorrespondenceThreads2(connection, TestDict("thread", thread, "a", first, "b", second, "ts", ts));
            }
            RelationshipsStore.InsertLetters2(connection, TestDict("id", letterId, "thread", thread, "actor", actor, "actorName", ReadString(data, "actorName", actor),
                    "spouse", spouse, "spouseName", ReadString(data, "spouseName", spouse),
                    "body", ReignSpouseSeparation.Letter(ReadString(data, "spouseName", spouse)), "source", ReignSpouseSeparation.Source,
                    "operation", operation, "day", day, "payload", Json.Serialize(data), "ts", ts, "timeline", timelineId));
            RelationshipsStore.UpdateCorrespondenceThreads2(connection, TestDict("id", letterId, "day", day, "ts", ts, "thread", thread));
        }

        internal static List<Dictionary<string, object>> RunSpouseSeparationSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass) => results.Add(TestDict("caseId", "spouse_letter_" + id, "passed", pass, "summary", id));
            string campaign = "__spouse_letter_test_" + Guid.NewGuid().ToString("N");
            Check("fixture_is_internal",Program.IsInternalCampaignId(campaign));
            try
            {
                using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                EnsureRelationshipAndCorrespondenceSchema(connection);
                EnsureMbtiRelationshipSchema(connection);
                EnsureRelationshipLifecycleSchema(connection);
                foreach (string hero in new[] { "leaver", "spouse", "clansperson" })
                    RelationshipsStore.InsertRelationshipPersonalities(connection, TestDict("id", hero));
                // Seed existing neutral directional pairs, with no provider or real campaign.
                foreach (string target in new[] { "spouse", "clansperson" })
                    ApplyAuthoritativeRelationshipDelta(connection, campaign, "leaver", target, 0, 1, "fixture", "main", true);
                var data = TestDict("source", ReignSpouseSeparation.Source, "operationId", "agreement:clause:0",
                    "termsHash", new string('a', 64), "actorId", "leaver", "actorName", "Leaver",
                    "spouseId", "spouse", "spouseName", "Spouse", "spouseClanId", "clan",
                    "relations", new object[] { TestDict("heroId", "spouse", "before", 0, "after", -50),
                        TestDict("heroId", "clansperson", "before", 0, "after", -50) });
                var evt = TestDict("event_type", "divorce", "phase", "completed", "source", "reign_system",
                    "correlation_id", "agreement:clause:0", "world_day", 10d, "payload_json", Json.Serialize(data));
                Dictionary<string,object> ingestion=null;
                Check("history_ingest_wait_does_not_hold_database_locks",
                    MarriageWorkWaitLeavesDatabaseAvailable(campaign, () =>
                        ingestion=WorldSimulationService.WorldHistoryIngestBatchApi(TestDict("campaignId",campaign,"timelineId","main",
                            "events",new[]{TestDict("eventId","spouse_lock_fixture","eventType","divorce",
                                "phase","completed","source","reign_system","correlationId","agreement:clause:0",
                                "worldDay",10d,"payload",data)}))));
                Check("history_ingest_committed_projection",ReadBool(ingestion,"ok",false)
                    && ReadInt(ingestion,"accepted",0)==1);
                Check("legacy_payload_source_is_recognized",IsSpouseLetterPayload(TryParseJsonObject(Json.Serialize(data))));
                Dictionary<string,object> legacyProbe=null;
                bool legacyAvailable=MarriageWorkWaitLeavesDatabaseAvailable(campaign, () =>
                        WorldSimulationService.WorldHistoryIngestBatchApi(TestDict("campaignId",campaign,"timelineId","main",
                            "events",new[]{TestDict("eventId","spouse_lock_legacy_fixture","eventType","divorce",
                                "phase","completed","source","reign_system","correlationId","agreement:clause:0",
                                "worldDay",10d,"payload_json",Json.Serialize(data))})), observation => legacyProbe=observation);
                Check("legacy_payload_history_ingest_uses_same_lock_order",legacyAvailable);
                results.Last()["data"]=legacyProbe;
                var letters = RelationshipsStore.SelectLetters15(connection);
                Check("one_private_notice_from_npc_to_spouse", letters.Count == 1 && ReadString(letters[0], "sender_id", "") == "leaver"
                    && ReadString(letters[0], "recipient_id", "") == "spouse" && ReadString(letters[0], "status", "") == "delivered"
                    && ReadString(letters[0], "body", "") == ReignSpouseSeparation.Letter("Spouse"));
                bool CorrectRelations() => RelationshipsStore.SelectRelationshipPairChemistry9(connection).Count(row =>
                    ReadInt(row, "affinity_a_to_b", 0) == -50 && ReadInt(row, "affinity_b_to_a", 0) == -50) == 2;
                Check("severe_consequences_persist_in_both_directions", CorrectRelations());
                ProjectSpouseSeparation(connection, campaign, "main", evt);
                Check("replay_never_sends_or_penalizes_twice", CorrectRelations()
                    && RelationshipsStore.SelectLetters15(connection).Count == 1);
                Check("lifecycle_marks_actual_divorce", ReadInt(RelationshipsStore.SelectRelationshipPairLifecycle5(connection, TestDict("pair", AmbientPairKey("leaver", "spouse"))).Single(), "divorced", 0) == 1);
                using (var transaction = connection.BeginTransaction())
                {
                    ProjectSpouseSeparation(connection, campaign, "alternate", evt);
                    // Deliberate failure after all projections: rollback must restore letter and affinities together.
                }
                Check("transaction_rollback_keeps_letter_and_affinities_together", CorrectRelations()
                    && RelationshipsStore.SelectLetters15(connection).Count == 1);
                var invalid = Program.CloneDictionary(evt);
                invalid["phase"] = "proposed";
                ProjectSpouseSeparation(connection, campaign, "alternate", invalid);
                Check("proposal_is_not_native_fulfillment", CorrectRelations()
                    && RelationshipsStore.SelectLetters16(connection).Count == 1);
                using var reopened = ReignDatabase.OpenCampaignConnection(campaign);
                Check("notice_survives_connection_reload", RelationshipsStore.SelectLetters15(reopened).Count == 1);
            }
            catch (Exception ex) { results.Add(TestDict("caseId", "spouse_letter_suite_exception", "passed", false, "summary", DialogueService.LimitText(ex.ToString(), 2000))); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
