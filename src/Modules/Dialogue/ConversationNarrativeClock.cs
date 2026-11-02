using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class NarrativeClockReceipt { public Dictionary<string, object> Clock; }
        private static readonly ConditionalWeakTable<Dictionary<string, object>, NarrativeClockReceipt> NarrativeClocks = new();

        // Never read a client/model-supplied clock as authority. This receipt is
        // constructed from saved accepted state and the admitted input projection.
        private static Dictionary<string, object> CurrentNarrativeClock(Dictionary<string, object> payload) =>
            payload != null && NarrativeClocks.TryGetValue(payload, out var receipt) ? receipt.Clock : TestDict();

        private static double NarrativeEffectiveHour(Dictionary<string, object> payload, double nativeHour)
        {
            double hour = ReadDouble(CurrentNarrativeClock(payload), "hour", nativeHour);
            return double.IsFinite(nativeHour) && double.IsFinite(hour) ? Math.Max(nativeHour, hour) : nativeHour;
        }

        private static (double Hours, string Label) NarratedTimeAdvance(Dictionary<string, object> payload, double previousHour)
        {
            if (!PlayerInputSources.TryGetValue(payload, out var source)
                || ReadString(source.Interpretation, "status", "") != "validated") return (0, "");
            var observable = ReadDictionaryList(source.Interpretation, "spans")
                .Where(s => ReadString(s, "kind", "") == "observable").Select(s => ReadString(s, "text", "")).ToList();
            foreach (string span in observable)
            {
                // Explicit scene transitions only. Recollections, plans, uncertain
                // duration and coerced claims of another person's sleep grant no clock.
                if (Regex.IsMatch(span, @"\b(?:if|would|could|might|tomorrow|yesterday|remember|imagine|wish|plan|will|whisper|say|tell|read|quote)\b", RegexOptions.IgnoreCase)) continue;
                var duration = Regex.Match(span, @"\b(?:(?<n>\d{1,3}|one|two|three|four|five|six|seven|eight|nine|ten|twelve)\s+(?<unit>hours?|days?)\s+(?:later|pass(?:es|ed)?)|after\s+(?<n>\d{1,3}|one|two|three|four|five|six|seven|eight|nine|ten|twelve)\s+(?<unit>hours?|days?))\b", RegexOptions.IgnoreCase);
                if (duration.Success)
                {
                    string number = duration.Groups["n"].Value.ToLowerInvariant();
                    string[] numbers = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve" };
                    int n = int.TryParse(number, out int parsed) ? parsed : Array.IndexOf(numbers, number);
                    double hours = n * (duration.Groups["unit"].Value.StartsWith("day", StringComparison.OrdinalIgnoreCase) ? 24d : 1d);
                    if (hours > 0 && hours <= 168) return (hours, duration.Value);
                }
                var morning = Regex.Match(span, @"\b(?:the\s+)?(?:next|following) morning\b", RegexOptions.IgnoreCase);
                if (morning.Success)
                {
                    double target = Math.Floor(previousHour / 24d) * 24d + 8d;
                    if (target <= previousHour) target += 24d;
                    return (target - previousHour, morning.Value);
                }
            }
            return (0, "");
        }

        private static void PrepareNarrativeClock(ReignDbConnection connection, Dictionary<string, object> payload, string speaker)
        {
            double native = ReadDouble(payload, "worldDay", 0d) * 24d;
            var handoff = QuerySql(connection, "SELECT payload_json FROM conversation_scene_handoffs WHERE handoff_id=$id;",
                TestDict("id", ContinuityTimeline(payload) + ":" + speaker)).SingleOrDefault();
            var saved = ReadDictionary(TryParseJsonObject(ReadString(handoff, "payload_json", "{}")), "narrativeClock") ?? TestDict();
            double previous = Math.Max(native, ReadDouble(saved, "hour", native));
            if (!double.IsFinite(previous)) previous = native;
            string contribution = ReadFirstString(payload, "contributionId", "sceneTurnId", "turnId");
            var advance = NarratedTimeAdvance(payload, previous);
            bool alreadyAccepted = contribution.Length > 0 && ReadString(saved, "contributionId", "") == contribution;
            bool changed = contribution.Length > 0 && advance.Hours > 0 && !alreadyAccepted;
            var clock = TestDict("schema", "reign-narrative-clock-v1", "nativeHour", native,
                "hour", previous + (changed ? advance.Hours : 0d), "advanced", changed,
                "label", changed ? advance.Label : ReadDouble(saved, "hour", 0d) > native ? ReadString(saved, "label", "") : "",
                "epoch", changed ? contribution : ReadString(saved, "epoch", ""),
                "contributionId", changed ? contribution : ReadString(saved, "contributionId", ""),
                "authority", "accepted_roleplay_only_no_native_time_or_actions");
            NarrativeClocks.Remove(payload);
            NarrativeClocks.Add(payload, new NarrativeClockReceipt { Clock = clock });
            payload["narrativeClockDiagnostic"] = clock;
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
                    : ReadDictionaryList(payload, "sceneParticipants").Select(CharacterIdFrom).Concat(identities))
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
                    "sceneTurnId", turn, "contributionId", turn, "sceneParticipants", people.Select(CloneDictionary).ToList());
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
            var exposed = CloneDictionary(reopened); exposed["witnessHeroIds"] = new[] { "outsider" };
            check("narrative_privacy_never_erases_native_outside_witness", !ReadBool(ConversationSceneSnapshot("party_chat", exposed), "private", true), null);
            var advance = Input("*Eight hours later, we meet again.*", "eight_hours");
            var advanced = PrepareConversationSceneState(campaign, advance, "npc", ReadString(advance, "playerText", ""), new(), "party_chat");
            check("roleplay_time_invalidates_old_room_and_privacy", ReadString(advanced, "conversationVenue", "") == "Lords Hall"
                && !ReadBool(ConversationSceneSnapshot("party_chat", advance), "private", true), advanced);
            check("roleplay_recovery_uses_separate_clock", IntoxicationWorldHour(advance) == 488d && ReadDouble(advance, "worldDay", 0) == 20d
                && ConversationWorldHour(advance) == 480 && RecoverIntoxication(TestDict("drinks", 4d, "hour", 480d, "hasClock", true), 4d, IntoxicationWorldHour(advance)).Drinks == 0d, null);
            advance["conversationDateLabel"] = "1085 Spring 25 Morning";
            check("narrative_heading_does_not_reassert_stale_native_time", RenderGroundedSceneHeading(advance, "npc", TestDict(), "Hello.").StartsWith("Eight hours later —", StringComparison.OrdinalIgnoreCase), null);
            FinalizeConversationSceneState(campaign, advance, "npc", ReadString(advance, "playerText", ""), "Here we are again.", "party_chat", TestDict());
            var retry = Input("*Eight hours later, we meet again.*", "eight_hours");
            PrepareConversationSceneState(campaign, retry, "npc", ReadString(retry, "playerText", ""), new(), "party_chat");
            check("accepted_time_advance_is_idempotent", IntoxicationWorldHour(retry) == 488d && !ReadBool(CurrentNarrativeClock(retry), "advanced", true), null);
            check("old_scene_correction_cannot_cross_narrative_epoch", ReadString(ReadDictionary(retry, "conversationSceneState"), "conversationVenue", "") == "Lords Hall"
                && !ReadBool(ConversationSceneSnapshot("party_chat", retry), "private", true), ReadDictionary(retry, "conversationSceneState"));
            var cloned = CloneDictionary(retry);
            check("internal_payload_clone_retains_clock_receipt", IntoxicationWorldHour(cloned) == 488d, null);
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
