using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',last_native_sync_day=$day,updated_ts=$ts
WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipNativeTargets5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets SET
status='failed',claimed_ts=0,last_error=$error,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=$ab,affinity_b_to_a=$ba,effective_affinity_a_to_b=$effectiveAB,
effective_affinity_b_to_a=$effectiveBA,tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,
projected_native_relation=$projected,native_action_pending=$pending,
native_action_id=$action,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpsertRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,
last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision,requires_observation)
VALUES($pair,$a,$b,$target,$observed,'pending',$day,-1000,0,0,'',$ts,1,$requiresObservation)
ON CONFLICT(pair_key) DO UPDATE SET target_relation=$target,status='pending',
requires_observation=CASE WHEN relationship_native_targets.requires_observation=1 OR $requiresObservation=1 THEN 1 ELSE 0 END,
world_day=$day,claimed_ts=0,last_error='',revision=CASE WHEN
relationship_native_targets.target_relation=$target THEN
relationship_native_targets.revision ELSE relationship_native_targets.revision+1 END,
updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipConceptionCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_conception_commitments WHERE conception_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipConceptionCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status='conception_failed',updated_ts=$ts WHERE conception_id=$id;", parameters, transaction);
        }

        internal static void UpdateRelationshipConceptionCommitments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status=$status,marriage_action_id=$action,updated_ts=$ts WHERE conception_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT status FROM relationship_director_actions
WHERE director_action_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipConceptionCommitments3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status='marriage_failed',updated_ts=$ts WHERE conception_id=$id
AND status='awaiting_marriage';", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='invalid',resolved_ts=$ts,result_json=$result
WHERE director_action_id=$id AND status IN ('pending','claimed');", parameters, transaction);
        }

        internal static void DeleteRelationshipMarriageReservations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_marriage_reservations
WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET
lover_active=0,affair_active=0,romance_stage='',romance_positive_bonus=0,
married=0,marriage_action_id='',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conceptions WHERE conception_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateConceptions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conceptions SET secrecy=0.75,
payload_json=$payload,updated_ts=$ts WHERE conception_id=$id;", parameters, transaction);
        }

        internal static void UpdateRelationshipConceptionCommitments4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status='broke_up',affinity_delta=$delta,updated_ts=$ts WHERE conception_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipConceptionCommitments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pair_key,status FROM relationship_conception_commitments
WHERE conception_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipConceptionCommitments5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_conception_commitments SET
status='married',affinity_delta=$delta,updated_ts=$ts WHERE conception_id=$id
AND status='awaiting_marriage';", parameters, transaction);
        }

        internal static void UpdateConceptions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conceptions SET payload_json=$payload,
updated_ts=$ts WHERE conception_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipAffairCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM relationship_affair_commitments WHERE divorce_action_a_id=$id
OR divorce_action_b_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipAffairCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_affair_commitments
SET status='divorce_failed',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipAffairCommitments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_affair_commitments
SET divorce_a_completed=1,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipAffairCommitments3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_affair_commitments
SET divorce_b_completed=1,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET
married=0,divorced=1,divorce_day=$day,divorce_action_id='',
divorce_rumor_id=$rumor,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
shared_tag='divorced',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipAffairCommitments4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_affair_commitments SET
status=$status,marriage_action_id=$action,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_director_actions WHERE director_action_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_director_actions SET status=$status,result_json=$result WHERE director_action_id=$id AND status IN ('pending','claimed','wedding_pending','wedding_offer_pending');", parameters, transaction);
        }

        internal static void UpdateMarriageEvaluations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE marriage_evaluations SET status='wedding_pending' WHERE evaluation_id=$id AND status='approved';", parameters, transaction);
        }

        internal static void UpdateMarriageEvaluations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE marriage_evaluations SET status=$status WHERE evaluation_id=$id AND status='player_offer_pending';", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_director_actions SET status='pending',claimed_ts=0 WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_director_actions SET status=$status,resolved_ts=$ts,result_json=$result WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET married=0,divorced=1,
divorce_day=$day,divorce_action_id='',divorce_rumor_id=$rumor,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET shared_tag='divorced',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_lifecycle SET divorce_action_id='',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT route,status,payload_json,timeline_id FROM marriage_evaluations WHERE evaluation_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateMarriageEvaluations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE marriage_evaluations SET status=$status,updated_ts=$ts WHERE evaluation_id=$id;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET married=1,divorced=0,
marriage_action_id='',marriage_blocked=0,marriage_block_reason='',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET shared_tag='married',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET
marriage_action_id='',marriage_blocked=1,marriage_block_reason=$reason,updated_ts=$ts
WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_lifecycle SET marriage_action_id='',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipConceptionCommitments3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT conception_id
FROM relationship_conception_commitments WHERE marriage_action_id=$action
AND status='awaiting_marriage' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipConceptionCommitments4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM relationship_conception_commitments WHERE conception_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipConceptionCommitments6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_conception_commitments SET
status='marriage_failed',updated_ts=$ts WHERE conception_id=$id;", parameters, transaction);
        }

        internal static void UpdateRelationshipAffairCommitments5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_affair_commitments SET
