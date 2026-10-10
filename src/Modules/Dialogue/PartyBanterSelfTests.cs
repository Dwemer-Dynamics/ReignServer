using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ReignBeta.Shared.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPartyBanterSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "party_banter_" + id, "passed", pass, "summary", id, "data", data));
            Check("eight_hour_boundary", !PartyBanterPolicy.CanStart(true, true, false, false, 2, 10 + 7.99 / 24, 10, 100, 0)
                && PartyBanterPolicy.CanStart(true, true, false, false, 2, 10 + 8d / 24, 10, 100, 0));
            Check("travel_and_modal_gates", !PartyBanterPolicy.CanStart(true, false, false, false, 2, 20, 10, 100, 0)
                && !PartyBanterPolicy.CanStart(true, true, false, true, 2, 20, 10, 100, 0)
                && !PartyBanterPolicy.CanStart(true, true, true, false, 2, 20, 10, 100, 0));
            Check("first_travel_and_backoff", PartyBanterPolicy.CanStart(true, true, false, false, 2, 1, -1, 60, 60)
                && !PartyBanterPolicy.CanStart(true, true, false, false, 2, 1, -1, 59, 60));
            double retryDeadline = PartyBanterPolicy.FailureRetryAfter("episode", "generating", "episode", "failed", 100, 0);
            foreach (double poll in new[] { 105d, 159d, 160d, 200d })
                retryDeadline = PartyBanterPolicy.FailureRetryAfter("episode", "failed", "episode", "failed", poll, retryDeadline);
            Check("failed_episode_polling_cannot_postpone_retry", retryDeadline == 160
                && !PartyBanterPolicy.CanStart(true, true, false, false, 2, 1, -1, 159, retryDeadline)
                && PartyBanterPolicy.CanStart(true, true, false, false, 2, 1, -1, 160, retryDeadline));
            Check("new_failure_rearms_backoff", PartyBanterPolicy.FailureRetryAfter("episode", "failed", "next", "failed", 200, retryDeadline) == 260
                && PartyBanterPolicy.FailureRetryAfter(null, null, "restored", "failed", 20, 0) == 80
                && PartyBanterPolicy.FailureRetryAfter("episode", "failed", "next", "generating", 200, retryDeadline) == retryDeadline);
            Check("paused_reading_clock", PartyBanterPolicy.ReadingSeconds("A short reply") == 2.6 && PartyBanterPolicy.ReadingSeconds(new string('x', 100)) == 2.2
                && PartyBanterPolicy.ReadingSeconds(string.Join(" ", Enumerable.Repeat("word", 100))) == 6);
            Check("unicode_character_limit", PartyBanterPolicy.CharacterCount("e\u0301🙂") == 2 && PartyBanterPolicy.Fits("e\u0301🙂", 2) && !PartyBanterPolicy.Fits("e\u0301🙂!", 2));
            Check("output_scales_with_options", PartyBanterPolicy.OutputTokens(12, 1000) > 5000 && PartyBanterPolicy.OutputTokens(6, 360) > 1200);
            Check("settings_validation", ValidateBanterOptions(TestDict("banterEnabled", false, "banterMaxResponses", 12, "banterMaxCharacters", 1000)) == ""
                && ValidateBanterOptions(TestDict("banterMaxResponses", 2.5)) != "" && ValidateBanterOptions(TestDict("banterMaxCharacters", 79)) != ""
                && ValidateBanterOptions(TestDict("banterEnabled", "true")) != "");
            Check("topic_range", BanterTopicSeeds.Split('\n').Length >= 60 && BanterTopicSeeds.Split('\n').Select(x => x.Split('|')[0]).Distinct().Count() == 12);
            string canceledUrl = "http://banter-offline.invalid/canceled";
            string canceledProvider = NormalizeLookup(canceledUrl) + "|" + NormalizeLookup("fixture");
            ProviderState providerState;
            lock (ProviderMiddlewareLock) providerState = GetProviderStateLocked(canceledProvider, TestDict());
            long priorFailures = providerState.Failures; bool cancellationObserved = false;
            try { ProviderPostJson(canceledUrl, "", "{}", TestDict(), TestDict(), "banter_cancel_fixture", "party_banter", "fixture", _ => { }, () => true); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Check("cancellation_preserves_shared_provider_circuit", cancellationObserved && providerState.Failures == priorFailures && providerState.ConsecutiveFailures == 0);
            bool subscriptionCanceled = false;
            try { CodexChatCompletionJson(TestDict(), TestDict(), "fixture", null, null, () => true); }
            catch (OperationCanceledException) { subscriptionCanceled = true; }
            Check("subscription_cancellation_precedes_runtime_startup", subscriptionCanceled);
            var options = TestDict("maxResponses", 6, "maxCharacters", 80); var ids = new List<string> { "noble", "commoner" };
            var draft = ValidateBanterDraft("{\"speakerId\":\"noble\",\"text\":\"Mind your footing.\"}", ids, options, new());
            Check("draft_validation", draft != null && ValidateBanterDraft("{\"speakerId\":\"player\",\"text\":\"Hello\"}", ids, options, new()) == null
                && ValidateBanterDraft("{\"speakerId\":\"noble\",\"text\":\"*strikes him*\"}", ids, options, new()) == null
                && ValidateBanterDraft("{\"speakerId\":\"noble\",\"text\":\"Again\"}", ids, options, new() { draft }) == null);
            Check("all_selected_speakers_precede_repetition", ValidateBanterDraft("{\"speakerId\":\"noble\",\"text\":\"Again\"}",
                new() { "noble", "commoner", "companion" }, options, new() { draft, TestDict("speakerId", "commoner", "text", "Of course.") }) == null);
            Check("honorifics_rejected_before_acceptance", new[] { "Yes, my lord.", "My LADY, listen.", "Of course, my\u00a0lady.", "No, milord.", "Milady?" }
                .All(text => ValidateBanterDraft(Json.Serialize(TestDict("speakerId", "noble", "text", text)), ids, options, new()) == null)
                && ValidateBanterDraft(Json.Serialize(TestDict("speakerId", "commoner", "text", "Not a chance.")), ids, options, new() { draft }) != null
                && ReadString(draft, "text", "") == "Mind your footing.");
            var reactionDrafts = new List<Dictionary<string, object>> {
                TestDict("speakerId", "noble", "text", "You are a fool.", "reactions", new[] { TestDict("observer", "commoner", "target", "noble", "source", 1, "quote", "You are a fool.", "valence", "negative", "tier", "hostile", "act", "insult", "reason", "The insult wounds their trust.") }),
                TestDict("speakerId", "commoner", "text", "I still wish you well.", "reactions", new[] { TestDict("observer", "noble", "target", "commoner", "source", 2, "quote", "I still wish you well.", "valence", "negative", "tier", "routine", "act", "kindness") }),
                TestDict("speakerId", "noble", "text", "Keep quiet, fool.", "reactions", new[] { TestDict("observer", "commoner", "target", "noble", "source", 3, "quote", "Keep quiet, fool.", "valence", "negative", "tier", "routine", "act", "insult") }) };
            var e = TestDict("sessionId", "fixture", "heroIds", ids, "accepted", 3, "drafts", reactionDrafts);
            var reactions = BanterAssessments(e);
            Check("direction_and_one_reaction_per_pair", reactions.Count == 1 && ReadString(reactions[0], "observerHeroStringId", "") == "commoner"
                && ReadString(reactions[0], "severityTier", "") == "hostile");
            e["accepted"] = 1; Check("unspoken_effects_excluded", BanterAssessments(e).Count == 1);
            string campaign = "party_banter_test_" + Guid.NewGuid().ToString("N");
            var priorProvider = BanterTestProvider;
            try
            {
                using var c = OpenCampaignConnection(campaign); EnsureBanterSchema(c); EnsureAcceptedConversationUnitSchema(c);
                string generation = BanterGeneration(c), session = "banter_" + Guid.NewGuid().ToString("N");
                Dictionary<string, object> Hero(string id, bool noble, bool ruler = false) => TestDict("heroStringId", id, "name", id == "noble" ? "Lady Mira" : "Jon",
                    "age", 30, "isAlive", true, "isLord", noble, "isRuler", ruler, "isFemale", noble, "currentPartyId", "main_party", "cultureId", "vlandia", "occupation", noble ? "Lord" : "Wanderer");
                var heroes = new List<Dictionary<string, object>> { Hero("noble", true, true), Hero("commoner", false) };
                string core = string.Join(" ", Enumerable.Repeat("They are reserved yet generous, proud of their work, slow to trust and quick to defend a friend.", 14));
                foreach (string id in ids) WriteJsonObject(CharacterFile(campaign, id, "traits.json"), TestDict("basePersonalitySummary", core));
                string prompt = BuildBanterPrompt(c, campaign, heroes, "travel|the wind", options, new(), TestDict(), false);
                Check("protected_identity_personality_addresses", prompt.Split(new[] { core }, StringSplitOptions.None).Length == 3
                    && prompt.Contains("queen") && prompt.Contains("commoner") && prompt.Contains("Your Majesty") && prompt.Contains("affinity"));
                var noblePeers = new List<Dictionary<string, object>> { Hero("noble", true), Hero("commoner", true) };
                string peerPrompt = BuildBanterPrompt(c, campaign, noblePeers, "ambition|what success would feel like", options, new(), TestDict(), false);
                var pairCues = peerPrompt.Split('\n').Select(line => TryParseJsonObject(line.Trim())).Where(row => row != null && row.ContainsKey("toward")).ToList();
                Check("rank_preserved_without_compulsory_vocatives", peerPrompt.Contains("noble peer") && pairCues.Count == 2
                    && pairCues.All(row => ReadString(row, "address", "") == "usually omit" && ReadString(row, "formalAddress", "") == ""
                        && !ReadBool(row, "nameKnown", true))
                    && !peerPrompt.Contains("my lady") && !peerPrompt.Contains("my lord"));
                UpsertVerifiedIdentity(c, heroes[1], heroes[0], "banter_fixture", "", 20d, "banter_fixture");
                string knownPrompt = BuildBanterPrompt(c, campaign, heroes, "travel|the wind", options, new(), TestDict(), false);
                var knownCue = knownPrompt.Split('\n').Select(line => TryParseJsonObject(line.Trim())).First(row => row != null && ReadString(row, "toward", "") == "noble");
                Check("known_name_optional_ruler_address_and_directed_identity", ReadBool(knownCue, "nameKnown", false)
                    && ReadString(knownCue, "knownAs", "") == "Lady Mira" && ReadString(knownCue, "formalAddress", "").Contains("Your Majesty")
                    && ReadString(knownCue, "address", "") == "usually omit"
                    && knownPrompt.Split('\n').Select(line => TryParseJsonObject(line.Trim())).Any(row => row != null
                        && ReadString(row, "toward", "") == "commoner" && !ReadBool(row, "nameKnown", true)));
                var p = TestDict("campaignId", campaign, "timelineId", "test", "generation", generation, "sessionId", session,
                    "playerHeroStringId", "player", "partyId", "main_party", "worldDay", 20d, "traveling", true, "heroes", heroes);
                var ineligible = CloneDictionary(heroes[1]); ineligible["isPrisoner"] = true;
                var rp = CloneDictionary(p); rp["heroes"] = new[] { heroes[0], ineligible, Hero("player", false) };
                Check("strict_party_roster", BanterRoster(rp).Count == 1);
                int calls = 0; bool requestContentValid = false;
                BanterTestProvider = (request, emit, canceled) => {
                    Interlocked.Increment(ref calls);
                    var wireMessages = ReadDictionaryList(TryParseJsonObject(Json.Serialize(request)), "messages");
                    requestContentValid = wireMessages.Count == 2
                        && ReadString(wireMessages[0], "role", "") == "system" && ReadString(wireMessages[0], "content", "") == BanterRules
                        && ReadString(wireMessages[1], "role", "") == "user" && !string.IsNullOrWhiteSpace(ReadString(wireMessages[1], "content", ""))
                        && ReadString(wireMessages[1], "content", "").Contains(core);
                    if (!requestContentValid) throw new InvalidOperationException("Provider messages require role and nonempty content.");
                    string content = "{\"speakerId\":\"noble\",\"text\":\"You are a worthless coward.\",\"reactions\":[{\"observer\":\"commoner\",\"target\":\"noble\",\"source\":1,\"quote\":\"worthless coward\",\"valence\":\"negative\",\"tier\":\"hostile\",\"act\":\"insult\",\"reason\":\"Their trust is wounded by the insult.\"}]}\n{\"speakerId\":\"commoner\",\"text\":\"I still wish you well.\"}\n{\"speakerId\":\"noble\",\"text\":\"Thank you, my lady.\"}\n";
                    foreach (string fragment in new[] { content.Substring(0, 15), content.Substring(15, 20), content.Substring(35) }) emit(fragment);
                    return TestDict("ok", true, "content", content);
                };
                var started = PartyBanterApi("start", p);
                bool completed = SpinWait.SpinUntil(() => { using var probe = OpenCampaignConnection(campaign); return ReadBool(ReadBanter(probe, session), "generationDone", false); }, 10000);
                var saved = ReadBanter(c, session);
                Check("provider_messages_include_content", completed && requestContentValid);
                Check("one_call_fragmented_stream", completed && calls == 1 && ReadDictionaryList(saved, "drafts").Count == 2, saved);
                Check("invalid_honorific_preserves_prefix_without_repair_call", completed && calls == 1 && ReadDictionaryList(saved, "drafts").Count == 2
                    && !string.IsNullOrEmpty(ReadString(saved, "error", ""))
                    && ReadString(ReadDictionaryList(saved, "drafts")[1], "text", "") == "I still wish you well.");
                Check("drafts_not_history", ReadCurrentSessionDialogueLines(campaign, session, 30).Count == 0);
                p["expectedRevision"] = ReadInt(saved, "revision", 0); p["sequence"] = 1;
                var accepted = PartyBanterApi("accept", p); var retry = PartyBanterApi("accept", p);
                Check("atomic_acceptance_and_retry", ReadInt(accepted, "accepted", 0) == 1 && ReadInt(retry, "accepted", 0) == 1
                    && ReadCurrentSessionDialogueLines(campaign, session, 30).Count == 1);
                Check("no_manufactured_player_source", QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id=$id AND role='player';", TestDict("id", session)).Count == 0);
                var listener = BuildKnowledgeAccessContext("commoner", "player", "", TestDict("timelineId", "test", "worldDay", 20d));
                var absent = BuildKnowledgeAccessContext("absent", "player", "", TestDict("timelineId", "test", "worldDay", 20d));
                Check("accepted_words_recalled_by_listener_only", ReadLogicalConversationTurns(c, session, 30, listener).Count == 1
                    && ReadLogicalConversationTurns(c, session, 30, absent).Count == 0);
                int fixtureOrdinal = 0;
                void ExcludedShared(string id, double day, string timeline, List<string> audience, bool invalid = false)
                {
                    InsertConversationTurn(c, id, "shared_fixture", id, fixtureOrdinal++,
                        id, "npc", "noble", "Lady Mira", "Words from " + id + " must remain private.", "party_chat", day, 5L, "", audience,
                        TestDict("timelineId", timeline, "worldDay", day));
                    if (invalid) ExecuteSql(c, "UPDATE conversation_turns SET status='invalidated' WHERE turn_id=$id;", TestDict("id", id));
                }
                ExcludedShared("foreign_branch", 20d, "sibling", ids);
                ExcludedShared("future_branch", 21d, "test", ids);
                ExcludedShared("private_whisper", 20d, "test", new() { "noble", "player" });
                ExcludedShared("revoked_words", 20d, "test", ids, true);
                var shared = BanterSharedExcerpt(c, ids, p);
                Check("shared_excerpt_excludes_foreign_future_private_and_invalidated", shared.Count == 1
                    && !ReadString(shared[0], "sourceId", "").Contains("branch") && ReadString(shared[0], "quote", "").Contains("worthless coward"));
                var cutoff = CloneDictionary(p); cutoff["memoryAsOfTimestamp"] = 0L;
                Check("shared_excerpt_respects_as_of_before_limit", BanterSharedExcerpt(c, ids, cutoff).Count == 0);
                p["expectedRevision"] = ReadInt(accepted, "revision", 0);
                var handoff = PartyBanterApi("handoff", p);
                Check("takeover_same_session_prefix_only", ReadString(handoff, "handoffSessionId", "") == session && ReadInt(handoff, "accepted", 0) == 1
                    && ReadInt(handoff, "draftCount", 0) == 1 && ReadCurrentSessionDialogueLines(campaign, session, 30).Count == 1);
                var relationshipEffects = ReadDictionary(handoff, "relationshipEffects");
                var receipts = ReadDictionaryList(relationshipEffects, "receipts");
                Check("accepted_insult_changes_only_listener_direction", receipts.Count == 1
                    && ReadString(receipts[0], "observerHeroStringId", "") == "commoner"
                    && ReadString(receipts[0], "targetHeroStringId", "") == "noble" && ReadInt(receipts[0], "finalDelta", 0) < 0);
                p["expectedRevision"] = ReadInt(handoff, "revision", 0);
                PartyBanterApi("handoff", p);
                Check("takeover_retry_cannot_duplicate_affinity", QuerySql(c,
                    "SELECT receipt_id FROM conversation_relationship_receipts WHERE exchange_id=$id;", TestDict("id", session + "_relations")).Count == 1);
                ExecuteSql(c, "UPDATE conversation_sessions SET status='closed',close_reason='closed_by_player' WHERE session_id=$id;", TestDict("id", session));
                Check("reopening_closed_takeover_is_read_only", ReadBool(PartyBanterApi("handoff", p), "readOnly", false));
                var later = CloneDictionary(p); later["sessionId"] = "banter_" + Guid.NewGuid().ToString("N"); later["worldDay"] = 20 + 7d / 24;
                Check("takeover_preserves_cooldown", !ReadBool(PartyBanterApi("start", later), "eligible", true));
                p["generation"] = "old_generation";
                Check("stale_callback_fence", ReadString(PartyBanterApi("accept", p), "errorCode", "") == "banter_stale_generation");
                // Simulate a restored snapshot retaining one unspoken draft and a
                // new authoritative restore generation. Fresh context can resume it.
                saved["sessionId"] = "restored_banter"; saved["state"] = "generating"; saved["generation"] = "old_generation"; saved["accepted"] = 0; saved["drafts"] = new[] { draft };
                WriteBanter(c, saved);
                var context = PartyBanterApi("context", later); var restored = ReadDictionary(context, "episode");
                Check("restore_retains_drafts_and_rebinds", ReadString(restored, "generation", "") == generation && ReadBool(restored, "generationDone", false)
                    && ReadInt(restored, "draftCount", 0) == 1);
                var abandoned = CloneDictionary(saved); abandoned["sessionId"] = "empty_restored_banter"; abandoned["drafts"] = new List<Dictionary<string, object>>();
                abandoned["effectsFinalized"] = false; WriteBanter(c, abandoned);
                PartyBanterApi("context", later);
                Check("empty_restore_finalizes_without_cooldown", ReadBool(ReadBanter(c, "empty_restored_banter"), "effectsFinalized", false)
                    && ReadDouble(ReadBanter(c, "empty_restored_banter"), "endedDay", 0) == -1);
                var stopped = ReadBanter(c, "restored_banter"); var stop = CloneDictionary(later);
                stop["sessionId"] = "restored_banter"; stop["expectedRevision"] = -1; stop["reason"] = "campaign_exit";
                var exited = PartyBanterApi("finish", stop);
                Check("exit_discards_unspoken_drafts_despite_inflight_revision", ReadBool(exited, "ok", false)
                    && ReadInt(exited, "draftCount", -1) == 0 && ReadInt(exited, "accepted", -1) == 0);
                foreach (bool withPrefix in new[] { false, true })
                {
                    string legacyId = "banter_" + Guid.NewGuid().ToString("N");
                    var legacy = CloneDictionary(saved);
                    legacy["sessionId"] = legacyId; legacy["generation"] = generation; legacy["revision"] = 1;
                    legacy["state"] = "ready"; legacy["effectsFinalized"] = false; legacy["accepted"] = 0;
                    legacy["startedDay"] = 25d; legacy["endedDay"] = -1d;
                    var legacyDrafts = new List<Dictionary<string, object>>();
                    if (withPrefix) legacyDrafts.Add(TestDict("speakerId", "noble", "text", "Mind your footing."));
                    legacyDrafts.Add(TestDict("speakerId", withPrefix ? "commoner" : "noble", "text", "Of course, my lady."));
                    legacy["drafts"] = legacyDrafts;
                    ConversationStartApi(TestDict("campaignId", campaign, "timelineId", "test", "sessionId", legacyId,
                        "npcId", "noble", "playerId", "player", "channel", "party_chat", "worldDay", 25d,
                        "participants", ids.Concat(new[] { "player" }).ToList()));
                    WriteBanter(c, legacy); // Simulate persisted output from the old installed prompt.
                    var legacyRequest = CloneDictionary(later);
                    legacyRequest["sessionId"] = legacyId; legacyRequest["worldDay"] = 25d;
                    legacyRequest["expectedRevision"] = 1; legacyRequest["sequence"] = 1;
                    if (withPrefix)
                    {
                        var first = PartyBanterApi("accept", legacyRequest);
                        legacyRequest["expectedRevision"] = ReadInt(first, "revision", 0); legacyRequest["sequence"] = 2;
                    }
                    var blocked = PartyBanterApi("accept", legacyRequest);
                    PartyBanterApi("accept", legacyRequest); // A stale retry must not publish the rejected suffix.
                    var history = ReadCurrentSessionDialogueLines(campaign, legacyId, 30);
                    Check(withPrefix ? "legacy_draft_recheck_preserves_accepted_prefix" : "legacy_draft_recheck_before_any_history",
                        ReadString(ReadBanter(c, legacyId), "reason", "") == "obsolete_address_draft"
                        && ReadInt(blocked, "accepted", -1) == (withPrefix ? 1 : 0)
                        && ReadInt(blocked, "draftCount", -1) == (withPrefix ? 1 : 0)
                        && history.Count == (withPrefix ? 1 : 0)
                        && (!withPrefix || ReadString(history[0], "text", "") == "Mind your footing.")
                        && ReadString(blocked, "state", "") == (withPrefix ? "finished" : "failed")
                        && (withPrefix || ReadDouble(blocked, "endedDay", 0) == -1d)
                        && calls == 1, blocked);
                }
            }
            catch (Exception ex) { Check("harness_execution", false, ex.ToString()); }
            finally { BanterTestProvider = priorProvider; ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
