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
        private const double DirectorRunIntervalDays = 7d;
        private const double DirectorPairCooldownDays = 7d;
        private const int PostgreSqlRelationshipDirectorSchemaRevision = 3;

        internal static void EnsureRelationshipDirectorSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_relationship_director_schema_revision";
            bool schemaReady = ReignDatabase.IsPostgreSqlComponentSchemaReady(connection,
                marker, PostgreSqlRelationshipDirectorSchemaRevision);
            if (!schemaReady)
            {
                EnsureRelationshipDirectorSchemaCore(connection);
                EnsureArrangedMarriageSchema(connection);
                if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                {
                    RelationshipsStore.UpsertSchemaMeta8(connection, new Dictionary<string, object>
                        {
                            ["revision"] =
                                PostgreSqlRelationshipDirectorSchemaRevision
                                    .ToString()
                        });
                    ReignDatabase.MarkPostgreSqlComponentSchemaReady(connection, marker,
                        PostgreSqlRelationshipDirectorSchemaRevision);
                }
            }
            // Save Sync can restore an older campaign snapshot after the process
            // has already cached schema readiness. The repair marker therefore
            // remains a cheap independent check on every schema entry point.
            RepairUnconfirmedOrganicMarriageHistory(connection);
            RepairAlreadyPregnantConceptionActions(connection);
        }

        private static void RepairAlreadyPregnantConceptionActions(
            ReignDbConnection connection)
        {
            const string repairMarker =
                "already_pregnant_conception_obsolete_repair_v1";
            if (ReadString(RelationshipsStore.SelectSchemaMeta3(connection, new Dictionary<string, object> { ["key"] = repairMarker })
                    .FirstOrDefault(), "value", "") == "1") return;

            foreach (Dictionary<string, object> action in RelationshipsStore.SelectRelationshipDirectorActions7(connection))
            {
                Dictionary<string, object> result = TryParseJsonObject(
                    ReadString(action, "result_json", "{}"))
                    ?? new Dictionary<string, object>();
                if (!ReadString(result, "error", "").Equals(
                        "Mother is already pregnant.",
                        StringComparison.OrdinalIgnoreCase)) continue;
                Dictionary<string, object> actionPayload = TryParseJsonObject(
                    ReadString(action, "payload_json", "{}"))
                    ?? new Dictionary<string, object>();
                string source = ReadString(actionPayload, "source", "");
                if (!source.Equals("mbti_relationship_lifecycle",
                        StringComparison.OrdinalIgnoreCase)
                    && !source.Equals("relationship_fling",
                        StringComparison.OrdinalIgnoreCase)) continue;
                string actionId = ReadString(action, "director_action_id", "");
                string conceptionId = ReadString(actionPayload,
                    "conceptionId", "");
                RelationshipsStore.UpdateRelationshipDirectorActions8(connection, new Dictionary<string, object> { ["id"] = actionId });
                if (!string.IsNullOrWhiteSpace(conceptionId))
                    RelationshipsStore.UpdateConceptions4(connection, new Dictionary<string, object>
                        {
                            ["id"] = conceptionId,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
            }
            RelationshipsStore.UpsertSchemaMeta9(connection, new Dictionary<string, object> { ["key"] = repairMarker });
        }

        private static void RepairUnconfirmedOrganicMarriageHistory(
            ReignDbConnection connection)
        {
            const string repairMarker =
                "organic_marriage_native_postcondition_repair_v1";
            string markerValue = ReadString(RelationshipsStore.SelectSchemaMeta3(connection, new Dictionary<string, object> { ["key"] = repairMarker })
                .FirstOrDefault(), "value", "");
            Dictionary<string, object> markerState = TryParseJsonObject(
                markerValue) ?? new Dictionary<string, object>();
            long auditTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!WorldSimulationService.TableExists(connection, "relationship_observed_heroes"))
                return;

            List<Dictionary<string, object>> completed = RelationshipsStore.SelectRelationshipDirectorActions8(connection)
                .Where(row =>
                {
                    Dictionary<string, object> payload = TryParseJsonObject(
                        ReadString(row, "payload_json", "{}"))
                        ?? new Dictionary<string, object>();
                    string route = ReadString(payload, "route", "");
                    return ReadString(payload, "source", "").Equals(
                            "mbti_relationship_lifecycle",
                            StringComparison.OrdinalIgnoreCase)
                        && (route.Equals("mutual_affinity",
                                StringComparison.OrdinalIgnoreCase)
                            || route.Equals("pregnancy_commitment",
                                StringComparison.OrdinalIgnoreCase));
                }).ToList();

            double observedThroughLatest = ReadDouble(RelationshipsStore.SelectRelationshipObservedHeroes2(connection).FirstOrDefault(), "latest", -1d);
            if (ReadInt(markerState, "completedActionCount", -1)
                    == completed.Count
                && Math.Abs(ReadDouble(markerState, "observedThrough", -1d)
                    - observedThroughLatest) < 0.000001d)
                return;

            Dictionary<string, Dictionary<string, object>> observations =
                RelationshipsStore.SelectRelationshipObservedHeroes3(connection)
                .ToDictionary(row => ReadString(row, "hero_id", ""), row => row,
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> invalidActions =
                new List<Dictionary<string, object>>();
            Dictionary<string, string> blockedPairs =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, Dictionary<string, object>> group in completed
                .GroupBy(row =>
                {
                    Dictionary<string, object> payload = TryParseJsonObject(
                        ReadString(row, "payload_json", "{}"))
                        ?? new Dictionary<string, object>();
                    return FirstNonEmpty(ReadString(payload, "pairKey", ""),
                        AmbientPairKey(ReadString(row, "actor_id", ""),
                            ReadString(row, "target_id", "")));
                }, StringComparer.OrdinalIgnoreCase))
            {
                Dictionary<string, object> first = group.First();
                string actorId = ReadString(first, "actor_id", "");
                string targetId = ReadString(first, "target_id", "");
                if (!observations.TryGetValue(actorId,
                        out Dictionary<string, object> actorObservation)
                    || !observations.TryGetValue(targetId,
                        out Dictionary<string, object> targetObservation))
                    continue;
                Dictionary<string, object> actorProfile = TryParseJsonObject(
                    ReadString(actorObservation, "profile_json", "{}"))
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> targetProfile = TryParseJsonObject(
                    ReadString(targetObservation, "profile_json", "{}"))
                    ?? new Dictionary<string, object>();
                bool reciprocal = ReadString(actorProfile, "spouseId", "")
                        .Equals(targetId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(targetProfile, "spouseId", "")
                        .Equals(actorId, StringComparison.OrdinalIgnoreCase);
                if (reciprocal) continue;
                bool sameSex = ReadBool(actorProfile, "isFemale", false)
                    == ReadBool(targetProfile, "isFemale", false);
                bool duplicateReports = group.Count() > 1;
                // Current spouse observations cannot disprove a completed historical marriage.
                if (!sameSex && !duplicateReports)
                    continue;

                string reason = sameSex
                    ? "Native marriage model does not support this same-sex couple."
                    : "Repeated completed reports lacked reciprocal native spouse state.";
                blockedPairs[group.Key] = reason;
                invalidActions.AddRange(group);
            }

            if (invalidActions.Count > 0)
            {
                DialogueService.EnsureSemanticMemorySchema(connection);
                WorldSimulationService.EnsureWorldTestTelemetrySchema(connection);
                string campaignId = ReignPostgreSqlStorage
                    .CampaignIdForConnection(connection);
                HashSet<string> affectedRollups = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> action in invalidActions)
                {
                    string actionId = ReadString(action,
                        "director_action_id", "");
                    Dictionary<string, object> payload = TryParseJsonObject(
                        ReadString(action, "payload_json", "{}"))
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> originalResult = TryParseJsonObject(
                        ReadString(action, "result_json", "{}"))
                        ?? new Dictionary<string, object>();
                    string pairKey = FirstNonEmpty(
                        ReadString(payload, "pairKey", ""),
                        AmbientPairKey(ReadString(action, "actor_id", ""),
                            ReadString(action, "target_id", "")));
                    string reason = blockedPairs.TryGetValue(pairKey,
                        out string storedReason) ? storedReason
                        : "Reciprocal native spouse state was not confirmed.";
                    Dictionary<string, object> repairedResult =
                        new Dictionary<string, object>
                        {
                            ["status"] = "invalid",
                            ["error"] = reason,
                            ["repair"] = repairMarker,
                            ["reclassifiedFrom"] = "completed",
                            ["originalResult"] = originalResult
                        };
                    RelationshipsStore.UpdateRelationshipDirectorActions9(connection, new Dictionary<string, object>
                        {
                            ["result"] = Json.Serialize(repairedResult),
                            ["id"] = actionId
                        });
                    RelationshipsStore.DeleteRelationshipIncidents(connection, new Dictionary<string, object>
                        {
                            ["needle"] = "%" + actionId + "%"
                        });
                    string eventId = "marriage_" + actionId;
                    DialogueService.DeleteWorldMemoryEventProjection(connection, eventId);
                    RelationshipsStore.DeleteEvents(connection, new Dictionary<string, object> { ["event"] = eventId });

                    string timelineId = FirstNonEmpty(
                        ReadString(payload, "timelineId", ""), "main");
                    Dictionary<string, object> result = originalResult;
                    int outcomeDay = (int)Math.Floor(ReadDouble(result,
                        "worldDay", ReadDouble(action, "world_day", 0d)));
                    if (!string.IsNullOrWhiteSpace(campaignId))
                    {
                        WorldSimulationService.RecordWorldTestCounterSchemaReady(connection, campaignId,
                            timelineId, outcomeDay, "marriages",
                            "outcome_" + actionId,
                            WorldSimulationService.BuildWorldTestMarriageOutcomeCounters(
                                WorldSimulationService.WorldTestMarriageOutcomeRoute(payload),
                                "invalid"));
                        affectedRollups.Add(timelineId + "|"
                            + outcomeDay.ToString(CultureInfo.InvariantCulture));
                    }
                }

                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (KeyValuePair<string, string> pair in blockedPairs)
                {
                    Dictionary<string, object> lifecycle = RelationshipsStore.SelectRelationshipPairLifecycle(connection, new Dictionary<string, object> { ["pair"] = pair.Key })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    RelationshipsStore.UpdateRelationshipPairLifecycle9(connection, new Dictionary<string, object>
                        {
                            ["reason"] = pair.Value,
                            ["ts"] = ts,
                            ["pair"] = pair.Key
                        });
                    RelationshipsStore.UpdateRelationshipPairChemistry20(connection, new Dictionary<string, object>
                        {
                            ["tag"] = ReadInt(lifecycle, "lover_active", 0) == 1
                                ? "lovers" : "",
                            ["ts"] = ts,
                            ["pair"] = pair.Key
                        });
                }
                foreach (string affected in affectedRollups)
                {
                    int separator = affected.LastIndexOf('|');
                    string timelineId = affected.Substring(0, separator);
                    int day = int.Parse(affected.Substring(separator + 1),
                        CultureInfo.InvariantCulture);
                    WorldSimulationService.EnqueueWorldTestRollup(connection, campaignId, timelineId,
                        day, repairMarker);
                }
                RelationshipsStore.UpsertSchemaMeta10(connection);
            }
            RelationshipsStore.UpsertSchemaMeta11(connection, new Dictionary<string, object>
            {
                ["key"] = repairMarker,
                ["value"] = Json.Serialize(new Dictionary<string, object>
                {
                    ["reclassifiedActions"] = invalidActions.Count,
                    ["blockedPairs"] = blockedPairs.Count,
                    ["completedActionCount"] = completed.Count,
                    ["observedThrough"] = observedThroughLatest,
                    ["auditedTs"] = auditTs,
                    ["completedUtc"] = DateTime.UtcNow.ToString("o")
                })
            });
        }

        private static void EnsureRelationshipDirectorSchemaCore(
            ReignDbConnection connection)
        {
            RelationshipsSchema.CreateRelationshipDirectorRuns(connection);
            RelationshipsSchema.CreateIndexDirectorRunsDay(connection);
            int prunedDirectorPayloads = ReadInt(RelationshipsStore.CountRelationshipDirectorRuns(connection).FirstOrDefault(), "count", 0);
            if (prunedDirectorPayloads > 0)
            {
                RelationshipsStore.UpdateRelationshipDirectorRuns(connection);
                RelationshipsStore.UpsertSchemaMeta7(connection);
            }
            RelationshipsSchema.CreateRelationshipDirectorActions(connection);
            RelationshipsSchema.CreateIndexDirectorActionsStatus(connection);
            RelationshipsSchema.CreateIndexDirectorActionsPairStatus(connection);
            EnsureRelationshipNativeTargetSchema(connection);
            RelationshipsSchema.CreateConceptionAttempts(connection);
            RelationshipsSchema.EnsureColumnConceptionAttemptsStatus(connection);
            RelationshipsSchema.CreateConceptions(connection);
            RelationshipsSchema.CreateParentage(connection);
            RelationshipsSchema.CreateDirectorOutbox(connection);
            RelationshipsSchema.CreateMarriageLeaderRolls(connection);
            RelationshipsSchema.CreateMarriageEvaluations(connection);
            RelationshipsSchema.CreateIndexMarriageEvaluationsStatus(connection);
            RelationshipsSchema.EnsureColumnMarriageEvaluationsTimelineId(connection);
            MigrateMarriageLeaderRollTimeline(connection);
            ReclassifyNativeMarriageEligibilityFailures(connection);
            EnsureRelationshipLifecycleSchema(connection);
        }

        private static void ReclassifyNativeMarriageEligibilityFailures(ReignDbConnection connection)
        {
            if(ReadString(RelationshipsStore.SelectSchemaMeta6(connection).FirstOrDefault(),"value","")=="1")return;
            foreach(Dictionary<string,object> action in RelationshipsStore.SelectRelationshipDirectorActions9(connection))
            {
                Dictionary<string,object> payload=TryParseJsonObject(ReadString(action,"payload_json","{}"))??new Dictionary<string,object>();
                string evaluationId=ReadString(payload,"evaluationId","");
                RelationshipsStore.UpdateRelationshipDirectorActions10(connection, new Dictionary<string,object>{{"id",ReadString(action,"director_action_id","")}});
                if(!string.IsNullOrWhiteSpace(evaluationId))RelationshipsStore.UpdateMarriageEvaluations5(connection, new Dictionary<string,object>{{"id",evaluationId}});
            }
            RelationshipsStore.UpsertSchemaMeta12(connection);
        }

        internal static void EnsureRelationshipNativeTargetSchema(ReignDbConnection connection)
        {
            if (!IsRelationshipMaintenanceTableReady(connection, "relationship_native_targets"))
            {
            RelationshipsSchema.CreateRelationshipNativeTargets(connection);
            RelationshipsSchema.CreateIndexRelationshipNativeTargetsStatus(connection);
            RelationshipsSchema.EnsureColumnRelationshipNativeTargetsRevision(connection);
            RelationshipsSchema.EnsureColumnRelationshipNativeTargetsRequiresObservation(connection);
            }

            bool chemistryExists = RelationshipsStore.SelectSqliteMaster6(connection).Any();
            if (!chemistryExists || ReadString(RelationshipsStore.SelectSchemaMeta7(connection)
                .FirstOrDefault(), "value", "") == "1") return;

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            RelationshipsStore.Savepoint3(connection);
            try
            {
                // The legacy chemistry row only stored the projected value; it
                // did not retain the native value that was actually observed.
                // Migrating it as both target and observation manufactured an
                // already-aligned pending target for every legacy action. Let
                // the next native observation create a real coalesced target.
                RelationshipsStore.DeleteRelationshipDirectorActions2(connection);
                RelationshipsStore.UpdateRelationshipPairChemistry21(connection);
                RelationshipsStore.UpsertSchemaMeta13(connection);
                RelationshipsStore.UpsertSchemaMeta7(connection);
                RelationshipsStore.Release3(connection);
            }
            catch
            {
                try { RelationshipsStore.Rollback4(connection); } catch { }
                try { RelationshipsStore.Release3(connection); } catch { }
                throw;
            }
        }

        private static readonly Dictionary<string, (string Columns, string Indexes)> RelationshipMaintenanceShapes =
            new Dictionary<string, (string Columns, string Indexes)>(StringComparer.Ordinal)
            {
                ["character_editor_heads"] = (
                    "hero_id,revision_token,latest_revision_id,updated_ts",
                    ""),
                ["character_editor_native_commands"] = (
                    "command_id,revision_id,hero_id,status,changes_json,inverse_json,dangerous,confirmation,created_ts,claimed_ts,resolved_ts,result_json,manual_origin_timeline_id,manual_origin_lineage_id,manual_origin_json,automatic_origin_json",
                    "idx_editor_native_commands_status"),
                ["character_editor_reconciliation"] = (
                    "job_id,revision_id,hero_id,section,status,summary,result_json,created_ts,updated_ts",
                    ""),
                ["character_editor_revisions"] = (
                    "revision_id,hero_id,section,description,before_json,after_json,native_changes_json,reconciliation_json,rollback_of,created_ts",
                    "idx_editor_revisions_hero"),
                ["notable_mbti_profiles"] = (
                    "hero_id,mbti_type,title,description,foundation_json,percentages_json,native_traits_json,source,template_version,first_observed_day,native_sync_status,native_action_id,native_action_created_day,last_native_observed_json,last_native_observed_day,updated_ts,startup_native_session_id",
                    "idx_notable_mbti_type,idx_notable_mbti_sync"),
                ["relationship_fling_encounters"] = (
                    "encounter_id,campaign_id,timeline_id,world_day,pair_key,hero_a_id,hero_b_id,location_kind,location_id,same_sex,short_affair,fixed_discovery_chance,fixed_discovery_roll,fixed_discovered,pregnancy_discovery_chance,pregnancy_discovery_roll,pregnancy_discovered,discovery_applied,rumor_id,conception_attempt_id,conception_id,conception_chance,conception_roll,conceived,conception_action_id,status,payload_json,created_ts,updated_ts",
                    "idx_relationship_fling_encounters_day,idx_relationship_fling_encounters_a,idx_relationship_fling_encounters_b"),
                ["relationship_fling_profiles"] = (
                    "hero_id,judgment,source,updated_ts",
                    ""),
                ["relationship_fling_rolls"] = (
                    "campaign_id,timeline_id,world_day,hero_id,location_kind,location_id,judgment,chance_percent,roll,passed,status,encounter_id,created_ts,updated_ts",
                    "idx_relationship_fling_rolls_hero"),
                ["relationship_fling_sparks"] = (
                    "campaign_id,timeline_id,world_day,pair_key,hero_a_id,hero_b_id,location_kind,location_id,chance_percent,roll,passed,encounter_id,created_ts,updated_ts",
                    "idx_relationship_fling_sparks_day"),
                ["relationship_native_targets"] = (
                    "pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision,requires_observation",
                    "idx_relationship_native_targets_status"),
            };

        private static bool IsRelationshipMaintenanceTableReady(ReignDbConnection connection, string table)
        {
            var shape = RelationshipMaintenanceShapes[table];
            return PostgreSqlRelationshipTableReady(connection, table, shape.Columns, shape.Indexes);
        }

        private static bool PostgreSqlRelationshipTableReady(ReignDbConnection connection,
            string table, string columns, string indexes)
        {
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection)) return false;
            // Read the current catalog instead of issuing no-op DDL. CREATE INDEX
            // takes a table lock even when it already exists. No process cache or
            // marker can outlive a rolled-back migration or a Save Sync restore.
            return RelationshipsStore.SelectPgClass(connection, TestDict("table",table,"columns",columns,"indexes",indexes)).Any();
        }

        private static void MigrateMarriageLeaderRollTimeline(ReignDbConnection connection)
        {
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                bool currentPostgreSqlConstraint = RelationshipsStore.SelectPgConstraint(connection).Any();
                if (currentPostgreSqlConstraint) return;
            }
            Dictionary<string,object> table=RelationshipsStore.SelectSqliteMaster7(connection).FirstOrDefault();
            string schema=ReadString(table,"sql","").Replace(" ",string.Empty).Replace("\r",string.Empty).Replace("\n",string.Empty).ToLowerInvariant();
            if(schema.Contains("unique(timeline_id,period_index,leader_id)"))return;
            RelationshipsSchema.AlterMarriageLeaderRolls(connection);
            RelationshipsSchema.CreateMarriageLeaderRolls2(connection);
            RelationshipsStore.InsertOrIgnoreMarriageLeaderRolls(connection);
            RelationshipsSchema.DropMarriageLeaderRollsLegacy(connection);
        }

        internal static Dictionary<string, object> VerifyPlayerConceptionAttemptApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string attemptId = FirstNonEmpty(ReadFirstString(payload, "attemptId", "eventId"), "attempt_" + Guid.NewGuid().ToString("N"));
            string decision = NormalizePregnancyChoiceToken(ReadString(payload, "decision", "proceed"));
            if (decision != "preview" && decision != "proceed" && decision != "pull_out")
                return new Dictionary<string, object>{{"ok",false},{"attemptId",attemptId},{"error","decision must be preview, proceed, or pull_out."}};
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId");
            string partnerId = ReadFirstString(payload, "partnerId", "npcId", "heroStringId");
            Dictionary<string, object> player = ReadDictionary(payload, "player") ?? new Dictionary<string, object>();
            Dictionary<string, object> partner = ReadDictionary(payload, "partner") ?? new Dictionary<string, object>();
            bool playerFemale = ReadBool(player, "isFemale", false);
            bool partnerFemale = ReadBool(partner, "isFemale", false);
            string motherId = playerFemale && !partnerFemale ? playerId : partnerFemale && !playerFemale ? partnerId : "";
            string fatherId = motherId == playerId ? partnerId : motherId == partnerId ? playerId : "";
            Dictionary<string, object> mother = motherId == playerId ? player : partner;
            double age = ReadDouble(mother, "age", 0d);
            string text = ReadFirstString(payload, "actionText", "npcReply", "playerText", "text", "description");
            bool verified = ReadBool(payload, "verifiedAct", false) || IsExplicitConceptionCapablePlayerText(text);
            bool eligible = verified && !string.IsNullOrWhiteSpace(motherId) && !string.IsNullOrWhiteSpace(fatherId)
                && age >= 18d && age <= 45d && !ReadBool(mother, "isPregnant", false)
                && ReadBool(payload, "coLocated", true) && ReadBool(payload, "completed", true);
            double chance = eligible ? PlayerConceptionChance(age) : 0d;
            double day = ReadDouble(payload, "worldDay", 0d);

            // A preview is deliberately stateless: displaying the choice must never
            // consume a deterministic roll or create a conception record.
            if (decision == "preview")
            {
                return new Dictionary<string, object> {
                    ["ok"] = true,["attemptId"] = attemptId,["decision"] = decision,
                    ["verified"] = verified,["eligible"] = eligible,["chance"] = chance,
                    ["roll"] = -1d,["rollPerformed"] = false,["success"] = false,["cancelled"] = false
                };
            }

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            using (ReignDbTransaction transaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted))
            {
                var key = new Dictionary<string, object> { ["id"] = attemptId };
                var prior = RelationshipsStore.SelectConceptionAttempts2(connection, key, transaction).FirstOrDefault();
                if (prior != null)
                    return ReadPlayerConceptionAttempt(connection, transaction, attemptId, verified, prior);

                bool cancelledNow = decision == "pull_out";
                double roll = cancelledNow ? -1d : StableUnit(attemptId + "|player_conception");
                bool success = !cancelledNow && chance > 0d && roll < chance;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string storedPayload = Json.Serialize(payload);
                if (!RelationshipsStore.TryInsertPlayerConceptionAttempt(connection, new Dictionary<string, object>
                {
                    ["id"] = attemptId, ["event"] = ReadString(payload, "eventId", attemptId), ["mother"] = motherId,
                    ["father"] = fatherId, ["age"] = age, ["chance"] = chance, ["roll"] = roll, ["success"] = success ? 1 : 0,
                    ["day"] = day, ["payload"] = storedPayload, ["ts"] = ts, ["status"] = cancelledNow ? "cancelled" : "resolved"
                }, transaction))
                {
                    // The unique attempt row waits for a concurrent winner. A fresh read
                    // in this READ COMMITTED transaction sees that whole committed result.
                    prior = RelationshipsStore.SelectConceptionAttempts2(connection, key, transaction).Single();
                    return ReadPlayerConceptionAttempt(connection, transaction, attemptId, verified, prior);
                }

                string conceptionId = "";
                string legalFatherId = "";
                if (success)
                {
                    conceptionId = "conception_" + Guid.NewGuid().ToString("N");
                    string spouseId = ReadString(mother, "spouseId", "");
                    legalFatherId = !string.IsNullOrWhiteSpace(spouseId) ? spouseId : fatherId;
                    double secrecy = !string.IsNullOrWhiteSpace(spouseId) && !spouseId.Equals(fatherId, StringComparison.OrdinalIgnoreCase) ? 0.75d : 0d;
                    RelationshipsStore.InsertConceptions(connection, new Dictionary<string, object>
                    {
                        ["id"] = conceptionId, ["attempt"] = attemptId, ["mother"] = motherId, ["bio"] = fatherId,
                        ["legal"] = legalFatherId, ["day"] = day, ["due"] = day + 36d, ["secrecy"] = secrecy,
                        ["payload"] = storedPayload, ["ts"] = ts
                    }, transaction);
                    InsertDirectorAction(connection, "director_action_" + Guid.NewGuid().ToString("N"),
                        "start_conception", day, motherId, fatherId, new Dictionary<string, object>
                        {
                            ["conceptionId"] = conceptionId, ["motherId"] = motherId, ["biologicalFatherId"] = fatherId,
                            ["legalFatherId"] = legalFatherId, ["conceptionDay"] = day, ["dueDay"] = day + 36d,
                            ["secrecy"] = secrecy, ["playerInvolved"] = true, ["scheduledWorldDay"] = day
                        }, transaction);
                }
                transaction.Commit();
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["attemptId"] = attemptId, ["decision"] = decision, ["verified"] = verified,
                    ["eligible"] = eligible, ["chance"] = chance, ["roll"] = roll, ["rollPerformed"] = !cancelledNow,
                    ["cancelled"] = cancelledNow, ["success"] = success, ["conceptionId"] = conceptionId, ["motherId"] = motherId,
                    ["biologicalFatherId"] = fatherId, ["legalFatherId"] = legalFatherId, ["dueDay"] = success ? day + 36d : 0d
                };
            }
        }

        private static Dictionary<string, object> ReadPlayerConceptionAttempt(ReignDbConnection connection,
            ReignDbTransaction transaction, string attemptId, bool verified, Dictionary<string, object> prior)
        {
            string status = ReadString(prior, "status", "resolved");
            bool cancelled = status.Equals("cancelled", StringComparison.OrdinalIgnoreCase);
            double chance = ReadDouble(prior, "chance", 0d);
            bool won = ReadInt(prior, "success", 0) == 1;
            var priorConception = new Dictionary<string, object>();
            string incomplete = "";
            if (!cancelled && won)
            {
                var conceptions = RelationshipsStore.SelectConceptions2(connection,
                    new Dictionary<string, object> { ["id"] = attemptId }, transaction);
                if (conceptions.Count != 1)
                    incomplete = conceptions.Count == 0 ? "missing_conception" : "ambiguous_conception";
                else
                {
                    priorConception = conceptions[0];
                    if (ReadString(priorConception, "status", "") == "pending_game")
                    {
                        int actions = RelationshipsStore.SelectPlayerConceptionActions(connection, new Dictionary<string, object>
                        {
                            ["actor"] = ReadString(priorConception, "mother_id", ""),
                            ["target"] = ReadString(priorConception, "biological_father_id", ""),
                            ["day"] = priorConception["conception_day"]
                        }, transaction).Count(action => IsPlayerConceptionFollowUp(action, priorConception));
                        if (actions != 1) incomplete = actions == 0 ? "missing_native_action" : "ambiguous_native_action";
                    }
                }
            }

            var result = new Dictionary<string, object>
            {
                ["ok"] = incomplete.Length == 0, ["idempotent"] = true, ["attemptId"] = attemptId, ["decision"] = status,
                ["verified"] = verified, ["eligible"] = won || chance > 0d, ["chance"] = chance,
                ["roll"] = ReadDouble(prior, "roll", -1d), ["rollPerformed"] = !cancelled, ["cancelled"] = cancelled,
                ["success"] = !cancelled && won && incomplete.Length == 0,
                ["conceptionId"] = ReadString(priorConception, "conception_id", ""),
                ["motherId"] = ReadString(priorConception, "mother_id", ""),
                ["biologicalFatherId"] = ReadString(priorConception, "biological_father_id", ""),
                ["legalFatherId"] = ReadString(priorConception, "legal_father_id", ""),
                ["dueDay"] = ReadDouble(priorConception, "due_day", 0d)
            };
            if (incomplete.Length > 0)
            {
                result["rollSucceeded"] = true;
                result["followUpStatus"] = incomplete;
                result["error"] = "This stored player conception roll succeeded, but its follow-up could not be verified.";
            }
            return result;
        }

        private static bool IsPlayerConceptionFollowUp(Dictionary<string, object> action, Dictionary<string, object> conception)
        {
            var payload = TryParseStrictJsonObject(ReadString(action, "payload_json", ""));
            string id = ReadString(conception, "conception_id", "");
            return id.Length > 0 && ReadBool(payload, "playerInvolved", false)
                && ReadString(payload, "conceptionId", "") == id
                && ReadString(payload, "motherId", "") == ReadString(conception, "mother_id", "")
                && ReadString(payload, "biologicalFatherId", "") == ReadString(conception, "biological_father_id", "");
        }

        internal static Dictionary<string, object> FamilyConceptionReportApi(Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            string campaign = ReadString(payload, "campaignId", "default");
            string conception = ReadFirstString(payload, "conceptionId", "id");
            string status = ReadString(payload, "status", "active");
            string child = ReadFirstString(payload, "childId", "childHeroStringId");
            string requestedTimeline = ReadString(payload, "timelineId", "");
            bool nativeObservation = ReadString(payload, "contextSource", "").StartsWith("bannerlord_native", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(conception) || status == "born" && child.Length == 0 && !nativeObservation)
                return TestDict("ok", false, "error", "The birth report does not identify its observed outcome.");

            // Profile reads take the file lock. Sample before taking the SQL mutation lock.
            string reportedBiological = ReadString(payload, "biologicalFatherId", "");
            string reportedLegal = ReadString(payload, "legalFatherId", "");
            double? concealChance = status == "born" && child.Length != 0
                && !reportedLegal.Equals(reportedBiological, StringComparison.OrdinalIgnoreCase)
                ? ConcealmentChance(LoadDirectorHeroProfile(campaign, ReadString(payload, "motherId", ""), null))
                : null;
            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            EnsureRelationshipDirectorSchema(connection);
            using var transaction = connection.BeginTransaction();
            AcquirePostgreSqlTransactionMutationLock(connection, transaction);
            string activeTimeline = WorldSimulationService.ActiveWorldHistoryTimeline(connection);
            bool typed = payload.TryGetValue("sharedActivityGeneration", out object expected);
            if ((typed || requestedTimeline.Length > 0) && requestedTimeline != activeTimeline
                || typed && !DialogueService.ExactSharedActivityGenerationMatches(connection, expected))
                return TestDict("ok", false, "error", "The conception report belongs to a different save.");

            var key = TestDict("id", conception);
            var row = RelationshipsStore.SelectConceptions3(connection, key, transaction).SingleOrDefault();
            string serialized = Json.Serialize(payload);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (row == null && nativeObservation && requestedTimeline == activeTimeline)
            {
                RelationshipsStore.InsertOrIgnoreConceptions(connection, TestDict("id", conception, "mother", ReadString(payload, "motherId", ""),
                    "father", ReadString(payload, "biologicalFatherId", ""), "legal", ReadString(payload, "legalFatherId", ""),
                    "start", ReadDouble(payload, "conceptionDay", 0), "due", ReadDouble(payload, "dueDay", 0),
                    "status", "active", "payload", serialized, "ts", now), transaction);
                row = RelationshipsStore.SelectConceptions3(connection, key, transaction).SingleOrDefault();
            }
            if (row == null)
                return TestDict("ok", false, "error", "The reported conception was not recorded.");

            bool alreadyBorn = ReadString(row, "status", "") == "born";
            string recordedChild = ReadString(row, "child_id", "");
            if (alreadyBorn && (status != "born" || recordedChild != child))
                return TestDict("ok", false, "error", "A recorded birth cannot be replaced by another outcome.");
            if (status == "born")
            {
                if (!ReportBirthParentage(connection, transaction, row, payload, child, now, concealChance, out bool insertedParentage))
                    return TestDict("ok", false, "error", "The observed birth conflicts with recorded parentage.");
                if (alreadyBorn && !insertedParentage)
                {
                    transaction.Commit();
                    return ConceptionReportReceipt(campaign, activeTimeline, conception, status, child, payload, true);
                }
            }
            RelationshipsStore.UpdateConceptions5(connection,
                TestDict("status", status, "child", child, "payload", serialized, "ts", now, "id", conception), transaction);
            transaction.Commit();
            return ConceptionReportReceipt(campaign, activeTimeline, conception, status, child, payload, false);
        }

        private static Dictionary<string, object> ConceptionReportReceipt(string campaign, string timeline, string conception,
            string status, string child, Dictionary<string, object> payload, bool idempotent)
        {
            var result = TestDict("ok", true, "campaignId", campaign, "timelineId", timeline,
                "conceptionId", conception, "status", status, "childId", child);
            if (idempotent) result["idempotent"] = true;
            if (payload.TryGetValue("sharedActivityGeneration", out object expected) && expected is string generation)
                result["sharedActivityGeneration"] = generation;
            return result;
        }

        private static bool ReportBirthParentage(ReignDbConnection connection, ReignDbTransaction transaction,
            Dictionary<string, object> conception, Dictionary<string, object> payload, string child, long now,
            double? concealChance, out bool inserted)
        {
            inserted = false;
            string mother = ReadString(conception, "mother_id", "");
            string biological = ReadString(conception, "biological_father_id", "");
            string intendedLegal = ReadString(conception, "legal_father_id", "");
            string legal = ReadString(payload, "legalFatherId", "");
            string conceptionId = ReadString(conception, "conception_id", "");
            if (mother.Length == 0 || mother != ReadString(payload, "motherId", "")
                || biological != ReadString(payload, "biologicalFatherId", ""))
                return false;
            if (payload.TryGetValue("intendedLegalFatherId", out object intent)
                && (intent is not string recordedIntent || recordedIntent != intendedLegal)) return false;
            bool observedFallback = legal != intendedLegal;
            if (observedFallback && (!payload.ContainsKey("sharedActivityGeneration")
                || ReadString(payload, "contextSource", "") != "reign_managed" || legal != biological))
                return false;
            if (child.Length == 0) return !observedFallback; // A native delivery without a surviving child has no parentage row.

            var prior = RelationshipsStore.SelectParentage(connection, TestDict("child", child), transaction).SingleOrDefault();
            if (prior != null)
                return ReadString(prior, "conception_id", "") == conceptionId && ReadString(prior, "mother_id", "") == mother
                    && ReadString(prior, "biological_father_id", "") == biological && ReadString(prior, "legal_father_id", "") == legal;
            if (observedFallback && intent is not string
                && !MatchesLegacyManagedFallback(conception, conceptionId, mother, biological)) return false;

            bool differentFather = !legal.Equals(biological, StringComparison.OrdinalIgnoreCase);
            bool illegitimate = ReadBool(payload, "isIllegitimate", false)
                || !intendedLegal.Equals(biological, StringComparison.OrdinalIgnoreCase);
            if (!RelationshipsStore.TryInsertBirthParentage(connection, TestDict("child", child, "mother", mother, "bio", biological,
                    "legal", legal, "illegitimate", illegitimate ? 1 : 0, "revealed", ReadBool(payload, "fatherExposed", false) ? 1 : 0,
                    "conception", conceptionId, "payload", Json.Serialize(payload), "ts", now), transaction))
                return false;
            inserted = true;
            if (!illegitimate || !differentFather) return true;
            if (!concealChance.HasValue) return false;

            double chance = concealChance.Value;
            double roll = StableUnit(conceptionId + "|conceal_parentage");
            if (roll <= chance) return true;
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(new[] { conceptionId, child }))));
            RelationshipsStore.InsertDirectorOutbox(connection, TestDict("id", "parentage_" + identity,
                "day", ReadDouble(payload, "worldDay", 0d), "subjects", Json.Serialize(new[] { child, mother, biological }),
                "payload", Json.Serialize(TestDict("childId", child, "motherId", mother, "biologicalFatherId", biological,
                    "legalFatherId", legal, "concealChance", chance, "concealRoll", roll)), "ts", now), transaction);
            return true;
        }

        private static bool MatchesLegacyManagedFallback(Dictionary<string, object> conception, string id, string mother, string biological)
        {
            if (ReadString(conception, "status", "") != "active") return false;
            var activation = TryParseStrictJsonObject(ReadString(conception, "payload_json", ""));
            return ReadString(activation, "contextSource", "") == "reign_managed"
                && ReadString(activation, "status", "") == "active"
                && ReadString(activation, "conceptionId", "") == id
                && ReadString(activation, "motherId", "") == mother
                && ReadString(activation, "biologicalFatherId", "") == biological
                && ReadString(activation, "legalFatherId", "") == biological;
        }

        internal static Dictionary<string, object> FamilyParentageRevealApi(Dictionary<string, object> payload)
        {
            payload=payload??new Dictionary<string, object>();string campaignId=ReadString(payload,"campaignId","default"),childId=ReadFirstString(payload,"childId","childHeroStringId");double day=ReadDouble(payload,"worldDay",0d);
            using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> row=RelationshipsStore.SelectParentage(connection, new Dictionary<string, object>{{"child",childId}}).FirstOrDefault();
                if(row==null)return new Dictionary<string, object>{{"ok",false},{"error","Parentage record was not found."}};
                if(ReadInt(row,"revealed",0)==1)return new Dictionary<string, object>{{"ok",true},{"idempotent",true},{"childId",childId}};
                RelationshipsStore.UpdateParentage(connection, new Dictionary<string, object>{{"surname",ReadString(payload,"bastardSurname","Baseborn")},{"payload",Json.Serialize(payload)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"child",childId}});
                QueueDirectorAction(connection,"reveal_parentage",day,childId,ReadString(row,"biological_father_id",""),new Dictionary<string, object>{{"childId",childId},{"biologicalFatherId",ReadString(row,"biological_father_id","")},{"legalFatherId",ReadString(row,"legal_father_id","")},{"bastardSurname",ReadString(payload,"bastardSurname","Baseborn")}});
                DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>{{"campaignId",campaignId},{"eventType","parentage_revealed"},{"worldDay",day},{"summary","The concealed biological parentage of "+childId+" was publicly exposed."},{"participants",new[]{childId,ReadString(row,"mother_id",""),ReadString(row,"biological_father_id","")}},{"visibility","public"},{"importance",0.95d}},"parentage_reveal");
                return new Dictionary<string, object>{{"ok",true},{"childId",childId},{"queued",true}};
            }
        }

        internal static Dictionary<string, object> RelationshipDirectorSnapshotApi(Dictionary<string, object> payload)
        {
            return RunRelationshipDirector(payload ?? new Dictionary<string, object>(), false);
        }

        internal static Dictionary<string, object> RelationshipDirectorRunApi(Dictionary<string, object> payload)
        {
            return RunRelationshipDirector(payload ?? new Dictionary<string, object>(), true);
        }

        private static Dictionary<string, object> RunRelationshipDirector(Dictionary<string, object> payload, bool force)
        {
            string campaignId = ReadString(payload,"campaignId","default");
            double day = ReadDouble(payload,"worldDay",0d);
            string snapshotKind = ReadString(payload,"snapshotKind","");
            if(snapshotKind!="" && snapshotKind!="political_marriage_only_v1")
                return TestDict("ok",false,"error","Unsupported marriage snapshot kind.");
            // Relationship effects re-enter this monitor. Acquire it before any
            // database work so a waiting effect cannot retain table locks needed
            // by the worker that already owns the campaign monitor.
            using var ownership = PersistenceService.EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaignId));
            ownership.SetStage("director_schema");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureRelationshipDirectorSchema(connection);
                // Publish telemetry schema readiness only after its DDL commits,
                // before the period transaction can fail or roll back.
                WorldSimulationService.EnsureWorldTestTelemetrySchema(connection);
                string timeline=ReadString(payload,"timelineId","main");
                long period=Reign.Relationships.ArrangedMarriagePolicy.Period(day);
                RevalidateLegacyMarriageOffers(connection,timeline,day);
                ownership.SetStage("director_transaction");
                using (var transaction=connection.BeginTransaction())
                {
                if(RelationshipsStore.SelectArrangedMarriagePeriods2(connection, new Dictionary<string,object>{{"t",timeline},{"p",period}}).Any())
                    return new Dictionary<string,object>{{"ok",true},{"idempotent",true},{"periodIndex",period},{"nextDay",(period+1)*7d}};
                // A replayed capture is evaluated once. Missing uncaptured historical periods are not synthesized.
                ownership.SetStage("arranged_marriage_candidates");
                Dictionary<string, object> marriage = ProcessMarriageSystem(connection,campaignId,payload,day,force);
                ownership.SetStage("director_receipt");
                int candidateCount = ReadInt(marriage, "leaderRolls", 0);
                int eventCount = ReadInt(marriage, "arrangedCreated", 0);
                string runId = "director_run_" + Guid.NewGuid().ToString("N");
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Dictionary<string, object> compactInput = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["timelineId"] = ReadString(payload, "timelineId", "main"),
                    ["worldDay"] = day,
                    ["heroCount"] = snapshotKind == "political_marriage_only_v1"
                        ? ReadInt(payload, "ambientHeroCount", 0) : ReadDictionaryList(payload, "heroes").Count,
                    ["clanCount"] = ReadDictionaryList(payload, "clans").Count,
                    ["purpose"] = "political_marriage_only"
                };
                RelationshipsStore.InsertRelationshipDirectorRuns(connection, new Dictionary<string, object>{{"id",runId},{"campaign",campaignId},{"day",day},
                        {"candidates",candidateCount},{"events",eventCount},
                        {"input",Json.Serialize(compactInput)},{"output",Json.Serialize(marriage)},{"ts",ts}});
                RelationshipsStore.DeleteRelationshipDirectorRuns(connection);
                RelationshipsStore.InsertArrangedMarriagePeriods(connection, new Dictionary<string,object>{{"t",timeline},{"p",period},{"d",day},{"ts",ts}});
                ownership.SetStage("director_commit");
                transaction.Commit();
                return new Dictionary<string, object>{{"ok",true},{"runId",runId},{"periodIndex",period},{"nextDay",(period+1)*7d},
                    {"candidateCount",candidateCount},{"eventCount",eventCount},{"passiveMechanicalEventsEnabled",false},{"llmCalls",0},{"eventPolicy","political_marriage_only"},{"marriage",marriage}};
                }
            }
        }

        private static string ChoosePassiveEventKind(ReignDbConnection connection,string campaignId,Dictionary<string, object> edge,Dictionary<string, object> a,Dictionary<string, object> b,Dictionary<string, object> ra,Dictionary<string, object> rb,double day)
        {
            return ChoosePassiveEventKind(connection,campaignId,edge,a,b,ra,rb,
                BuildDirectorRomanticDecision(campaignId,a,b,edge,day),BuildDirectorRomanticDecision(campaignId,b,a,edge,day),day);
        }

        private static string ChoosePassiveEventKind(ReignDbConnection connection,string campaignId,Dictionary<string, object> edge,Dictionary<string, object> a,Dictionary<string, object> b,
            Dictionary<string, object> ra,Dictionary<string, object> rb,Dictionary<string, object> postureA,Dictionary<string, object> postureB,double day)
        {
            double affinity=Math.Max(0d,ReadDouble(edge,"ambientAffinity",0d));
            double tension=Math.Max(0d,ReadDouble(edge,"ambientTension",0d));
            double exposure=Math.Min(1d,ReadDouble(edge,"ambientExposure",0d)/30d);
            double ambition=Math.Max(0d,(TraitFromProfile(a,"ambition")+TraitFromProfile(b,"ambition")+TraitFromProfile(a,"pride")+TraitFromProfile(b,"pride"))/8d);
            string heroA=ReadString(a,"heroStringId",""),heroB=ReadString(b,"heroStringId","");
            double favorMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"favor_debt",day);
            double secretMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"shared_secret",day);
            double rivalryMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"rivalry",day);
            double maritalMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"marital_conflict",day);
            double romanceMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"romance",day);
            List<KeyValuePair<string,double>> eligible=new List<KeyValuePair<string,double>>();
            eligible.Add(new KeyValuePair<string,double>("practical_favor",(24d+14d*affinity+8d*exposure)*favorMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"favors","bargaining")));
            eligible.Add(new KeyValuePair<string,double>("shared_confidence",(18d+24d*affinity+12d*exposure)*secretMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"secrets","information")));
            if(ReadBool(edge,"sameClan",false)||ReadBool(edge,"family",false))eligible.Add(new KeyValuePair<string,double>("family_duty_cooperation",(20d+18d*affinity)*CourtService.CourtCharacterEventWeight(postureA,postureB,"protection","favors")));
            if(ReadBool(edge,"spouses",false))eligible.Add(new KeyValuePair<string,double>("private_spousal_confidence",(18d+22d*affinity)*CourtService.CourtCharacterEventWeight(postureA,postureB,"secrets","mediation")));
            double rivalryThread=PairStoryKindIntensity(connection,heroA,heroB,"rivalry",day,false);
            double maritalThread=PairStoryKindIntensity(connection,heroA,heroB,"marital_conflict",day,false);
            double romanceThread=PairStoryKindIntensity(connection,heroA,heroB,"romance",day,true);
            double competition=Math.Max(Math.Max(ReadDouble(ra,"rivalry",0d),ReadDouble(rb,"rivalry",0d)),
                Math.Max(Math.Max(ReadDouble(ra,"envy",0d),ReadDouble(rb,"envy",0d)),Math.Max(ReadDouble(ra,"resentment",0d),ReadDouble(rb,"resentment",0d))))/100d;
            if(tension>=0.025d||ambition>=0.25d||competition>=0.10d||rivalryThread>0d)
                eligible.Add(new KeyValuePair<string,double>(rivalryThread>=50d?"political_obstruction":"political_rivalry_argument",
                    (10d+45d*Math.Max(tension,competition)+20d*ambition)*rivalryMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"rivalry","threats","schemes")));
            if(ReadBool(edge,"spouses",false)&&(tension>=0.025d||competition>=0.10d||maritalThread>0d))
                eligible.Add(new KeyValuePair<string,double>("marital_argument",(8d+35d*Math.Max(tension,competition))*maritalMultiplier));
            if(ReadBool(edge,"spouses",false)&&(maritalThread>=60d||ReadDouble(ra,"resentment",0d)>=25d||ReadDouble(rb,"resentment",0d)>=25d||ReadDouble(edge,"nativeRelation",0d)<=-15d
                ||((ReadDouble(ra,"resentment",0d)>=15d||ReadDouble(rb,"resentment",0d)>=15d)&&(ReadDouble(ra,"affection",0d)<=0d||ReadDouble(rb,"affection",0d)<=0d))))
                eligible.Add(new KeyValuePair<string,double>("marital_separation",((maritalThread>=80d?45d:20d)+45d*tension)*maritalMultiplier));
            bool reciprocalRomance=new[]{"lovers","soulmates"}.Contains(ReadString(edge,"sharedRelationshipTag",""),StringComparer.OrdinalIgnoreCase);
            bool flirtEligible=reciprocalRomance&&ReadBool(edge,"coLocated",false)&&DirectorRomanceEligible(postureA,40d)&&DirectorRomanceEligible(postureB,40d);
            if(flirtEligible)eligible.Add(new KeyValuePair<string,double>("mutual_romantic_flirtation",(16d+12d*exposure)*romanceMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"flirtation","strategic_seduction")));
            bool confidenceEligible=reciprocalRomance&&DirectorRomanceEligible(postureA,40d)&&DirectorRomanceEligible(postureB,40d);
            if(confidenceEligible&&romanceThread>=25d)eligible.Add(new KeyValuePair<string,double>("romantic_confidence",(14d+10d*exposure)*romanceMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"flirtation","strategic_seduction","secrets")));
            bool outsideMarriage=(!string.IsNullOrWhiteSpace(ReadString(a,"spouseId",""))&&!ReadString(a,"spouseId","").Equals(heroB,StringComparison.OrdinalIgnoreCase))
                ||(!string.IsNullOrWhiteSpace(ReadString(b,"spouseId",""))&&!ReadString(b,"spouseId","").Equals(heroA,StringComparison.OrdinalIgnoreCase));
            bool bothUnmarried=string.IsNullOrWhiteSpace(ReadString(a,"spouseId",""))&&string.IsNullOrWhiteSpace(ReadString(b,"spouseId",""));
            if(reciprocalRomance&&StoryPhysicalRomanceEligible(ReadBool(edge,"coLocated",false),bothUnmarried,romanceThread,50d,postureA,postureB))
                eligible.Add(new KeyValuePair<string,double>("romantic_intimacy",(12d+8d*exposure)*romanceMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"strategic_seduction","flirtation")));
            if(reciprocalRomance&&StoryPhysicalRomanceEligible(ReadBool(edge,"coLocated",false),outsideMarriage,romanceThread,75d,postureA,postureB))
                eligible.Add(new KeyValuePair<string,double>("secret_affair_intimacy",(10d+8d*exposure)*romanceMultiplier*CourtService.CourtCharacterEventWeight(postureA,postureB,"strategic_seduction")));
            return SelectWeightedPassiveEvent(eligible,ReadString(edge,"pairKey","")+"|"+Math.Floor(day/DirectorRunIntervalDays));
        }

        private static string SelectWeightedPassiveEvent(List<KeyValuePair<string,double>> eligible,string seed)
        {
            if(eligible==null||eligible.Count==0)return "practical_favor";
            double total=eligible.Sum(x=>Math.Max(0d,x.Value));
            if(total<=0d)return eligible[0].Key;
            double roll=StableUnit(seed)*total;
            foreach(KeyValuePair<string,double> option in eligible){roll-=Math.Max(0d,option.Value);if(roll<=0d)return option.Key;}
            return eligible[eligible.Count-1].Key;
        }

        private static Dictionary<string, object> BuildDirectorRomanticDecision(string campaignId,Dictionary<string, object> actor,Dictionary<string, object> other,Dictionary<string, object> edge,double day)
        {
            string actorId=ReadString(actor,"heroStringId",""),otherId=ReadString(other,"heroStringId","");
            bool closeKin=ReadString(actor,"fatherId","")==otherId||ReadString(actor,"motherId","")==otherId||ReadString(other,"fatherId","")==actorId||ReadString(other,"motherId","")==actorId;
            bool adults=ReadDouble(actor,"age",0d)>=18d&&ReadDouble(other,"age",0d)>=18d;
            bool nativeSuitable=adults&&ReadBool(actor,"isFemale",false)!=ReadBool(other,"isFemale",false)&&!closeKin;
            Dictionary<string, object> payload=new Dictionary<string, object>
            {
                ["playerHeroStringId"]=otherId,["worldDay"]=day,["mode"]="autonomous_relationship",
                ["conversationSessionId"]="director:"+Math.Floor(day).ToString(CultureInfo.InvariantCulture)+":"+ReadString(edge,"pairKey",actorId+"|"+otherId),
                ["opportunitySnapshot"]=new Dictionary<string, object>
                {
                    ["identityKnown"]=true,["observer"]=actor,["target"]=other,
                    ["suitability"]=new Dictionary<string, object>{{"adults",adults},{"nativeSuitable",nativeSuitable},{"closeKin",closeKin},{"marriageBlocks",false}},
                    ["scene"]=new Dictionary<string, object>{{"private",true},{"exposure",0.12d},{"coercive",false},{"witnessIds",new List<string>()}}
                },
                ["sceneOpportunity"]=new Dictionary<string, object>{{"private",true},{"exposure",0.12d},{"coercive",false},{"witnessIds",new List<string>()}}
            };
            Dictionary<string, object> context=DialogueService.BuildConversationDecisionContext(campaignId,"autonomous_relationship",actorId,actor,CharactersService.LoadCharacterStack(campaignId,actorId),new Dictionary<string, object>(),"","A private autonomous social opportunity.",payload,new Dictionary<string, object>{{"identityState","known"}});
            return ReadDictionary(context,"romance")??new Dictionary<string, object>();
        }

        private static bool DirectorRomanceEligible(Dictionary<string, object> romance,double minimumReceptivity)
        {
            Dictionary<string, object> hard=ReadDictionary(romance,"hardConstraints")??new Dictionary<string, object>();
            return ReadBool(hard,"adults",false)&&ReadBool(hard,"nativeSuitable",false)&&!ReadBool(hard,"closeKin",true)&&!ReadBool(hard,"coercive",true)
                &&Math.Max(ReadDouble(romance,"genuineInterest",0d),ReadDouble(romance,"strategicInterest",0d))>=25d
                &&ReadDouble(romance,"receptivity",0d)>=minimumReceptivity;
        }

        private static string PassiveEventSummary(string kind,string a,string b)
        {
            switch(kind){case "secret_affair_intimacy":return a+" and "+b+" consummated a secret affair after knowingly accepting the personal and political risk.";case "romantic_intimacy":return a+" and "+b+" privately deepened their consensual courtship into romantic intimacy.";case "romantic_confidence":return a+" and "+b+" shared a private romantic confidence and acknowledged their growing interest.";case "mutual_romantic_flirtation":return a+" and "+b+" exchanged mutual flirtation and tested a growing attraction.";case "political_obstruction":return a+" and "+b+" deliberately obstructed one another's ambitions, escalating their political rivalry.";case "political_rivalry_argument":return a+" and "+b+" argued over status and ambition, sharpening their political rivalry.";case "marital_argument":return a+" and "+b+" suffered a serious marital argument that deepened the strain between them.";case "marital_separation":return a+" and "+b+" separated after a bitter marital conflict and sustained resentment.";case "marriage_proposal":return a+" and "+b+" made a serious mutual marriage proposal after sustained trust, affection, and attraction.";case "private_spousal_confidence":return a+" trusted "+b+" with a private confidence, strengthening affection and loyalty.";case "family_duty_cooperation":return a+" and "+b+" cooperated on a family duty and helped one another.";case "shared_confidence":return a+" trusted "+b+" with a personal confidence and received a respectful hearing.";default:return a+" performed a practical favor for "+b+", who offered sincere thanks.";}
        }

        private static Dictionary<string, object> ProcessMarriageSystem(ReignDbConnection connection,string campaignId,Dictionary<string, object> payload,double day,bool force)
        {
            return ProcessArrangedMarriageSystem(connection,campaignId,payload,day);
        }

        private static bool RomanticMarriageReady(Dictionary<string, object> a,Dictionary<string, object> b)
        {
            if(a==null||b==null)return false;
            return ReadDouble(a,"affection",0d)>=70d&&ReadDouble(b,"affection",0d)>=70d&&ReadDouble(a,"trust",0d)>=55d&&ReadDouble(b,"trust",0d)>=55d
                &&ReadDouble(a,"attraction",0d)>=45d&&ReadDouble(b,"attraction",0d)>=45d&&ReadDouble(a,"resentment",0d)<70d&&ReadDouble(b,"resentment",0d)<70d;
        }

        private static bool SnapshotMarriageEligible(Dictionary<string, object> a,Dictionary<string, object> b)
        {
            if(a==null||b==null||ReadString(a,"heroStringId","")==ReadString(b,"heroStringId",""))return false;
            if(!ReadBool(a,"isAlive",false)||!ReadBool(b,"isAlive",false)||ReadDouble(a,"age",0d)<18d||ReadDouble(b,"age",0d)<18d)return false;
            if(ReadBool(a,"isFemale",false)==ReadBool(b,"isFemale",false)||!string.IsNullOrWhiteSpace(ReadString(a,"spouseId",""))||!string.IsNullOrWhiteSpace(ReadString(b,"spouseId","")))return false;
            if((a.ContainsKey("nativeCanMarry")&&!ReadBool(a,"nativeCanMarry",false))
                ||(b.ContainsKey("nativeCanMarry")&&!ReadBool(b,"nativeCanMarry",false))
                ||(a.ContainsKey("nativeMarriageClanSuitable")&&!ReadBool(a,"nativeMarriageClanSuitable",false))
                ||(b.ContainsKey("nativeMarriageClanSuitable")&&!ReadBool(b,"nativeMarriageClanSuitable",false))
                ||(ReadBool(a,"isClanLeader",false)&&ReadBool(b,"isClanLeader",false)))return false;
            string aId=ReadString(a,"heroStringId",""),bId=ReadString(b,"heroStringId","");
            HashSet<string> ancestorsA=new HashSet<string>(ReadStringList(a,"marriageAncestorIds"),StringComparer.OrdinalIgnoreCase);
            HashSet<string> ancestorsB=new HashSet<string>(ReadStringList(b,"marriageAncestorIds"),StringComparer.OrdinalIgnoreCase);
            bool extendedKin=ancestorsA.Contains(bId)||ancestorsB.Contains(aId)||ancestorsA.Overlaps(ancestorsB);
            return !extendedKin&&!MarriageCourtshipBlocks(a,b)&&!MarriageCourtshipBlocks(b,a)
                &&ReadString(a,"fatherId","")!=bId&&ReadString(a,"motherId","")!=bId&&ReadString(b,"fatherId","")!=aId&&ReadString(b,"motherId","")!=aId
                &&!(ReadString(a,"fatherId","")!=""&&ReadString(a,"fatherId","")==ReadString(b,"fatherId",""))&&!(ReadString(a,"motherId","")!=""&&ReadString(a,"motherId","")==ReadString(b,"motherId",""));
        }

        private static bool MarriageCourtshipBlocks(Dictionary<string, object> subject,Dictionary<string, object> candidate)
        {
            string candidateId=ReadString(candidate,"heroStringId",""),candidateClan=ReadString(candidate,"clanId","");
            if(string.IsNullOrWhiteSpace(candidateClan))return false;
            return ReadDictionaryList(subject,"activeCourtships").Any(x=>
                ReadString(x,"clanId","").Equals(candidateClan,StringComparison.OrdinalIgnoreCase)
                &&!ReadString(x,"heroId","").Equals(candidateId,StringComparison.OrdinalIgnoreCase));
        }

        private static double MarriageUrgency(Dictionary<string, object> clan,Dictionary<string, object> leader,List<Dictionary<string, object>> members)
        {
            int children=members.Sum(x=>ReadInt(x,"childrenCount",0));
            double agePressure=members.Count==0?0d:members.Average(x=>DialogueService.ClampDouble((ReadDouble(x,"age",18d)-18d)/25d,0d,1d));
            double succession=DialogueService.ClampDouble((members.Count==1?0.45d:0.15d)+(children==0?0.30d:0d)+0.25d*agePressure,0d,1d);
            double isolation=DialogueService.ClampDouble(0.55d-ReadDouble(clan,"tier",0d)/12d-ReadDouble(clan,"gold",0d)/1000000d,0d,1d);
            double traits=(Trait01(leader,"ambition")+Trait01(leader,"dutyMotivation")+Trait01(leader,"traditionalism")+Trait01(leader,"familyMotivation")+Trait01(leader,"powerMotivation")+Trait01(leader,"pragmatism")+Trait01(leader,"riskTolerance"))/7d;
            return DialogueService.ClampDouble(0.45d*succession+0.25d*isolation+0.30d*traits,0d,1d);
        }

        private static double DynasticMarriageScore(Dictionary<string, object> own,Dictionary<string, object> other,Dictionary<string, object> leader,Dictionary<string, object> member,Dictionary<string, object> candidate)
        {
            double standing=DialogueService.ClampDouble(ReadDouble(other,"tier",0d)/6d,0d,1d)*18d+DialogueService.ClampDouble(ReadDouble(other,"renown",0d)/6000d,0d,1d)*10d;
            double resources=DialogueService.ClampDouble(ReadDouble(other,"gold",0d)/500000d,0d,1d)*10d+DialogueService.ClampDouble(ReadDouble(other,"influence",0d)/1000d,0d,1d)*8d;
            double compatibility=ReadString(own,"kingdomId","")==ReadString(other,"kingdomId","")?14d:8d;
            if(ReadString(own,"cultureId","")==ReadString(other,"cultureId",""))compatibility+=8d;
            double household=12d*(ReadInt(candidate,"childrenCount",0)==0?1d:0.5d)+8d*(1d-DialogueService.ClampDouble(Math.Abs(ReadDouble(member,"age",25d)-ReadDouble(candidate,"age",25d))/30d,0d,1d));
            double strategy=12d*Trait01(leader,"ambition")+10d*Trait01(leader,"familyMotivation")+8d*Trait01(leader,"pragmatism");
            return DialogueService.ClampDouble(standing+resources+compatibility+household+strategy,0d,100d);
        }

        private static double ArrangedMarriageResentment(Dictionary<string, object> spouse,Dictionary<string, object> match,Dictionary<string, object> leader,double politicalValue)
        {
            double resistance=0.20d*Trait01(spouse,"assertiveness")+0.16d*Trait01(spouse,"pride")+0.12d*Trait01(spouse,"impulsiveness")+0.12d*(1d-Trait01(spouse,"traditionalism"));
            double acceptance=0.16d*Trait01(spouse,"dutyMotivation")+0.14d*Trait01(spouse,"familyMotivation")+0.10d*Trait01(spouse,"loyalty")+0.10d*Trait01(spouse,"pragmatism")+0.08d*Trait01(spouse,"ambition");
            double relation=DialogueService.ClampDouble((ReadDouble(spouse,"nativeRelationToLeader",0d)+100d)/200d,0d,1d);
            double personal=0.10d*Trait01(spouse,"attraction")+0.08d*Trait01(spouse,"socialTrust");
            return Math.Round(DialogueService.ClampDouble(15d+65d*resistance-55d*acceptance-15d*relation-10d*personal-10d*(politicalValue/100d),0d,60d),2);
        }

        private static bool MarriagePairAlreadyActive(ReignDbConnection connection,string timelineId,string a,string b)
        {
            return RelationshipsStore.SelectMarriageEvaluations11(connection, new Dictionary<string, object>{{"timeline",timelineId},{"a",a},{"b",b}}).Any();
        }

        private static void CreateMarriageEvaluation(ReignDbConnection connection,string campaignId,string timelineId,string route,string status,double day,Dictionary<string, object> a,Dictionary<string, object> b,Dictionary<string, object> clanA,Dictionary<string, object> clanB,double score,double resentmentA,double resentmentB,Dictionary<string, object> detail)
        {
            string aId=ReadString(a,"heroStringId",""),bId=ReadString(b,"heroStringId","");long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string id="marriage_eval_"+Guid.NewGuid().ToString("N");
            string clanAId=ReadString(a,"clanId",ReadString(clanA,"clanId","")),clanBId=ReadString(b,"clanId",ReadString(clanB,"clanId",""));
            string leaderA=ReadString(clanA,"leaderId",aId),leaderB=ReadString(clanB,"leaderId",bId);
            Dictionary<string, object> data=new Dictionary<string, object>(detail??new Dictionary<string, object>()){{"evaluationId",id},{"route",route},{"heroA",a},{"heroB",b},{"clanA",clanA??new Dictionary<string, object>()},{"clanB",clanB??new Dictionary<string, object>()},{"resentmentA",resentmentA},{"resentmentB",resentmentB}};
            RelationshipsStore.InsertMarriageEvaluations(connection, new Dictionary<string, object>{{"aa",ReadDouble(detail,"leaderAApproval",100)>=25?1:0},{"ba",ReadDouble(detail,"leaderBApproval",100)>=25?1:0},{"id",id},{"route",route},{"status",status},{"a",aId},{"b",bId},{"ca",clanAId},{"cb",clanBId},{"la",leaderA},{"lb",leaderB},{"day",day},{"score",score},{"ra",resentmentA},{"rb",resentmentB},{"payload",Json.Serialize(data)},{"ts",ts},{"timeline",timelineId}});
            bool diplomatic=ReadBool(detail,"diplomaticPackageRequired",false);
            string funnel=diplomatic?"diplomatic":"arranged";
            WorldSimulationService.RecordWorldTestCounter(connection,campaignId,timelineId,(int)Math.Floor(day),"marriages",
                funnel+"_evaluation_"+id,new Dictionary<string, object>
                {
                    {funnel+"Evaluations",1},
                    {funnel+"Accepted",status=="approved"?1:0},
                    {funnel+"PlayerCourtPending",status=="player_court_pending"?1:0},
                    {funnel+"AwaitingDiplomacy",status=="awaiting_diplomacy"?1:0}
                });
            if(status=="refused")
            {
                string refusalKey=ReadString(detail,"refusalKey",id);
                RelationshipsStore.InsertOrIgnoreArrangedMarriageRefusals(connection, new Dictionary<string,object>{{"k",refusalKey},{"t",timelineId},{"p",id}});
                ApplyMarriageLeaderEffect(connection,campaignId,timelineId,id,leaderA,leaderB,-10,day);
                WorldSimulationService.RecordWorldTestCounter(connection,campaignId,timelineId,(int)Math.Floor(day),"marriages","refusal_"+id,new Dictionary<string,object>{{"arrangedRefused",1},{"refused:"+ReadString(detail,"refusalReason","approval_too_low"),1}});
            }
            if(status=="approved" || status=="player_offer_pending")
            {
                string actionId=QueueDirectorAction(connection,status=="approved" ? "marriage" : "player_marriage_offer",day,aId,bId,new Dictionary<string, object>{{"evaluationId",id},{"route",route},{"timelineId",timelineId},{"marriageTerms",ReadDictionary(detail,"marriageTerms")},{"resentmentA",resentmentA},{"resentmentB",resentmentB},{"leaderAId",leaderA},{"leaderBId",leaderB}});
                if(string.IsNullOrWhiteSpace(actionId))
                    RelationshipsStore.UpdateMarriageEvaluations6(connection, new Dictionary<string, object>{{"ts",ts},{"id",id}});
            }
        }

        private static double ConcealmentChance(Dictionary<string, object> mother)
        {
            double motivation=0.20d*Trait01(mother,"shame")+0.15d*Trait01(mother,"familyMotivation")+0.15d*Trait01(mother,"traditionalism")+0.10d*Trait01(mother,"dutyMotivation")+0.10d*Trait01(mother,"survivalMotivation")+0.10d*Trait01(mother,"religionMotivation")+0.20d*(1d-Trait01(mother,"honesty"));
            double competence=0.25d*Trait01(mother,"tact")+0.20d*Trait01(mother,"discipline")+0.15d*Trait01(mother,"emotionalStability")+0.15d*Trait01(mother,"pragmatism")+0.15d*Trait01(mother,"confidence")+0.10d;
            return DialogueService.ClampDouble(0.15d+0.35d*motivation+0.40d*competence-0.10d,0.05d,0.95d);
        }

        internal static Dictionary<string, object> LoadDirectorHeroProfile(string campaignId,string heroId,Dictionary<string, object> snapshot)
        {
            Dictionary<string, object> result = snapshot == null ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object>(snapshot,StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> stored = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId,heroId,"profile.json"));
            foreach(var pair in stored) if(!result.ContainsKey(pair.Key) || string.IsNullOrWhiteSpace(Convert.ToString(result[pair.Key],CultureInfo.InvariantCulture))) result[pair.Key]=pair.Value;
            Dictionary<string, object> traits = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId,heroId,"traits.json"));
            Dictionary<string, object> foundation=ReadDictionary(traits,"foundationTraits")??ReadDictionary(result,"foundationTraits");
            if(foundation==null||foundation.Count==0)foundation=ReadDictionary(CharactersService.BuildTraitDocument(result),"foundationTraits")??new Dictionary<string, object>();
            result["foundationTraits"] = foundation;
            result["traits"] = traits;
            result["heroStringId"] = heroId;
            return result;
        }

        private static string QueueDirectorAction(ReignDbConnection connection,string type,double day,string actor,string target,Dictionary<string, object> payload)
        {
            payload["scheduledWorldDay"]=day;
            string id="director_action_"+Guid.NewGuid().ToString("N");
            if (type.Equals("marriage", StringComparison.OrdinalIgnoreCase))
            {
                EnsureRelationshipLifecycleSchema(connection);
                string pairKey = AmbientPairKey(actor, target);
                long reservationTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                RelationshipsStore.InsertOrIgnoreRelationshipMarriageReservations(connection, new Dictionary<string, object>
                    {
                        ["actor"] = actor, ["target"] = target,
                        ["pair"] = pairKey, ["action"] = id,
                        ["day"] = day, ["ts"] = reservationTs
                    });
                int reserved = ReadInt(RelationshipsStore.CountRelationshipMarriageReservations(connection, new Dictionary<string, object> { ["action"] = id })
                    .FirstOrDefault(), "count", 0);
                if (reserved != 2)
                {
                    RelationshipsStore.DeleteRelationshipMarriageReservations3(connection, new Dictionary<string, object> { ["action"] = id });
                    return "";
                }
            }
            InsertDirectorAction(connection, id, type, day, actor, target, payload);
            return id;
        }

        private static void InsertDirectorAction(ReignDbConnection connection, string id, string type, double day,
            string actor, string target, Dictionary<string, object> payload, ReignDbTransaction transaction = null)
        {
            RelationshipsStore.InsertRelationshipDirectorActions2(connection, new Dictionary<string, object>
            {
                ["id"] = id, ["type"] = type, ["day"] = day, ["actor"] = actor, ["target"] = target,
                ["payload"] = Json.Serialize(payload), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, transaction);
        }

        private static Dictionary<string, object> ReadRelationshipDirectorOwner(Dictionary<string, object> payload)
        {
            if (payload == null || !payload.TryGetValue("ownership", out object raw)
                || !(raw is Dictionary<string, object> scope)
                || !scope.TryGetValue("campaignId", out object campaignValue) || !(campaignValue is string campaign)
                || string.IsNullOrWhiteSpace(campaign) || !payload.TryGetValue("campaignId", out object envelope)
                || !(envelope is string outerCampaign) || outerCampaign != campaign
                || !scope.TryGetValue("timelineId", out object timelineValue) || !(timelineValue is string timeline)
                || string.IsNullOrWhiteSpace(timeline)
                || !scope.TryGetValue("sharedActivityGeneration", out object generation) || !(generation is string))
                return null;
            return TestDict("campaignId", campaign, "timelineId", timeline, "sharedActivityGeneration", generation);
        }

        private static Dictionary<string, object> RelationshipDirectorOwnerError() =>
            TestDict("ok", false, "error", "The relationship director save owner did not match.");

        private static bool RelationshipDirectorTimelineMatches(Dictionary<string, object> actionPayload, string timeline) =>
            actionPayload != null && (!actionPayload.TryGetValue("timelineId", out object tagged)
                || tagged is string rowTimeline && rowTimeline == timeline);

        internal static Dictionary<string, object> RelationshipDirectorPollActionsApi(Dictionary<string, object> payload)
        {
            bool typed = payload?.ContainsKey("ownership") == true;
            var owner = typed ? ReadRelationshipDirectorOwner(payload) : null;
            if (typed && owner == null) return RelationshipDirectorOwnerError();

            string campaign = typed ? (string)owner["campaignId"] : ReadString(payload, "campaignId", "default");
            string timeline = typed ? (string)owner["timelineId"] : "";
            using var timelineScope = typed ? WorldSimulationService.EnterWorldHistoryTimelineScope(campaign) : null;
            using var connection = typed
                ? ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign, trackCampaignMutations: true)
                : ReignDatabase.OpenCampaignConnection(campaign);
            if (connection == null) return RelationshipDirectorOwnerError();
            using var transaction = connection.BeginTransaction();
            if (typed)
            {
                AcquirePostgreSqlTransactionMutationLock(connection, transaction);
                if (!WorldSimulationService.NoticeAcknowledgementOwnerMatches(connection, transaction, owner, campaign, timeline))
                    return RelationshipDirectorOwnerError();
                if (!WorldSimulationService.TableExists(connection, "relationship_director_actions"))
                    return TestDict("ok", true, "actions", new List<Dictionary<string, object>>(), "ownership", owner);
            }
            else
                EnsureRelationshipDirectorSchema(connection);

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var candidates = new List<Dictionary<string, object>>();
            const int limit = 50;
            int offset = 0;
            while (candidates.Count < limit)
            {
                var page = RelationshipsStore.SelectRelationshipDirectorActions10(connection,
                    TestDict("stale", ts - 300, "offset", offset), transaction);
                foreach (var row in page)
                {
                    string json = ReadString(row, "payload_json", "{}");
                    var actionPayload = typed ? TryParseStrictJsonObject(json)
                        : TryParseJsonObject(json) ?? new Dictionary<string, object>();
                    string id = ReadString(row, "director_action_id", "");
                    if (typed && (string.IsNullOrWhiteSpace(id) || IsRelationshipNativeTargetId(id)
                        || !RelationshipDirectorTimelineMatches(actionPayload, timeline))) continue;
                    row["payload"] = actionPayload;
                    candidates.Add(row);
                    if (candidates.Count == limit) break;
                }
                if (!typed || page.Count < limit) break;
                offset += page.Count;
            }

            var rows = new List<Dictionary<string, object>>(candidates.Count);
            foreach (var row in candidates)
            {
                if (RelationshipsStore.TryClaimRelationshipDirectorAction(connection,
                    TestDict("ts", ts, "stale", ts - 300, "id", ReadString(row, "director_action_id", "")), transaction))
                    rows.Add(row);
            }
            transaction.Commit();
            var result = TestDict("ok", true, "actions", rows);
            if (typed) result["ownership"] = owner;
            return result;
        }

    }
}
