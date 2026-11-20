using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Spymaster module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class SpymasterSelfTestStore
    {
        internal static int ReadMitigationFixtureValue(ReignDbConnection connection, string occurrence)
        {
            var parameters = new Dictionary<string, object> { ["occurrence"] = occurrence };
            return (int)ReignDatabase.QuerySql(connection,
                "SELECT rumor_value FROM rumor_subject_tags WHERE occurrence_id=$occurrence;", parameters)[0]["rumor_value"];
        }

        internal static void SetMitigationProjectionFailure(ReignDbConnection connection, bool enabled)
        {
            ReignDatabase.ExecuteSql(connection, enabled
                ? "ALTER TABLE character_public_standing ADD CONSTRAINT spymaster_projection_failure CHECK (subject_id<>'blocked_fixture');"
                : "ALTER TABLE character_public_standing DROP CONSTRAINT spymaster_projection_failure;");
        }

        internal static List<Dictionary<string, object>> SelectForeignAgentAssignments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT agent_hero_id FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,first_day,last_day,
last_context_kind,last_context_id,state_revision,updated_ts)
VALUES($pair,'enemy_ruler','player_ruler',-29,-80,-95,-95,0,0,
'fixture_initialization','directional-outlook-fixture',1,0);", parameters, transaction);
        }

        internal static void InsertForeignAgentAssignments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO foreign_agent_assignments(campaign_id,timeline_id,agent_hero_id,sponsor_kingdom_id,
sponsor_ruler_id,settlement_id,is_notable,status,recruited_day,next_action_day,last_action_day,payload_json,updated_ts)
VALUES($campaign,$timeline,$agent,'enemy_kingdom','enemy_ruler','player_town',0,'active',0,1000,-1,'{}',0);", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=$outlook,affinity_b_to_a=$reverse,
effective_affinity_a_to_b=$effective,last_day=$day,
last_context_kind='fixture_explicit_player_incident',
last_context_id='directional-outlook-fixture',state_revision=state_revision+1
WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAgentAssignments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status,last_action_day FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET affinity_a_to_b=-30 WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateForeignAgentAssignments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE foreign_agent_assignments SET status='active',next_action_day=14,last_action_day=-1
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;", parameters, transaction);
        }
    }
}
