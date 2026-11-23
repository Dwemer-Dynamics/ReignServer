using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void InsertConceptions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conceptions(
conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,conception_day,due_day,status,secrecy,
payload_json,created_ts,updated_ts)
VALUES($id,$attempt,$mother,$father,$legal,$day,$due,'pending_game',$secrecy,$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry22(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
UPDATE relationship_pair_chemistry
SET last_decay_period=$period,updated_ts=$ts
WHERE last_decay_period<0
AND (affinity_a_to_b<>0 OR affinity_b_to_a<>0);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE last_decay_period>=0 AND last_decay_period<$period
AND (affinity_a_to_b<>0 OR affinity_b_to_a<>0);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT subject_id,standing_value FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle;", parameters, transaction);
        }

        internal static void InsertRelationshipIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_incidents(
incident_id,pair_key,kind,hero_a_id,hero_b_id,world_day,summary,rumor_id,payload_json,created_ts)
VALUES($id,$pair,$kind,$a,$b,$day,$summary,$rumor,$payload,$ts);", parameters, transaction);
        }

        internal const string UpsertRelationshipPairLifecycleSql = @"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,lover_active,lover_started_day,lover_exception_attempted,lover_exception_passed,
lover_exception_roll,lover_rumor_id,romance_stage,romance_stage_started_day,romance_last_colocated_day,
romance_positive_bonus,flirt_episode,flirt_attempted,flirt_passed,flirt_trait_a,flirt_trait_b,flirt_roll_a,flirt_roll_b,
affair_episode,affair_attempted,affair_passed,judgment_trait_a,judgment_trait_b,judgment_roll_a,judgment_roll_b,
affair_active,affair_started_day,affair_rumor_id,
estranged_a_to_b,estranged_b_to_a,estranged_rumor_id,married,marriage_action_id,
marriage_blocked,marriage_block_reason,divorced,divorce_day,
divorce_action_id,divorce_rumor_id,conception_action_id,last_conception_day,last_marriage_evaluation_day,organic_marriage_colocated_days,last_processed_day,version,updated_ts)
VALUES($pair,$a,$b,$lovers,$loverDay,$attempted,$passed,$exceptionRoll,$loverRumor,$romanceStage,$romanceStageDay,$romanceColocatedDay,
$romanceBonus,$flirtEpisode,$flirtAttempted,$flirtPassed,$flirtTraitA,$flirtTraitB,$flirtRollA,$flirtRollB,
$affairEpisode,$affairAttempted,$affairPassed,$judgmentTraitA,$judgmentTraitB,$judgmentRollA,$judgmentRollB,
$affair,$affairDay,$affairRumor,
$estrangedAB,$estrangedBA,$estrangedRumor,$married,$marriageAction,$marriageBlocked,$marriageBlockReason,
$divorced,$divorceDay,$divorceAction,
$divorceRumor,$conceptionAction,$conceptionDay,$marriageEvaluationDay,$marriageColocatedDays,$day,$version,$ts)
ON CONFLICT(pair_key) DO UPDATE SET lover_active=$lovers,lover_started_day=$loverDay,
 lover_exception_attempted=$attempted,lover_exception_passed=$passed,lover_exception_roll=$exceptionRoll,
 lover_rumor_id=$loverRumor,romance_stage=$romanceStage,romance_stage_started_day=$romanceStageDay,
 romance_last_colocated_day=$romanceColocatedDay,romance_positive_bonus=$romanceBonus,
 flirt_episode=$flirtEpisode,flirt_attempted=$flirtAttempted,flirt_passed=$flirtPassed,
 flirt_trait_a=$flirtTraitA,flirt_trait_b=$flirtTraitB,flirt_roll_a=$flirtRollA,flirt_roll_b=$flirtRollB,
 affair_episode=$affairEpisode,affair_attempted=$affairAttempted,affair_passed=$affairPassed,
 judgment_trait_a=$judgmentTraitA,judgment_trait_b=$judgmentTraitB,judgment_roll_a=$judgmentRollA,
 judgment_roll_b=$judgmentRollB,affair_active=$affair,affair_started_day=$affairDay,affair_rumor_id=$affairRumor,
