using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void AppendCourtLifeCreatorProfileTests(List<Dictionary<string, object>> results, string campaign)
        {
            void Check(string id, bool passed) => results.Add(TestDict("caseId", "court_creator_" + id,
                "passed", passed, "summary", id));
            const string capital = "creator_test_capital", role = "musician", timeline = "creator_branch";
            string actor = "reign_artist_" + CourtHash(campaign + "|" + capital + "|" + role);
            var payload = TestDict("campaignId", campaign, "timelineId", timeline, "worldDay", 20d,
                "playerHeroStringId", "creator_test_player", "playerKingdomStringId", "creator_test_kingdom",
                "playerIsKingdomRuler", true, "courtScope", "capital", "hostSettlementStringId", capital,
                "capitalSettlementStringId", capital, "source", "Patronage", "templateId", "patronage-smaller-counteroffer",
                "speakerRole", role, "speakerName", "Nera", "actorId", actor, "matterId", "patronage_creator_test",
                "turnId", "creator_question", "phase", "conversation", "playerText", "No order yet.  \r\nExplain the smaller commission.");
            var profile = CourtLifeCreatorProfile(payload, actor);
            Check("catalog_creator_has_virtual_non_noble_profile", profile != null && CharacterIdFrom(profile) == actor
                && !ReadBool(profile, "isNativeHero", true) && !ReadBool(profile, "isLord", true)
                && ReadString(profile, "politicalTimelineId", "") == timeline);
            foreach (var variant in new[] {
                TestDict("source", "International"), TestDict("templateId", "patronage-spouse-portrait"),
                TestDict("speakerRole", "ambassador"), TestDict("campaignId", campaign + "_other"),
                TestDict("capitalSettlementStringId", "other_capital"), TestDict("hostSettlementStringId", "other_host"),
                TestDict("playerIsKingdomRuler", false), TestDict("matterId", "international_other") })
            {
                var changed = CloneDictionary(payload);
                foreach (var entry in variant) changed[entry.Key] = entry.Value;
                Check("rejects_" + variant.Keys.Single(), CourtLifeCreatorProfile(changed, actor) == null);
            }
            int calls = 0; bool requiredPolitics = false, requiredEvidence = false, alteredEvidenceRejected = false;
            Dictionary<string, object> Writer(Dictionary<string, object> request)
            {
                calls++;
                requiredPolitics = ReadDictionaryList(request, "politicalAuthors").Any(p => CharacterIdFrom(p) == actor)
                    && ReadDictionaryList(request, "messages").Any(m => ReadString(m, "content", "")
                        .Contains("REQUIRED PRIVATE POLITICAL BELIEFS — " + actor, StringComparison.Ordinal));
                var diagnostics = ReadDictionary(request, "promptEnvelope");
                var continuity = ReadDictionary(ReadDictionary(diagnostics, "continuityPreflight"), "continuityEvidence");
                var messages = ReadDictionaryList(request, "messages");
                requiredEvidence = MissingContinuityContent(ConversationEvidenceText(diagnostics, messages), continuity).Count == 0;
                var altered = messages.Select(CloneDictionary).ToList();
                foreach (var message in altered) message["content"] = ReadString(message, "content", "")
                    .Replace("Explain the smaller commission.", "Explain a different commission.", StringComparison.Ordinal);
                alteredEvidenceRejected = MissingContinuityContent(ConversationEvidenceText(diagnostics, altered), continuity).Count > 0;
                return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "I can compose a short piece for your household.")));
            }
            var settings = TestDict("highMemoryMode", false, "memoryPrecisionMode", "legacy", "enableSemanticMemory", false,
                "enableMinimeMemoryWorker", false, "enableMinimeMemoryReranking", false, "mainModel", "fixture-main");
            var response = CourtLifeRespond(payload, settings, Writer);
            Check("production_route_accepts_creator_without_native_profile", ReadBool(response, "ok", false)
                && calls == 1 && requiredPolitics && ReadJsonObject(CharacterFile(campaign, actor, "profile.json")).Count == 0);
            Check("provider_preflight_retains_canonical_required_evidence", requiredEvidence);
            Check("provider_preflight_still_rejects_altered_evidence", alteredEvidenceRejected);
            Check("political_revision_is_persisted_in_exact_timeline", ReadJsonObject(CharacterFile(campaign, actor,
                "politics", "revision-1-" + SafeMemoryKey(timeline) + ".json")).Count > 0);
            var replay = CourtLifeRespond(payload, settings, Writer);
            using (var connection = OpenCampaignConnection(campaign))
            {
                Check("replay_preserves_sources_without_provider_duplicate", ReadBool(replay, "ok", false)
                    && calls == 1 && ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS n FROM court_life_turns WHERE actor_id=$id;", TestDict("id", actor)).Single(), "n", 0) == 1);
                Check("original_player_whitespace_is_stored_without_editing", ReadString(QuerySql(connection,
                    "SELECT player_text FROM court_life_turns WHERE actor_id=$id;", TestDict("id", actor)).Single(), "player_text", "")
                    == ReadString(payload, "playerText", ""));
            }
            var forged = CloneDictionary(payload); forged["actorId"] = "missing_native_court_lord";
            forged["turnId"] = "forged_creator";
            Check("native_identity_cannot_use_creator_profile", !ReadBool(CourtLifeRespond(forged, settings, Writer), "ok", true) && calls == 1);
            forged["source"] = "International";
            bool nativeGate = false;
            try { CourtLifeRespond(forged, settings, Writer); }
            catch (InvalidOperationException ex) { nativeGate = ex.Message.Contains("native profile", StringComparison.Ordinal); }
            Check("native_speaker_profile_requirement_remains", nativeGate && calls == 1);
        }
    }
}
