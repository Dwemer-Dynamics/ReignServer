using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Only the saved NPC Lives operation creates this authority. Serialized
        // client/provider flags can never skip ordinary dialogue publication.
        private sealed class NpcApproachDraftScope
        {
            public string Campaign, Timeline, Player, Hero, Operation, Generation, Session;
            public double Day;
            public NpcAnnouncementReviewWindow Review;
            public Dictionary<string, object> PublicationContext;
            public string ReplyHash, ActivityHash;
        }
        private static readonly ConditionalWeakTable<Dictionary<string, object>, NpcApproachDraftScope> NpcApproachRequests = new();
        private static readonly ConditionalWeakTable<Dictionary<string, object>, NpcApproachDraftScope> NpcApproachDrafts = new();

        private const string NpcLifeArrivalCue =
            "[ARRIVAL EVENT: The player has approached. No new player speech, request, consent, delivery or completed action has occurred. React first in character using your established relationship, current circumstances and any expected return.]";

        private static bool IsNpcLifeApproach(Dictionary<string, object> payload) =>
            payload != null && NpcApproachRequests.TryGetValue(payload, out _);

        private static Dictionary<string, object> RunNpcApproachDialogue(Dictionary<string, object> payload,
            Func<Dictionary<string, object>, Dictionary<string, object>> prepare = null)
        {
            if (payload == null || !NpcApproachRequests.TryGetValue(payload, out var scope))
                return TestDict("ok", false, "status", "superseded");
            if (prepare != null && string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                throw new InvalidOperationException("Injected approach preparation requires the isolated Verification Lab.");
            // The job outlives its initiating HTTP request. Ordinary dialogue's
            // preparation may upsert a character, so it needs its own admission
            // lease before touching context, not just a publication lease later.
            var access = EnterCampaignRequestAccess("/npc-lives/opening");
            try
            {
                using (var c = OpenCampaignConnection(scope.Campaign))
                {
                    if (NpcLivesGeneration(c) != scope.Generation
                        || !ReadNpcLives(c, scope.Campaign, scope.Timeline).Encounters.Any(e => e.OpeningPending
                            && e.HeroId == scope.Hero && e.OpeningOperationId == scope.Operation
                            && e.OpeningGeneration == scope.Generation && e.SessionId == scope.Session && e.OpeningDay == scope.Day))
                        return TestDict("ok", false, "status", "superseded");
                }
                return RunInteractiveRequest(() => prepare == null ? DialogueRespondCore(payload) : prepare(payload));
            }
            finally { ExitCampaignRequestAccess(access); }
        }

        private static Dictionary<string, object> CreateNpcApproachPayload(NpcLivesState state, NpcConversationEncounter encounter,
            Dictionary<string, object> source, string player, string hero, string operation, double day)
        {
            var p = DeepCloneProfileDictionary(source);
            p["campaignId"] = state.CampaignId; p["timelineId"] = state.TimelineId;
            p["heroStringId"] = hero; p["playerHeroStringId"] = player; p["playerId"] = player;
            p["hero"] = CloneDictionary(ReadDictionary(source, "hero") ?? ReadDictionary(source, "profile") ?? TestDict());
            p["correlationId"] = operation; p["sceneTurnId"] = operation;
            p["conversationSessionId"] = encounter.SessionId; p["worldDay"] = day;
            p["playerText"] = ""; p.Remove("text"); p.Remove("message");
            p["turnType"] = "npc_approach_opening"; p["suppressPlayerTranscript"] = true;
            p["suppressConceptionGate"] = true;
            string scene = ReadString(source, "sceneContext", "");
            if (scene.Length == 0) scene = "Current native place: " + ReadString(source, "location", "unknown") + ".";
            p["sceneContext"] = scene + "\nNative game hour: " + ((day % 1) * 24).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            NpcApproachRequests.Add(p, new NpcApproachDraftScope {
                Campaign = state.CampaignId, Timeline = state.TimelineId, Player = player, Hero = hero,
                Operation = operation, Generation = encounter.OpeningGeneration, Session = encounter.SessionId, Day = day
            });
            return p;
        }

        private static string NpcApproachInstructions(Dictionary<string, object> p)
        {
            if (!IsNpcLifeApproach(p)) return "";
            return "NPC LIVES ARRIVAL — accuracy has priority over speed and brevity.\n"
                + NpcLifeArrivalCue + "\n"
                + "Use the complete supplied character, emotional state, identity knowledge, directional relationship, memories, letters, commitments and current scene, as in ordinary conversation. "
                + "Recognize a known person according to actual familiarity; a neutral score does not erase acquaintance, and high affinity alone does not establish romance or reveal an unknown identity. "
                + "An expected return, urgent concern, recent conflict or unfinished promise should shape your first reaction when relevant. "
                + "Do not ask as though the visitor's purpose is wholly unknown when your own supplied history establishes why you await them. "
                + "Warmth and availability are independent: a busy friend can be pleased, worried, teasing or distracted according to personality and circumstances. "
                + "Do not force friendliness, intimacy, refusal or an invitation from a score. "
                + "Continue the established venue and activity. You may add compatible incidental business only when no established circumstance contradicts it; work is background, not a mandatory interruption. "
                + "A return does not prove that supplies arrived, a quest succeeded, a letter was read, or another person accompanied the player. Preserve uncertainty and audience privacy. "
                + "Prefer one brief gesture and one or two short spoken sentences; take more space if accuracy requires it. "
                + "Return the complete ordinary dialogue JSON schema, with additional string fields activity (a short description of your visible current activity, or empty if unknown) "
                + "and receptiveness (busy, neutral, receptive or needs_help). These describe this moment, not permission to block conversation. "
                + "This arrival alone grants no player action, agreement, relationship reward, conception, story progress or lasting character change.\n"
                + "Current directional affinity toward the player: " + ReadInt(p, "npcEffectiveAffinity", 0)
                + ". Express the relationship naturally; never disclose the number.\n"
                + "Actual conversation scope: " + ReadString(p, "npcLifeContext", "in_person")
                + "; audience IDs: " + Json.Serialize(ReadStringList(p, "npcLifeAudienceIds"))
                + ". Speak only for yourself and protect private memories from other listeners.";
        }

        private static void ConfigureNpcApproachGeneration(Dictionary<string, object> request, Dictionary<string, object> payload)
        {
            if (!IsNpcLifeApproach(payload)) return;
            request["requestType"] = "npc_approach";
            request["eventId"] = ReadString(payload, "correlationId", "");
            request["heroName"] = ReadString(ReadDictionary(payload, "hero"), "name", "");
            var nativeSpeaker = ReadDictionary(payload, "hero");
            if (nativeSpeaker != null && CharacterIdFrom(nativeSpeaker) == ReadString(request, "heroStringId", ""))
            {
                var author = CloneDictionary(nativeSpeaker);
                author["politicalTimelineId"] = ContinuityTimeline(payload);
                request["politicalAuthors"] = new[] { author };
            }
            // Retain the ordinary schema budget, provider reasoning preference and
            // bounded repairs. A tiny output allowance must not force false brevity.
        }

        private static Dictionary<string, object> BuildNpcApproachDraft(Dictionary<string, object> payload,
            Dictionary<string, object> llm, Dictionary<string, object> parsed, string reply,
            Dictionary<string, object> motive, Dictionary<string, object> identity,
            List<Dictionary<string, object>> prior, string hero, string heroName, string playerName)
        {
            var view = CloneDictionary(parsed ?? TestDict()); view["reply"] = reply;
            if (parsed == null || !ReadBool(llm, "ok", false) || ReadBool(llm, "visibleRepairFailure", false)
                || ReadBool(ReadDictionary(llm, "conversationAgency"), "visibleFailure", false)
                || !NpcApproachReplyShape(view)
                || !ValidateAgencyRepairedResponse(view, motive, payload, identity, prior, hero, heroName, playerName))
                return TestDict("ok", false, "error", "The approach could not be grounded in the current conversation. Please try again.",
                    "npcApproachGroundingFailed", true);
            var draft = TestDict("ok", true, "reply", reply,
                "activity", ReadString(parsed, "activity", "").Trim(),
                "receptiveness", ReadString(parsed, "receptiveness", "neutral"));
            foreach (string key in new[] { "announcementReview", "announcementReactions" })
                if (parsed.TryGetValue(key, out var value)) draft[key] = value;
            return SealNpcApproachDraft(payload, draft);
        }

        private static bool NpcApproachReplyShape(Dictionary<string, object> draft)
        {
            string reply = ReadString(draft, "reply", "").Trim();
            return reply.Length > 0 && reply.Length <= 24000
                && ReadString(draft, "activity", "").Length <= 400 && !IsTechnicalConversationNotice(reply)
                && !reply.StartsWith("[Bannerlord Reign:", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> SealNpcApproachDraft(Dictionary<string, object> payload, Dictionary<string, object> draft)
        {
            if (!NpcApproachRequests.TryGetValue(payload, out var scope) || !NpcApproachReplyShape(draft))
                return TestDict("ok", false, "error", "The approach draft has no matching saved operation.");
            var receipt = new NpcApproachDraftScope {
                Campaign = scope.Campaign, Timeline = scope.Timeline, Player = scope.Player, Hero = scope.Hero,
                Operation = scope.Operation, Generation = scope.Generation, Session = scope.Session, Day = scope.Day,
                Review = CapturedNpcAnnouncementReview(payload, scope.Hero, scope.Player),
                PublicationContext = NpcApproachPublicationContext(payload),
                ReplyHash = PromptHash(ReadString(draft, "reply", "")), ActivityHash = PromptHash(ReadString(draft, "activity", ""))
            };
            NpcApproachDrafts.Add(draft, receipt);
            return draft;
        }

        private static bool NpcApproachDraftMatches(Dictionary<string, object> draft, NpcLivesState state,
            NpcConversationEncounter encounter, Dictionary<string, object> saved, string generation,
            out NpcApproachDraftScope receipt)
        {
            receipt = null;
            if (draft == null || !NpcApproachDrafts.TryGetValue(draft, out var captured)
                || captured.Campaign != state.CampaignId || captured.Timeline != state.TimelineId
                || captured.Hero != encounter.HeroId || captured.Player != ReadString(saved, "playerId", "")
                || captured.Operation != encounter.OpeningOperationId || captured.Generation != generation
                || captured.Session != encounter.SessionId || captured.Day != encounter.OpeningDay
                || captured.ReplyHash != PromptHash(ReadString(draft, "reply", ""))
                || captured.ActivityHash != PromptHash(ReadString(draft, "activity", ""))) return false;
            receipt = captured; return true;
        }

        private static Dictionary<string, object> NpcApproachPublicationContext(Dictionary<string, object> payload)
        {
            var context = TestDict();
            foreach (string key in new[] { "sceneContext", "conversationSceneState", "conversationScenePrompt",
                "sceneParticipants", "localPresence", "partyContext", "nativePoliticalContext", "temporaryPartyGuest",
                "nativeSceneMovement", "campaignCalendar", "conversationDateLabel", "playerPartyId", "playerIdentity",
                "identityView", "conversationMode" })
                if (payload.TryGetValue(key, out var value)) context[key] = value;
            context["playerText"] = ""; context["suppressPlayerTranscript"] = true;
            context["turnType"] = "npc_approach_opening";
            return DeepCloneProfileDictionary(context);
        }

        private static Dictionary<string, object> NpcOpeningStatus(NpcConversationEncounter encounter, string generation)
        {
            bool failed = !encounter.OpeningPending && string.IsNullOrWhiteSpace(encounter.OpeningText);
            return TestDict("ok", !failed, "status", failed ? "failed" : encounter.OpeningPending ? "pending" : "opened",
                "operationId", encounter.OpeningOperationId, "reply", encounter.OpeningText, "activity", encounter.Activity,
                "generation", generation, "sessionId", encounter.SessionId,
                "error", failed ? ReadString(TryParseJsonObject(encounter.OpeningPayload), "openingError", "The approach was interrupted. Please try again.") : "");
        }

        private static void FailNpcOpening(ReignDbConnection c, NpcLivesState state, NpcConversationEncounter encounter)
        {
            var saved = TryParseJsonObject(encounter.OpeningPayload) ?? TestDict();
            saved["openingError"] = "The approach could not produce a grounded opening. You can speak normally or try approaching again.";
            encounter.OpeningPayload = Json.Serialize(saved);
            encounter.OpeningPending = false; encounter.OpeningText = "";
            // Preserve activity, accepted clock and pending announcement reviews.
            // No invented fallback is inserted into the canonical transcript.
            WriteNpcLives(c, state);
        }
    }
}