estranged_a_to_b=$estrangedAB,estranged_b_to_a=$estrangedBA,estranged_rumor_id=$estrangedRumor,
married=$married,marriage_action_id=$marriageAction,marriage_blocked=$marriageBlocked,
marriage_block_reason=$marriageBlockReason,divorced=$divorced,divorce_day=$divorceDay,
divorce_action_id=$divorceAction,divorce_rumor_id=$divorceRumor,conception_action_id=$conceptionAction,
last_conception_day=$conceptionDay,last_marriage_evaluation_day=$marriageEvaluationDay,
organic_marriage_colocated_days=$marriageColocatedDays,last_processed_day=$day,version=$version,updated_ts=$ts;";

        internal const string SelectSql2 = @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        pair text,a text,b text,ab integer,ba integer,ts bigint)
)
UPDATE relationship_pair_lifecycle AS lifecycle
SET lover_active=0,
    affair_active=0,
    romance_stage=CASE WHEN LEAST(x.ab,x.ba) >= 25
        THEN 'flirtation' ELSE '' END,
    romance_stage_started_day=0,
    romance_positive_bonus=CASE WHEN LEAST(x.ab,x.ba) >= 25
        THEN 10 ELSE 0 END,
    affair_episode=CASE WHEN affair_attempted=1
        THEN GREATEST(1,affair_episode)+1 ELSE affair_episode END,
    affair_attempted=0,
    affair_passed=0,
    judgment_trait_a=0,
    judgment_trait_b=0,
    judgment_roll_a=0,
    judgment_roll_b=0,
    updated_ts=x.ts
FROM x WHERE lifecycle.pair_key=x.pair;";

        internal const string SelectSql3 = @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        pair text,ab integer,ba integer,tagab text,tagba text,
        shared text,projected integer,pending integer,action text,
        period integer,ts bigint)
)
UPDATE relationship_pair_chemistry AS chemistry
SET affinity_a_to_b=x.ab,
    affinity_b_to_a=x.ba,
    effective_affinity_a_to_b=x.ab,
    effective_affinity_b_to_a=x.ba,
    tag_a_to_b=x.tagab,
    tag_b_to_a=x.tagba,
    shared_tag=x.shared,
    projected_native_relation=x.projected,
    native_action_pending=x.pending,
    native_action_id=x.action,
    native_incident_offset=0,
    last_decay_period=x.period,
    state_revision=chemistry.state_revision+1,
    updated_ts=x.ts
