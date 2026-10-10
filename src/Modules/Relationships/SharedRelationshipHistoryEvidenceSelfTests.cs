using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunSharedRelationshipHistoryEvidenceSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed) => results.Add(TestDict("caseId", "relationship_history_evidence_" + id,
                "passed", passed, "summary", id));
            string campaign = "_shared_relationship_history_test_evidence_" + Guid.NewGuid().ToString("N");
            var context = TestDict("campaignId", campaign, "timelineId", "main", "worldDay", 10d,
                "playerText", "our relationship and the orchard", "playerHeroStringId", "player");
            try
            {
                using var connection = OpenCampaignConnection(campaign);
                string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                EnsureSharedRelationshipHistorySchema(connection);
                ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('history_sources',$campaign,'a','player','closed',1,2,'[""a"",""b"",""player""]','{""timelineId"":""main""}');", TestDict("campaign", campaign));
                void Turn(string id, string speaker, string words, List<string> audience, int order, double day = 3d, string timeline = "main") =>
                    InsertConversationTurn(connection, id, "history_sources", id, order, id, "npc", speaker, speaker.ToUpperInvariant(), words,
                        "party_chat", day, 2, "", audience, TestDict("timelineId", timeline));
                const string intention = "I might visit the orchard tomorrow if we have time.";
                Turn("shared_plan", "a", intention, new List<string> { "a", "b" }, 0);
                Turn("private_disclosure", "a", "The orchard reminds me of my secret relationship with PLAYER-SECRET-901.", new List<string> { "a", "player" }, 1);
                Turn("mixed_narration", "a", "*I privately remember MARRIAGE-SECRET-902.* The orchard is beautiful.", new List<string> { "a", "b" }, 2);
                Turn("other_branch", "a", "Our relationship involved OTHER-BRANCH-903.", new List<string> { "a", "b" }, 3, timeline: "wrong");
                Turn("future", "b", "Our relationship involved FUTURE-HISTORY-904.", new List<string> { "a", "b" }, 4, day: 12d);
                var aView = RelationshipHistorySourceEvidence(connection, "a", "b", context, false);
                var bView = RelationshipHistorySourceEvidence(connection, "b", "a", context, false);
                var shared = RelationshipHistorySourceEvidence(connection, "a", "b", context, true);
                string Text(List<Dictionary<string, object>> records) => string.Join("\n", records.Select(r => ReadString(r, "text", "")));
                Check("owner_retains_prior_disclosure", Text(aView).Contains("PLAYER-SECRET-901"));
                Check("other_observer_and_joint_writer_exclude_private_history", !Text(bView).Contains("PLAYER-SECRET-901")
                    && !Text(bView).Contains("MARRIAGE-SECRET-902") && !Text(shared).Contains("PLAYER-SECRET-901") && !Text(shared).Contains("MARRIAGE-SECRET-902"));
                Check("shared_intention_keeps_qualifications_and_asof", Text(shared).Contains(intention)
                    && !Text(shared).Contains("OTHER-BRANCH-903") && !Text(shared).Contains("FUTURE-HISTORY-904"));
                var prior = SharedRelationshipHistoryPriorForJointWriter(TestDict("objectiveSummary", "They exchanged ordinary counsel.",
                    "aInterpretation", "A-PRIVATE-905", "bInterpretation", "B-PRIVATE-906", "chapterOrdinal", 3));
                Check("joint_writer_does_not_receive_private_chapter_interpretations", !Json.Serialize(prior).Contains("PRIVATE-")
                    && ReadInt(prior, "chapterOrdinal", 0) == 3);

                var sent = InsertLetter(connection, "a", "A", "b", "B", "The orchard letter proposes a visit; it does not confirm one.",
                    "npc_initiated", "visit", "", "", "", 3d, 5d, TestDict("timelineId", "main"));
                string letterId = ReadString(sent, "letterId", "");
                ExecuteSql(connection, "UPDATE letters SET status='read',read_day=5,delivery_timeline_id='main' WHERE letter_id=$id;", TestDict("id", letterId));
                var bounded = CloneDictionary(context);
                bounded["memoryAsOfAcceptedSequence"] = ReadAcceptedMemorySourceWatermark(connection);
                var withLetter = RelationshipHistorySourceEvidence(connection, "a", "b", bounded, true);
                Check("post_conversation_letter_included_by_shared_watermark", withLetter.Any(r => ReadString(r, "sourceId", "") == letterId)
                    && Text(withLetter).Contains("not evidence that either party knows it was read"));

                var oldChapter = TestDict("created_world_day", 3d, "source_basis_json", "{}");
                var scopedChapter = TestDict("created_world_day", 3d, "source_basis_json", Json.Serialize(TestDict("timelineId", "main")));
                Check("unknown_other_branch_and_future_chapters_not_prompted", !SharedRelationshipHistoryContextVisible(oldChapter, context)
                    && SharedRelationshipHistoryContextVisible(scopedChapter, context)
                    && !SharedRelationshipHistoryContextVisible(scopedChapter, TestDict("timelineId", "other", "worldDay", 10d))
                    && !SharedRelationshipHistoryContextVisible(scopedChapter, TestDict("timelineId", "main", "worldDay", 2d)));
                var basis = TestDict("pairKey", "a|b", "heroAId", "a", "heroBId", "b", "heroA", TestDict("name", "A"), "heroB", TestDict("name", "B"),
                    "campaignId", campaign, "timelineId", "main", "worldDay", 10d, "stateFingerprint", "fixture", "restoreGeneration", generation,
                    "sharedSources", shared, "affinityAtoB", 55, "affinityBtoA", 55, "tagAtoB", "friend", "tagBtoA", "friend");
                var growing = TestDict("pairKey", "a|b", "objectiveSummary", "They have recently made room for more ordinary conversation.",
                    "aInterpretation", "A feels growing warmth without erasing earlier feelings.", "bInterpretation", "B enjoys their shared interests.");
                Check("independent_growth_is_allowed", NormalizeSharedRelationshipHistoryCandidate(growing, basis, out _) != null);
                var oversized = CloneDictionary(growing); oversized["objectiveSummary"] = new string('a', 1201);
                Check("overlong_history_not_clipped_into_canon", NormalizeSharedRelationshipHistoryCandidate(oversized, basis, out string reason) == null
                    && reason == "overlong_complete_field");
                Check("fallback_does_not_invent_past_activities", ReadString(DeterministicSharedRelationshipHistory(basis, "fixture"), "objectiveSummary", "")
                    .Contains("No particular past exchange or activity is established"));

                ValidateSharedRelationshipHistorySources(connection, basis);
                AdvanceMemoryRestoreGeneration(campaign);
                bool stale = false;
                try { ValidateSharedRelationshipHistorySources(connection, basis); }
                catch (InvalidOperationException) { stale = true; }
                Check("old_background_generation_cannot_commit", stale);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
