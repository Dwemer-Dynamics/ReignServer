using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunNpcLifeInvitationOpeningSelfTests(string campaign)
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass) => results.Add(TestDict("caseId", "npc_invitation_" + id, "passed", pass, "summary", id));
            const string hero = "invitation_npc", session = "invitation_opening_session", timeline = "invitation_opening";
            var state = new NpcLivesState { CampaignId = campaign, TimelineId = timeline };
            var story = new NpcStoryInstance { Id = "invitation_story", HeroId = hero, Revision = 1, Status = "invited",
                ProposalId = "invitation_proposal", LocationName = "Zeonica", Definition = new NpcStoryDefinition {
                    Title = "An unfinished verse", Category = "brief", Phases = new List<NpcStoryPhase> {
                        new NpcStoryPhase { Title = "An unfinished verse", Prompt = "Offer a rough verse and invite a fitting line." } } } };
            state.Stories.Add(story);
            var source = TestDict("campaignId", campaign, "timelineId", timeline, "playerId", "player", "heroId", hero,
                "worldDay", 20d, "conversationSessionId", session, "npcLifeContext", "in_person",
                "npcLifeAudienceIds", new[] { "player", hero }, "npcLifeInvitationEntry", true,
                "npcLifeRequestId", story.Id, "npcStoryId", story.Id, "npcLivesRevision", 1);
            var options = new NpcLivesOptions();
            var initialOff = new NpcLivesOptions(k => k != NpcLivesOptions.InitialApproachKey, _ => null);
            var invitation = ResolveNpcLifeInvitationOpening(state, source, hero, initialOff);
            Check("explicit_owned_offer_has_its_own_feature_gate", invitation != null && NpcOpeningEnabled(initialOff, invitation)
                && !NpcOpeningEnabled(initialOff, null));
            foreach (string disabled in new[] { NpcLivesOptions.MasterKey, "npcLivesStoriesEnabled", "npcLivesBriefEnabled" })
                Check("respects_" + disabled, ResolveNpcLifeInvitationOpening(state, source, hero,
                    new NpcLivesOptions(k => k != disabled, _ => null)) == null);
            foreach (string mutation in new[] { "ordinary", "foreign_owner", "foreign_story", "stale_revision", "public_audience", "already_accepted" })
            {
                var p = DeepCloneProfileDictionary(source);
                var copy = Json.Deserialize<NpcLivesState>(Json.Serialize(state));
                if (mutation == "ordinary") p.Remove("npcLifeInvitationEntry");
                if (mutation == "foreign_owner") copy.Stories[0].HeroId = "other";
                if (mutation == "foreign_story") p["npcStoryId"] = "other";
                if (mutation == "stale_revision") p["npcLivesRevision"] = 2;
                if (mutation == "public_audience") p["npcLifeAudienceIds"] = new[] { "player", hero, "bystander" };
                if (mutation == "already_accepted") copy.Stories[0].Status = "active";
                Check("no_authority_for_" + mutation, ResolveNpcLifeInvitationOpening(copy, p, hero, options) == null);
            }
            var leave = new NpcPersonalLeave { Id = "invitation_leave", HeroId = hero, Revision = 1,
                Status = "discussing", Purpose = "Visit the seed market", ProposalId = "leave_proposal" };
            state.Leaves.Add(leave);
            var leaveSource = DeepCloneProfileDictionary(source); leaveSource["npcLifeRequestId"] = leave.Id; leaveSource["npcStoryId"] = "";
            Check("leave_introduces_the_saved_purpose_without_changing_terms",
                ResolveNpcLifeInvitationOpening(state, leaveSource, hero, initialOff)?.Reference.Contains(leave.Purpose) == true
                && !leave.DepartureDay.HasValue && !leave.ReturnDay.HasValue);

            using var c = OpenCampaignConnection(campaign);
            string generation = NpcLivesGeneration(c);
            var encounter = NpcEncounter(state, "player", hero, source);
            encounter.LastAcceptedDay = 20; encounter.SessionId = session;
            WriteNpcLives(c, state);
            ExecuteSql(c, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,$hero,'player','open',1,1,$audience,$payload);",
                TestDict("session", session, "campaign", campaign, "hero", hero, "audience", Json.Serialize(new[] { "player", hero }),
                    "payload", Json.Serialize(TestDict("timelineId", timeline))));
            var oldProvider = NpcOpeningTestProvider;
            using var release = new ManualResetEventSlim();
            int calls = 0;
            try
            {
                NpcOpeningTestProvider = (request, _) => {
                    Interlocked.Increment(ref calls);
                    if (!release.Wait(TimeSpan.FromSeconds(10))) return TestDict("ok", false);
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "Would you hear the verse I have been trying to finish?",
                        "activity", "considering a verse", "receptiveness", "receptive")));
                };
                var opened = NpcLivesOpenApi("open", DeepCloneProfileDictionary(source), initialOff);
                bool called = SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 2000);
                var reopened = NpcLivesOpenApi("open", DeepCloneProfileDictionary(source), initialOff);
                Check("new_invitation_bypasses_recent_greeting_but_reuses_its_pending_job", called
                    && ReadString(opened, "status", "") == "pending" && ReadString(reopened, "status", "") == "pending"
                    && ReadString(opened, "operationId", "") == ReadString(reopened, "operationId", "") && Volatile.Read(ref calls) == 1);
                release.Set();
                bool completed = SpinWait.SpinUntil(() => !ReadNpcLives(c, campaign, timeline).Encounters.Single().OpeningPending, 10000);
                var settled = ReadNpcLives(c, campaign, timeline);
                var intro = settled.Encounters.Single();
                Check("accepted_intro_has_one_turn_and_no_consent_progress_or_rewards", completed
                    && intro.OpeningText.Contains("verse") && intro.LastAcceptedDay == 20
                    && settled.OperationReceipts.TryGetValue(invitation.ReceiptKey, out var receipt) && receipt == intro.OpeningOperationId
                    && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id=$session;", TestDict("session", session)).Count == 1
                    && settled.Stories[0].Status == "invited" && settled.Stories[0].Evidence.Count == 0 && settled.Effects.Count == 0);
                var replay = NpcLivesOpenApi("open", DeepCloneProfileDictionary(source), initialOff);
                var ordinary = DeepCloneProfileDictionary(source); ordinary.Remove("npcLifeInvitationEntry");
                var ordinaryResume = NpcLivesOpenApi("open", ordinary, options);
                Check("saved_receipt_and_clock_keep_both_reopen_routes_silent", ReadString(replay, "status", "") == "resume"
                    && ReadString(ordinaryResume, "status", "") == "resume" && Volatile.Read(ref calls) == 1
                    && Json.Deserialize<NpcLivesState>(Json.Serialize(settled)).OperationReceipts.ContainsKey(invitation.ReceiptKey));
            }
            finally { release.Set(); NpcOpeningTestProvider = oldProvider; }

            foreach (string mutation in new[] { "revision", "declined", "disabled", "missing_draft" })
            {
                var copy = Json.Deserialize<NpcLivesState>(Json.Serialize(state));
                var pending = copy.Encounters.Single();
                pending.BeginOpening("obsolete_invitation_" + mutation, 20, 1, freshInvitation: true);
                pending.OpeningDay = 20; pending.OpeningGeneration = generation;
                pending.OpeningPayload = Json.Serialize(source);
                var payload = CreateNpcApproachPayload(copy, pending, source, "player", hero, pending.OpeningOperationId, 20, invitation);
                string instructions = NpcApproachInstructions(payload);
                Check("intro_is_scoped_not_a_continuing_activity_" + mutation, instructions.Contains("NPC-INITIATED PERSONAL INVITATION")
                    && instructions.Contains(story.Definition.Phases[0].Prompt) && instructions.Contains("still optional and unaccepted")
                    && !NpcApproachInstructions(DeepCloneProfileDictionary(payload)).Contains("NPC-INITIATED PERSONAL INVITATION"));
                var draft = SealNpcApproachDraft(payload, TestDict("reply", "Would you like to hear my verse?", "activity", "considering a verse"));
                if (mutation == "revision") copy.Stories[0].Revision++;
                if (mutation == "declined") copy.Stories[0].Status = "declined";
                var currentOptions = mutation == "disabled" ? new NpcLivesOptions(k => k != "npcLivesStoriesEnabled", _ => null) : options;
                CompleteNpcOpening(c, copy, pending, TestDict(), mutation == "missing_draft" ? TestDict() : draft, generation, currentOptions);
                Check("obsolete_or_failed_intro_cannot_publish_" + mutation, !pending.OpeningPending && pending.LastAcceptedDay == 20
                    && !copy.OperationReceipts.ContainsKey(invitation.ReceiptKey)
                    && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id=$session;", TestDict("session", session)).Count == 1);
            }
            return results;
        }
    }
}