status=$status,updated_ts=$ts WHERE pair_key=$pair AND status='awaiting_marriage';", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_lifecycle SET conception_action_id='',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateConceptions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conceptions SET status=$status,updated_ts=$ts
WHERE conception_id=$id AND status='pending_game';", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET native_action_pending=0,native_action_id='',last_native_sync_day=$day,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void DeleteRelationshipDirectorActions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_director_actions WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET native_action_pending=0,native_action_id='',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations
SET status='resolved_by_divorce',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id IN ($a,$b) AND tag_id='marital_strife' AND status='active';", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='resolved_by_divorce',updated_ts=$ts
WHERE occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences
 WHERE campaign_id=$campaign AND timeline_id=$timeline
 AND archetype_id='marital_strife' AND thread_key=$pair)
AND subject_id IN ($a,$b) AND status='active';", parameters, transaction);
        }

        internal static void UpdateRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_occurrences SET status='resolved_by_divorce',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='marital_strife' AND thread_key=$pair AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM marriage_evaluations WHERE evaluation_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM marriage_evaluations WHERE evaluation_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreComprehension(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO comprehension(comprehension_id,event_id,owner_id,text,stance,confidence,known_by_json,about_entities_json,hidden_from_json,ts,payload_json)
VALUES($id,$event,$owner,$text,'private_response',0.9,$known,$about,'[]',$ts,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_director_runs ORDER BY world_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDirectorActions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_director_actions WHERE status='pending';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM marriage_evaluations WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 50;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageLeaderRolls4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM marriage_leader_rolls WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 50;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConceptionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM conception_attempts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conception_attempts WHERE attempt_id='attempt_preview_cancel' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPressures2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_pressures WHERE ((subject_id='story_a' AND target_id='story_b' AND kind='divided_loyalty') OR (subject_id='story_b' AND target_id='story_a' AND kind='romantic_displacement')) AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipStoryThreads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(DISTINCT pair_key) AS count FROM relationship_story_threads WHERE kind='rivalry' AND intensity>=75;", parameters, transaction);
        }

        internal static void InsertRelationshipPressures(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pressures(pressure_id,subject_id,target_id,kind,status,intensity,secrecy,summary,related_entities_json,trigger_json,evidence_json,created_day,updated_day,updated_ts,payload_json)
VALUES('story_pressure','story_c','story_d','strategic_seduction','active',60,.8,'','[]','{}','[]',40,55,$ts,'{}');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_relationship_director_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT director_action_id,payload_json,result_json
FROM relationship_director_actions
WHERE action_type='start_conception' AND status='failed';", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
UPDATE relationship_director_actions SET status='obsolete'
WHERE director_action_id=$id AND status='failed';", parameters, transaction);
        }

        internal static void UpdateConceptions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
UPDATE conceptions SET status='obsolete_game',updated_ts=$ts
WHERE conception_id=$id AND status='pending_game';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT director_action_id,world_day,actor_id,target_id,payload_json,result_json
FROM relationship_director_actions
WHERE action_type='marriage' AND status='completed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipObservedHeroes2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(last_observed_day) AS latest
FROM relationship_observed_heroes;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipObservedHeroes3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id,profile_json,last_observed_day
FROM relationship_observed_heroes;", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='invalid',result_json=$result WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static void DeleteRelationshipIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_incidents
WHERE kind='marriage' AND payload_json LIKE $needle;", parameters, transaction);
        }

        internal static void DeleteEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM events WHERE event_id=$event;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT lover_active FROM relationship_pair_lifecycle
WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET
married=0,marriage_action_id='',marriage_blocked=1,
marriage_block_reason=$reason,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
shared_tag=$tag,updated_ts=$ts WHERE pair_key=$pair AND shared_tag='married';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO schema_meta(key,value)
VALUES('campaign_compaction_required','1');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO schema_meta(key,value)
VALUES($key,$value);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDirectorRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_director_runs WHERE length(input_json)>65536;", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_director_runs SET input_json='{\"legacyPayloadPruned\":true}' WHERE length(input_json)>65536;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='native_marriage_invalid_classification_v1' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT director_action_id,payload_json
FROM relationship_director_actions
WHERE action_type='marriage' AND status='failed'
AND result_json LIKE '%Native marriage model rejected the couple.%';", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_director_actions SET status='invalid' WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static void UpdateMarriageEvaluations5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE marriage_evaluations SET status='invalid_native' WHERE evaluation_id=$id AND status='failed';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('native_marriage_invalid_classification_v1','1');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name='relationship_pair_chemistry' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='relationship_native_targets_v1' LIMIT 1;", parameters, transaction);
        }

        internal static void Savepoint3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SAVEPOINT migrate_relationship_native_targets;", parameters, transaction);
        }

        internal static void DeleteRelationshipDirectorActions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_director_actions WHERE action_type='native_relation';", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('relationship_native_targets_v1','1');", parameters, transaction);
        }

        internal static void Release3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "RELEASE migrate_relationship_native_targets;", parameters, transaction);
        }

        internal static void Rollback4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK TO migrate_relationship_native_targets;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgClass(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT 1 AS ready FROM pg_class t
WHERE t.relnamespace=(SELECT n.oid FROM pg_namespace n WHERE n.nspname=current_schema())
AND t.relname=$table AND t.relkind IN ('r','p')
AND NOT EXISTS (
 SELECT 1 FROM unnest(string_to_array($columns,',')) AS expected(name)
 WHERE NOT EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid=t.oid
  AND a.attname=expected.name AND a.attnum>0 AND NOT a.attisdropped))
AND NOT EXISTS (
 SELECT 1 FROM unnest(string_to_array($indexes,',')) AS expected(name)
 WHERE NOT EXISTS (SELECT 1 FROM pg_class index_row
  JOIN pg_index i ON i.indexrelid=index_row.oid AND i.indrelid=t.oid
  WHERE index_row.relnamespace=t.relnamespace AND index_row.relname=expected.name
  AND i.indisvalid AND i.indisready));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgConstraint(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT 1 AS present
FROM pg_constraint constraint_row
JOIN pg_class table_row ON table_row.oid=constraint_row.conrelid
JOIN pg_namespace schema_row ON schema_row.oid=table_row.relnamespace
WHERE schema_row.nspname=current_schema()
  AND table_row.relname='marriage_leader_rolls'
  AND constraint_row.contype='u'
  AND regexp_replace(
        lower(pg_get_constraintdef(constraint_row.oid)),
        '\s+', '', 'g')
      LIKE 'unique(timeline_id,period_index,leader_id)%'
LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT sql FROM sqlite_master WHERE type='table' AND name='marriage_leader_rolls' LIMIT 1;", parameters, transaction);
        }

        internal static void InsertOrIgnoreMarriageLeaderRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO marriage_leader_rolls(roll_id,timeline_id,period_index,leader_id,clan_id,world_day,chance,roll,urgency,status,payload_json,created_ts)
