using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> GossipTestTraits(params object[] overrides)
        {
            var values = GossipFields("sociability", 50, "impulsiveness", 50, "pride", 50, "curiosity", 50,
                "honesty", 50, "vengefulness", 50, "compassion", 50, "loyalty", 50, "tact", 50,
                "discipline", 50, "empathy", 50, "shame", 50, "familyMotivation", 50, "socialTrust", 50);
            foreach (var pair in GossipFields(overrides)) values[pair.Key] = pair.Value;
            return GossipFields("traitPercentages", values);
        }
        private static Dictionary<string, object> GossipTestHero(string id, string town = "town_a", string party = "") =>
            GossipFields("heroStringId", id, "name", id, "settlementId", town, "partyId", party, "kingdomId", "empire",
                "isAlive", true, "isPrisoner", false, "available", true, "positionKnown", true, "positionX", 0d, "positionY", 0d);
        private static void GossipTestPair(ReignDbConnection c, string a, string b, int aToB, int bToA)
        {
            ExecuteSql(c, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($key,$a,$b,$ab,$ba,1,1,1) ON CONFLICT(pair_key) DO UPDATE SET affinity_a_to_b=excluded.affinity_a_to_b,affinity_b_to_a=excluded.affinity_b_to_a;",
                GossipFields("key", AmbientPairKey(a, b), "a", a, "b", b, "ab", aToB, "ba", bToA));
        }
        private static string GossipTestSeed(string prefix, Func<string, bool> predicate)
        {
            for (int i = 0; i < 100000; i++) if (predicate(prefix + i)) return prefix + i;
            throw new InvalidOperationException("No deterministic gossip fixture seed found.");
        }

        private static List<Dictionary<string, object>> RunPersonalGossipSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> check = (id, passed, detail) => results.Add(GossipFields("ok", true, "passed", passed,
                "suite", "personal_gossip", "caseId", id, "name", id, "summary", passed || detail == null ? id : id + ": " + LimitText(Json.Serialize(detail), 6000), "detail", detail, "durationMs", 0));
            var policy = DefaultPersonalGossipSettings();
            Func<string, double, bool, bool, bool, GossipCandidate> candidate = (id, relation, family, named, local) =>
                new GossipCandidate { Id = id, Affinity = relation, Family = family, Mentioned = named, Local = local, Hurt = 75, Person = GossipTestHero(id) };
            Func<double, bool, string, bool> eligible = (r, family, type) =>
                GossipRecipients(new[] { candidate("npc", r, family, false, true) }, type, 50, policy).Count == 1;
            check("friendship_boundary", !eligible(29, false, "positive") && eligible(30, false, "positive"), null);
            check("family_boundary", !eligible(-1, true, "positive") && eligible(0, true, "positive"), null);
            check("dislike_boundary", !eligible(-9, false, "negative") && eligible(-10, false, "negative"), null);
            check("negative_impression_friendly_audience", eligible(30, false, "impression") && !eligible(-10, false, "impression"), null);
            var namedNeutral = candidate("named", 0, false, true, false);
            check("name_never_bypasses_relationship", GossipRecipients(new[] { namedNeutral }, "positive", 50, policy).Count == 0, namedNeutral.Audit());
            check("distant_unnamed_best_friend_excluded", GossipRecipients(new[] { candidate("friend", 100, false, false, false) }, "positive", 50, policy).Count == 0, null);
            check("named_eligible_remote_contact", GossipRecipients(new[] { candidate("friend", 30, false, true, false) }, "positive", 50, policy).Count == 1, null);
            var mildEnemy = candidate("enemy", -80, false, true, true); mildEnemy.Hurt = 25;
            check("malice_requires_motive", GossipRecipients(new[] { mildEnemy }, "negative", 50, policy).Count == 0, null);
            var names = GossipRecipients(new[] { candidate("named", 30, false, true, false), candidate("local1", 40, false, false, true), candidate("local2", 40, false, false, true) }, "positive", 50, policy);
            double sum = names.Sum(x => x.Weight);
            check("named_80_10_10", Math.Abs(names.Single(x => x.Id == "named").Weight / sum - .8) < 1e-12
                && names.Where(x => !x.Mentioned).All(x => Math.Abs(x.Weight / sum - .1) < 1e-12), names.Select(x => x.Audit()).ToList());
            var multiple = GossipRecipients(new[] { candidate("named1", 40, false, true, false), candidate("named2", 40, false, true, false), candidate("local", 40, false, false, true) }, "positive", 50, policy);
            check("two_names_44_44_11", multiple.Where(x => x.Mentioned).All(x => Math.Abs(x.Weight / multiple.Sum(y => y.Weight) - 4d / 9) < 1e-12), null);
            var rivals = GossipRecipients(new[] { candidate("named", -20, false, true, false), candidate("rival", -60, false, false, true) }, "negative", 50, policy);
            check("named_enemy_over_stronger_local_rival", Math.Abs(rivals.Single(x => x.Mentioned).Weight / rivals.Sum(x => x.Weight) - .8) < 1e-12, null);
            var regular = GossipRecipients(new[] { candidate("strong", 70, false, false, true), candidate("friend", 30, false, false, true) }, "positive", 50, policy);
            check("ordinary_weighted_2_to_1", Math.Abs(regular.Single(x => x.Id == "strong").Weight / regular.Sum(x => x.Weight) - 2d / 3) < 1e-12, null);
            var capped = GossipRecipients(Enumerable.Range(0, 20).Select(i => candidate("local" + i, 100, false, false, true))
                .Concat(new[] { candidate("named_low", 30, false, true, false) }), "positive", 50, policy);
            check("candidate_cap_keeps_named", capped.Count == 8 && capped.Any(x => x.Id == "named_low"), null);
            var unequalNames = GossipRecipients(new[] { candidate("big", 90, false, true, false), candidate("small", 30, false, true, false), candidate("local", 60, false, false, true) }, "positive", 50, policy);
            check("each_named_outweighs_ordinary_group", unequalNames.Where(x => x.Mentioned).All(x => x.Weight >= 4 * unequalNames.Where(y => !y.Mentioned).Sum(y => y.Weight)), null);
            var sole = GossipRecipients(new[] { candidate("only", 30, false, false, true) }, "positive", 50, policy);
            check("sole_candidate_selection_not_sharing_guarantee", Enumerable.Range(0, 100).All(i => GossipChooseRecipient(sole, i / 100d).Id == "only")
                && Enumerable.Range(0, 100).Any(i => GossipRoll("sole" + i, "share") > 85), null);
            check("same_settlement", GossipLocal(GossipTestHero("a"), GossipTestHero("b")), null);
            check("same_party", GossipLocal(GossipTestHero("a", "", "party"), GossipTestHero("b", "", "party")), null);
            var nowhere = GossipTestHero("a", ""); nowhere["armyId"] = "army";
            var elsewhere = GossipTestHero("b", ""); elsewhere["armyId"] = "army";
            check("empty_locations_and_shared_army_do_not_qualify", !GossipLocal(nowhere, elsewhere), null);
            var child = GossipTestHero("Ira"); var mother = GossipTestHero("Rhagaea"); child["motherId"] = "Rhagaea";
            var player = GossipTestHero("Traveler");
            check("kin_reference_resolves_without_omniscient_mentions", GossipMentionedIds("Did your mother speak to me?", player, child, new[] { child, mother, player }).SequenceEqual(new[] { "Rhagaea" })
                && GossipMentionedIds("I enjoyed that discussion.", child, player, new[] { mother }).Count == 0, null);
            var duplicateName = GossipTestHero("other_rhagaea"); duplicateName["name"] = "Rhagaea";
            check("ambiguous_name_cannot_unlock_travel", GossipMentionedIds("Rhagaea told me about it.", child, player, new[] { mother, duplicateName }).Count == 0, null);
            check("kin_reference_stays_unambiguous", GossipMentionedIds("My mother told me about it.", child, player, new[] { mother, duplicateName }).SequenceEqual(new[] { "Rhagaea" }), null);
            var sibling = GossipTestHero("Sibling"); sibling["motherId"] = "Rhagaea";
            var cousin = GossipTestHero("Cousin"); cousin["motherId"] = "aunt";
            check("immediate_family_only", GossipFamily(child, mother) && GossipFamily(child, sibling) && !GossipFamily(child, cousin), null);
            check("local_one_hour", Math.Abs(GossipTransitDays(child, mother, policy, true) - 1d / 24) < 1e-12, null);
            mother["positionX"] = 80d;
            check("same_kingdom_distance", GossipTransitDays(child, mother, policy, false) == 2, null);
            mother["kingdomId"] = "foreign"; mother["positionX"] = 55d;
            check("foreign_distance", GossipTransitDays(child, mother, policy, false) == 4, null);
            mother["positionKnown"] = false;
            check("unknown_route_slower", GossipTransitDays(child, mother, policy, false) == 5, null);
            var ordinary = GossipTestTraits();
            var excitable = GossipTestTraits("sociability", 85, "impulsiveness", 80, "pride", 65, "curiosity", 70,
                "honesty", 65, "vengefulness", 30, "compassion", 65, "tact", 25, "discipline", 30, "empathy", 50, "shame", 25);
            var spiteful = GossipTestTraits("sociability", 75, "impulsiveness", 75, "pride", 85, "curiosity", 65,
                "honesty", 15, "vengefulness", 90, "compassion", 15, "loyalty", 25, "tact", 40, "discipline", 45, "empathy", 20, "shame", 20);
            check("documented_share_examples", GossipShareChance(75, ordinary) == 44 && GossipShareChance(75, excitable) == 62 && GossipShareChance(75, spiteful) == 60, null);
            check("documented_disclosure_examples", GossipDetailChance(excitable, "personal", 75, 60, 60, false, false, 0) == 78
                && GossipDetailChance(excitable, "secret", 75, 60, 60, false, true, 0) == 8, null);
            check("documented_negative_and_distortion", GossipNegativeChance(spiteful, 75, -60, 0, false, false) == 57 && GossipDistortionChance(spiteful, 75) == 71, null);
            var honest = GossipTestTraits("honesty", 90, "vengefulness", 90, "pride", 85, "impulsiveness", 75);
            check("honest_spite_can_hurt_but_rarely_distorts", GossipDistortionChance(honest, 75) == 8, null);
            check("categorical_distortion_boundaries", GossipDistortionSeverity(39, 80) == "loaded_exaggeration" && GossipDistortionSeverity(39, 81) == "false_escalation"
                && GossipDistortionSeverity(69, 90) == "false_escalation" && GossipDistortionSeverity(69, 91) == "invented_detail"
                && GossipDistortionSeverity(70, 66) == "invented_detail", null);
            check("no_minimum_secret_leak", GossipDetailChance(GossipTestTraits("tact", 100, "discipline", 100, "empathy", 100, "loyalty", 100, "honesty", 100, "impulsiveness", 0),
                "secret", 75, 60, 100, false, true, 0) == 0, null);
            check("promise_cannot_be_relabelled_public", GossipDetailChance(excitable, "public", 75, 60, 60, false, true, 0) == 8, null);
            check("trait_monotonicity", Enumerable.Range(0, 100).All(i => GossipShareChance(75, GossipTestTraits("sociability", i))
                    <= GossipShareChance(75, GossipTestTraits("sociability", i + 1)))
                && Enumerable.Range(0, 100).All(i => GossipDistortionChance(GossipTestTraits("honesty", i), 75)
                    >= GossipDistortionChance(GossipTestTraits("honesty", i + 1), 75)), null);
            check("credibility_and_retelling_examples", GossipCredibility(60, 60, "firsthand", 0, false, false, false) == 67
                && GossipRetellChance(62, 67) == 19 && GossipRaiseChance(70, 75, 33, 75) == 68, null);
            check("recipient_proof_and_speculation", GossipCredibility(80, 80, "firsthand", 0, false, false, false, "disproved") == 0
                && GossipCredibility(-80, 20, "secondhand", 1, false, false, false, "confirmed") == 100
                && GossipCredibility(0, 50, "speculation", 0, false, false, false) == 25, null);
            check("strong_feelings_gate", !GossipEmotionEligible(0, excitable) && !GossipEmotionEligible(25, ordinary) && GossipEmotionEligible(25, excitable), null);
            check("rounding_and_roll_endpoints", GossipPercent(.5) == 1 && GossipPercent(-1) == 0 && GossipPercent(120, 85) == 85
                && Enumerable.Range(0, 500).All(i => GossipRoll("endpoint" + i, "share") is >= 1 and <= 100), null);
            check("stable_stage_rolls", Enumerable.Range(0, 100).All(_ => GossipRoll("same_topic", "share") == GossipRoll("same_topic", "share"))
                && GossipTopicId("main", "a", "player", 20.1, "family", "mother") == GossipTopicId("main", "a", "player", 20.9, "family", "mother"), null);
            check("unapproved_fabrication_rejected", !GossipBodyAllowed("We spent the night together.", "We talked privately.", "none")
                && GossipBodyAllowed("We spent the night together.", "We talked privately.", "invented_detail"), null);
            try { RunPersonalGossipPersistenceTests(check, spiteful); }
            catch (Exception ex) { check("persistence_fixture", false, ex.ToString()); }
            return results;
        }

        private static void RunPersonalGossipPersistenceTests(Action<string, bool, object> check, Dictionary<string, object> spiteful)
        {
            string campaign = "__gossip_test_" + Guid.NewGuid().ToString("N");
            string snapshotId = "gossip_before_delivery";
            var source = GossipTestHero("source"); var target = GossipTestHero("target"); var player = GossipTestHero("player");
            var people = new List<Dictionary<string, object>> { source, target, player };
            var policy = DefaultPersonalGossipSettings();
            var excitable = GossipTestTraits("sociability", 100, "impulsiveness", 100, "pride", 100, "curiosity", 100);
            WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), excitable);
            Func<double, Dictionary<string, object>> presence = day => GossipFields("campaignId", campaign, "timelineId", "main", "worldDay", day,
                "gossipPresence", GossipFields("schema", "reign-gossip-presence-v1", "timelineId", "main", "worldDay", day, "rosterComplete", true, "people", people));
            Dictionary<string, object> NewTopic(ReignDbConnection c, string id, double day, bool named, bool malicious = false)
            {
                string text = "Traveler and I talked privately about the tournament.";
                // Seed a closed fixture session without scheduling normal consolidation/providers.
                ExecuteSql(c, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,participants_json,payload_json)
VALUES($id,$campaign,'source','player','in_person','closed',1,1,$day,$day,'[""source"",""player""]','{}') ON CONFLICT(session_id) DO NOTHING;",
                    GossipFields("id", id + "_conversation", "campaign", campaign, "day", day));
                var request = GossipFields("campaignId", campaign, "conversationSessionId", id + "_conversation", "timelineId", "main", "worldDay", day, "channel", "in_person");
                var exchange = StoreConversationExchange(campaign, request, "source", "player", "Traveler", "Source", text, "That was an interesting conversation.", id + "_exchange", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var signal = GossipFields("topic", "conversation", "confidence", 1d, "evidenceQuote", text, "excitement", malicious ? 0 : 100,
                    "curiosity", 0, "hurt", malicious ? 75 : 0, "privacy", "public", "existencePrivacy", "public", "question", !malicious,
                    "targets", malicious ? new[] { GossipFields("heroId", "target", "evidenceQuote", text, "hurt", 75, "grievance", 0) } : Array.Empty<Dictionary<string, object>>());
                return GossipFields("id", id, "timelineId", "main", "speakerId", "source", "playerId", "player", "sessionId", ReadString(exchange, "sessionId", ""),
                    "createdDay", day, "worldDay", day, "category", "conversation", "signal", signal, "speaker", source, "policy", policy,
                    "mentionedIds", named ? new[] { "target" } : Array.Empty<string>(), "sourceText", text, "sourceQuote", text,
                    "sourceTurnIds", ReadStringList(exchange, "turnIds"), "sourceExchangeId", ReadString(exchange, "exchangeId", ""), "rootId", id, "hop", 1, "holders", new[] { "source" });
            }
            Dictionary<string, object> Compose(Dictionary<string, object> data, bool reply) => reply
                ? GossipFields("body", "I will think about that.", "sourceQuote", "", "privacy", "public", "answerKind", "acknowledged", "gossipSignals", Array.Empty<object>())
                : GossipFields("body", ReadString(data, "distortion", "none") == "invented_detail" ? "Traveler spent the night with me." : "Did you speak with Traveler about the tournament?",
                    "sourceQuote", ReadString(data, "sourceQuote", ""), "distortionSeverity", ReadString(data, "distortion", "none"), "account", "firsthand");
            try
            {
                string localId = GossipTestSeed("local_", id => GossipRoll(id, "share") <= 85);
                using (var c = OpenCampaignConnection(campaign))
                {
                    EnsurePersonalGossipSchema(c); EnsureMbtiRelationshipSchema(c); EnsureCategorizedMemorySchema(c); EnsureMemoryPrecisionSchema(c);
                    GossipTestPair(c, "source", "target", 50, -60);
                    check("directional_authority_not_native_average", GossipAffinity(c, "source", "target", source, target) == 50
                        && GossipAffinity(c, "target", "source", target, source) == -60, null);
                    GossipAcceptPresence(c, presence(20));
                    var stale = presence(20); ReadDictionary(stale, "gossipPresence")["worldDay"] = 19d;
                    check("stale_native_snapshot_rejected", !GossipAcceptPresence(c, stale), null);
                    var partial = NewTopic(c, "incomplete_roster", 20, true); partial["presenceComplete"] = false;
                    EvaluatePersonalGossip(c, campaign, partial, people);
                    check("partial_roster_cannot_select_audience", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='incomplete_roster';").Count == 0, null);
                    var topic = NewTopic(c, localId, 20, false);
                    EvaluatePersonalGossip(c, campaign, topic, people);
                    for (int i = 0; i < 100; i++) EvaluatePersonalGossip(c, campaign, topic, people);
                    check("one_queue_item_per_topic", QuerySql(c, "SELECT * FROM personal_gossip_messages;").Count == 1, null);
                    check("queue_has_no_recipient_history", QuerySql(c, "SELECT * FROM personal_gossip_history;").Count == 0, null);
                }
                RunPersonalGossipWork(campaign, Compose);
                PersonalGossipTickApi(presence(20 + .9 / 24));
                using (var c = OpenCampaignConnection(campaign))
                    check("before_one_hour_no_history", QuerySql(c, "SELECT * FROM personal_gossip_history;").Count == 0, null);
                source["settlementId"] = "town_left"; target["settlementId"] = "town_other";
                var arrived = PersonalGossipTickApi(presence(20 + 1d / 24));
                check("departure_keeps_local_delivery", ReadInt(arrived, "delivered", 0) == 1, arrived);
                using (var c = OpenCampaignConnection(campaign))
                {
                    var receipt = QuerySql(c, "SELECT * FROM personal_gossip_history WHERE owner_id='target';").Single();
                    check("delivered_claim_not_private_source", !ReadString(receipt, "payload_json", "").Contains("sourceText")
                        && !ReadString(receipt, "payload_json", "").Contains("distortion") && ReadDouble(receipt, "acquired_day", 0) >= 20 + 1d / 24, receipt);
                    check("return_not_known_before_composed", !QuerySql(c, "SELECT * FROM personal_gossip_history WHERE owner_id='source';")
                        .Any(r => ReadString(GossipJson(r), "claimKind", "") == "reply"), null);
                }
                RunPersonalGossipWork(campaign, Compose);
                PersonalGossipTickApi(presence(20 + 1d / 24));
                using (var c = OpenCampaignConnection(campaign))
                {
                    check("local_reply_and_idempotent_receipts", QuerySql(c, "SELECT * FROM personal_gossip_history;").Count == 4, null);
                    var payload = presence(20 + 1d / 24); payload["playerHeroStringId"] = "player";
                    string own = BuildPersonalGossipPrompt(campaign, "target", payload, new List<string>());
                    string outsider = BuildPersonalGossipPrompt(campaign, "outsider", payload, new List<string>());
                    check("observer_private_recent_history", own.Contains("Did you speak with Traveler") && !outsider.Contains("Did you speak with Traveler"), null);
                    var rewind = presence(19); rewind["playerHeroStringId"] = "player";
                    check("future_history_filtered", !BuildPersonalGossipPrompt(campaign, "target", rewind, new List<string>()).Contains("Did you speak with Traveler"), null);
                }
                string remoteId = GossipTestSeed("remote_", id => GossipRoll(id, "share") <= 85);
                target["positionX"] = 160d;
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipAcceptPresence(c, presence(30));
                    var noAudience = NewTopic(c, "no_audience", 30, false);
                    EvaluatePersonalGossip(c, campaign, noAudience, people);
                    check("no_audience_no_job", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='no_audience';").Count == 0
                        && ReadString(QuerySql(c, "SELECT status FROM personal_gossip_topics WHERE topic_id='no_audience';").Single(), "status", "") == "no_audience", null);
                    EvaluatePersonalGossip(c, campaign, NewTopic(c, remoteId, 30, true), people);
                }
                RunPersonalGossipWork(campaign, Compose);
                target["settlementId"] = source["settlementId"];
                PersonalGossipTickApi(presence(31));
                using (var c = OpenCampaignConnection(campaign))
                    check("arrival_does_not_retry_no_audience_story", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='no_audience';").Count == 0, null);
                target["settlementId"] = "town_other";
                PersonalGossipTickApi(presence(32.99));
                using (var c = OpenCampaignConnection(campaign))
                    check("remote_not_early", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE root_id=$root AND owner_id='target';", GossipFields("root", remoteId)).Count == 0, null);
                PersonalGossipTickApi(presence(33));
                RunPersonalGossipWork(campaign, Compose);
                using (var c = OpenCampaignConnection(campaign))
                    check("remote_answer_waits_for_return", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='source';",
                        GossipFields("id", remoteId + "_out_reply")).Count == 0, null);
                PersonalGossipTickApi(presence(36));
                using (var c = OpenCampaignConnection(campaign))
                    check("remote_reply_arrives_once", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='source';",
                        GossipFields("id", remoteId + "_out_reply")).Count == 1, null);

                WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), spiteful);
                source["settlementId"] = "town_a"; target["settlementId"] = "town_a";
                string maliciousId = GossipTestSeed("malice_", id => GossipRoll(id, "share") <= 60 && GossipRoll(id, "negative") <= 57
                    && GossipRoll(id, "distortion") <= 71 && GossipRoll(id, "severity") >= 66);
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipTestPair(c, "source", "target", -60, -60);
                    GossipAcceptPresence(c, presence(40));
                    EvaluatePersonalGossip(c, campaign, NewTopic(c, maliciousId, 40, false, true), people);
                }
                RunPersonalGossipWork(campaign, Compose);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, snapshotId);
                PersonalGossipTickApi(presence(40.05));
                using (var c = OpenCampaignConnection(campaign))
                {
                    var recipientReceipt = GossipJson(QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='target';", GossipFields("id", maliciousId + "_out")).Single());
                    var authorReceipt = GossipJson(QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='source';", GossipFields("id", maliciousId + "_out")).Single());
                    check("intentional_false_event_separate_from_truth", ReadString(recipientReceipt, "body", "").Contains("spent the night")
                        && !recipientReceipt.ContainsKey("authorKnowsDistortion") && ReadBool(authorReceipt, "authorKnowsDistortion", false)
                        && QuerySql(c, "SELECT text FROM conversation_turns WHERE exchange_id=$id AND role='player';", GossipFields("id", maliciousId + "_exchange"))
                            .All(r => ReadString(r, "text", "").Contains("talked privately")), null);
                    check("gossip_never_changes_relationship", GossipAffinity(c, "target", "source", target, source) == -60, null);
                }
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, snapshotId);
                AdvanceMemoryRestoreGeneration(campaign);
                using (var c = OpenCampaignConnection(campaign))
                {
                    check("save_restore_rewinds_receipts_retains_route", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE root_id=$root;", GossipFields("root", maliciousId)).Count == 0
                        && ReadString(QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", maliciousId + "_out")).Single(), "status", "") == "in_transit", null);
                    ExecuteSql(c, "UPDATE conversation_turns SET status='invalidated' WHERE exchange_id=$id;", GossipFields("id", maliciousId + "_exchange"));
                }
                PersonalGossipTickApi(presence(40.05));
                using (var c = OpenCampaignConnection(campaign))
                    check("invalidated_source_cancels_undelivered", ReadString(QuerySql(c, "SELECT status FROM personal_gossip_messages WHERE message_id=$id;",
                        GossipFields("id", maliciousId + "_out")).Single(), "status", "") == "cancelled_unavailable", null);

                WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), excitable);
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipTestPair(c, "source", "target", 60, 60);
                    EnsureConversationRelationshipSchema(c);
                    GossipAcceptPresence(c, presence(50));
                    foreach (int sign in new[] { -1, 0, 1 })
                    {
                        string impressionId = GossipTestSeed("impression_" + sign + "_", id => GossipRoll(id, "share") <= 85 && GossipRoll(id, "disclosure") <= 50);
                        var impression = NewTopic(c, impressionId, 50, false);
                        impression["category"] = "impression";
                        impression["signal"] = GossipFields("privacy", "personal", "existencePrivacy", "public");
                        ExecuteSql(c, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,native_application_status,created_ts)
VALUES($id,$campaign,'main',$exchange,'source','player',$delta,'applied','pending',1);",
                            GossipFields("id", impressionId, "campaign", campaign, "exchange", impressionId + "_exchange", "delta", 5 * sign));
                        EvaluatePersonalGossip(c, campaign, impression, people);
                        var queued = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", impressionId));
                        check("applied_delta_impression_" + sign, sign == 0 ? queued.Count == 0 : queued.Count == 1
                            && ReadString(GossipJson(queued[0]), "type", "") == "impression", null);
                        if (sign != 0)
                        {
                            var composed = GossipFields("body", "We kissed at the castle and exchanged gifts.");
                            bool valid = ValidatePersonalGossipComposition(c, GossipJson(queued[0]), composed, false);
                            check("impression_contains_no_private_detail_" + sign, valid && ReadString(composed, "body", "").Contains(sign > 0 ? "good impression" : "bad impression")
                                && !ReadString(composed, "body", "").Contains("castle"), composed);
                        }
                    }
                    var mixed = NewTopic(c, "mixed_zero", 50, false);
                    mixed["category"] = "impression";
                    ExecuteSql(c, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,created_ts)
