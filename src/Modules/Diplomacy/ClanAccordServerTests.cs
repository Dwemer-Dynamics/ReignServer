using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunClanAccordServerSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> add = (id, passed) => rows.Add(new Dictionary<string, object> {
                ["id"] = "clan_accords.server." + id, ["suite"] = "clan_accords", ["passed"] = passed,
                ["summary"] = id, ["evidence"] = new Dictionary<string, object> { ["layer"] = "deterministic-server-contract" } });
            var record = new Dictionary<string, object> {
                ["Id"] = "accord:test", ["ActionId"] = "action:test", ["Type"] = 0,
                ["PlayerClanId"] = "player-clan", ["PartnerClanId"] = "partner-clan",
                ["PlayerArrangerId"] = "player", ["NpcArrangerId"] = "npc", ["StartDay"] = 1d, ["IsActive"] = true
            };
            var payload = new Dictionary<string, object> {
                ["campaignId"] = "contract-test", ["timelineId"] = "main", ["playerHeroId"] = "player",
                ["playerClanId"] = "player-clan", ["playerClanTier"] = 2,
                ["ledger"] = new Dictionary<string, object> { ["Records"] = new[] { record } },
                ["events"] = new Dictionary<string, object>[0]
            };
            add("initial_snapshot_without_events", ValidateClanAccordsSync(payload) == "");
            payload["timelineId"] = "";
            add("timeline_required", ValidateClanAccordsSync(payload) == "timelineId_required");
            payload["timelineId"] = "main";
            record["PlayerClanId"] = "other";
            add("cross_clan_rejected", ValidateClanAccordsSync(payload) == "player_clan_scope_mismatch");
            record["PlayerClanId"] = "player-clan";
            record["Type"] = 99;
            add("unknown_type_rejected", ValidateClanAccordsSync(payload) == "unsupported_accord_type");
            record["Type"] = 0;
            var evt = new Dictionary<string, object> {
                ["eventId"] = "season:test", ["kind"] = "season", ["worldDay"] = 21d,
                ["playerHeroId"] = "player", ["partnerClanId"] = "partner-clan", ["year"] = 1084, ["season"] = 1,
                ["knownHeroIds"] = new[] { "player", "npc" }, ["partnerMemberIds"] = new[] { "npc" }
            };
            payload["events"] = new[] { evt };
            add("season_with_live_accord", ValidateClanAccordsSync(payload) == "");
            evt["partnerClanId"] = "unrelated";
            add("season_requires_partner_accord", ValidateClanAccordsSync(payload) == "season_without_accord");
            evt["partnerClanId"] = "partner-clan";
            payload["events"] = Enumerable.Range(0, 65).Select(x => evt).ToArray();
            add("bounded_event_batch", ValidateClanAccordsSync(payload) == "too_many_events");

            var action = new Dictionary<string, object> { ["actorHeroStringId"] = "npc", ["targetHeroStringId"] = "player" };
            var terms = new Dictionary<string, object> { ["consentConfirmed"] = true, ["playerConfirmed"] = true, ["accordType"] = "Trade" };
            Func<string, string, string> validate = (player, reply) => ValidateClanAccordDialogueAction("create_clan_accord", action, terms, player, reply, "npc", "player");
            add("mutual_present_acceptance", validate("Agreed, let us establish trade cooperation.", "I agree to trade cooperation for our clans.") == "");
            add("proposal_only_rejected", validate("Could we have a trade accord?", "Perhaps we could discuss that.") != "");
            add("conditional_rejected", validate("Agreed to trade cooperation.", "I agree if you pay first.") != "");
            add("refusal_rejected", validate("Let us establish trade cooperation.", "I will not agree to trade cooperation.") != "");
            add("past_negative_acceptance_rejected", validate("Agreed to trade.", "I have never agreed to trade.") != "");
            add("third_party_acceptance_rejected", validate("Agreed to trade.", "My brother agreed to trade.") != "");
            add("stage_direction_not_consent", validate("Agreed to trade.", "*I agree to trade.* We should discuss matters.") != "");
            add("unrelated_acceptance_rejected", validate("Yes, agreed.", "I agree to the marriage.") != "");
            add("discussion_only_rejected", validate("Let us discuss a trade agreement.", "Yes, trade deserves discussion.") != "");
            add("incidental_would_is_not_conditional", validate("I accept the trade accord. Our merchants would benefit.", "I accept the trade accord.") == "");
            add("conditional_would_commit_rejected", validate("I would accept the trade accord.", "I accept the trade accord.") != "");
            var bindPayload = new Dictionary<string, object> {
                ["heroStringId"] = "npc", ["playerHeroStringId"] = "player",
                ["playerText"] = "I agree to trade.\nDo not finalize anything yet.",
                ["clanAccords"] = new Dictionary<string, object> { ["playerRuler"] = true, ["eligible"] = true, ["partnerClanId"] = "partner-clan" }
            };
            var bindErrors = new List<string>();
            BindAndValidateClanAccord(action, terms, bindPayload,
                BuildRouterResolutionText(ReadString(bindPayload, "playerText", ""), "I agree to trade."), "create_clan_accord", bindErrors);
            add("multiline_actual_player_turn_preserved", bindErrors.Contains("clan_accord_conditional_or_refused"));
            terms.Remove("playerConfirmed");
            add("no_inferred_player_consent", validate("Agreed to trade.", "I agree to trade.") != "");
            terms["playerConfirmed"] = true;
            action["actorHeroStringId"] = "third_person";
            add("third_party_actor_rejected", validate("Agreed to trade.", "I agree to trade.") != "");
            action["actorHeroStringId"] = "npc";
            terms["accordId"] = "accord:test";
            add("cancellation_player_decision", ValidateClanAccordDialogueAction("cancel_clan_accord", action, terms,
                "End our trade accord.", "I regret your decision.", "npc", "player") == "");
            add("cancellation_question_rejected", ValidateClanAccordDialogueAction("cancel_clan_accord", action, terms,
                "Should we end our trade accord?", "That is your decision.", "npc", "player") != "");
            add("cancel_does_not_create", validate("Let us cancel our trade accord.", "Agreed.") != "");
            AddClanAccordPersistenceTests(rows);
            return rows;
        }

        private static void AddClanAccordPersistenceTests(List<Dictionary<string, object>> rows)
        {
            string campaign = "__clan_accord_self_test_" + Guid.NewGuid().ToString("N");
            Action<string, bool, string> add = (id, passed, detail) => rows.Add(new Dictionary<string, object> {
                ["id"] = "clan_accords.persistence." + id, ["name"] = "clan_accords.persistence." + id,
                ["suite"] = "clan_accords", ["passed"] = passed, ["summary"] = detail,
                ["evidence"] = new Dictionary<string, object> { ["layer"] = "isolated-campaign-database", ["campaignId"] = campaign } });
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,affinity_a_to_b,affinity_b_to_a,
tag_a_to_b,tag_b_to_a,first_day,last_day,projected_native_relation,updated_ts)
VALUES('npc|player','npc','player','INTJ','ESFP',18,25,'neutral','neutral',1,1,18,1);");
                }
                var record = new Dictionary<string, object> {
                    ["Id"] = "accord:test", ["ActionId"] = "action:test", ["Type"] = 0,
                    ["PlayerClanId"] = "player-clan", ["PartnerClanId"] = "partner-clan",
                    ["PlayerArrangerId"] = "player", ["NpcArrangerId"] = "npc",
                    ["PlayerArrangerName"] = "Player", ["NpcArrangerName"] = "Lesser Noble",
                    ["StartDay"] = 1d, ["IsActive"] = true
                };
                var evt = new Dictionary<string, object> {
                    ["eventId"] = "formed:test", ["kind"] = "formed", ["worldDay"] = 1d,
                    ["playerHeroId"] = "player", ["record"] = CloneDictionary(record),
                    ["knownHeroIds"] = new[] { "player", "npc", "clan_leader" }, ["partnerMemberIds"] = new[] { "npc" }
                };
                var payload = new Dictionary<string, object> {
                    ["campaignId"] = campaign, ["timelineId"] = "main", ["playerHeroId"] = "player",
                    ["playerClanId"] = "player-clan", ["playerClanTier"] = 2,
                    ["ledger"] = new Dictionary<string, object> { ["Records"] = new[] { record } }, ["events"] = new[] { evt }
                };
                add("formation", ReadBool(ClanAccordsSyncApi(payload), "ok", false), "Native formation commits a server snapshot and scoped attributed memory.");
                string originalMemory;
                using (var connection = OpenCampaignConnection(campaign))
                    originalMemory = Json.Serialize(QuerySql(connection, "SELECT memory_id FROM memories ORDER BY memory_id;"));
                add("retry_ack", ReadBool(ClanAccordsSyncApi(payload), "ok", false), "Lost native acknowledgement can replay the exact event.");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    add("memory_identity_stable", originalMemory == Json.Serialize(QuerySql(connection, "SELECT memory_id FROM memories ORDER BY memory_id;")), "Replay retains memory identities without delete/regenerate.");
                    var memory = QuerySql(connection, "SELECT visibility,known_by_json,summary FROM events WHERE event_id='clan_accord:main:formed:test';").FirstOrDefault();
                    add("memory_scope_attribution", ReadString(memory, "visibility", "") == "private"
                        && ReadString(memory, "known_by_json", "").Contains("clan_leader")
                        && ReadString(memory, "summary", "").Contains("Lesser Noble"), "Private clan knowledge names the actual lesser-member arranger.");
                    ExecuteSql(connection, "UPDATE clan_accord_event_receipts SET memory_done=0 WHERE event_id='formed:test';");
                }
                ClanAccordsSyncApi(payload);
                using (var connection = OpenCampaignConnection(campaign))
                    add("receipt_crash_recovery", originalMemory == Json.Serialize(QuerySql(connection, "SELECT memory_id FROM memories ORDER BY memory_id;")), "Crash between memory storage and receipt completion preserves existing complete projection.");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, "DELETE FROM knowledge_receipts WHERE event_id='clan_accord:main:formed:test' AND npc_id='clan_leader';");
                    ExecuteSql(connection, "UPDATE clan_accord_event_receipts SET memory_done=0 WHERE event_id='formed:test';");
                }
                ClanAccordsSyncApi(payload);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    add("failure_after_participants_restores_clan_knowledge",
                        QuerySql(connection, "SELECT receipt_id FROM knowledge_receipts WHERE event_id='clan_accord:main:formed:test' AND npc_id='clan_leader' AND status='active';").Count == 1
                        && originalMemory == Json.Serialize(QuerySql(connection, "SELECT memory_id FROM memories ORDER BY memory_id;"))
                        && ReadInt(QuerySql(connection, "SELECT memory_done FROM clan_accord_event_receipts WHERE event_id='formed:test';").First(), "memory_done", 0) == 1,
                        "Interrupted knowledge tail is completed for the clan leader before acknowledgement, without regenerating participant memories.");
                }
                evt = new Dictionary<string, object> {
                    ["eventId"] = "season:test", ["kind"] = "season", ["worldDay"] = 21d, ["playerHeroId"] = "player",
                    ["partnerClanId"] = "partner-clan", ["year"] = 1084, ["season"] = 1,
                    ["partnerMemberIds"] = new[] { "npc", "npc" }
                };
                payload["events"] = new[] { evt };
                add("season_commits", ReadBool(ClanAccordsSyncApi(payload), "ok", false), "Season event is accepted without a record object.");
                evt["eventId"] = "season:same-tick-new-id";
                ClanAccordsSyncApi(payload);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var pair = QuerySql(connection, "SELECT affinity_a_to_b,affinity_b_to_a FROM relationship_pair_chemistry WHERE pair_key='npc|player';").First();
                    add("directional_cap_and_season_receipts", ReadInt(pair, "affinity_a_to_b", 0) == 20 && ReadInt(pair, "affinity_b_to_a", 0) == 25,
                        "18 becomes 20, 25 stays 25; duplicate member and alternate event ID cannot double-award a season.");
                    add("native_target_projection", QuerySql(connection, "SELECT pair_key FROM relationship_native_targets WHERE pair_key='npc|player';").Count == 1,
                        "Authoritative relationships publish one native target for the pair, not direct native deltas.");
                }
                record["IsActive"] = false; record["EndReason"] = 1; record["EndDay"] = 22d;
                record["EndActionId"] = "cancel:test"; record["EndActorId"] = "player"; record["EndActorName"] = "Player";
                evt = new Dictionary<string, object> {
                    ["eventId"] = "cancel:test", ["kind"] = "ended", ["worldDay"] = 22d, ["playerHeroId"] = "player",
                    ["record"] = CloneDictionary(record), ["endReason"] = "Cancelled", ["partnerMemberIds"] = new[] { "npc" },
                    ["knownHeroIds"] = new[] { "player", "npc", "clan_leader" }
                };
                payload["events"] = new[] { evt };
                ClanAccordsSyncApi(payload); ClanAccordsSyncApi(payload);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var pair = QuerySql(connection, "SELECT affinity_a_to_b,affinity_b_to_a FROM relationship_pair_chemistry WHERE pair_key='npc|player';").First();
                    add("cancellation_once", ReadInt(pair, "affinity_a_to_b", 0) == 10 && ReadInt(pair, "affinity_b_to_a", 0) == 15,
                        "One cancellation costs ten in each direction despite replay.");
                }
                add("snapshot_reopen", ReadDictionary(ClanAccordsSnapshot(campaign, "main"), "ledger") != null,
                    "A fresh database connection reloads the persisted ledger.");
                add("timeline_isolation", !ReadBool(ClanAccordsSnapshot(campaign, "other"), "available", true), "A different timeline cannot read this snapshot.");
                var history = Enumerable.Range(0, 105).Select(i => {
                    var ended = CloneDictionary(record); ended["Id"] = "history:" + i; ended["ActionId"] = "history-action:" + i;
                    ended["StartDay"] = 30d + i; ended["EndDay"] = 31d + i; return ended;
                }).ToList();
                var oldActive = CloneDictionary(record);
                oldActive["Id"] = "accord:old-active"; oldActive["ActionId"] = "old-active-action";
                oldActive["IsActive"] = true; oldActive["EndReason"] = 0; oldActive["EndDay"] = null;
                oldActive["EndActionId"] = ""; oldActive["EndActorId"] = ""; oldActive["EndActorName"] = "";
                history.Add(oldActive);
                payload["ledger"] = new Dictionary<string, object> { ["Records"] = history };
                payload["events"] = new Dictionary<string, object>[0];
                ClanAccordsSyncApi(payload);
                add("active_prompt_survives_large_history", BuildClanAccordsPrompt(campaign, "main", "npc", "partner-clan").Contains("ACTIVE accord:old-active"),
                    "An old active accord is retained before the bounded recent terminated history.");
            }
            catch (Exception ex) { add("database_fixture", false, ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                // Campaign files and PostgreSQL schemas have independent lifetimes.
                // Drop only this freshly generated internal fixture, never a caller-supplied ID.
                try { ReignPostgreSqlStorage.DropCampaign(campaign); }
                catch (Exception ex) { add("fixture_cleanup", false, "Could not remove isolated fixture storage: " + ex.Message); }
                string root = Path.GetFullPath(CampaignsRoot()) + Path.DirectorySeparatorChar;
                string path = Path.GetFullPath(CampaignDirectory(campaign));
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) == campaign)
                    TryDeleteDirectory(path);
            }
        }
    }
}