FROM x
WHERE chemistry.pair_key=x.pair;";

        internal static List<Dictionary<string, object>> SelectRelationshipInteractionReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_id FROM relationship_interaction_receipts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pair_key FROM relationship_pair_lifecycle WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> Pragma2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "PRAGMA table_info(relationship_pair_lifecycle);", parameters, transaction);
        }

        internal static void InsertConceptions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conceptions(
conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,
conception_day,due_day,status,secrecy,payload_json,created_ts,updated_ts)
VALUES('stale_conception','stale_attempt','stale_mother','stale_father',
'stale_father',10,46,'pending_game',0,'{}',$ts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT a.status AS action_status,
c.status AS conception_status FROM relationship_director_actions a
JOIN conceptions c ON c.conception_id='stale_conception'
WHERE a.director_action_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void InsertConceptions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conceptions(
conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,
conception_day,due_day,status,secrecy,payload_json,created_ts,updated_ts)
VALUES('historic_stale_conception','historic_stale_attempt','historic_mother',
'historic_father','historic_father',11,47,'pending_game',0,'{}',$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='failed',result_json=$result WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static void DeleteSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM schema_meta
WHERE key='already_pregnant_conception_obsolete_repair_v1';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT a.status AS action_status,
c.status AS conception_status FROM relationship_director_actions a
JOIN conceptions c ON c.conception_id='historic_stale_conception'
WHERE a.director_action_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id FROM rumor_occurrences
WHERE thread_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT director_action_id
FROM relationship_director_actions WHERE action_type='marriage'
AND ((actor_id=$a AND target_id=$b) OR (actor_id=$b AND target_id=$a));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT director_action_id FROM relationship_director_actions
WHERE action_type='marriage' AND actor_id='marriage_man' AND target_id='marriage_woman' AND status='pending';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT director_action_id FROM relationship_director_actions
WHERE action_type='marriage' AND actor_id='marriage_man' AND status='pending';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT director_action_id FROM relationship_director_actions
WHERE action_type='marriage' AND actor_id='marriage_man'
AND target_id='marriage_woman' AND status='pending' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT marriage_blocked,marriage_block_reason
FROM relationship_pair_lifecycle WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT director_action_id
FROM relationship_director_actions WHERE action_type='marriage'
AND actor_id='marriage_man' AND target_id='marriage_woman'
AND status IN ('pending','claimed');", parameters, transaction);
        }

        internal static void InsertRelationshipConceptionCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_conception_commitments(
conception_id,pair_key,hero_a_id,hero_b_id,honor_a,honor_b,chance_a,chance_b,
roll_a,roll_b,passed_a,passed_b,status,marriage_action_id,world_day,updated_ts)
VALUES('commitment_conception','commitment_man|commitment_woman',
'commitment_man','commitment_woman',50,50,60,60,1,1,1,1,
'awaiting_conception',$action,40,$ts);", parameters, transaction);
        }

        internal static void UpdateRelationshipConceptionCommitments7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status='awaiting_marriage' WHERE conception_id='commitment_conception';", parameters, transaction);
        }

        internal static void InsertRelationshipConceptionCommitments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_conception_commitments(
conception_id,pair_key,hero_a_id,hero_b_id,honor_a,honor_b,chance_a,chance_b,
roll_a,roll_b,passed_a,passed_b,status,marriage_action_id,world_day,updated_ts)
VALUES('refused_conception','refused_man|refused_woman',
'refused_man','refused_woman',50,50,60,60,99,1,0,1,
'awaiting_conception',$action,41,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipMarriageReservations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id
FROM relationship_marriage_reservations WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT last_marriage_evaluation_day FROM relationship_pair_lifecycle
WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id FROM rumor_occurrences
WHERE thread_key=$pair AND archetype_id='marital_strife' AND world_day=$day AND expires_day=$expires;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id FROM rumor_subject_tags
WHERE occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE thread_key=$pair AND world_day=$day);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT director_action_id FROM relationship_director_actions
WHERE action_type='divorce' AND actor_id='spouse_a' AND target_id='spouse_b' AND status='pending';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT payload_json FROM conceptions WHERE mother_id='affair_woman' AND biological_father_id='affair_man'
ORDER BY conception_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertRelationshipObservedHeroes2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO relationship_observed_heroes(
hero_id,profile_json,profile_hash,first_observed_day,last_observed_day,updated_ts)
VALUES
('repair_man_a','{""heroStringId"":""repair_man_a"",""isFemale"":false,""isAlive"":true,""spouseId"":""""}','',1,20,1),
('repair_man_b','{""heroStringId"":""repair_man_b"",""isFemale"":false,""isAlive"":true,""spouseId"":""""}','',1,20,1);", parameters, transaction);
        }

        internal static void UpsertRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,lover_active,married,last_processed_day,updated_ts)
VALUES($pair,'repair_man_a','repair_man_b',1,1,20,1);", parameters, transaction);
        }

        internal static void InsertRelationshipDirectorActions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_director_actions(
director_action_id,action_type,status,world_day,actor_id,target_id,payload_json,
created_ts,resolved_ts,result_json) VALUES
('repair_action_1','marriage','completed',10,'repair_man_a','repair_man_b',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""repair_man_a|repair_man_b"",""timelineId"":""main""}',1,2,
'{""status"":""completed"",""worldDay"":10.25}'),
('repair_action_2','marriage','completed',15,'repair_man_a','repair_man_b',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""repair_man_a|repair_man_b"",""timelineId"":""main""}',3,4,
'{""status"":""completed"",""worldDay"":15.25}');", parameters, transaction);
        }

        internal static void InsertRelationshipIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_incidents(
incident_id,pair_key,kind,hero_a_id,hero_b_id,world_day,summary,payload_json,created_ts)
VALUES
('repair_incident_1',$pair,'marriage','repair_man_a','repair_man_b',10,'false marriage','{""directorActionId"":""repair_action_1""}',1),
('repair_incident_2',$pair,'marriage','repair_man_a','repair_man_b',15,'false marriage','{""directorActionId"":""repair_action_2""}',2);", parameters, transaction);
        }

        internal static void DeleteSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM schema_meta
