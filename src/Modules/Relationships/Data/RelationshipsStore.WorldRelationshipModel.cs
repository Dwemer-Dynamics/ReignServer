using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static void DeleteIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM identity_roster;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM identity_roster ORDER BY hero_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM identity_roster;", parameters, transaction);
        }

        internal static void InsertCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,
calculated_revision,last_changed_day,updated_ts,calculation_status,last_error) VALUES
($campaign,'main','noop_a',-5,'[]',2,2,4,10,'ready',''),
($campaign,'main','noop_b',11,'[]',3,3,4,10,'ready',''),
($campaign,'main','orphan',30,'[]',4,4,4,10,'ready','');", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,affinity_a_to_b,
affinity_b_to_a,tag_a_to_b,tag_b_to_a,first_day,last_day,
projected_native_relation,native_action_pending,native_action_id,
compatibility_version,updated_ts)
VALUES('noop_a|noop_b','noop_a','noop_b','INTJ','ENFP',80,80,70,70,
7,-3,'neutral','neutral',1,4,5,1,'native_target:noop_a|noop_b',
$version,101);", parameters, transaction);
        }

        internal static void InsertIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO identity_roster(hero_id,canonical_name,kingdom_id,current_charm,is_player,updated_ts)
VALUES($id,$id,$kingdom,150,$player,1);", parameters, transaction);
        }

        internal static void UpdateCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_public_standing SET standing_value=1,sources_json=$sources WHERE subject_id='noop_b';", parameters, transaction);
        }

        internal static void UpdateCharacterPublicStanding2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_public_standing SET standing_value=17,sources_json='[]' WHERE subject_id='noop_b';", parameters, transaction);
        }

        internal static void UpdateIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET current_charm=210,is_player=0 WHERE hero_id='player_observer';", parameters, transaction);
        }

        internal static void InsertCharacterPublicStanding2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,
calculated_revision,last_changed_day,updated_ts,calculation_status,last_error)
VALUES($campaign,'main','missing_hero',23,'[]',1,1,4,10,'ready','');", parameters, transaction);
        }

        internal static void DeleteCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM character_public_standing WHERE subject_id='missing_hero';", parameters, transaction);
        }

        internal static void UpdateIdentityRoster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET current_charm=150,is_player=1 WHERE hero_id='player_observer';", parameters, transaction);
        }

        internal static void UpdateCharacterPublicStanding3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_public_standing SET standing_value=11,sources_json='[]' WHERE subject_id='noop_b';", parameters, transaction);
        }

        internal static void UpdateCharacterPublicStanding4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_public_standing
SET standing_value=0 WHERE subject_id IN ('noop_a','noop_b');", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry27(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET affinity_a_to_b=4,affinity_b_to_a=6,updated_ts=101
WHERE pair_key='noop_a|noop_b';", parameters, transaction);
        }

        internal static void InsertRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,
world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision)
VALUES('noop_a|noop_b','noop_a','noop_b',5,0,'pending',4,-1000,0,0,'',202,7);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry22(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT updated_ts FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT updated_ts,revision FROM relationship_native_targets WHERE pair_key='noop_a|noop_b';", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry28(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET affinity_a_to_b=3,affinity_b_to_a=7,updated_ts=303
WHERE pair_key='noop_a|noop_b';", parameters, transaction);
        }

        internal static void InsertRelationshipPairLifecycle3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,last_processed_day,version,updated_ts)
VALUES('family_a|family_b','family_a','family_b',1,$version,515);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT last_processed_day,updated_ts
FROM relationship_pair_lifecycle WHERE pair_key='family_a|family_b';", parameters, transaction);
        }

        internal static void InsertRelationshipPersonalities4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,
template_version,traits_json,created_ts,updated_ts)
VALUES($hero,'INTJ','Architect','Self-test personality',
'pregenerated_profile',0,1,'{}',1,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE pair_key='race_pair_a|race_pair_b';", parameters, transaction);
        }

        internal static void InsertIdentityRoster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO identity_roster(
hero_id,canonical_name,clan_id,kingdom_id,is_lord,is_ruler,
family_ids_json,updated_ts,is_alive,is_adult,is_player,is_clan_leader)
VALUES
('political_ruler','Ruler','clan_r','kingdom_test',1,1,'[]',1,1,1,0,1),
('political_leader_a','Leader A','clan_a','kingdom_test',1,0,'[]',1,1,1,0,1),
('political_leader_b','Leader B','clan_b','kingdom_test',1,0,'[]',1,1,1,0,1);", parameters, transaction);
        }

        internal static void UpdateRelationshipPairProvenance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_provenance
