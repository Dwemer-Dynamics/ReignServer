using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunSocialEventTurnProgressSelfTests()
        {
            var tests = new List<Dictionary<string, object>>();
            Action<string, bool, string> check = (id, passed, detail) =>
                tests.Add(ProfileSelfTest("social_progress_" + id, passed, detail, null));
            string campaignId = "_social_progress_" + Guid.NewGuid().ToString("N");
            try
            {
                var ids = new List<string> { "npc_a", "npc_b", "npc_c" };
                var payload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["eventId"] = "progress_event", ["turnId"] = "progress_turn",
                    ["correlationId"] = "progress_turn", ["playerName"] = "Player", ["playerText"] = "Everyone, hello.",
                    ["activeHeroIds"] = ids, ["progressiveReplies"] = true, ["replyCursor"] = 0,
                    ["conversationRelationshipChangesEnabled"] = false,
                    ["attendees"] = ids.Select(id => new Dictionary<string, object>
                    { ["heroStringId"] = id, ["name"] = id }).ToList(),
                    ["participantRequests"] = ids.Select(id => new Dictionary<string, object>
                    { ["speakerHeroStringId"] = id, ["heroStringId"] = id, ["correlationId"] = "progress_turn-npc-" + id }).ToList()
                };
                int calls = 0;
                bool awareness = true;
                bool failNext = false;
                var produced = new List<string>();
                Func<Dictionary<string, object>, Dictionary<string, object>> responder = request =>
                {
                    calls++;
                    var prior = ReadDictionaryList(request, "groupTurnResponses");
                    awareness &= prior.Select(row => ReadString(row, "heroStringId", "")).SequenceEqual(produced)
                        && ReadInt(request, "groupSpeakerIndex", -1) == produced.Count
                        && ReadBool(request, "mustAccountForPriorSpeaker", false) == (produced.Count > 0);
                    if (failNext)
                    {
                        failNext = false;
                        return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Injected missing speaker response." };
                    }
                    string id = ReadString(request, "speakerHeroStringId", "");
                    produced.Add(id);
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["reply"] = "Reply from " + id,
                        ["correlationId"] = ReadString(request, "correlationId", ""),
                        ["participation"] = "speak"
                    };
                };
                var first = SocialEventTurnWithResponder(payload, responder);
                check("first_reply_before_later_work", calls == 1 && produced.Count == 1
                    && ReadBool(first, "hasMore", false) && !ReadBool(first, "completedExchange", false)
                    && ReadDictionaryList(first, "participantResults").Count == 1,
                    "The first request returns one completed reply before invoking either later speaker or final group adjudication.");

                var duplicates = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
                    SocialEventTurnWithResponder(new Dictionary<string, object>(payload), responder))).ToArray();
                Task.WaitAll(duplicates);
                check("lost_response_replays_without_new_generation", calls == 1 && duplicates.All(task =>
                    ReadInt(task.Result, "replyCursor", 0) == 1),
                    "Concurrent retries of cursor zero return the durable first reply without generating another participant.");

                payload["replyCursor"] = 4;
                var ahead = SocialEventTurnWithResponder(payload, responder);
                check("reject_cursor_ahead", !ReadBool(ahead, "ok", true) && calls == 1,
                    "An unearned cursor cannot skip a participant or advance the turn.");

                payload["replyCursor"] = 1;
                failNext = true;
                var failure = SocialEventTurnWithResponder(payload, responder);
                check("later_failure_retains_first_reply", !ReadBool(failure, "ok", true)
                    && ReadDictionaryList(failure, "participantResults").Count(row => ReadBool(row, "ok", false)) == 1,
                    "A failed later speaker retains the already returned reply and its resumable turn identity.");
                var second = SocialEventTurnWithResponder(payload, responder);
                check("retry_only_missing_speaker", calls == 3 && produced.Count == 2
                    && ReadDictionaryList(second, "participantResults").Count == 2
                    && ReadInt(second, "replyCursor", 0) == 2,
                    "Resuming the failed step replaces its failed result and does not repeat the successful first speaker.");

                payload["replyCursor"] = 2;
                var third = SocialEventTurnWithResponder(payload, responder);
                check("last_reply_precedes_finalization", calls == 4 && produced.Count == 3
                    && ReadBool(third, "hasMore", false) && !ReadBool(third, "completedExchange", false),
                    "The last reply is delivered before joins, group relationships and completion can delay the UI.");
                check("preserve_prior_speaker_awareness", awareness && produced.Distinct().Count() == 3,
                    "Each generated speaker receives the successful earlier replies in the original stable order, including after retry.");

                payload["replyCursor"] = 3;
                var completed = SocialEventTurnWithResponder(payload, responder);
                var replay = SocialEventTurnWithResponder(payload, responder);
                check("complete_and_replay_once", calls == 4 && ReadBool(completed, "completedExchange", false)
                    && ReadString(completed, "status", "") == "completed" && ReadBool(replay, "idempotent", false)
                    && ReadDictionaryList(replay, "participantResults").Count == 3,
                    "Finalization returns the complete group receipt; replay invokes no provider and keeps each reply once.");
                check("one_player_transcript_line", ReadJsonLinesFromPath(SocialEventTranscriptFile(campaignId, "progress_event"))
                    .Count(row => ReadString(row, "turnId", "") == "progress_turn" && ReadString(row, "role", "") == "player") == 1,
                    "All progress requests, failures and replay retain exactly one authoritative player submission.");

                payload["turnId"] = "legacy_turn";
                payload["progressiveReplies"] = false;
                payload.Remove("replyCursor");
                produced.Clear();
                calls = 0;
                var legacy = SocialEventTurnWithResponder(payload, responder);
                check("legacy_single_response_compatible", calls == 3 && ReadBool(legacy, "ok", false)
                    && ReadBool(legacy, "completedExchange", false) && !ReadBool(legacy, "hasMore", false),
                    "Existing callers without the opt-in flag still receive a complete ordered group response.");
            }
            catch (Exception ex)
            {
                check("exception", false, ex.ToString());
            }
            finally
            {
                // This identifier is generated above, never supplied by a caller.
                // The Verification Lab owns the surrounding isolated database.
                TryDeleteDirectory(Path.Combine(CampaignsRoot(), SafePathSegment(campaignId, "campaign")));
            }
            return tests;
        }
    }
}