WHERE key='organic_marriage_native_postcondition_repair_v1';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT married,marriage_blocked,marriage_block_reason
FROM relationship_pair_lifecycle WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDirectorActions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_director_actions WHERE director_action_id IN
('repair_action_1','repair_action_2') AND status='invalid';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_incidents WHERE incident_id IN
('repair_incident_1','repair_incident_2');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM events WHERE event_id IN
('marriage_repair_action_1','marriage_repair_action_2');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM memories WHERE event_id IN
('marriage_repair_action_1','marriage_repair_action_2');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT director_action_id FROM relationship_director_actions
WHERE action_type='divorce' AND actor_id='spouse_a' AND target_id='spouse_b'
ORDER BY created_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM relationship_director_actions WHERE director_action_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT divorce_rumor_id FROM relationship_pair_lifecycle
WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id FROM character_reputations
WHERE timeline_id='main' AND tag_id='divorcee' AND subject_id IN ('spouse_a','spouse_b') AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id FROM character_reputations
WHERE timeline_id='main' AND tag_id='marital_strife' AND subject_id IN ('spouse_a','spouse_b') AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_id FROM events WHERE event_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_id FROM world_history_events WHERE event_id=$id AND timeline_id='main' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_relationship_lifecycle_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static void DeleteRelationshipMarriageReservations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_marriage_reservations
WHERE NOT EXISTS (SELECT 1 FROM relationship_director_actions a
WHERE a.director_action_id=relationship_marriage_reservations.director_action_id
AND a.action_type='marriage' AND a.status IN ('pending','claimed'));", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipMarriageReservations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_marriage_reservations(
hero_id,pair_key,director_action_id,created_day,created_ts)
SELECT actor_id,
CASE WHEN lower(actor_id)<=lower(target_id) THEN actor_id||'|'||target_id
ELSE target_id||'|'||actor_id END,
director_action_id,world_day,created_ts
FROM relationship_director_actions
WHERE action_type='marriage' AND status IN ('pending','claimed')
UNION ALL
SELECT target_id,
CASE WHEN lower(actor_id)<=lower(target_id) THEN actor_id||'|'||target_id
ELSE target_id||'|'||actor_id END,
director_action_id,world_day,created_ts
FROM relationship_director_actions
WHERE action_type='marriage' AND status IN ('pending','claimed');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='relationship_lifecycle_sparse_v1' LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_pair_lifecycle
WHERE lover_active=0 AND lover_exception_attempted=0 AND lover_rumor_id=''
  AND affair_active=0 AND affair_rumor_id=''
  AND romance_stage='' AND flirt_attempted=0 AND affair_attempted=0
  AND estranged_a_to_b=0 AND estranged_b_to_a=0 AND estranged_rumor_id=''
  AND married=0 AND marriage_action_id='' AND marriage_blocked=0
  AND divorced=0 AND divorce_action_id='' AND divorce_rumor_id=''
  AND conception_action_id='';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('relationship_lifecycle_sparse_v1','1');", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipConceptionCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_conception_commitments(
conception_id,pair_key,hero_a_id,hero_b_id,honor_a,honor_b,chance_a,chance_b,
roll_a,roll_b,passed_a,passed_b,status,marriage_action_id,world_day,updated_ts)
VALUES($id,$pair,$a,$b,$honorA,$honorB,$chanceA,$chanceB,$rollA,$rollB,
$passedA,$passedB,'awaiting_conception',$marriageAction,$day,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipAffairCommitments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pair_key FROM relationship_affair_commitments
WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRelationshipAffairCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_affair_commitments(
pair_key,hero_a_id,hero_b_id,spouse_a_id,spouse_b_id,chance_a,chance_b,roll_a,roll_b,
passed_a,passed_b,divorce_action_a_id,divorce_action_b_id,status,world_day,updated_ts)
VALUES($pair,$a,$b,$spouseA,$spouseB,$chanceA,$chanceB,$rollA,$rollB,$passedA,$passedB,
$divorceA,$divorceB,$status,$day,$ts);", parameters, transaction);
        }
    }
}
