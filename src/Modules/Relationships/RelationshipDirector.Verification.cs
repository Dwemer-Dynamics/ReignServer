using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        internal static List<Dictionary<string, object>> RunPregnancyChoiceSelfTests()
        {
            List<Dictionary<string, object>> rows=new List<Dictionary<string, object>>();
            Action<string,bool,string> add=(id,passed,summary)=>rows.Add(new Dictionary<string, object>{{"ok",true},{"passed",passed},{"suite","pregnancy_choice"},{"caseId",id},{"name",id},{"summary",summary},{"durationMs",0}});
            string previousRoot=Program.CampaignsRootOverride.Value;
            string campaign="pregnancy_choice_"+Guid.NewGuid().ToString("N");
            string isolatedRoot=Path.Combine(Program.DataDir,"test_runs",campaign);
            Program.CampaignsRootOverride.Value=isolatedRoot;
            try
            {
                Func<string,string,Dictionary<string, object>> request=(attempt,decision)=>new Dictionary<string, object>
                {
                    {"campaignId",campaign},{"attemptId",attempt},{"eventId",attempt},{"decision",decision},
                    {"playerId","main_hero"},{"partnerId","partner_hero"},{"verifiedAct",true},{"actionText","*She completes the insemination.*"},
                    {"coLocated",true},{"completed",true},{"worldDay",42d},
                    {"player",new Dictionary<string, object>{{"isFemale",false},{"age",30d},{"isPregnant",false}}},
                    {"partner",new Dictionary<string, object>{{"isFemale",true},{"age",30d},{"isPregnant",false},{"spouseId",""}}}
                };

                Dictionary<string, object> preview=VerifyPlayerConceptionAttemptApi(request("attempt_preview_cancel","preview"));
                int previewRows;
                using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(campaign))
                    previewRows=ReadInt(RelationshipsStore.CountConceptionAttempts(connection).FirstOrDefault(),"count",-1);
                add("preview_is_stateless",
                    ReadBool(preview,"ok",false)&&ReadBool(preview,"eligible",false)&&!ReadBool(preview,"rollPerformed",true)&&ReadDouble(preview,"roll",0d)==-1d&&previewRows==0,
                    "Opening the popup reports eligibility and chance without consuming or storing a pregnancy roll.");

                Dictionary<string, object> cancelled=VerifyPlayerConceptionAttemptApi(request("attempt_preview_cancel","pull_out"));
                Dictionary<string, object> cancelledThenProceed=VerifyPlayerConceptionAttemptApi(request("attempt_preview_cancel","proceed"));
                Dictionary<string, object> cancelledRow;
                using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(campaign))
                    cancelledRow=RelationshipsStore.SelectConceptionAttempts(connection).FirstOrDefault();
                add("pull_out_never_rolls",
                    ReadBool(cancelled,"cancelled",false)&&!ReadBool(cancelled,"rollPerformed",true)&&ReadDouble(cancelled,"roll",0d)==-1d
                    &&ReadString(cancelledRow,"status","")=="cancelled"&&ReadDouble(cancelledRow,"roll",0d)==-1d,
                    "Pull Out persists a cancellation with the no-roll sentinel and creates no conception.");
                add("cancelled_attempt_cannot_be_replayed",
                    ReadBool(cancelledThenProceed,"idempotent",false)&&ReadBool(cancelledThenProceed,"cancelled",false)
                    &&!ReadBool(cancelledThenProceed,"rollPerformed",true)&&!ReadBool(cancelledThenProceed,"success",true),
                    "A later Proceed retry against the same cancelled attempt remains cancelled and cannot roll.");

                Dictionary<string, object> proceeded=VerifyPlayerConceptionAttemptApi(request("attempt_proceed","proceed"));
                Dictionary<string, object> proceededAgain=VerifyPlayerConceptionAttemptApi(request("attempt_proceed","proceed"));
                add("proceed_rolls_exactly_once",
                    ReadBool(proceeded,"rollPerformed",false)&&ReadDouble(proceeded,"roll",-1d)>=0d
                    &&ReadBool(proceededAgain,"idempotent",false)&&Math.Abs(ReadDouble(proceeded,"roll",-1d)-ReadDouble(proceededAgain,"roll",-2d))<double.Epsilon,
                    "Proceed performs one deterministic roll and retries return the same stored outcome.");

                VerifyPlayerConceptionAtomicity(campaign, request, add);
                VerifyFamilyBirthReceipts(campaign, add);
                VerifyOwnedDirectorNativeReceipts(add);

                Dictionary<string, object> gate=ActionPipelineService.NormalizeConceptionGate(new Dictionary<string, object>{{"needed",true},{"completed",true},{"confidence",0.95d}},"",false);
                Dictionary<string, object> suppressed=ActionPipelineService.NormalizeConceptionGate(new Dictionary<string, object>{{"needed",true},{"completed",true},{"confidence",0.95d}},"*She inseminates him.*",true);
                add("llm_gate_and_pull_out_suppression",
                    ReadBool(gate,"needed",false)&&!ReadBool(suppressed,"needed",true),
                    "Completed LLM insemination actions open the choice, while the pull_out follow-up cannot recursively trigger another roll.");

                Dictionary<string, Dictionary<string, object>> metadata=PromptService.PromptMetadataByName();
                add("pull_out_prompt_is_editable",
                    Program.PromptFileNames.Contains("pull_out.txt",StringComparer.OrdinalIgnoreCase)
                    &&metadata.ContainsKey("pull_out.txt")&&ReadBool(metadata["pull_out.txt"],"visible",false)
                    &&!string.IsNullOrWhiteSpace(PromptService.LoadPromptTemplate("pull_out.txt")),
                    "The pull_out prompt is registered, visible in the Control Center editor, and has a non-empty default.");
            }
            finally
            {
                try { ReignPostgreSqlStorage.DropCampaign(campaign); }
                catch (Exception ex) { add("pregnancy_choice_fixture_cleanup", false, ex.Message); }
                Program.CampaignsRootOverride.Value=previousRoot;
                try{if(Directory.Exists(isolatedRoot))Directory.Delete(isolatedRoot,true);}catch{}
            }
            return rows;
        }

        private static void VerifyPlayerConceptionAtomicity(string campaign,
            Func<string, string, Dictionary<string, object>> request, Action<string, bool, string> add)
        {
            string WinningAttempt(string prefix) => Enumerable.Range(0, 1000).Select(i => prefix + i)
                .First(id => StableUnit(id + "|player_conception") < PlayerConceptionChance(30d));
            string attempt = WinningAttempt("d43_atomic_");
            var key = new Dictionary<string, object> { ["id"] = attempt };
            const string addConstraint = "ALTER TABLE relationship_director_actions ADD CONSTRAINT d43_fixture_reject_queue CHECK (action_type <> 'start_conception') NOT VALID;";
            const string dropConstraint = "ALTER TABLE relationship_director_actions DROP CONSTRAINT d43_fixture_reject_queue;";
            bool failedAtQueue = false;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                ReignDatabase.ExecuteSql(connection, addConstraint);
                try { VerifyPlayerConceptionAttemptApi(request(attempt, "proceed")); }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.CheckViolation
                    && ex.ConstraintName == "d43_fixture_reject_queue") { failedAtQueue = true; }
                finally { ReignDatabase.ExecuteSql(connection, dropConstraint); }
                add("conception_queue_failure_rolls_back_whole_attempt", failedAtQueue
                    && RelationshipsStore.SelectConceptionAttempts2(connection, key).Count == 0
                    && RelationshipsStore.SelectConceptions2(connection, key).Count == 0,
                    "A rejected queue insert leaves neither the successful attempt nor its conception committed.");
            }

            var recovered = VerifyPlayerConceptionAttemptApi(request(attempt, "proceed"));
            var replayed = VerifyPlayerConceptionAttemptApi(request(attempt, "proceed"));
            string conception = ReadString(recovered, "conceptionId", "");
            add("conception_retry_commits_original_deterministic_outcome", ReadBool(recovered, "success", false)
                && ReadBool(replayed, "success", false) && ReadBool(replayed, "idempotent", false)
                && conception.Length > 0 && conception == ReadString(replayed, "conceptionId", "")
                && ReadDouble(replayed, "roll", -1d) == StableUnit(attempt + "|player_conception"),
                "Retry after rollback commits the original deterministic roll and one linked native action.");

            var concurrentAttempt = WinningAttempt("d43_concurrent_");
            Dictionary<string, object> first = null, second = null;
            System.Threading.Tasks.Parallel.Invoke(
                () => first = VerifyPlayerConceptionAttemptApi(request(concurrentAttempt, "proceed")),
                () => second = VerifyPlayerConceptionAttemptApi(request(concurrentAttempt, "proceed")));
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            using (var transaction = connection.BeginTransaction())
            {
                var rows = RelationshipsStore.SelectConceptions2(connection,
                    new Dictionary<string, object> { ["id"] = concurrentAttempt }, transaction);
                var candidates = RelationshipsStore.SelectPlayerConceptionActions(connection,
                    new Dictionary<string, object> { ["actor"] = "partner_hero", ["target"] = "main_hero", ["day"] = 42d }, transaction);
                add("conception_concurrent_retry_has_single_durable_owner", ReadBool(first, "success", false)
                    && ReadBool(second, "success", false) && ReadBool(first, "idempotent", false) != ReadBool(second, "idempotent", false)
                    && ReadString(first, "conceptionId", "") == ReadString(second, "conceptionId", "")
                    && rows.Count == 1 && candidates.Count(row => IsPlayerConceptionFollowUp(row, rows[0])) == 1,
                    "Competing requests share one successful attempt, conception ID and native action.");
            }

            const string historicalAttempt = "d43_legacy_partial";
            var historicalKey = new Dictionary<string, object> { ["id"] = historicalAttempt };
            const double historicalRoll = 0.0125d;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                RelationshipsStore.TryInsertPlayerConceptionAttempt(connection, new Dictionary<string, object>
                {
                    ["id"] = historicalAttempt, ["event"] = historicalAttempt, ["mother"] = "partner_hero", ["father"] = "main_hero",
                    ["age"] = 30d, ["chance"] = 0.09d, ["roll"] = historicalRoll, ["success"] = 1, ["day"] = 42d,
                    ["payload"] = "{}", ["ts"] = 1L, ["status"] = "resolved",
                });
                var missingConception = VerifyPlayerConceptionAttemptApi(request(historicalAttempt, "proceed"));
                add("conception_legacy_success_without_conception_is_held",
                    IsIncompleteConceptionReceipt(missingConception, "missing_conception", historicalRoll)
                    && RelationshipsStore.SelectConceptions2(connection, historicalKey).Count == 0,
                    "A historical winning roll without a linked conception is reported without inventing a conception or roll.");

                RelationshipsStore.InsertConceptions(connection, new Dictionary<string, object>
                {
                    ["id"] = "d43_legacy_conception", ["attempt"] = historicalAttempt, ["mother"] = "partner_hero",
                    ["bio"] = "main_hero", ["legal"] = "main_hero", ["day"] = 42d, ["due"] = 78d,
                    ["secrecy"] = 0d, ["payload"] = "{}", ["ts"] = 1L,
                });
                var missingAction = VerifyPlayerConceptionAttemptApi(request(historicalAttempt, "proceed"));
                var again = VerifyPlayerConceptionAttemptApi(request(historicalAttempt, "proceed"));
                add("conception_legacy_pending_without_exact_action_is_held",
                    IsIncompleteConceptionReceipt(missingAction, "missing_native_action", historicalRoll)
                    && IsIncompleteConceptionReceipt(again, "missing_native_action", historicalRoll)
                    && ReadString(again, "conceptionId", "") == "d43_legacy_conception",
                    "Actions for the same pair and day cannot stand in for this conception or authorize a new native effect.");
            }
        }

        private static void VerifyFamilyBirthReceipts(string campaign, Action<string, bool, string> add)
        {
            const string mother = "d17_mother";
            const string biological = "d17_biological";
            const string intended = "d17_intended";
            const string child = "d17_child";
            string conception = Enumerable.Range(0, 1000).Select(i => "d17_birth_" + i)
                .First(id => StableUnit(id + "|conceal_parentage") > 0.95d);

            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            EnsureRelationshipDirectorSchema(connection);
            WorldSimulationService.EnsureWorldHistorySchema(connection);
            DialogueService.EnsureMemoryPrecisionSchema(connection);
            if (WorldSimulationStore.SelectWorldHistoryTimelines2(connection).Count == 0)
            {
                WorldSimulationStore.InsertWorldHistoryTimelines(connection, TestDict(
                    "timeline", "d17_fixture_timeline", "campaign", campaign, "head", 0,
                    "now", DateTime.UtcNow.ToString("o")));
            }
            string timeline = WorldSimulationService.ActiveWorldHistoryTimeline(connection);
            string generation = ReadString(DialogueStore.SelectMemoryPrecisionState4(connection).Single(),
                "restore_generation", "");
            Dictionary<string, object> Report(string id, string status, string observedChild, string legal) => TestDict(
                "campaignId", campaign, "timelineId", timeline, "sharedActivityGeneration", generation,
                "conceptionId", id, "status", status, "childId", observedChild,
                "motherId", mother, "biologicalFatherId", biological, "legalFatherId", legal,
                "contextSource", "reign_managed", "worldDay", 78d, "fatherExposed", false);
            void Seed(string id) => RelationshipsStore.InsertOrIgnoreConceptions(connection, TestDict(
                "id", id, "mother", mother, "father", biological, "legal", intended,
                "start", 42d, "due", 78d, "status", "active", "payload", "{}", "ts", 1L));
            Dictionary<string, object> Conception(string id) =>
                RelationshipsStore.SelectConceptions3(connection, TestDict("id", id)).Single();
            Dictionary<string, object> Parentage(string id) =>
                RelationshipsStore.SelectParentage(connection, TestDict("child", id)).SingleOrDefault();
            int OutboxCount() => ReadInt(ReignDatabase.QuerySql(connection,
                "SELECT COUNT(*) AS count FROM director_outbox WHERE kind='parentage_rumor_seed';").Single(), "count", -1);
            bool Receipt(Dictionary<string, object> result, string id, string acknowledgedChild, bool replay)
                => ReadBool(result, "ok", false) && ReadBool(result, "idempotent", false) == replay
                    && ReadString(result, "campaignId", "") == campaign && ReadString(result, "timelineId", "") == timeline
                    && ReadString(result, "conceptionId", "") == id && ReadString(result, "status", "") == "born"
                    && ReadString(result, "childId", "") == acknowledgedChild
                    && result.TryGetValue("sharedActivityGeneration", out object value)
                    && value is string receiptGeneration && receiptGeneration == generation;

            Seed(conception);
            var report = Report(conception, "born", child, intended);
            report["intendedLegalFatherId"] = intended;
            int originalOutbox = OutboxCount();
            const string reject = "ALTER TABLE conceptions ADD CONSTRAINT d17_fixture_reject_birth CHECK (status <> 'born') NOT VALID;";
            const string restore = "ALTER TABLE conceptions DROP CONSTRAINT d17_fixture_reject_birth;";
            bool rejectedAtCompletion = false;
            ReignDatabase.ExecuteSql(connection, reject);
            try { FamilyConceptionReportApi(report); }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.CheckViolation
                && ex.ConstraintName == "d17_fixture_reject_birth") { rejectedAtCompletion = true; }
            finally { ReignDatabase.ExecuteSql(connection, restore); }
            add("birth_receipt_failure_rolls_back_parentage_and_outbox",
                rejectedAtCompletion && ReadString(Conception(conception), "status", "") == "active"
                && Parentage(child) == null && OutboxCount() == originalOutbox,
                "A PostgreSQL rejection at the final conception update rolls back both prior writes.");

            var born = FamilyConceptionReportApi(report);
            var stored = Parentage(child);
            add("birth_receipt_exact_identity_commits_once",
                Receipt(born, conception, child, replay: false) && ReadString(Conception(conception), "status", "") == "born"
                && ReadString(Conception(conception), "child_id", "") == child
                && ReadString(stored, "conception_id", "") == conception && ReadString(stored, "mother_id", "") == mother
                && ReadString(stored, "biological_father_id", "") == biological && ReadString(stored, "legal_father_id", "") == intended
                && ReadInt(stored, "revealed", -1) == 0 && OutboxCount() == originalOutbox + 1,
                "The exact born outcome commits conception, parentage, and one deterministic rumor seed.");

            string revealedPayload = Json.Serialize(TestDict("existingReveal", true));
            RelationshipsStore.UpdateParentage(connection, TestDict("child", child, "surname", "d17_revealed",
                "payload", revealedPayload, "ts", 2L));
            var replay = FamilyConceptionReportApi(report);
            var changedChild = FamilyConceptionReportApi(Report(conception, "born", "d17_other_child", intended));
            var delayedActive = FamilyConceptionReportApi(Report(conception, "active", "", intended));
            stored = Parentage(child);
            add("birth_receipt_replay_preserves_revealed_child",
                Receipt(replay, conception, child, replay: true) && !ReadBool(changedChild, "ok", true)
                && !ReadBool(delayedActive, "ok", true) && ReadString(Conception(conception), "child_id", "") == child
                && Parentage("d17_other_child") == null && ReadInt(stored, "revealed", 0) == 1
                && ReadString(stored, "bastard_surname", "") == "d17_revealed"
                && ReadString(stored, "payload_json", "") == revealedPayload && OutboxCount() == originalOutbox + 1,
                "An exact replay leaves the existing reveal intact and rejects both a replacement child and a delayed active report.");

            const string legacy = "d17_legacy_fallback";
            const string legacyChild = "d17_legacy_child";
            Seed(legacy);
            var unsupported = FamilyConceptionReportApi(Report(legacy, "born", legacyChild, biological));
            add("birth_receipt_legacy_fallback_requires_existing_active_evidence", !ReadBool(unsupported, "ok", true)
                && ReadString(Conception(legacy), "status", "") == "active" && Parentage(legacyChild) == null,
                "An uncorroborated observed legal-parent fallback does not create durable birth effects.");
            var activation = Report(legacy, "active", "", biological);
            activation.Remove("sharedActivityGeneration");
            bool activationAccepted = ReadBool(FamilyConceptionReportApi(activation), "ok", false);
            var legacyBorn = FamilyConceptionReportApi(Report(legacy, "born", legacyChild, biological));
            var legacyReplay = FamilyConceptionReportApi(Report(legacy, "born", legacyChild, biological));
            var legacyParentage = Parentage(legacyChild);
            add("birth_receipt_legacy_fallback_records_observed_parent",
                activationAccepted && Receipt(legacyBorn, legacy, legacyChild, replay: false)
                && Receipt(legacyReplay, legacy, legacyChild, replay: true)
                && ReadString(Conception(legacy), "legal_father_id", "") == intended
                && ReadString(legacyParentage, "biological_father_id", "") == biological
                && ReadString(legacyParentage, "legal_father_id", "") == biological
                && ReadInt(legacyParentage, "is_illegitimate", 0) == 1 && OutboxCount() == originalOutbox + 1,
                "An exact prior managed activation corroborates the fallback, preserving intended and observed parentage separately.");

            const string stale = "d17_stale_scope";
            const string staleChild = "d17_stale_child";
            Seed(stale);
            var staleGeneration = Report(stale, "born", staleChild, intended);
            staleGeneration["sharedActivityGeneration"] = generation + "_stale";
            var staleTimeline = Report(stale, "born", staleChild, intended);
            staleTimeline["timelineId"] = timeline + "_stale";
            var missingTimeline = Report(stale, "born", staleChild, intended);
            missingTimeline.Remove("timelineId");
            add("birth_receipt_rejects_stale_or_missing_typed_scope",
                !ReadBool(FamilyConceptionReportApi(staleGeneration), "ok", true)
                && !ReadBool(FamilyConceptionReportApi(staleTimeline), "ok", true)
                && !ReadBool(FamilyConceptionReportApi(missingTimeline), "ok", true)
                && ReadString(Conception(stale), "status", "") == "active"
                && Parentage(staleChild) == null && OutboxCount() == originalOutbox + 1,
                "Stale generation, stale timeline, and missing typed timeline all leave the seeded conception unchanged.");

            var stillborn = Report("d17_native_stillborn", "born", "", biological);
            stillborn["contextSource"] = "bannerlord_native_schedule";
            add("birth_receipt_native_stillbirth_without_parentage",
                Receipt(FamilyConceptionReportApi(stillborn), "d17_native_stillborn", "", replay: false)
                && Receipt(FamilyConceptionReportApi(stillborn), "d17_native_stillborn", "", replay: true)
                && ReadString(Conception("d17_native_stillborn"), "status", "") == "born"
                && Parentage("") == null && OutboxCount() == originalOutbox + 1,
                "Native delivery without a surviving child has an idempotent born receipt and no parentage or rumor row.");
        }

        private static bool IsIncompleteConceptionReceipt(Dictionary<string, object> result, string status, double roll) =>
            !ReadBool(result, "ok", true) && !ReadBool(result, "success", true) && ReadBool(result, "idempotent", false)
            && ReadBool(result, "rollSucceeded", false) && ReadDouble(result, "roll", -1d) == roll
            && ReadString(result, "followUpStatus", "") == status;


        private static void VerifyOwnedDirectorNativeReceipts(Action<string, bool, string> add)
        {
            string previousRoot = Program.CampaignsRootOverride.Value;
            string campaign = "d4_director_owner_" + Guid.NewGuid().ToString("N");
            string isolatedRoot = Path.Combine(Program.DataDir, "test_runs", campaign);
            Program.CampaignsRootOverride.Value = isolatedRoot;
            try
            {
                using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                EnsureRelationshipDirectorSchema(connection);
                WorldSimulationService.EnsureWorldHistorySchema(connection);
                DialogueService.EnsureMemoryPrecisionSchema(connection);
                if (WorldSimulationStore.SelectWorldHistoryTimelines2(connection).Count == 0)
                {
                    WorldSimulationStore.InsertWorldHistoryTimelines(connection, TestDict(
                        "timeline", "d4_fixture_timeline", "campaign", campaign, "head", 0,
                        "now", DateTime.UtcNow.ToString("o")));
                }
                string timeline = WorldSimulationService.ActiveWorldHistoryTimeline(connection);
                string generation = ReadString(DialogueStore.SelectMemoryPrecisionState4(connection).Single(),
                    "restore_generation", "");

                const string current = "d4_current";
                const string foreign = "d4_foreign";
                const string legacy = "d4_untagged";
                void Seed(string id, string ownedTimeline)
                {
                    var metadata = TestDict("source", "d4_fixture");
                    if (ownedTimeline != null) metadata["timelineId"] = ownedTimeline;
                    RelationshipsStore.InsertRelationshipDirectorActions2(connection, TestDict(
                        "id", id, "type", "divorce", "day", 42d, "actor", "fixture_a", "target", "fixture_b",
                        "payload", Json.Serialize(metadata), "ts", 1L));
                }
                Dictionary<string, object> Row(string id) =>
                    RelationshipsStore.SelectRelationshipDirectorActions6(connection, TestDict("id", id)).Single();
                string Status(string id) => ReadString(Row(id), "status", "");
                Dictionary<string, object> Owner(string stamp) =>
                    TestDict("campaignId", campaign, "timelineId", timeline, "sharedActivityGeneration", stamp);
                bool EchoesOwner(Dictionary<string, object> result) =>
                    ReadDictionary(result, "ownership") is Dictionary<string, object> owner
                    && ReadString(owner, "campaignId", "") == campaign && ReadString(owner, "timelineId", "") == timeline
                    && owner.TryGetValue("sharedActivityGeneration", out object echoed)
                    && echoed is string echoedGeneration && echoedGeneration == generation;
                Dictionary<string, object> Poll(string stamp) => RelationshipDirectorPollActionsApi(
                    TestDict("campaignId", campaign, "ownership", Owner(stamp)));
                Dictionary<string, object> Report(string stamp, params Dictionary<string, object>[] receipts) =>
                    RelationshipDirectorReportActionsApi(TestDict("campaignId", campaign,
                        "ownership", Owner(stamp), "receipts", receipts));
                Dictionary<string, object> Receipt(string id, string ownedTimeline = null, double day = 42d) =>
                    TestDict("directorActionId", id, "timelineId", ownedTimeline ?? timeline, "status", "completed", "worldDay", day);
                string[] Ids(Dictionary<string, object> result) => ReadDictionaryList(result, "actions")
                    .Select(action => ReadString(action, "director_action_id", "")).ToArray();
                bool Ok(Dictionary<string, object> result) => ReadBool(result, "ok", false);

                Seed(current, timeline);
                Seed(foreign, timeline + "_stale");
                Seed(legacy, null);
                string staleGeneration = generation + "_stale";
                add("director_owner_stale_generation_cannot_claim",
                    !Ok(Poll(staleGeneration)) && Status(current) == "pending"
                    && Status(foreign) == "pending" && Status(legacy) == "pending",
                    "A stale save owner cannot claim any persisted native action.");

                Dictionary<string, object> first = null, second = null;
                System.Threading.Tasks.Parallel.Invoke(
                    () => first = Poll(generation), () => second = Poll(generation));
                string[] claimed = Ids(first).Concat(Ids(second)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                add("director_owner_concurrent_claims_exclude_foreign_timeline",
                    Ok(first) && Ok(second) && EchoesOwner(first) && EchoesOwner(second)
                    && claimed.SequenceEqual(new[] { current, legacy }) && Status(current) == "claimed"
                    && Status(legacy) == "claimed" && Status(foreign) == "pending",
                    "Competing polls claim each eligible action once while leaving a foreign timeline untouched.");

                string[] activeClaim = Ids(Poll(generation));
                ReignDatabase.ExecuteSql(connection,
                    "UPDATE relationship_director_actions SET claimed_ts=$ts WHERE director_action_id=$id;",
                    TestDict("ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 301, "id", current));
                var reclaimed = Poll(generation);
                add("director_owner_preserves_five_minute_reclaim",
                    activeClaim.Length == 0 && Ok(reclaimed) && EchoesOwner(reclaimed)
                    && Ids(reclaimed).SequenceEqual(new[] { current }),
                    "A fresh claim remains exclusive and becomes eligible again after five minutes.");

                var stale = Report(staleGeneration, Receipt(current));
                var wrongTimeline = Report(generation, Receipt(current, timeline + "_stale"));
                add("director_owner_rejects_stale_receipts_without_writes",
                    !Ok(stale) && !Ok(wrongTimeline) && Status(current) == "claimed"
                    && string.IsNullOrEmpty(ReadString(Row(current), "result_json", "")),
                    "A stale generation or mismatched receipt timeline cannot complete an active claim.");

                var mixed = Report(generation, Receipt(foreign), Receipt(current));
                var results = ReadDictionaryList(mixed, "results");
                var savedResult = ReadString(Row(current), "result_json", "");
                var retry = Report(generation, Receipt(current, day: 43d));
                var repeated = ReadDictionaryList(retry, "results").SingleOrDefault();
                add("director_owner_exact_terminal_receipt_is_idempotent",
                    !Ok(mixed) && EchoesOwner(mixed) && results.Count == 2
                    && ReadString(results[0], "directorActionId", "") == foreign && !Ok(results[0])
                    && ReadString(results[1], "directorActionId", "") == current && Ok(results[1])
                    && Status(foreign) == "pending" && Status(current) == "completed"
                    && ReadDouble(TryParseStrictJsonObject(savedResult), "worldDay", -1d) == 42d
                    && Ok(retry) && EchoesOwner(retry) && ReadBool(repeated, "idempotent", false)
                    && ReadString(repeated, "directorActionId", "") == current
                    && ReadString(Row(current), "result_json", "") == savedResult,
                    "The exact accepted receipt is stored once and a repeat returns its ID without rewriting the terminal row.");
            }
            finally
            {
                try { ReignPostgreSqlStorage.DropCampaign(campaign); }
                catch (Exception ex) { add("director_owner_fixture_cleanup", false, ex.Message); }
                Program.CampaignsRootOverride.Value = previousRoot;
                try { if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, true); }
                catch (Exception ex) { add("director_owner_fixture_files_cleanup", false, ex.Message); }
            }
        }

        private static List<Dictionary<string, object>> RunRelationshipSubsystemSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string,bool,string> add = (id,passed,summary) => results.Add(new Dictionary<string, object>{{"ok",true},{"passed",passed},{"suite","relationship_system"},{"caseId",id},{"name",id},{"summary",summary},{"durationMs",0}});
            add("player_conception_age_curve",Math.Abs(PlayerConceptionChance(18d)-0.15d)<0.000001d&&Math.Abs(PlayerConceptionChance(30d)-0.0966666667d)<0.000001d&&Math.Abs(PlayerConceptionChance(45d)-0.03d)<0.000001d&&PlayerConceptionChance(17.9d)==0d&&PlayerConceptionChance(45.1d)==0d,"Player curve is 15% at 18, linearly 3% at 45, and zero outside the range.");
            add("player_conception_explicit_verifier",IsExplicitConceptionCapablePlayerText("We made love and attempted to have a child together.")&&!IsExplicitConceptionCapablePlayerText("I kiss you and tell you that I love you."),"Explicit conception-capable acts pass while vague romance does not.");
            add("player_conception_choice_contract",
                NormalizePregnancyChoiceToken("preview")=="preview"&&NormalizePregnancyChoiceToken("proceed")=="proceed"&&NormalizePregnancyChoiceToken("pull_out")=="pull_out",
                "Pregnancy choice decisions distinguish stateless preview, one-time proceed, and no-roll pull-out cancellation.");
            Dictionary<string, object> eligibleRomance = new Dictionary<string, object>{{"genuineInterest",55d},{"strategicInterest",70d},{"receptivity",72d},{"hardConstraints",new Dictionary<string, object>{{"adults",true},{"nativeSuitable",true},{"closeKin",false},{"coercive",false}}}};
            Dictionary<string, object> restrainedRomance = new Dictionary<string, object>{{"genuineInterest",20d},{"strategicInterest",15d},{"receptivity",30d},{"hardConstraints",new Dictionary<string, object>{{"adults",true},{"nativeSuitable",true},{"closeKin",false},{"coercive",false}}}};
            add("affair_shared_posture_gate",DirectorRomanceEligible(eligibleRomance,60d)&&!DirectorRomanceEligible(restrainedRomance,60d),"The director uses the shared interest, receptivity, suitability, and consent posture gate.");
            add("story_physical_romance_routes",
                StoryPhysicalRomanceEligible(true,true,75d,75d,eligibleRomance,eligibleRomance)
                &&StoryPhysicalRomanceEligible(true,true,50d,50d,eligibleRomance,eligibleRomance)
                &&!StoryPhysicalRomanceEligible(false,true,100d,75d,eligibleRomance,eligibleRomance)
                &&!StoryPhysicalRomanceEligible(true,false,100d,75d,eligibleRomance,eligibleRomance)
                &&!StoryPhysicalRomanceEligible(true,true,74d,75d,eligibleRomance,eligibleRomance),
                "Physical intimacy and affairs require co-location, an allowed marital route, bilateral thread intensity, suitability, receptivity, and consent.");
            add("stable_roll_idempotency",Math.Abs(StableUnit("same_attempt")-StableUnit("same_attempt"))<double.Epsilon,"Seeded conception and director rolls are retry-stable.");
            Dictionary<string, object> readyA=new Dictionary<string, object>{{"affection",70d},{"trust",55d},{"attraction",45d},{"resentment",0d}};
            Dictionary<string, object> readyB=new Dictionary<string, object>{{"affection",85d},{"trust",70d},{"attraction",60d},{"resentment",10d}};
            add("romantic_marriage_bilateral_gate",RomanticMarriageReady(readyA,readyB)&&!RomanticMarriageReady(readyA,new Dictionary<string, object>{{"affection",69d},{"trust",80d},{"attraction",80d}}),"Romantic marriage requires both directions to meet affection 70, trust 55, and attraction 45.");
            Dictionary<string, object> marriageA=new Dictionary<string, object>
            {
                {"heroStringId","marriage_a"},{"clanId","clan_a"},{"isAlive",true},{"age",24d},{"isFemale",true},
                {"nativeCanMarry",true},{"nativeMarriageClanSuitable",true},
                {"marriageAncestorIds",new List<string>{"ancestor_a"}},
                {"activeCourtships",new List<Dictionary<string, object>>()}
            };
            Dictionary<string, object> marriageB=new Dictionary<string, object>
            {
                {"heroStringId","marriage_b"},{"clanId","clan_b"},{"isAlive",true},{"age",26d},{"isFemale",false},
                {"nativeCanMarry",true},{"nativeMarriageClanSuitable",true},
                {"marriageAncestorIds",new List<string>{"ancestor_b"}},
                {"activeCourtships",new List<Dictionary<string, object>>()}
            };
            Dictionary<string, object> nativeBlocked=new Dictionary<string, object>(marriageB){{"nativeCanMarry",false}};
            Dictionary<string, object> related=new Dictionary<string, object>(marriageB){{"marriageAncestorIds",new List<string>{"ancestor_a"}}};
            Dictionary<string, object> courtshipBlocked=new Dictionary<string, object>(marriageA)
            {
                {"activeCourtships",new List<Dictionary<string, object>>
                    {new Dictionary<string, object>{{"heroId","other_suitor"},{"clanId","clan_b"},{"level",4}}}}
            };
            add("arranged_marriage_native_eligibility",
                SnapshotMarriageEligible(marriageA,marriageB)
                &&!SnapshotMarriageEligible(marriageA,nativeBlocked)
                &&!SnapshotMarriageEligible(marriageA,related)
                &&!SnapshotMarriageEligible(courtshipBlocked,marriageB),
                "Server-side arranged-marriage selection honors native marriage suitability, extended kinship, and active native courtships before queuing an action.");
            Dictionary<string, object> dutiful=new Dictionary<string, object>{{"foundationTraits",new Dictionary<string, object>{{"dutyMotivation",2},{"familyMotivation",2},{"loyalty",2},{"traditionalism",2},{"pragmatism",2},{"assertiveness",-2},{"pride",-2},{"impulsiveness",-2}}},{"nativeRelationToLeader",80d}};
            Dictionary<string, object> resistant=new Dictionary<string, object>{{"foundationTraits",new Dictionary<string, object>{{"dutyMotivation",-2},{"familyMotivation",-2},{"loyalty",-2},{"traditionalism",-2},{"pragmatism",-2},{"assertiveness",2},{"pride",2},{"impulsiveness",2}}},{"nativeRelationToLeader",-50d}};
            add("compelled_marriage_trait_resentment",ArrangedMarriageResentment(resistant,new Dictionary<string, object>(),new Dictionary<string, object>(),40d)>ArrangedMarriageResentment(dutiful,new Dictionary<string, object>(),new Dictionary<string, object>(),40d),"Resistant personalities receive more arranged-marriage resentment than dutiful traditional personalities.");
            double lowChance=DialogueService.ClampDouble(0.01d+0.04d*0d,0.01d,0.05d),highChance=DialogueService.ClampDouble(0.01d+0.04d*1d,0.01d,0.05d);
            add("arranged_marriage_weekly_chance_cap",Math.Abs(lowChance-0.01d)<0.000001d&&Math.Abs(highChance-0.05d)<0.000001d,"Weekly clan-leader marriage chance scales from 1% and never exceeds 5%.");
            List<KeyValuePair<string,double>> eventCatalog=new List<KeyValuePair<string,double>>
            {
                new KeyValuePair<string,double>("shared_confidence",1),new KeyValuePair<string,double>("practical_favor",1),
                new KeyValuePair<string,double>("family_duty_cooperation",1),new KeyValuePair<string,double>("private_spousal_confidence",1),
                new KeyValuePair<string,double>("political_rivalry_argument",1),new KeyValuePair<string,double>("mutual_romantic_flirtation",1),
                new KeyValuePair<string,double>("romantic_confidence",1),new KeyValuePair<string,double>("romantic_intimacy",1),
                new KeyValuePair<string,double>("political_obstruction",1),new KeyValuePair<string,double>("marital_argument",1),
                new KeyValuePair<string,double>("secret_affair_intimacy",1),new KeyValuePair<string,double>("marital_separation",1)
            };
            HashSet<string> reached=new HashSet<string>(Enumerable.Range(0,2000).Select(i=>SelectWeightedPassiveEvent(eventCatalog,"reachability_"+i)),StringComparer.OrdinalIgnoreCase);
            add("weighted_event_catalog_reachability",eventCatalog.All(x=>reached.Contains(x.Key)),"Every discrete relationship event remains selectable when its eligibility gate supplies positive weight.");
            double simulated=0d;HashSet<int> processedDays=new HashSet<int>();bool duplicate=false;
            for(int day=0;day<630;day++){if(!processedDays.Add(day)){duplicate=true;break;}double gap=25d-simulated;if(Math.Abs(gap)>=0.5d)simulated=DialogueService.ClampDouble(simulated+DialogueService.ClampDouble(gap*0.02d,-0.50d,0.50d),-100d,100d);}
            add("five_year_ambient_bounded_simulation",!duplicate&&simulated>=-25d&&simulated<=25d&&processedDays.Count==630,"A seeded five-year, 126-day-year daily simulation remains bounded and processes each day once.");
            string storyCampaign="relationship_story_selftest_"+Guid.NewGuid().ToString("N");
            using(ReignDbConnection storyConnection=ReignDatabase.OpenCampaignConnection(storyCampaign))
            {
                EnsureRelationshipDirectorSchema(storyConnection);
                ApplyStoryEventToDatabase(storyConnection,"mutual_romantic_flirtation","story_flirt","story_a","story_b",10d,false,false);
                Dictionary<string,object> story=LoadPairStorySummary(storyConnection,"story_a","story_b",17d);
                add("persistent_story_schema_and_priority",Math.Abs(ReadDouble(story,"multiplier",0d)-3d)<0.0001d
                    && Math.Abs(PairStoryKindIntensity(storyConnection,"story_a","story_b","romance",17d,true)-25d)<0.0001d,
                    "Persistent directional romance threads survive storage and give recent active stories exactly 3x priority.");
                ApplyStoryEventToDatabase(storyConnection,"mutual_romantic_flirtation","story_flirt","story_a","story_b",10d,false,false);
                add("story_event_idempotency",Math.Abs(PairStoryKindIntensity(storyConnection,"story_a","story_b","romance",17d,true)-25d)<0.0001d,
                    "Reprocessing one source event cannot advance a storyline twice.");
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_1","story_a","story_b",18d,false,false,eligibleRomance,eligibleRomance);
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_2","story_a","story_b",19d,false,false,eligibleRomance,eligibleRomance);
                add("production_developed_courtship_penalty",Math.Abs(ArrangedMarriageCourtshipMultiplier(storyConnection,"story_a","story_c",19d)-.75d)<.0001d
                    &&Math.Abs(ArrangedMarriageCourtshipMultiplier(storyConnection,"story_a","story_b",19d)-1d)<.0001d,
                    "Persisted developed courtships apply the exact third-party arrangement penalty without penalizing their own couple.");
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_3","story_a","story_b",20d,false,false,eligibleRomance,eligibleRomance);
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_4","story_a","story_b",21d,false,false,eligibleRomance,eligibleRomance);
                add("production_major_courtship_penalty",Math.Abs(ArrangedMarriageCourtshipMultiplier(storyConnection,"story_a","story_c",21d)-.50d)<.0001d,
                    "Persisted major courtships apply the exact 50% third-party arrangement penalty.");
                int displaced=ConvertDisplacedCourtshipsToTemptation(storyConnection,"story_a","story_c",22d,"story_arranged");
                Dictionary<string,object> displacedThread=LoadStoryThread(storyConnection,"story_a","story_b","romance",22d);
                int displacementPressures=ReadInt(RelationshipsStore.CountRelationshipPressures2(storyConnection).FirstOrDefault(),"count",0);
                add("production_displaced_courtship",displaced==1&&ReadString(displacedThread,"route","")=="temptation"&&displacementPressures==2,
                    "An overriding arranged marriage preserves the persisted romance as temptation and creates only private directional pressures.");
                for(int i=0;i<4;i++)ApplyStoryEventToDatabase(storyConnection,"political_rivalry_argument","rivalry_"+i,"story_a","story_b",20+i*7,false,false);
                int feuds=ReadInt(RelationshipsStore.CountRelationshipStoryThreads(storyConnection).FirstOrDefault(),"count",0);
                add("persistent_feud_progression",feuds==1,"Repeated political conflicts create one pair-level feud state.");
                RelationshipsStore.InsertRelationshipPressures(storyConnection, new Dictionary<string,object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});
                LoadPairStorySummary(storyConnection,"story_c","story_d",60d);
                add("lazy_story_seed_from_pressure",PairStoryKindIntensity(storyConnection,"story_c","story_d","romance",60d,false)>=60d,
                    "Existing active relationship pressures lazily seed storyline state without rewriting facets or narrative data.");
            }
            results.AddRange(RunArrangedMarriageSelfTests());
            results.AddRange(RunMbtiRelationshipSelfTests());
            return results;
        }
    }
}
