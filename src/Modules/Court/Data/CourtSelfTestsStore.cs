using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Court module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class CourtSelfTestStore
    {
        internal static void UpdateForeignAmbassadorPostings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE foreign_ambassador_postings SET authority_revision=$revision,authority_charter_json=$charter WHERE posting_id=$posting;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorReferrals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambassador_referrals WHERE referral_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateAmbassadorReferrals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE ambassador_referrals SET status='countered',counter_terms_json=$terms WHERE referral_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorDecisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambassador_decisions WHERE decision_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbassadorReferrals2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM ambassador_referrals WHERE referral_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCourtLifeTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM court_life_turns WHERE actor_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtLifeTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT player_text FROM court_life_turns WHERE actor_id=$id;", parameters, transaction);
        }

        internal static void InsertIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult,is_player,is_ruler,kingdom_id) VALUES('ruler','Ruler',1,1,1,1,'realm'),('child','Child',1,0,0,0,'realm');", parameters, transaction);
        }

        internal static void UpdateCourtFamilyAttention(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_family_attention SET dismissals=4 WHERE timeline_id='catchup';", parameters, transaction);
        }

        internal static void InsertIdentityRoster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult,is_player,is_ruler,kingdom_id) VALUES('favorite','Favorite',1,1,0,0,'realm'),('foreign_ruler','Foreign Ruler',1,1,0,1,'foreign');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_ruler_favor_contact WHERE ruler_id='ruler' AND favorite_id='favorite';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtRulerFavorContact2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM court_ruler_favor_contact WHERE ruler_id='foreign_ruler' AND favorite_id='favorite';", parameters, transaction);
        }

        internal static void UpdateCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations SET status='active' WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id='foreign_ruler';
DELETE FROM court_ruler_favor_contact WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id='foreign_ruler';", parameters, transaction);
        }

        internal static void InsertIdentityRoster3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult) VALUES('adult_family','Family',1,1);", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES('adult_family|ruler','adult_family','ruler','ESTJ','ISFJ',2,17,10,10,0);", parameters, transaction);
        }

        internal static void UpsertCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_public_standing(campaign_id,timeline_id,subject_id,standing_value,updated_ts)
VALUES($campaign,$timeline,'ruler',$offset,0) ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET standing_value=$offset;
UPDATE relationship_pair_chemistry SET affinity_a_to_b=$affinity WHERE pair_key='adult_family|ruler';
UPDATE court_family_attention SET neglected=1,decay_finished=0,last_decay_day=10 WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id='adult_family';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key='adult_family|ruler';", parameters, transaction);
        }

        internal static void UpdateCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_public_standing SET standing_value=3 WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id='ruler';
UPDATE court_family_attention SET child_relation_owned=1,adult_migrated=0,directional_relation=0 WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id='adult_family';", parameters, transaction);
        }
    }
}
