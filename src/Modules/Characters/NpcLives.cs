using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureNpcLivesSchema(ReignDbConnection c) => ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS character_npc_lives_state(
timeline_id TEXT PRIMARY KEY, value_json TEXT NOT NULL);");

        private static NpcLivesState ReadNpcLives(ReignDbConnection c, string campaign, string timeline)
        {
            EnsureNpcLivesSchema(c);
            string json = ReadString(QuerySql(c, "SELECT value_json FROM character_npc_lives_state WHERE timeline_id=$timeline;",
                TestDict("timeline", timeline)).FirstOrDefault(), "value_json", "");
            var state = json.Length == 0 ? new NpcLivesState { CampaignId = campaign, TimelineId = timeline } : Json.Deserialize<NpcLivesState>(json);
            if (state.Version != 1 || state.CampaignId != campaign || state.TimelineId != timeline)
                throw new InvalidOperationException("The saved NPC Lives state does not match this campaign and timeline.");
            return state;
        }
        private static void WriteNpcLives(ReignDbConnection c, NpcLivesState state)
        {
            state.Revision++;
            ExecuteSql(c, @"INSERT INTO character_npc_lives_state(timeline_id,value_json) VALUES($timeline,$json)
ON CONFLICT(timeline_id) DO UPDATE SET value_json=EXCLUDED.value_json;", TestDict("timeline", state.TimelineId, "json", Json.Serialize(state)));
        }
        private static string NpcLivesGeneration(ReignDbConnection c) => ReadString(ReadMemoryPrecisionState(c), "restore_generation", "");
        private static int NpcLivesAffinity(ReignDbConnection c, string hero, string player, int native = 0)
        {
            EnsureMbtiRelationshipSchema(c);
            var pair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                TestDict("pair", AmbientPairKey(hero, player))).FirstOrDefault();
            return pair == null ? native : EffectiveDirectionalAffinity(pair, ReadString(pair, "hero_a_id", "") == hero, native);
        }
        private static Dictionary<string, object> NpcLivesSnapshot(ReignDbConnection c, NpcLivesState state, string invitation = "") =>
            TestDict("ok", true, "generation", NpcLivesGeneration(c), "state", state, "invitationId", invitation);

        private static Dictionary<string, object> NpcLivesApi(string operation, Dictionary<string, object> p)
        {
            if (operation == "editor-list" || operation == "editor-save") return NpcStoryEditorApi(operation, p);
            string campaign = ReadString(p, "campaignId", ""), timeline = ContinuityTimeline(p);
            string player = ReadFirstString(p, "playerId", "playerHeroStringId");
            double day = ReadDouble(p, "worldDay", -1);
            if (campaign.Length == 0 || player.Length == 0 || !NpcLivesPolicy.Finite(day) || day < 0)
                return TestDict("ok", false, "error", "NPC Lives requires campaign, player, timeline and native campaign time.");
            if (operation == "open" || operation == "opening") return NpcLivesOpenApi(operation, p);
            using var ownership = EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign));
            using var c = OpenCampaignConnection(campaign);
            var state = ReadNpcLives(c, campaign, timeline);
            string generation = NpcLivesGeneration(c);
            if (operation == "state") return NpcLivesSnapshot(c, state);
            if (operation == "history")
            {
                string storyId = ReadString(p, "npcStoryId", "");
                int revision = ReadInt(p, "npcLivesRevision", -1);
                var story = state.Stories.FirstOrDefault(s => s.Id == storyId);
                if (story == null || revision < 1 || revision > story.Revision)
                    return TestDict("ok", false, "error", "That story chapter does not exist.");
                string session = "npc_story_scene_" + storyId + "_" + revision;
                var lines = ReadOriginalConversationHistory(campaign, p, story.HeroId, session, 400, new List<Dictionary<string, object>>());
                return TestDict("ok", true, "lines", lines, "generation", generation);
            }
            if (ReadString(p, "generation", "") != generation)
                return TestDict("ok", false, "status", "stale_generation", "error", "The campaign was restored. Refresh NPC Lives before continuing.");
            var options = ReadNpcLivesOptions();
            using var transaction = c.BeginTransaction();
            string invitation = ""; Dictionary<string, object> inputReceipt = null;
            try
            {
            switch (operation)
            {
                case "test-fixture":
                    invitation = PrepareNpcLivesFixture(c, state, p, options, day);
                    break;
                case "evaluate":
                    string party = ReadString(p, "partyId", "");
                    var roster = ReadDictionaryList(p, "members").Take(512).Where(h => ReadBool(h, "inMainParty", false)
                        && ReadString(h, "partyId", "") == party && party.Length > 0).GroupBy(h => ReadString(h, "heroId", "")).Select(g => g.First());
                    var candidates = roster.Where(h => ReadString(h, "heroId", "").Length > 0 && ReadString(h, "heroId", "") != player).Select(h =>
                    {
                        string hero = ReadString(h, "heroId", "");
                        var traits = ReadJsonObject(CharacterFile(campaign, hero, "traits.json"));
                        return new NpcLifeCandidate { HeroId = hero, Affinity = NpcLivesAffinity(c, hero, player, ReadInt(h, "nativeRelation", 0)),
                            Honor = NarrativePersonalityScore(traits, h, "honor"), Compassion = NarrativePersonalityScore(traits, h, "compassion"),
                            DutyPressure = ReadBool(h, "isGovernor", false) || ReadBool(h, "isPartyLeader", false) ? 1 : 0,
                            Available = ReadBool(h, "alive", false) && ReadBool(h, "free", false) && ReadBool(h, "adult", false),
                            Interests = Json.Serialize(traits ?? TestDict()) };
                    }).ToList();
                    var locations = ReadDictionaryList(p, "locations").Take(2048).Select(l => new NpcStoryLocation { Id = ReadString(l, "id", ""),
                        Name = ReadString(l, "name", ""), Kind = ReadString(l, "kind", ""), Available = ReadBool(l, "available", false), Coastal = ReadBool(l, "coastal", false), TravelDays = ReadDouble(l, "travelDays", double.MaxValue) }).ToList();
                    invitation = NpcLivesEngine.Evaluate(state, candidates, ReadNpcStoryDefinitions(), locations, options, day, ReadString(p, "locationId", ""));
                    break;
                case "announcement":
                    inputReceipt = NpcLivesAnnouncement(c, state, p, player, options, day);
                    break;
                case "decision":
                    NpcLivesDecision(c, state, p, player, options, day);
                    break;
                case "native":
                    NpcLivesNativeReceipt(c, state, p, player, day);
                    break;
                case "communication_receipt":
                    AcceptCommunicationNativeReceipt(c, state, p, day);
                    break;
                case "acknowledge":
                    foreach (var row in ReadDictionaryList(p, "receipts").Take(128))
                    {
                        string id = ReadString(row, "id", "");
                        var effect = state.Effects.FirstOrDefault(e => e.Id == id);
                        if (effect == null || effect.Kind != "skill_xp" || ReadString(row, "nativeReceipt", "") != id
                            || ReadString(row, "heroId", "") != effect.HeroId) continue;
                        effect.Status = "applied"; effect.NativeReceipt = id;
                    }
                    break;
                default: return TestDict("ok", false, "error", "Unknown NPC Lives operation.");
            }
            WriteNpcLives(c, state); transaction.Commit();
            var snapshot = NpcLivesSnapshot(c, state, invitation);
            if (inputReceipt != null) snapshot["inputReceipt"] = inputReceipt;
            return snapshot;
            }
            catch (InvalidOperationException ex) { return TestDict("ok", false, "status", "rejected", "operationId", ReadString(p, "operationId", ""), "error", ex.Message); }
        }

        private static Dictionary<string, object> NpcLivesAnnouncement(ReignDbConnection c, NpcLivesState state, Dictionary<string, object> p,
            string player, NpcLivesOptions options, double day)
        {
            string id = ReadString(p, "operationId", ""), text = ReadString(p, "text", "");
            if (!Regex.IsMatch(id, "^announcement_[a-f0-9]{32}$") || string.IsNullOrWhiteSpace(text) || text.Length > 6000)
                throw new InvalidOperationException("An announcement requires its stable identity and 1–6000 characters.");
            var old = state.Announcements.FirstOrDefault(a => a.Id == id);
            if (old != null)
            {
                if (old.PlayerId != player || old.Text != text) throw new InvalidOperationException("This announcement identity already belongs to different words.");
                return PreserveNpcAnnouncementInput(c, state, p, old); // The original audience stays frozen across a transport retry.
            }
            if (!options.Allows("announcement")) throw new InvalidOperationException("Party announcements are disabled.");
            string party = ReadString(p, "partyId", "");
            var ids = ReadDictionaryList(p, "members").Take(512).Where(h => party.Length > 0 && ReadBool(h, "inMainParty", false)
                && ReadString(h, "partyId", "") == party && ReadBool(h, "alive", false) && ReadBool(h, "free", false))
                .Select(h => ReadString(h, "heroId", "")).Where(h => h.Length > 0 && h != player).Distinct(StringComparer.Ordinal).OrderBy(h => h).ToList();
            if (ids.Count == 0) throw new InvalidOperationException("No party members are gathered to hear this announcement.");
            var announcement = new NpcPartyAnnouncement { Id = id, PlayerId = player, Text = text, WorldDay = day,
                LocationId = ReadString(p, "locationId", ""), RecipientIds = ids };
            state.Announcements.Add(announcement);
            var audience = ids.Concat(new[] { player }).ToList();
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // One canonical player source, with an explicit audience. No dialogue,
            // social evaluator, agency evaluator, reward or provider fan-out runs.
            ExecuteSql(c, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,'',$player,'party_announcement','closed',$ts,$ts,$day,$day,$location,$audience,$payload);",
                TestDict("session", id, "campaign", state.CampaignId, "player", player, "ts", ts, "day", day, "location", announcement.LocationId,
                    "audience", Json.Serialize(audience), "payload", SerializeConversationTurnStoragePayload(p)));
            InsertConversationTurn(c, id + "_source", id, id, 0, id, "player", player, ReadString(p, "playerName", "Player"),
                text, "party_announcement", day, ts, announcement.LocationId, audience, p);
            return PreserveNpcAnnouncementInput(c, state, p, announcement);
        }
        private static Dictionary<string, object> PreserveNpcAnnouncementInput(ReignDbConnection c, NpcLivesState state,
            Dictionary<string, object> p, NpcPartyAnnouncement announcement)
        {
            // Announcement publication already owns the transaction. Preserve its
            // immutable original and author receipt atomically with the visible line;
            // do not open a second admission transaction or generate an NPC reply.
            var captured = CapturePlayerInput(p);
            if (captured.Original != announcement.Text) throw new InvalidOperationException("The announcement original differs from its published words.");
            string input = "input_" + PromptHash(state.CampaignId + "|" + state.TimelineId + "|" + announcement.PlayerId + "|" + announcement.Id);
            string source = "input_source_" + input.Substring(6), hash = ConversationExactTextHash(announcement.Text), generation = NpcLivesGeneration(c);
            EnsureConversationInputSchema(c);
            captured.InputId = input; captured.SourceId = source; captured.Generation = generation;
            p["contributionId"] = announcement.Id;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            PreservePlayerInputSource(c, p, announcement.Id, announcement.Id, announcement.Id, announcement.PlayerId,
                ReadString(p, "playerName", "Player"), announcement.WorldDay, now, announcement.LocationId);
            BindAcceptedInputSource(c, p, announcement.Id, announcement.Id, new[] { announcement.Id + "_source" });
            ExecuteSql(c, @"INSERT INTO conversation_input_receipts
(input_id,timeline_id,session_id,player_id,source_turn_id,source_hash,restore_generation,status,created_ts,updated_ts)
VALUES($id,$timeline,$session,$player,$source,$hash,$generation,'received',$now,$now) ON CONFLICT(input_id) DO NOTHING;",
                TestDict("id", input, "timeline", state.TimelineId, "session", announcement.Id, "player", announcement.PlayerId,
                    "source", source, "hash", hash, "generation", generation, "now", now));
            var saved = QuerySql(c, "SELECT source_hash,source_turn_id FROM conversation_input_receipts WHERE input_id=$id;", TestDict("id", input)).Single();
            if (ReadString(saved, "source_hash", "") != hash || ReadString(saved, "source_turn_id", "") != source)
                throw new InvalidOperationException("The original announcement receipt failed its identity check.");
            return TestDict("schema", ConversationInputSchema, "contributionId", announcement.Id, "inputId", input,
                "sourceId", source, "sourceHash", hash, "persisted", true, "status", "received");
        }
    }
}