SET updated_ts=404 WHERE source='kingdom_leadership' AND active=1;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry29(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET updated_ts=405 WHERE pair_key LIKE 'political_%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairProvenance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_provenance
WHERE source='kingdom_leadership' AND active=1 AND updated_ts=404;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE pair_key LIKE 'political_%' AND updated_ts=405;", parameters, transaction);
        }

        internal static void UpdateIdentityRoster3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE identity_roster
SET is_clan_leader=0 WHERE hero_id='political_leader_b';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE pair_key IN (
'political_leader_a|political_leader_b',
'political_leader_b|political_ruler');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairProvenance2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_provenance
WHERE source='kingdom_leadership' AND active=0
AND pair_key IN (
'political_leader_a|political_leader_b',
'political_leader_b|political_ruler');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS character_count,
SUM(CASE WHEN standing_value>0 THEN 1 ELSE 0 END) AS positive_count,
SUM(CASE WHEN standing_value<0 THEN 1 ELSE 0 END) AS negative_count,
SUM(CASE WHEN standing_value=0 THEN 1 ELSE 0 END) AS neutral_count,
COALESCE(MIN(standing_value),0) AS minimum,
COALESCE(MAX(standing_value),0) AS maximum,
COALESCE(AVG(standing_value),0) AS average,
SUM(CASE WHEN calculation_status<>'ready' THEN 1 ELSE 0 END) AS error_count,
SUM(CASE WHEN calculated_revision<>revision THEN 1 ELSE 0 END) AS stale_count
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT standing_value FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY standing_value;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT sources_json,revision FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPublicStandingInvalidations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS total,
SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END) AS pending,
SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) AS failed,
COALESCE(SUM(CASE WHEN status='pending' THEN affected_pairs-processed_pairs ELSE 0 END),0)
AS remaining_pairs,
COALESCE(MAX(CASE WHEN status='pending' THEN $now-created_ts ELSE 0 END),0)
AS oldest_pending_seconds,
COALESCE(SUM(affected_pairs),0) AS affected_observers
FROM public_standing_invalidations
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairProvenance3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS active_required,
SUM(CASE WHEN source='kingdom_leadership' THEN 1 ELSE 0 END) AS kingdom_pairs,
SUM(CASE WHEN source='ruler_network' THEN 1 ELSE 0 END) AS ruler_pairs
FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND source IN ('kingdom_leadership','ruler_network')
AND active=1 AND required=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND is_player=0
AND (is_clan_leader=1 OR is_ruler=1) AND hero_id<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairProvenance2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT pair_key,source FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND source IN ('kingdom_leadership','ruler_network')
AND active=1 AND required=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry23(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pair_key FROM relationship_pair_chemistry;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry24(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT p.affinity_a_to_b,p.affinity_b_to_a,
COALESCE(sa.standing_value,0) AS standing_a,
COALESCE(sb.standing_value,0) AS standing_b
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sa ON sa.campaign_id=$campaign
AND sa.timeline_id=$timeline AND sa.subject_id=p.hero_a_id
LEFT JOIN character_public_standing sb ON sb.campaign_id=$campaign
AND sb.timeline_id=$timeline AND sb.subject_id=p.hero_b_id; ", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,standing_value,sources_json,
revision,last_changed_day,updated_ts,calculation_status,last_error FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT hero_id,kingdom_id,current_charm,is_player FROM identity_roster WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement2(ReignDbConnection connection, object query, object idColumn, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT source.* FROM (" + query + @") source
JOIN jsonb_array_elements_text(CAST($requestedHeroes AS jsonb)) requested
ON requested.value=source." + idColumn + ";", parameters, transaction);
        }

        internal static void UpsertSchemaMeta17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_world_relationship_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static void UpsertRelationshipPairProvenance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_provenance(
campaign_id,timeline_id,pair_key,source,active,required,first_day,last_day,details_json,updated_ts)
VALUES($campaign,$timeline,$pair,$source,1,$required,$day,$day,$details,$ts)
ON CONFLICT(campaign_id,timeline_id,pair_key,source) DO UPDATE SET
active=1,required=$required,last_day=$day,details_json=$details,updated_ts=$ts
WHERE relationship_pair_provenance.active<>1
OR relationship_pair_provenance.required<>$required
OR relationship_pair_provenance.last_day<>$day
OR relationship_pair_provenance.details_json<>$details;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,standing_value,sources_json,
revision,last_changed_day,updated_ts,calculation_status,last_error
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT kingdom_id,current_charm FROM identity_roster WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT kingdom_id,is_player FROM identity_roster WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairProvenance3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT source FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline AND pair_key=$pair AND active=1
ORDER BY source;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_pair_chemistry
WHERE hero_a_id=$subject OR hero_b_id=$subject;", parameters, transaction);
        }

        internal static void UpsertPublicStandingInvalidations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO public_standing_invalidations(
campaign_id,timeline_id,subject_id,standing_revision,status,pair_cursor,
affected_pairs,processed_pairs,created_day,created_ts,updated_ts,last_error)
VALUES($campaign,$timeline,$subject,$revision,'pending','',$affected,0,$day,$ts,$ts,'')
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
standing_revision=$revision,status='pending',pair_cursor='',affected_pairs=$affected,
processed_pairs=0,created_day=$day,created_ts=$ts,updated_ts=$ts,last_error='';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPublicStandingInvalidations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM public_standing_invalidations
WHERE status='pending' ORDER BY updated_ts,subject_id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry25(ReignDbConnection connection, object publicStandingInvalidationBatchSize, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE (hero_a_id=$subject OR hero_b_id=$subject) AND pair_key>$cursor
ORDER BY pair_key LIMIT " + publicStandingInvalidationBatchSize + ";", parameters, transaction);
        }

        internal static void UpdatePublicStandingInvalidations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE public_standing_invalidations SET
status=$status,pair_cursor=$cursor,processed_pairs=$processed,updated_ts=$ts,last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
AND standing_revision=$revision;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets12(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_native_targets WHERE pair_key IN ("
                        + part + ");", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry30(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
projected_native_relation=$projected,native_action_pending=$pending,
native_action_id=$action,updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpsertRelationshipNativeTargets3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,
last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision,requires_observation)
VALUES($pair,$a,$b,$target,$observed,'pending',$day,-1000,0,0,'',$ts,1,$requiresObservation)
ON CONFLICT(pair_key) DO UPDATE SET hero_a_id=$a,hero_b_id=$b,
target_relation=$target,observed_relation=$observed,status='pending',world_day=$day,
requires_observation=CASE WHEN relationship_native_targets.requires_observation=1 OR $requiresObservation=1 THEN 1 ELSE 0 END,
claimed_ts=0,last_error='',
attempt_count=CASE WHEN relationship_native_targets.target_relation=$target
THEN relationship_native_targets.attempt_count ELSE 0 END,
revision=CASE WHEN relationship_native_targets.target_relation=$target
THEN relationship_native_targets.revision
ELSE relationship_native_targets.revision+1 END,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND is_player=0
AND (is_clan_leader=1 OR is_ruler=1)
AND hero_id<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairProvenance4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND source IN ('kingdom_leadership','ruler_network');", parameters, transaction);
        }

        internal static void UpdateRelationshipPairProvenance2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_provenance SET
active=0,required=0,last_day=$day,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND pair_key=$pair AND source=$source
AND (active<>0 OR required<>0);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry26(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 AS present FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> InsertOrIgnoreRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,affinity_a_to_b,
affinity_b_to_a,tag_a_to_b,tag_b_to_a,first_day,last_day,
processing_shard,last_presence_day,projected_native_relation,
compatibility_version,updated_ts)
VALUES($pair,$a,$b,$typeA,$typeB,$baseAB,$baseBA,$chanceAB,$chanceBA,
0,0,'neutral','neutral',$day,$day,$shard,-1,0,$version,$ts)
ON CONFLICT(pair_key) DO NOTHING
RETURNING pair_key;", parameters, transaction);
        }

        internal const string SelectCharacterPublicStandingSql = "SELECT subject_id,standing_value,sources_json,revision,last_changed_day,updated_ts,calculation_status,last_error FROM character_public_standing WHERE campaign_id=$campaign AND timeline_id=$timeline";

        internal const string SelectIdentityRosterSql = "SELECT hero_id,kingdom_id,current_charm,is_player FROM identity_roster";

        internal const string SelectSql5 = @"
    WITH x AS (
        SELECT * FROM jsonb_to_recordset(@rows) AS r(
            pair text,projected integer,pending integer,action text,ts bigint)
    )
    UPDATE relationship_pair_chemistry AS chemistry
    SET projected_native_relation=x.projected,
        native_action_pending=x.pending,
        native_action_id=x.action,
        updated_ts=x.ts
    FROM x WHERE chemistry.pair_key=x.pair;";
    }
}
