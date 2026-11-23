using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static void RunPublicWitnessGossipSelfTests(Action<string, bool, object> check)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                throw new InvalidOperationException("Witness fixtures require isolated Verification Lab state.");
            var priorSettings = LlmProviderService.CodexPerformanceSettings.Value;
            string campaign = "__gossip_test_witness_" + Guid.NewGuid().ToString("N");
            var settings = SettingsService.DefaultSettings();
            settings["personalGossipEnabled"] = true;
            settings["personalGossipShareAdjustment"] = 100;
            settings["personalGossipDisclosureAdjustment"] = 100;
            LlmProviderService.CodexPerformanceSettings.Value = settings;
            var source = GossipTestHero("source"); var player = GossipTestHero("player");
            var target = GossipTestHero("target"); var witness = GossipTestHero("witness");
            var second = GossipTestHero("second"); var third = GossipTestHero("third");
            var unrecorded = GossipTestHero("town_only");
            var people = new List<Dictionary<string, object>> { source, player, target, witness, second, third, unrecorded };
            const string quote = "The next dance is yours, traveler.";
            const string privateWords = "The treasury key is under my pillow.";
            Dictionary<string, object> Payload(double day, string session) => GossipFields(
                "campaignId", campaign, "timelineId", "main", "worldDay", day, "mode", "social_event",
                "conversationSessionId", session, "playerHeroStringId", "player", "heroStringId", "source",
                "attendees", people, "opportunitySnapshot", GossipFields("scene", GossipFields("private", false,
                    "witnessIds", new[] { "witness", "second", "third", "source", "player" })),
                "gossipPresence", GossipFields("schema", "reign-gossip-presence-v1", "timelineId", "main", "worldDay", day,
                    "rosterComplete", true, "people", people));
            Dictionary<string, object> Signal(string words, int salience = 50) => GossipFields("publicWitnessSignals", new[] {
                GossipFields("topic", "shared_activity", "evidenceQuote", words, "salience", salience, "confidence", 1d,
                    "privacy", "public", "existencePrivacy", "public", "promisedSecrecy", false) });
            Dictionary<string, object> Store(ReignDbConnection c, Dictionary<string, object> payload, string id,
                string playerWords = "Would you care to dance?", string npcWords = quote)
            {
                string session = ReadString(payload, "conversationSessionId", "");
                double day = ReadDouble(payload, "worldDay", 0);
                DialogueSelfTestStore.InsertOrIgnoreConversationSessions2(c, GossipFields("id", session, "campaign", campaign, "day", day));
                return StoreConversationExchange(campaign, payload, "source", "player", "Traveler", "Source", playerWords, npcWords, id, 1L);
            }
            try
            {
                using var c = ReignDatabase.OpenCampaignConnection(campaign);
                EnsurePersonalGossipSchema(c); RelationshipsService.EnsureMbtiRelationshipSchema(c); EnsureCategorizedMemorySchema(c); EnsureMemoryPrecisionSchema(c);
                RelationshipsService.EnsureConversationRelationshipSchema(c);
                foreach (string id in new[] { "witness", "second", "third" })
                {
                    CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, id, "traits.json"), GossipTestTraits("sociability", 100, "curiosity", 100));
                    GossipTestPair(c, id, "target", 60, 30);
                }
                GossipTestPair(c, "witness", "source", 60, 30);
                var publicPayload = Payload(20.125, "public_scene");
                var exchange = Store(c, publicPayload, "public_exchange");
                Task.WaitAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => AcceptPersonalGossip(campaign, publicPayload, Signal(quote), exchange,
                    "source", "player", "Would you care to dance?", quote))).ToArray());
                var jobs = DialogueSelfTestStore.SelectPersonalGossipMessages20(c);
                check("witness_actual_public_source_queues_with_global_cap_and_no_replay", jobs.Count == 2
                    && jobs.All(job => ReadString(GossipJson(job), "sourceOrigin", "") == "public_witness")
                    && jobs.All(job => ReadString(job, "speaker_id", "") != "town_only")
                    && DialogueSelfTestStore.SelectPersonalGossipHistory2(c).Count == 0, jobs.Count);
                string queuedBytes = CanonicalJson(jobs);
                AcceptPersonalGossip(campaign, publicPayload, Signal(quote, 100), exchange,
                    "source", "player", "Would you care to dance?", quote);
                check("witness_queued_source_and_decision_remain_frozen", CanonicalJson(DialogueSelfTestStore.SelectPersonalGossipMessages20(c)) == queuedBytes, null);
                check("gossip_impression_feelings_do_not_supply_detailed_source_strength", PersonalGossipSourceStrength(
                    GossipFields("category", "impression", "signal", GossipFields("curiosity", 75, "excitement", 100))) == 0, null);
                var first = GossipJson(jobs[0]);
                var canonical = ReadStringList(first, "sourceTurnIds").SelectMany(id => DialogueSelfTestStore.SelectConversationTurns32(c, GossipFields("id", id))).ToList();
                BindPersonalGossipEncounterTurns(first, canonical);
                var framed = PersonalGossipFramedSource(first);
                check("witness_frame_preserves_original_author_addressee_and_anonymous_player", framed != null
                    && ReadString(framed, "body", "").StartsWith("I heard ", StringComparison.Ordinal)
                    && ReadString(framed, "body", "").Contains(" say to ", StringComparison.Ordinal)
                    && ReadString(framed, "body", "").Contains(quote, StringComparison.Ordinal)
                    && !ReadString(framed, "body", "").Contains("told me", StringComparison.Ordinal)
                    && ReadDouble(first, "delta", -1) == 0 && ReadString(first, "type", "") == "positive", framed);
                var mutated = canonical.Select(row => new Dictionary<string, object>(row) { ["text"] = "A replaced source with different public words." }).ToList();
                check("witness_source_hash_mismatch_cannot_regenerate_authority", !PublicWitnessGossipSourcesActive(first, mutated), null);
                check("witness_duplicate_or_rebound_source_fails_closed", !PublicWitnessGossipSourcesActive(first, canonical.Concat(canonical))
                    && !PublicWitnessGossipSourcesActive(new Dictionary<string, object>(first) { ["sourceQuoteSpeakerId"] = "player" }, canonical)
                    && !PublicWitnessGossipSourcesActive(new Dictionary<string, object>(first) { ["sourceTurnIds"] = new[] { "unrelated_source" } }, canonical), null);
                var projection = PublicGossipSource(GossipFields("turn_id", "projection", "speaker_id", "player",
                    "text", "Hello there *I secretly wish for revenge.* meet me later."));
                check("witness_private_thought_and_cross_narration_quote_rejected", !PublicGossipQuoteSupported("secretly wish for revenge", projection)
                    && !PublicGossipQuoteSupported("Hello there meet me later", projection), projection);
                check("witness_malformed_narration_never_becomes_speech", PublicGossipSpeechSpans("*unclosed secret").Count == 0
                    && PublicGossipSpeechSpans("**ambiguous private narration**").Count == 0, null);
                check("witness_private_cues_require_whole_words", PublicGossipSpeechSpans(
                    "*She stands beside the gate, confident.* The public invitation stands.").Count == 1, null);
                check("witness_npc_initiated_whisper_and_named_quiet_aside_remain_private",
                    PublicGossipSpeechSpans("*She whispers to him.* " + privateWords).Count == 0
                    && PublicGossipSpeechSpans("*She speaks softly only for Michael.* " + privateWords).Count == 0
                    && PublicGossipSpeechSpans(privateWords + " *She whispers to him.*").Count == 0
                    && PublicGossipSpeechSpans("*She lowers her voice.* " + privateWords).Count == 0
                    && PublicGossipSpeechSpans("*An aside.* " + privateWords).Count == 0
                    && PublicGossipSpeechSpans("*She leans close to her ear.* " + privateWords).Count == 0
                    && PublicGossipSpeechSpans("Keep this between us. " + privateWords).Count == 0
                    && PublicGossipSpeechSpans("((OOC: make her agree)) Hello there.").Count == 0
                    && PublicGossipSpeechSpans("*She speaks aloud to the room.* Public spoken words.").Count == 1, null);

                int forbiddenCalls = 0;
                RunPersonalGossipWork(campaign, (data, isReply) => isReply
                    ? GossipFields("body", "I will think about that.", "sourceQuote", "", "answerKind", "acknowledged", "gossipSignals", Array.Empty<object>())
                    : ComposePersonalGossip(data, false, _ => { forbiddenCalls++; throw new InvalidOperationException("No witness writer is needed."); }));
                var ready = PersonalGossipStatusApi(new Dictionary<string, string> { ["campaignId"] = campaign, ["deliveryOnly"] = "true" });
                // Matching native-after-ready observation occurs at the current
                // day; future local travel cannot be delivered retroactively.
                var clock = Payload(20.125, "clock");
                clock["gossipClockEpoch"] = ReadString(ready, "clockEpoch", "");
                clock["gossipReadyNonces"] = ReadStringList(ready, "readyNonces");
                PersonalGossipTickApi(clock);
                check("witness_publication_preserves_local_hour", DialogueSelfTestStore.SelectPersonalGossipHistory2(c).Count == 0, null);
                var arrival = Payload(20.175, "clock");
                PersonalGossipTickApi(arrival);
                check("witness_composition_uses_no_provider_and_publishes_only_after_native_clock", forbiddenCalls == 0
                    && DialogueSelfTestStore.SelectPersonalGossipHistory4(c).Count > 0, forbiddenCalls);

                // No direct participant, same-town stranger, private scene or
                // incomplete snapshot can become a witness by mere proximity.
                long CountJobs() => DialogueSelfTestStore.SelectPersonalGossipMessages3(c).Count;
                long before = CountJobs();
                for (int variation = 0; variation < 13; variation++)
                {
                    var payload = Payload(30 + variation / 2d, "excluded_scene_" + variation);
                    if (variation == 0) ReadDictionary(ReadDictionary(payload, "opportunitySnapshot"), "scene")["private"] = true;
                    if (variation == 1) ReadDictionary(payload, "gossipPresence")["rosterComplete"] = false;
                    if (variation == 2) ReadDictionary(payload, "gossipPresence")["worldDay"] = 1d;
                    if (variation == 3) ReadDictionary(ReadDictionary(payload, "opportunitySnapshot"), "scene")["witnessIds"] = new[] { "source", "player" };
                    if (variation == 4) payload["attendees"] = new[] { source, player };
                    if (variation == 5) payload["officialMemoryFirewall"] = true;
                    if (variation == 7) payload["conversationSceneState"] = GossipFields("current", new[] { source, player });
                    if (variation == 8) ReadDictionary(payload, "gossipPresence")["people"] = new[] { source, player, target };
                    if (variation == 9) payload["conversationSceneState"] = GossipFields("current", Array.Empty<object>());
                    var excluded = Store(c, payload, "excluded_exchange_" + variation);
                    var metadata = Signal(quote, variation == 6 ? 0 : 50);
                    if (variation == 10) ReadDictionaryList(metadata, "publicWitnessSignals")[0].Remove("privacy");
                    if (variation == 11) ReadDictionaryList(metadata, "publicWitnessSignals")[0]["existencePrivacy"] = "secret";
                    if (variation == 12) ReadDictionaryList(metadata, "publicWitnessSignals")[0]["promisedSecrecy"] = true;
                    AcceptPersonalGossip(campaign, payload, metadata, excluded, "source", "player", "Would you care to dance?", quote);
                }
                check("witness_requires_actual_public_audience_complete_current_presence_and_salience", CountJobs() == before, null);
                var quietPayload = Payload(40, "quiet_scene");
                string input = "*I speak quietly only to Source.* " + privateWords;
                ProjectPlayerInputForAudience(input, "witness", people, out string publicWords, out _, privateFocusId: "source");
                var quiet = Store(c, quietPayload, "quiet_exchange", publicWords, "*A private reply was exchanged.*");
                AcceptPersonalGossip(campaign, quietPayload, Signal(privateWords), quiet, "source", "player", input, privateWords);
                check("witness_quiet_aside_and_private_npc_reply_cannot_supply_source", CountJobs() == before, null);

                // Retellers and replies receive only the communicated account.
                var onward = new Dictionary<string, object>(first) { ["hop"] = 2, ["sourceQuote"] = "A guest reportedly accepted a dance." };
                var reply = new Dictionary<string, object>(first) { ["reply"] = true, ["receivedBody"] = "A guest reportedly accepted a dance." };
                check("witness_retell_reply_have_only_communicated_context", !Json.Serialize(PersonalGossipEncounterContext(onward)).Contains(quote)
                    && !Json.Serialize(PersonalGossipEncounterContext(reply)).Contains(quote), null);
                var retellControl = new Dictionary<string, object>(first) { ["id"] = "witness_retell_interest_control", ["hop"] = 2,
                    ["signal"] = GossipFields("excitement", 0, "curiosity", 0, "hurt", 0, "privacy", "public", "existencePrivacy", "public") };
                EvaluatePersonalGossip(c, campaign, retellControl, people);
                check("witness_reteller_does_not_inherit_original_observers_interest", ReadDouble(retellControl, "emotion", -1) == 0
                    && !IsOriginalPublicWitnessGossip(onward) && !IsOriginalPublicWitnessGossip(reply), retellControl);


                // A weaker update must retain the entire strong source and its
                // episode, not attach its emotion to a new bland quotation.
                settings["personalGossipShareAdjustment"] = -100;
                var strongPayload = Payload(50, "strong_episode");
                var strong = Store(c, strongPayload, "strong_exchange", "An important public promise.", "That matters to me.");
                var strongSignal = GossipFields("topic", "politics", "confidence", 1d, "evidenceQuote", "An important public promise.",
                    "curiosity", 75, "privacy", "secret", "existencePrivacy", "secret", "promisedSecrecy", true, "ownCompromising", false);
                AcceptPersonalGossip(campaign, strongPayload, GossipFields("gossipSignals", new[] { strongSignal }), strong,
                    "source", "player", "An important public promise.", "That matters to me.");
                string topicId = GossipTopicId("main", "source", "player", 50, "politics", "");
                var originalTopic = GossipJson(DialogueSelfTestStore.SelectPersonalGossipTopics4(c, GossipFields("id", topicId)).Single());
                var sameEpisode = Store(c, strongPayload, "same_episode_weak_exchange", "It is a pleasant evening.", "Indeed it is.");
                var mildSignal = new Dictionary<string, object>(strongSignal) { ["curiosity"] = 25, ["evidenceQuote"] = "It is a pleasant evening." };
                AcceptPersonalGossip(campaign, strongPayload, GossipFields("gossipSignals", new[] { mildSignal }), sameEpisode,
                    "source", "player", "It is a pleasant evening.", "Indeed it is.");
                DialogueSelfTestStore.InsertConversationRelationshipReceipts8(c, GossipFields("campaign", campaign, "exchange", ReadString(sameEpisode, "exchangeId", "")));
                PersonalGossipRelationshipApplied(campaign, GossipFields("exchangeId", ReadString(sameEpisode, "exchangeId", ""), "worldDay", 50d));
                var afterReceipt = GossipJson(DialogueSelfTestStore.SelectPersonalGossipTopics4(c, GossipFields("id", topicId)).Single());
                check("gossip_retained_source_reassesses_late_receipt_in_same_episode", ReadDouble(afterReceipt, "delta", 0) == 2
                    && ReadString(afterReceipt, "sourceQuote", "") == "An important public promise."
                    && ReadString(afterReceipt, "sourceEvidenceExchangeId", "") == ReadString(strong, "exchangeId", ""), afterReceipt);
                var weakPayload = Payload(50, "weaker_episode");
                var weak = Store(c, weakPayload, "weak_exchange", "It is a pleasant evening.", "Indeed it is.");
                var weakSignal = new Dictionary<string, object>(strongSignal) { ["curiosity"] = 25, ["evidenceQuote"] = "It is a pleasant evening." };
                AcceptPersonalGossip(campaign, weakPayload, GossipFields("gossipSignals", new[] { weakSignal }), weak,
                    "source", "player", "It is a pleasant evening.", "Indeed it is.");
                var retained = GossipJson(DialogueSelfTestStore.SelectPersonalGossipTopics4(c, GossipFields("id", topicId)).Single());
                check("gossip_stronger_evidence_bundle_and_episode_survive_weaker_update", ReadString(retained, "sourceQuote", "") == "An important public promise."
                    && ReadString(retained, "sourceSessionId", "") == ReadString(strong, "sessionId", "")
                    && ReadString(retained, "deltaSourceSessionId", "") == ReadString(strong, "sessionId", "")
                    && ReadInt(ReadDictionary(retained, "signal"), "curiosity", 0) == 75
                    && Json.Serialize(ReadDictionary(retained, "savedRolls")) == Json.Serialize(ReadDictionary(originalTopic, "savedRolls")), retained);
                DialogueSelfTestStore.UpdateConversationTurns27(c);
                AcceptPersonalGossip(campaign, weakPayload, GossipFields("gossipSignals", new[] { weakSignal }), weak,
                    "source", "player", "It is a pleasant evening.", "Indeed it is.");
                var replaced = GossipJson(DialogueSelfTestStore.SelectPersonalGossipTopics4(c, GossipFields("id", topicId)).Single());
                check("gossip_inactive_strong_source_does_not_shadow_new_valid_source", ReadString(replaced, "sourceQuote", "") == "It is a pleasant evening.", replaced);
                var newEpisodePayload = Payload(50, "new_strong_episode");
                var newEpisode = Store(c, newEpisodePayload, "new_strong_exchange", "A different important promise.", "That is exceptional.");
                var newEpisodeSignal = new Dictionary<string, object>(strongSignal) { ["curiosity"] = 100, ["evidenceQuote"] = "A different important promise." };
                AcceptPersonalGossip(campaign, newEpisodePayload, GossipFields("gossipSignals", new[] { newEpisodeSignal }), newEpisode,
                    "source", "player", "A different important promise.", "That is exceptional.");
                var newEpisodeTopic = GossipJson(DialogueSelfTestStore.SelectPersonalGossipTopics4(c, GossipFields("id", topicId)).Single());
                check("gossip_new_source_episode_discards_older_reassessment_ids", ReadString(newEpisodeTopic, "sessionId", "") == ReadString(newEpisode, "sessionId", "")
                    && ReadStringList(newEpisodeTopic, "relationshipReassessmentExchangeIds").SequenceEqual(new[] { ReadString(newEpisode, "exchangeId", "") }), newEpisodeTopic);
                GossipTestPair(c, "witness", "target", 20, -20);
                DialogueSelfTestStore.UpdateRelationshipPairChemistry(c, GossipFields("pair", RelationshipsService.AmbientPairKey("witness", "target")));
                check("gossip_effective_directional_affinity_includes_social_modifier_and_clamps", GossipAffinity(c, "witness", "target", witness, target) == 30
                    && GossipAffinity(c, "target", "witness", target, witness) == -100, null);
                settings["personalGossipShareAdjustment"] = 100;
                target["settlementId"] = "remote_town"; target["positionX"] = 100d;
                var namedPayload = Payload(70, "named_remote_public_scene");
                const string namedQuote = "Ask target whether the next dance is theirs.";
                var namedExchange = Store(c, namedPayload, "named_remote_public_exchange", npcWords: namedQuote);
                AcceptPersonalGossip(campaign, namedPayload, Signal(namedQuote), namedExchange, "source", "player", "Would you care to dance?", namedQuote);
                var remoteJobs = DialogueSelfTestStore.SelectPersonalGossipMessages21(c);
                check("witness_public_literal_name_preserves_existing_remote_contact_route", remoteJobs.Count == 2
                    && remoteJobs.All(job => ReadString(job, "recipient_id", "") == "target"
                        && ReadStringList(GossipJson(job), "mentionedIds").Contains("target") && !ReadBool(GossipJson(job), "local", true)
                        && ReadDouble(job, "due_day", 0) > ReadDouble(job, "created_day", 0) + 1d / 24), remoteJobs.Count);
                var unnamedPayload = Payload(71, "unnamed_remote_public_scene");
                const string unnamedQuote = "Another dance would be welcome.";
                var unnamedExchange = Store(c, unnamedPayload, "unnamed_remote_public_exchange", npcWords: unnamedQuote);
                AcceptPersonalGossip(campaign, unnamedPayload, Signal(unnamedQuote), unnamedExchange, "source", "player", "Would you care to dance?", unnamedQuote);
                check("witness_unnamed_remote_friend_does_not_gain_travel_permission", DialogueSelfTestStore.SelectPersonalGossipMessages22(c).Count == 0, null);
            }
            catch (Exception ex) { check("witness_fixture_completed", false, ex.ToString()); }
            finally { LlmProviderService.CodexPerformanceSettings.Value = priorSettings; ReignPostgreSqlStorage.DropCampaign(campaign); }
        }
    }
}
