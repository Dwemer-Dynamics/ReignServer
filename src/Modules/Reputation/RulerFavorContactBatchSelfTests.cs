using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunRulerFavorContactBatchSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object data = null) => results.Add(TestDict(
                "caseId", id, "passed", passed, "suite", "character_social_reputation",
                "summary", id, "data", data ?? new Dictionary<string, object>()));
            string campaign = "__favor_bulk_" + Guid.NewGuid().ToString("N");
            try
            {
                using var c = OpenCampaignConnection(campaign);
                // Commit schema readiness before rollback-based comparisons.
                EnsureSocialReputationSchema(c);
                EnsureIdentitySchema(c);
                EnsurePublicStandingSchema(c);
                EnsureWorldTestTelemetrySchema(c);
                EnsureRulerFavorContactSchema(c);
                foreach (double day in new[] { 99d, 100d, 101d })
                {
                    string Run(bool legacy)
                    {
                        using var tx = c.BeginTransaction();
                        SeedRulerFavorBatchParityFixture(c, campaign);
                        var changed = legacy
                            ? ExpireRulerFavorContactLegacyForTests(c, campaign, "main", day)
                            : ExpireRulerFavorContact(c, campaign, "main", day);
                        var snapshot = RulerFavorBatchFixtureSnapshot(c);
                        // Include ordered return values; only generated wall timestamps are excluded.
                        return Json.Serialize(TestDict("changed", changed, "tables", snapshot));
                    }
                    string expected = Run(true);
                    string actual = Run(false);
                    Check("favor_contact_bulk_parity_day_" + (int)day, actual == expected,
                        TestDict("day", day, "legacySha256", Sha256Hex(expected),
                            "bulkSha256", Sha256Hex(actual)));
                }
                var reads = new Dictionary<int, int>();
                foreach (int count in new[] { 1, 173, 600 })
                {
                    using var tx = c.BeginTransaction();
                    for (int i = 0; i < count; i++)
                        SeedRulerFavorBatchTag(c, campaign, "main", "ruler_" + i,
                            "favorite_" + i, "size", null);
                    int queries;
                    List<string> changed;
                    using (var timing = new SqlReadTimingContext())
                    {
                        changed = ExpireRulerFavorContact(c, campaign, "main", 100d);
                        queries = timing.Queries;
                    }
                    reads[count] = queries;
                    int contacts = ReadInt(QuerySql(c,
                        "SELECT COUNT(*) AS count FROM court_ruler_favor_contact;")
                        .First(), "count", -1);
                    int expectedReads = 1 + (count + RulerFavorContactLookupBatchSize - 1)
                        / RulerFavorContactLookupBatchSize;
                    Check("favor_contact_bulk_lookup_reads_" + count,
                        changed.Count == 0 && contacts == count && queries == expectedReads,
                        TestDict("tags", count, "contacts", contacts, "readQueries", queries,
                            "expectedReadQueries", expectedReads));
                }
                Check("favor_contact_bulk_read_growth_is_chunked",
                    reads[1] == reads[173] && reads[600] == reads[1] + 2,
                    TestDict("reads1", reads[1], "reads173", reads[173], "reads600", reads[600]));
            }
            catch (Exception ex)
            {
                Check("favor_contact_bulk_contract_exception", false,
                    TestDict("type", ex.GetType().Name, "error", ex.ToString()));
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }

        private static void SeedRulerFavorBatchTag(ReignDbConnection c, string campaign,
            string timeline, string ruler, string favorite, string suffix, double? anchor)
        {
            ExecuteSql(c, @"INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult,is_player,is_ruler,kingdom_id)
VALUES($ruler,$ruler,1,1,0,1,'fixture_realm') ON CONFLICT(hero_id) DO NOTHING;",
                TestDict("ruler", ruler));
            ExecuteSql(c, @"INSERT INTO character_reputations
(campaign_id,timeline_id,subject_id,tag_id,acquired_day,catalog_revision,snapshot_json,updated_ts)
VALUES($campaign,$timeline,$ruler,$tag,0,1,$snapshot,0);",
                TestDict("campaign", campaign, "timeline", timeline, "ruler", ruler,
                    "tag", "ruler_favoring:" + suffix,
                    "snapshot", Json.Serialize(TestDict("linkedHeroId", favorite, "fixture", suffix))));
            if (anchor.HasValue)
                ExecuteSql(c, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day)
VALUES($campaign,$timeline,$ruler,$favorite,$anchor,10)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO NOTHING;",
                    TestDict("campaign", campaign, "timeline", timeline, "ruler", ruler,
                        "favorite", favorite, "anchor", anchor.Value));
        }

        private static void SeedRulerFavorBatchParityFixture(ReignDbConnection c, string campaign)
        {
            SeedRulerFavorBatchTag(c, campaign, "main", "boundary", "favorite", "boundary", 70d);
            SeedRulerFavorBatchTag(c, campaign, "main", "before", "favorite", "before", 70.001d);
            SeedRulerFavorBatchTag(c, campaign, "main", "old", "favorite", "old", 60d);
            SeedRulerFavorBatchTag(c, campaign, "main", "legacy", "favorite", "legacy", null);
            SeedRulerFavorBatchTag(c, campaign, "main", "zero_exchange", "favorite", "zero", null);
            SeedRulerFavorBatchTag(c, campaign, "main", "exchange", "favorite", "exchange", null);
            SeedRulerFavorBatchTag(c, campaign, "main", "null_anchor", "favorite", "null", null);
            SeedRulerFavorBatchTag(c, campaign, "main", "duplicate", "favorite", "duplicate_a", 60d);
            SeedRulerFavorBatchTag(c, campaign, "main", "duplicate", "favorite", "duplicate_b", null);
            SeedRulerFavorBatchTag(c, campaign, "main", "Case", "favorite", "upper", 60d);
            SeedRulerFavorBatchTag(c, campaign, "main", "case", "favorite", "lower", 95d);
            SeedRulerFavorBatchTag(c, campaign, "main", "blank", "  ", "blank", null);
            SeedRulerFavorBatchTag(c, campaign, "other", "boundary", "favorite", "other", 0d);
            ExecuteSql(c, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day)
VALUES($campaign,'main','null_anchor','favorite',NULL,60);
UPDATE court_ruler_favor_contact SET expired_day=80 WHERE ruler_id='old';
INSERT INTO court_player_favoring_exchanges
(campaign_id,timeline_id,ruler_id,favorite_id,exchange_count,last_world_day,updated_ts)
VALUES($campaign,'main','zero_exchange','favorite',0,1,0),
($campaign,'main','exchange','favorite',1,50,0);
INSERT INTO rumor_occurrences
(occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,world_day,expires_day,catalog_revision,created_ts,updated_ts)
VALUES('mixed',$campaign,'main','ruler_favoring_dialogue','mixed',0,200,1,0,0),
('only_favor',$campaign,'main','ruler_favoring_dialogue','only',0,200,1,0,0);
INSERT INTO rumor_subject_tags(occurrence_id,subject_id,tag_id,snapshot_json,updated_ts)
VALUES('mixed','duplicate','ruler_favoring:duplicate_a','{""linkedHeroId"":""favorite"",""extra"":true}',0),
('mixed','duplicate','neutral_fixture','{}',0),
('only_favor','old','ruler_favoring:old','{""linkedHeroId"":""favorite""}',0);",
                TestDict("campaign", campaign));
        }

        private static Dictionary<string, object> RulerFavorBatchFixtureSnapshot(ReignDbConnection c)
        {
            var output = new Dictionary<string, object>();
            foreach (var table in new[] {
                ("court_ruler_favor_contact", "campaign_id,timeline_id,ruler_id,favorite_id"),
                ("court_player_favoring_exchanges", "campaign_id,timeline_id,ruler_id,favorite_id"),
                ("character_reputations", "campaign_id,timeline_id,subject_id,tag_id"),
                ("rumor_occurrences", "occurrence_id"),
                ("rumor_subject_tags", "occurrence_id,subject_id,tag_id"),
                ("character_public_standing", "campaign_id,timeline_id,subject_id") })
            {
                var rows = QuerySql(c, "SELECT * FROM " + table.Item1 + " ORDER BY " + table.Item2 + ";");
                foreach (var row in rows)
                {
                    row.Remove("updated_ts");
                    row.Remove("created_ts");
                }
                output[table.Item1] = rows;
            }
            return output;
        }

        // Frozen pre-batching algorithm, independently retained as a database
        // behavior oracle. Do not route production through this reference.
        private static List<string> ExpireRulerFavorContactLegacyForTests(ReignDbConnection connection, string campaignId, string timelineId, double day)
        {
            EnsureRulerFavorContactSchema(connection);
            var changed = new List<string>();
            foreach (var tag in ActiveRulerFavorRows(connection, campaignId, timelineId, day))
            {
                var snapshot = TryParseJsonObject(ReadString(tag, "snapshot_json", "{}"));
                string rulerId = ReadString(tag, "subject_id", ""), favoriteId = ReadString(snapshot, "linkedHeroId", "");
                if (string.IsNullOrWhiteSpace(favoriteId)) continue;
                var p = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = rulerId, ["favorite"] = favoriteId, ["day"] = day, ["tag"] = ReadString(tag, "tag_id", ""),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                // The old exchange timestamp is evidence; proximity is deliberately not contact.
                ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day)
VALUES($campaign,$timeline,$ruler,$favorite,
(SELECT last_world_day FROM court_player_favoring_exchanges WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite AND exchange_count>0),$day)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO NOTHING;", p);
                var contact = QuerySql(connection, @"SELECT * FROM court_ruler_favor_contact WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite;", p).First();
                object contactDay;
                double anchor = contact.TryGetValue("last_contact_day", out contactDay) && contactDay != null && contactDay != DBNull.Value
                    ? ReadDouble(contact, "last_contact_day", day) : ReadDouble(contact, "grace_day", day);
                if (!ReignFamilyVisitRules.FavorExpired(anchor, day)) continue;
                ExecuteSql(connection, @"UPDATE character_reputations SET status='forgotten',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$ruler AND tag_id=$tag AND status='active';", p);
                ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='forgotten',updated_ts=$ts
WHERE subject_id=$ruler AND tag_id=$tag AND status='active' AND occurrence_id IN
(SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline);", p);
                ExecuteSql(connection, @"UPDATE rumor_occurrences SET status='forgotten',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
AND occurrence_id IN (SELECT occurrence_id FROM rumor_subject_tags WHERE subject_id=$ruler AND tag_id=$tag AND status='forgotten')
AND NOT EXISTS (SELECT 1 FROM rumor_subject_tags live WHERE live.occurrence_id=rumor_occurrences.occurrence_id AND live.status='active');", p);
                ExecuteSql(connection, @"UPDATE court_ruler_favor_contact SET expired_day=$day
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite;", p);
                ExecuteSql(connection, @"UPDATE court_player_favoring_exchanges SET exchange_count=0
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite;", p);
                changed.Add(rulerId);
            }
            var subjects = changed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (subjects.Count > 0) RecomputePublicStandingForSubjects(connection, campaignId, timelineId, subjects, day);
            return subjects;
        }
    }
}