SELECT roll_id,'main',period_index,leader_id,clan_id,world_day,chance,roll,urgency,status,payload_json,created_ts FROM marriage_leader_rolls_legacy;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptionAttempts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conception_attempts WHERE attempt_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conceptions WHERE attempt_id=$id LIMIT 2;", parameters, transaction);
        }

        internal static bool TryInsertPlayerConceptionAttempt(ReignDbConnection connection, Dictionary<string, object> parameters, ReignDbTransaction transaction = null)
        {
            bool inserted = ReignDatabase.QuerySql(connection, @"INSERT INTO conception_attempts(attempt_id,source,event_id,mother_id,father_id,mother_age,chance,roll,success,world_day,payload_json,created_ts,status)
VALUES($id,'player_dialogue_action',$event,$mother,$father,$age,$chance,$roll,$success,$day,$payload,$ts,$status)
ON CONFLICT(attempt_id) DO NOTHING RETURNING attempt_id;", parameters, transaction).Count == 1;
            if (inserted)
            {
                // QuerySql returns the winning attempt but does not perform the dirty marking of ExecuteSql.
                string campaignId = ReignPostgreSqlStorage.CampaignIdForConnection(connection);
                if (!string.IsNullOrWhiteSpace(campaignId))
                {
                    try { PersistenceService.MarkSaveSyncActiveStateDirty(campaignId); }
                    catch { }
                }
            }
            return inserted;
        }

        internal static List<Dictionary<string, object>> SelectPlayerConceptionActions(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT payload_json FROM relationship_director_actions
WHERE action_type='start_conception' AND actor_id=$actor AND target_id=$target AND world_day=$day;
", parameters, transaction);
        }

        internal static void InsertConceptions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conceptions(conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,conception_day,due_day,status,secrecy,payload_json,created_ts,updated_ts)
VALUES($id,$attempt,$mother,$bio,$legal,$day,$due,'pending_game',$secrecy,$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void InsertOrIgnoreConceptions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conceptions(conception_id,mother_id,biological_father_id,legal_father_id,
conception_day,due_day,status,payload_json,created_ts,updated_ts) VALUES($id,$mother,$father,$legal,$start,$due,$status,$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateConceptions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conceptions SET status=$status,child_id=$child,payload_json=$payload,updated_ts=$ts WHERE conception_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conceptions WHERE conception_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertParentage(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO parentage(child_id,mother_id,biological_father_id,legal_father_id,is_illegitimate,revealed,bastard_surname,conception_id,payload_json,updated_ts)
VALUES($child,$mother,$bio,$legal,$illegitimate,$revealed,'',$conception,$payload,$ts);", parameters, transaction);
        }

        internal static void InsertDirectorOutbox(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO director_outbox(outbox_id,kind,status,world_day,subject_ids_json,payload_json,created_ts)
VALUES($id,'parentage_rumor_seed','pending',$day,$subjects,$payload,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectParentage(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM parentage WHERE child_id=$child LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateParentage(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE parentage SET revealed=1,is_illegitimate=1,bastard_surname=$surname,payload_json=$payload,updated_ts=$ts WHERE child_id=$child;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriagePeriods2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT period_index FROM arranged_marriage_periods WHERE timeline_id=$t AND period_index=$p;", parameters, transaction);
        }

        internal static void InsertRelationshipDirectorRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_director_runs(run_id,campaign_id,world_day,status,candidate_count,event_count,minime_json,input_json,output_json,created_ts)
VALUES($id,$campaign,$day,'completed',$candidates,$events,'{""disabled"":true}',$input,$output,$ts);", parameters, transaction);
        }

        internal static void DeleteRelationshipDirectorRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_director_runs
WHERE run_id NOT IN (
    SELECT run_id FROM relationship_director_runs ORDER BY world_day DESC,created_ts DESC LIMIT 64
);", parameters, transaction);
        }

        internal static void InsertArrangedMarriagePeriods(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO arranged_marriage_periods(timeline_id,period_index,world_day,status,created_ts) VALUES($t,$p,$d,'completed',$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT evaluation_id FROM marriage_evaluations WHERE timeline_id=$timeline AND ((hero_a_id=$a AND hero_b_id=$b) OR (hero_a_id=$b AND hero_b_id=$a)) AND status IN ('evaluating','approved','player_court_pending','player_offer_pending','wedding_pending','awaiting_diplomacy','recovery','executed') LIMIT 1;", parameters, transaction);
        }

        internal static void InsertMarriageEvaluations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO marriage_evaluations(evaluation_id,route,status,hero_a_id,hero_b_id,clan_a_id,clan_b_id,leader_a_id,leader_b_id,world_day,score,leader_a_approved,leader_b_approved,resentment_a,resentment_b,payload_json,created_ts,updated_ts,timeline_id)
VALUES($id,$route,$status,$a,$b,$ca,$cb,$la,$lb,$day,$score,$aa,$ba,$ra,$rb,$payload,$ts,$ts,$timeline);", parameters, transaction);
        }

        internal static void InsertOrIgnoreArrangedMarriageRefusals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO arranged_marriage_refusals(refusal_key,timeline_id,proposal_id) VALUES($k,$t,$p);", parameters, transaction);
        }

        internal static void UpdateMarriageEvaluations6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE marriage_evaluations SET status='reservation_blocked',updated_ts=$ts WHERE evaluation_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipMarriageReservations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_marriage_reservations(
hero_id,pair_key,director_action_id,created_day,created_ts) VALUES
($actor,$pair,$action,$day,$ts),($target,$pair,$action,$day,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipMarriageReservations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_marriage_reservations WHERE director_action_id=$action;", parameters, transaction);
        }

        internal static void DeleteRelationshipMarriageReservations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_marriage_reservations
WHERE director_action_id=$action;", parameters, transaction);
        }

        internal static void InsertRelationshipDirectorActions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_director_actions(director_action_id,action_type,status,world_day,actor_id,target_id,payload_json,created_ts)
VALUES($id,$type,'pending',$day,$actor,$target,$payload,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_director_actions
WHERE action_type<>'native_relation'
  AND (status='pending' OR (status='claimed' AND claimed_ts<$stale))
  AND NOT (action_type='marriage' AND EXISTS (
      SELECT 1 FROM relationship_conception_commitments commitment
      WHERE commitment.marriage_action_id=
          relationship_director_actions.director_action_id
        AND commitment.status='awaiting_conception'))
ORDER BY world_day,created_ts LIMIT 50;", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_director_actions SET status='claimed',claimed_ts=$ts WHERE director_action_id=$id;", parameters, transaction);
        }
    }
}