VALUES('mixed_receipt',$campaign,'main','mixed_zero_exchange','source','player',0,'applied',1);", GossipFields("campaign", campaign));
                    EvaluatePersonalGossip(c, campaign, mixed, people);
                    check("capped_to_zero_not_positive_prose", QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id='mixed_zero';").Count == 0, null);
                    var secretTraits = GossipTestTraits("sociability", 100, "curiosity", 100, "impulsiveness", 0, "tact", 100, "discipline", 100, "empathy", 100, "honesty", 100, "loyalty", 100);
                    WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), secretTraits);
                    GossipTestPair(c, "source", "player", 100, 0);
                    string secretId = GossipTestSeed("private_", id => GossipRoll(id, "share") <= 65);
                    var secret = NewTopic(c, secretId, 51, false);
                    var privateSignal = ReadDictionary(secret, "signal");
                    privateSignal["privacy"] = "secret"; privateSignal["existencePrivacy"] = "secret";
                    privateSignal["promisedSecrecy"] = true; privateSignal["confidenceOwnerIds"] = new[] { "player" };
                    ExecuteSql(c, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,created_ts)
VALUES($id,$campaign,'main',$exchange,'source','player',5,'applied',1);", GossipFields("id", secretId, "campaign", campaign, "exchange", secretId + "_exchange"));
                    EvaluatePersonalGossip(c, campaign, secret, people);
                    check("discretion_refuses_private_details", QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", secretId)).Count == 0
                        && ReadString(QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", secretId)).Single(), "status", "") == "privacy_declined", null);
                    var failure = GossipFields("error", "Injected provider failure");
                    check("provider_failure_never_becomes_placeholder_reply", !ValidatePersonalGossipComposition(c, GossipFields(), failure, true), null);
                    var inventedReply = GossipFields("body", "Yes, I spent the night with Traveler.", "sourceQuote", "", "answerKind", "acknowledged");
                    check("unsupported_reply_cannot_invent_answer", ValidatePersonalGossipComposition(c, GossipFields(), inventedReply, true)
                        && ReadString(inventedReply, "body", "") == "I will think about what you said.", null);
                }
                WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), excitable);
                string retryId = GossipTestSeed("retry_", id => GossipRoll(id, "share") <= 85);
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipAcceptPresence(c, presence(60));
                    EvaluatePersonalGossip(c, campaign, NewTopic(c, retryId, 60, false), people);
                }
                RunPersonalGossipWork(campaign, (data, reply) => GossipFields("error", "Injected failure"));
                RunPersonalGossipWork(campaign, (data, reply) => GossipFields("error", "Injected failure"));
                using (var c = OpenCampaignConnection(campaign))
                {
                    var failed = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", retryId + "_out")).Single();
                    check("three_attempts_keep_recipient_and_no_history", ReadInt(failed, "attempts", 0) == 3 && ReadString(failed, "status", "") == "generation_failed"
                        && ReadString(failed, "recipient_id", "") == "target" && QuerySql(c, "SELECT * FROM personal_gossip_history WHERE root_id=$id;", GossipFields("id", retryId)).Count == 0, null);
                }
                string generationId = GossipTestSeed("generation_", id => GossipRoll(id, "share") <= 85);
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipAcceptPresence(c, presence(65));
                    EvaluatePersonalGossip(c, campaign, NewTopic(c, generationId, 65, false), people);
                }
                int compositions = 0;
                RunPersonalGossipWork(campaign, (data, reply) =>
                {
                    if (ReadString(data, "id", "") == generationId && ++compositions == 1) AdvanceMemoryRestoreGeneration(campaign);
                    return Compose(data, reply);
                });
                using (var c = OpenCampaignConnection(campaign))
                {
                    var generated = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", generationId + "_out")).Single();
                    check("restore_generation_discards_stale_result", compositions == 2 && ReadInt(generated, "attempts", 0) == 2
                        && ReadString(generated, "status", "") == "in_transit", null);
                }

                var third = GossipTestHero("third"); people.Add(third);
                WriteJsonObject(CharacterFile(campaign, "target", "traits.json"), excitable);
                string retellId = GossipTestSeed("retell_", id => GossipRoll(id, "share") <= 85
                    && GossipRoll(id + "_via_" + PromptHash("target").Substring(0, 12).ToLowerInvariant(), "share") <= 25);
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipTestPair(c, "target", "third", 60, 30);
                    GossipAcceptPresence(c, presence(80));
                    var topic = NewTopic(c, retellId, 80, false);
                    ReadDictionary(topic, "signal")["question"] = false;
                    EvaluatePersonalGossip(c, campaign, topic, people);
                }
                Dictionary<string, object> ComposeRetell(Dictionary<string, object> data, bool reply)
                {
                    if (!reply) return GossipFields("body", (ReadInt(data, "hop", 1) > 1 ? "Source said: " : "") + ReadString(data, "sourceQuote", ""), "sourceQuote", ReadString(data, "sourceQuote", ""),
                        "distortionSeverity", "none", "account", ReadInt(data, "hop", 1) > 1 ? "secondhand" : "firsthand");
                    return GossipFields("body", "That is interesting news.", "sourceQuote", "", "answerKind", "acknowledged", "gossipSignals", new[] {
                        GossipFields("confidence", 1d, "evidenceQuote", ReadString(data, "receivedBody", ""), "excitement", 100, "privacy", "public", "existencePrivacy", "public") });
                }
                RunPersonalGossipWork(campaign, ComposeRetell);
                PersonalGossipTickApi(presence(80.05));
                RunPersonalGossipWork(campaign, ComposeRetell);
                PersonalGossipTickApi(presence(80.10));
                RunPersonalGossipWork(campaign, ComposeRetell);
                using (var c = OpenCampaignConnection(campaign))
                {
                    var outward = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE root_id=$root AND is_reply=0;", GossipFields("root", retellId));
                    check("one_onward_hop_and_no_loop", outward.Count == 2 && outward.Any(r => ReadString(r, "recipient_id", "") == "third" && ReadInt(r, "hop", 0) == 2)
                        && outward.All(r => ReadString(r, "recipient_id", "") != "source"), outward.Select(r => ReadString(r, "recipient_id", "")).ToList());
                    var pastLimit = NewTopic(c, "past_hop_limit", 80, true); pastLimit["hop"] = 3;
                    EvaluatePersonalGossip(c, campaign, pastLimit, people);
                    var expired = NewTopic(c, "expired_root", 80, true); expired["rootCreatedDay"] = 60;
                    EvaluatePersonalGossip(c, campaign, expired, people);
                    check("hop_and_root_expiry_prevent_new_jobs", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id IN ('past_hop_limit','expired_root');").Count == 0, null);
                    string sharedSession = "capacity_conversation";
                    for (int i = 0; i < 4; i++)
                    {
                        var cappedTopic = NewTopic(c, GossipTestSeed("capacity" + i + "_", id => GossipRoll(id, "share") <= 85), 90, false);
                        if (i < 3) cappedTopic["sessionId"] = sharedSession;
                        EvaluatePersonalGossip(c, campaign, cappedTopic, people);
                    }
                    var daily = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE speaker_id='source' AND is_reply=0 AND created_day=90;");
                    check("two_per_conversation_three_per_day", daily.Count == 3 && QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE session_id=$session AND status='queued';", GossipFields("session", sharedSession)).Count == 2, null);

                    double acceptedDay = 100;
                    while (GossipRoll(GossipTopicId("main", "source", "player", acceptedDay, "conversation", ""), "share") > 85) acceptedDay++;
                    var acceptedTopic = NewTopic(c, "accepted_hook", acceptedDay, false);
                    var acceptedPayload = presence(acceptedDay);
                    acceptedPayload["playerHeroStringId"] = "player";
                    var acceptedExchange = GossipFields("sessionId", ReadString(acceptedTopic, "sessionId", ""),
                        "exchangeId", ReadString(acceptedTopic, "sourceExchangeId", ""), "turnIds", ReadStringList(acceptedTopic, "sourceTurnIds"));
                    var acceptedParsed = GossipFields("gossipSignals", new[] { ReadDictionary(acceptedTopic, "signal") });
                    for (int i = 0; i < 3; i++) AcceptPersonalGossip(campaign, acceptedPayload, acceptedParsed, acceptedExchange, "source", "player",
                        ReadString(acceptedTopic, "sourceText", ""), "That was an interesting conversation.");
                    string acceptedId = GossipTopicId("main", "source", "player", acceptedDay, "conversation", "");
                    check("accepted_exchange_hook_is_idempotent", QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", acceptedId)).Count == 1, null);

                    // Private storage identities protect the source. The enclosing conversation
                    // still owns its capacity and the native adjudication's exchange identity.
                    string[] categories = { "family", "politics", "gift" };
                    double whisperDay = 140;
                    while (categories.Any(category => GossipRoll(GossipTopicId("main", "source", "player", whisperDay, category, ""), "share") > 85)) whisperDay++;
                    var parent = NewTopic(c, "whisper_parent", whisperDay, false);
                    string parentSession = ReadString(parent, "sessionId", "");
                    bool privateReferencesPreserved = true;
                    for (int i = 0; i < categories.Length; i++)
                    {
                        var privateTopic = NewTopic(c, "private_turn_" + i, whisperDay, false);
                        ReadDictionary(privateTopic, "signal")["topic"] = categories[i];
                        var publicExchange = StoreConversationExchange(campaign,
                            GossipFields("conversationSessionId", parentSession, "worldDay", whisperDay, "timelineId", "main", "channel", "party_chat"),
                            "source", "player", "Traveler", "Source", "The player speaks quietly to Source.", "Source replies privately.",
                            "parent_whisper_exchange_" + i, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        var privateExchange = GossipFields("sessionId", ReadString(privateTopic, "sessionId", ""),
                            "exchangeId", ReadString(privateTopic, "sourceExchangeId", ""), "turnIds", ReadStringList(privateTopic, "sourceTurnIds"));
                        AcceptPersonalGossip(campaign, presence(whisperDay), GossipFields("gossipSignals", new[] { ReadDictionary(privateTopic, "signal") }),
                            privateExchange, "source", "player", ReadString(privateTopic, "sourceText", ""), "That was an interesting conversation.", publicExchange);
                        var decision = GossipJson(QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;",
                            GossipFields("id", GossipTopicId("main", "source", "player", whisperDay, categories[i], ""))).Single());
                        privateReferencesPreserved &= ReadString(decision, "sourceSessionId", "") == ReadString(privateTopic, "sessionId", "")
                            && ReadString(decision, "sessionId", "") == parentSession
                            && ReadStringList(decision, "sourceTurnIds").SequenceEqual(ReadStringList(privateTopic, "sourceTurnIds"));
                    }
                    check("private_whispers_share_parent_conversation_cap", QuerySql(c,
                        "SELECT topic_id FROM personal_gossip_topics WHERE session_id=$session AND status='queued';", GossipFields("session", parentSession)).Count == 2,
                        QuerySql(c, "SELECT topic_id,status FROM personal_gossip_topics WHERE session_id=$session;", GossipFields("session", parentSession)));
                    check("private_whisper_source_ids_remain_private", privateReferencesPreserved, null);

                    double receiptDay = 160;
                    while (GossipRoll(GossipTopicId("main", "source", "player", receiptDay, "impression", ""), "share") > 85
                        || GossipRoll(GossipTopicId("main", "source", "player", receiptDay, "impression", ""), "disclosure") > 80) receiptDay++;
                    var receiptParent = NewTopic(c, "receipt_parent", receiptDay, false);
                    var receiptPrivate = NewTopic(c, "receipt_private", receiptDay, false);
                    var receiptEpisode = GossipFields("sessionId", ReadString(receiptParent, "sessionId", ""),
                        "exchangeId", ReadString(receiptParent, "sourceExchangeId", ""));
                    var receiptSource = GossipFields("sessionId", ReadString(receiptPrivate, "sessionId", ""),
                        "exchangeId", ReadString(receiptPrivate, "sourceExchangeId", ""), "turnIds", ReadStringList(receiptPrivate, "sourceTurnIds"));
                    GossipTestPair(c, "source", "player", 0, 0);
                    AcceptPersonalGossip(campaign, presence(receiptDay), GossipFields(), receiptSource, "source", "player",
                        ReadString(receiptPrivate, "sourceText", ""), "That was an interesting conversation.", receiptEpisode);
                    string receiptTopicId = GossipTopicId("main", "source", "player", receiptDay, "impression", "");
                    bool noProposal = QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", receiptTopicId)).Count == 0;
                    ExecuteSql(c, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,native_application_status,created_ts)
VALUES('whisper_receipt',$campaign,'main',$exchange,'source','player',5,'applied','pending',1);",
                        GossipFields("campaign", campaign, "exchange", ReadString(receiptParent, "sourceExchangeId", "")));
                    PersonalGossipRelationshipApplied(campaign, GossipFields("exchangeId", ReadString(receiptParent, "sourceExchangeId", ""), "worldDay", receiptDay));
                    var receiptJobs = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", receiptTopicId));
                    check("parent_receipt_activates_private_impression", noProposal && receiptJobs.Count == 1
                        && ReadString(GossipJson(receiptJobs[0]), "type", "") == "impression"
                        && ReadStringList(GossipJson(receiptJobs[0]), "sourceTurnIds").SequenceEqual(ReadStringList(receiptPrivate, "sourceTurnIds")),
                        QuerySql(c, "SELECT status,payload_json FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", receiptTopicId)));
                }
            }
            finally
            {
                ReignPostgreSqlStorage.DropSnapshot(campaign, snapshotId);
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
        }
    }
}
