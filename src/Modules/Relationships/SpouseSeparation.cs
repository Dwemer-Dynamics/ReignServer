using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Runs inside the history-ingest transaction, against its immutable stored event.
        // The letter doubles as the once-only projection receipt. Failed ingestion retries all effects together.
        private static void ProjectSpouseSeparation(ReignDbConnection connection, string campaignId,
            string timelineId, Dictionary<string, object> evt)
        {
            if (ReadString(evt, "event_type", "") != "divorce" || ReadString(evt, "source", "") != "reign_system"
                || ReadString(evt, "phase", "") != "completed") return;
            var data = TryParseJsonObject(ReadString(evt, "payload_json", "{}"));
            if (ReadString(data, "source", "") != ReignSpouseSeparation.Source) return;
            string operation = ReadString(data, "operationId", ""), actor = ReadString(data, "actorId", ""),
                spouse = ReadString(data, "spouseId", "");
            if (operation.Length == 0 || actor.Length == 0 || spouse.Length == 0 || actor == spouse
                || ReadString(evt, "correlation_id", "") != operation || ReadString(data, "termsHash", "").Length != 64)
                throw new InvalidOperationException("The completed spouse-letter action lacks its exact native identity.");
            string letterId = "spouse_notice_" + Sha256Hex(Encoding.UTF8.GetBytes(timelineId + "|" + operation));
            if (QuerySql(connection, "SELECT letter_id FROM letters WHERE letter_id=$id;", TestDict("id", letterId)).Any()) return;
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
            ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(pair_key,hero_a_id,hero_b_id,married,divorced,divorce_day,updated_ts)
VALUES($pair,$a,$b,0,1,$day,$ts) ON CONFLICT(pair_key) DO UPDATE SET married=0,divorced=1,divorce_day=$day,
lover_active=0,affair_active=0,romance_stage='',estranged_a_to_b=0,estranged_b_to_a=0,divorce_action_id='',updated_ts=$ts;",
                TestDict("pair", pair, "a", pair.Split('|')[0], "b", pair.Split('|')[1], "day", day, "ts", ts));
            ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET shared_tag='divorced',updated_ts=$ts WHERE pair_key=$pair;",
                TestDict("ts", ts, "pair", pair));
            // Ordinary private correspondence; no generated reply, invented courtship or movement.
            string first = string.Compare(actor, spouse, StringComparison.OrdinalIgnoreCase) <= 0 ? actor : spouse;
            string second = first == actor ? spouse : actor;
            string thread = ReadString(QuerySql(connection,
                "SELECT thread_id FROM correspondence_threads WHERE participant_a=$a AND participant_b=$b;",
                TestDict("a", first, "b", second)).FirstOrDefault(), "thread_id", "");
            if (thread.Length == 0)
            {
                thread = "thread_" + Guid.NewGuid().ToString("N");
                ExecuteSql(connection, @"INSERT INTO correspondence_threads(thread_id,participant_a,participant_b,created_ts,updated_ts)
VALUES($thread,$a,$b,$ts,$ts);", TestDict("thread", thread, "a", first, "b", second, "ts", ts));
            }
            ExecuteSql(connection, @"INSERT INTO letters(letter_id,thread_id,sender_id,sender_name,recipient_id,recipient_name,body,
status,source,reason,dispatch_day,delivery_day,delivered_day,relationship_evaluated,history_written,payload_json,created_ts,updated_ts,timeline_id)
VALUES($id,$thread,$actor,$actorName,$spouse,$spouseName,$body,'delivered',$source,$operation,$day,$day,$day,1,1,$payload,$ts,$ts,$timeline);",
                TestDict("id", letterId, "thread", thread, "actor", actor, "actorName", ReadString(data, "actorName", actor),
                    "spouse", spouse, "spouseName", ReadString(data, "spouseName", spouse),
                    "body", ReignSpouseSeparation.Letter(ReadString(data, "spouseName", spouse)), "source", ReignSpouseSeparation.Source,
                    "operation", operation, "day", day, "payload", Json.Serialize(data), "ts", ts, "timeline", timelineId));
            ExecuteSql(connection, "UPDATE correspondence_threads SET last_letter_id=$id,last_activity_day=$day,updated_ts=$ts WHERE thread_id=$thread;",
                TestDict("id", letterId, "day", day, "ts", ts, "thread", thread));
        }

        private static List<Dictionary<string, object>> RunSpouseSeparationSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass) => results.Add(TestDict("caseId", "spouse_letter_" + id, "passed", pass, "summary", id));
            string campaign = "spouse_letter_test_" + Guid.NewGuid().ToString("N");
            try
            {
                using var connection = OpenCampaignConnection(campaign);
                EnsureRelationshipAndCorrespondenceSchema(connection);
                EnsureMbtiRelationshipSchema(connection);
                EnsureRelationshipLifecycleSchema(connection);
                foreach (string hero in new[] { "leaver", "spouse", "clansperson" })
                    ExecuteSql(connection, @"INSERT INTO relationship_personalities(hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($id,'INTJ','Fixture','','fixture',1,1,'{}',1,1);", TestDict("id", hero));
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
                using (var transaction = connection.BeginTransaction())
                {
                    ProjectSpouseSeparation(connection, campaign, "main", evt);
                    transaction.Commit();
                }
                var letters = QuerySql(connection, "SELECT * FROM letters WHERE source='explicit_spouse_letter';");
                Check("one_private_notice_from_npc_to_spouse", letters.Count == 1 && ReadString(letters[0], "sender_id", "") == "leaver"
                    && ReadString(letters[0], "recipient_id", "") == "spouse" && ReadString(letters[0], "status", "") == "delivered"
                    && ReadString(letters[0], "body", "") == ReignSpouseSeparation.Letter("Spouse"));
                bool CorrectRelations() => QuerySql(connection, "SELECT * FROM relationship_pair_chemistry;").Count(row =>
                    ReadInt(row, "affinity_a_to_b", 0) == -50 && ReadInt(row, "affinity_b_to_a", 0) == -50) == 2;
                Check("severe_consequences_persist_in_both_directions", CorrectRelations());
                ProjectSpouseSeparation(connection, campaign, "main", evt);
                Check("replay_never_sends_or_penalizes_twice", CorrectRelations()
                    && QuerySql(connection, "SELECT * FROM letters WHERE source='explicit_spouse_letter';").Count == 1);
                Check("lifecycle_marks_actual_divorce", ReadInt(QuerySql(connection,
                    "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair;", TestDict("pair", AmbientPairKey("leaver", "spouse"))).Single(), "divorced", 0) == 1);
                using (var transaction = connection.BeginTransaction())
                {
                    ProjectSpouseSeparation(connection, campaign, "alternate", evt);
                    // Deliberate failure after all projections: rollback must restore letter and affinities together.
                }
                Check("transaction_rollback_keeps_letter_and_affinities_together", CorrectRelations()
                    && QuerySql(connection, "SELECT * FROM letters WHERE source='explicit_spouse_letter';").Count == 1);
                var invalid = CloneDictionary(evt);
                invalid["phase"] = "proposed";
                ProjectSpouseSeparation(connection, campaign, "alternate", invalid);
                Check("proposal_is_not_native_fulfillment", CorrectRelations()
                    && QuerySql(connection, "SELECT * FROM letters;").Count == 1);
                using var reopened = OpenCampaignConnection(campaign);
                Check("notice_survives_connection_reload", QuerySql(reopened, "SELECT * FROM letters WHERE source='explicit_spouse_letter';").Count == 1);
            }
            catch (Exception ex) { results.Add(TestDict("caseId", "spouse_letter_suite_exception", "passed", false, "summary", LimitText(ex.ToString(), 2000))); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
