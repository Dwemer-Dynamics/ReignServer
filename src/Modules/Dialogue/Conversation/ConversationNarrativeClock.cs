using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        // Retained historical clock fields never grant elapsed time. Campaign
        // snapshots include fractional days; ordinary native progression remains.
        private static Dictionary<string, object> CurrentNarrativeClock(Dictionary<string, object> payload) => TestDict();
        private static (double Hours, string Label) NarratedTimeAdvance(Dictionary<string, object> payload, double previousHour) => (0d, "");
        private static void PrepareNarrativeClock(ReignDbConnection connection, Dictionary<string, object> payload, string speaker)
        {
            payload.Remove("narrativeClockDiagnostic");
        }

        private static List<Dictionary<string, object>> SpokenSceneCorrections(string text, Dictionary<string, object> payload)
        {
            var changes = new List<Dictionary<string, object>>();
            // A spoken correction establishes a proposed local scene, never native
            // presence or a knowledge audience. Each NPC retains explicit refusal.
            string speech = ConversationSpeech(text);
            foreach (Match match in Regex.Matches(speech, @"(?:^|(?<=[.!?]))\s*[Ww]e are\s+(?:already\s+)?(?:in|inside)\s+(?:the\s+)?(?<place>(?:small |private |quiet )?(?:study|library(?: room)?|bedchamber|tent|courtyard))\b[^.!?\r\n]{0,180}"))
            {
                string quote = match.Value.Trim().TrimEnd('.');
                if (!Regex.IsMatch(quote, @"\b(?:right now|already|door is (?:shut|closed)|nobody is around)\b", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(speech.Substring(Math.Max(0, match.Index - 35), match.Index - Math.Max(0, match.Index - 35)), @"\b(?:if|imagine|remember|pretend|wish|said|says)\b", RegexOptions.IgnoreCase)) continue;
                var active = ReadStringList(payload, "activeHeroIds");
                var identities = new[] { ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), ReadFirstString(payload, "heroStringId", "speakerHeroStringId") };
                var ids = (active.Count > 0 ? active.Concat(identities)
                    : ReadDictionaryList(payload, "sceneParticipants").Select(CampaignFileService.CharacterIdFrom).Concat(identities))
                    .Where(id => id.Length > 0).Distinct().ToList();
                if (ids.Count < 2) continue;
                changes.Add(TestDict("quote", quote, "location", match.Groups["place"].Value, "locationClass", "formal",
                    "subjectIds", ids, "kind", "establish", "linkedWithPlayer", true));
            }
            return changes;
        }

        private static bool ExplicitClosedScene(string quote) => Regex.IsMatch(quote ?? "",
            @"\bdoor is (?:shut|closed)\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(quote ?? "", @"\b(?:nobody is around but us|only (?:the three of |the two of )?us|alone|no one else)\b", RegexOptions.IgnoreCase);

        private static void RunNarrativeClockCorrectionTests(string campaign, Action<string, bool, object> check)
        {
            var people = new[] { TestDict("heroStringId", "player", "name", "Player", "role", "player", "nativeLocationDescription", "Lords Hall"),
                TestDict("heroStringId", "npc", "name", "Rhagaea", "role", "npc", "nativeLocationDescription", "Lords Hall") };
            Dictionary<string, object> Input(string text, string turn) {
                var p = TestDict("campaignId", campaign, "timelineId", "clock_branch", "conversationSessionId", "clock_session",
                    "worldDay", 20d, "locationId", "town", "playerHeroStringId", "player", "heroStringId", "npc", "playerText", text,
                    "sceneTurnId", turn, "contributionId", turn, "sceneParticipants", people.Select(VerificationService.CloneDictionary).ToList());
                var candidate = TestDict("narration", InputNarrationBlocks(text).Select(b => TestDict("index", ReadInt(b, "index", 0),
                    "spans", new[] { TestDict("quote", ReadString(b, "text", ""), "kind", "observable", "actorId", "player") })).ToList());
                CapturePlayerInput(p).Interpretation = ValidateNarrationInterpretation(text, candidate, p);
                return p;
            }
            const string correction = "We are in the study that we used last right now, nobody is around but us and the door is shut.";
            var initial = Input(correction, "study_correction");
            var scene = PrepareConversationSceneState(campaign, initial, "npc", correction, new(), "party_chat");
            check("spoken_scene_correction_becomes_source_bound_proposal", ReadString(scene, "conversationVenue", "") == "study"
                && ReadDictionaryList(scene, "proposals").Count == 2, scene);
            FinalizeConversationSceneState(campaign, initial, "npc", correction, "Yes, we are already in the study.", "party_chat", TestDict());
            var reopened = Input("Good morning.", "reopen"); reopened["conversationSessionId"] = "new_session";
            var resumed = PrepareConversationSceneState(campaign, reopened, "npc", "Good morning.", new(), "party_chat");
            check("same_anchor_scene_and_privacy_survive_session_reopen", ReadString(resumed, "conversationVenue", "") == "study"
                && ReadBool(ConversationSceneSnapshot("party_chat", reopened), "private", false), resumed);
            var exposed = VerificationService.CloneDictionary(reopened); exposed["witnessHeroIds"] = new[] { "outsider" };
            check("narrative_privacy_never_erases_native_outside_witness", !ReadBool(ConversationSceneSnapshot("party_chat", exposed), "private", true), null);
            var advance = Input("*Eight hours later, we meet again.*", "eight_hours");
            var advanced = PrepareConversationSceneState(campaign, advance, "npc", ReadString(advance, "playerText", ""), new(), "party_chat");
            check("narrated_time_does_not_invalidate_room_or_privacy", ReadString(advanced, "conversationVenue", "") == "study"
                && ReadBool(ConversationSceneSnapshot("party_chat", advance), "private", false), advanced);
            check("narrated_time_grants_no_recovery", IntoxicationWorldHour(advance) == 480d && ReadDouble(advance, "worldDay", 0) == 20d
                && ConversationWorldHour(advance) == 480 && RecoverIntoxication(TestDict("drinks", 4d, "hour", 480d, "hasClock", true), 4d, IntoxicationWorldHour(advance)).Drinks == 4d, null);
            advance["conversationDateLabel"] = "1085 Spring 25 Morning";
            check("heading_keeps_native_campaign_date", RenderGroundedSceneHeading(advance, "npc", TestDict(), "Hello.").StartsWith("1085 Spring 25 Morning —", StringComparison.OrdinalIgnoreCase), null);
            FinalizeConversationSceneState(campaign, advance, "npc", ReadString(advance, "playerText", ""), "Here we are again.", "party_chat", TestDict());
            var retry = Input("*Eight hours later, we meet again.*", "eight_hours");
            PrepareConversationSceneState(campaign, retry, "npc", ReadString(retry, "playerText", ""), new(), "party_chat");
            check("accepted_time_advance_is_zero", IntoxicationWorldHour(retry) == 480d && !ReadBool(CurrentNarrativeClock(retry), "advanced", false), null);
            check("narrated_time_creates_no_scene_epoch", ReadString(ReadDictionary(retry, "conversationSceneState"), "conversationVenue", "") == "study"
                && ReadBool(ConversationSceneSnapshot("party_chat", retry), "private", false), ReadDictionary(retry, "conversationSceneState"));
            var cloned = VerificationService.CloneDictionary(retry);
            check("internal_payload_clone_retains_clock_receipt", IntoxicationWorldHour(cloned) == 480d, null);
            var native = Input("Hello.", "native_catchup"); native["worldDay"] = 20.5d;
            PrepareConversationSceneState(campaign, native, "npc", "Hello.", new(), "party_chat");
            check("native_catchup_does_not_add_narrative_duration_twice", IntoxicationWorldHour(native) == 492d, null);
            foreach (string text in new[] { "*If eight hours pass, I will return.*", "*I remember the next morning.*", "Eight hours later I will return.", "*I whisper that the next morning would be better.*" })
            {
                var p = Input(text, "negative_" + rowsafe(text));
                check("noncurrent_time_is_not_advancement_" + rowsafe(text), NarratedTimeAdvance(p, 480d).Hours == 0d, null);
            }
            string rowsafe(string text) => PromptHash(text).Substring(0, 8);
            var spoofed = Input("Hello.", "spoof"); spoofed["narrativeClockDiagnostic"] = TestDict("hour", 1000000d);
            check("caller_clock_cannot_grant_recovery", IntoxicationWorldHour(spoofed) == 480d, null);
            var branch = Input("Hello.", "other_branch"); branch["timelineId"] = "other_branch";
            PrepareConversationSceneState(campaign, branch, "npc", "Hello.", new(), "party_chat");
            check("clock_does_not_leak_across_timelines", IntoxicationWorldHour(branch) == 480d, null);
            check("hypothetical_spoken_location_cannot_set_scene", SpokenSceneCorrections("Imagine we are in the study right now, alone and the door is closed.", initial).Count == 0, null);
            check("quoted_spoken_location_cannot_set_scene", SpokenSceneCorrections("She said: We are in the study right now, alone and the door is closed.", initial).Count == 0, null);
        }
    }
}
