using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Family;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPregnancyContextAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "pregnancy_context_" + id, "passed", pass, "summary", id, "data", data));
            string campaign = "pregnancy_context_" + Guid.NewGuid().ToString("N");
            try
            {
                using var c = OpenCampaignConnection(campaign);
                EnsurePregnancyContextSchema(c);
                var f = new PregnancyFacts { PregnancyId = "pregnancy", MotherId = "mother", MotherName = "Ingunde",
                    BiologicalFatherId = "actual_father", BiologicalFatherName = "Actual Father", LegalFatherId = "husband", LegalFatherName = "Husband",
                    Active = true, Illicit = true, TimingKnown = true, ConceptionDay = 10, DueDay = 46, WorldDay = 28 };
                ExecuteSql(c, "INSERT INTO conception_contexts(timeline_id,pregnancy_id,facts_json,world_day) VALUES('test','pregnancy',$facts,28);", TestDict("facts", Json.Serialize(f)));
                GrantPregnancyKnowledge(c, "test", f, "mother", true, true, "native", "native_hash", 28);
                Check("self_and_unknown_existing_save", BuildPregnancyPrompt(TestDict("heroStringId", "mother", "name", "Ingunde", "isPregnant", true)).Contains("ARE pregnant"));
                Dictionary<string, object> Disclosure(string kind, string quote) => TestDict("pregnancyDisclosures", new[] {
                    TestDict("pregnancyId", "pregnancy", "kind", kind, "evidenceQuote", quote) });
                AcceptPregnancyDisclosures(c, "test", "mother", "I am pregnant.", Disclosure("pregnancy", "I am pregnant."), new[] { "player" }, "accepted_turn", 28);
                var known = ReadPregnancyKnowledge(c, "test", "pregnancy", "player");
                Check("accepted_admission_separate_from_paternity", known.Pregnancy && !known.Paternity && !known.Illicit && known.EvidenceId == "accepted_turn");
                AcceptPregnancyDisclosures(c, "test", "mother", "I am not pregnant.", Disclosure("pregnancy", "I am not pregnant."), new[] { "denial" }, "denial_source", 28);
                AcceptPregnancyDisclosures(c, "test", "mother", "Perhaps I am pregnant.", Disclosure("pregnancy", "Perhaps I am pregnant."), new[] { "guess" }, "guess_source", 28);
                AcceptPregnancyDisclosures(c, "other_branch", "mother", "I am pregnant.", Disclosure("pregnancy", "I am pregnant."), new[] { "wrong_timeline" }, "wrong_source", 28);
                Check("denial_and_speculation_do_not_grant", !ReadPregnancyKnowledge(c, "test", "pregnancy", "denial").Pregnancy && !ReadPregnancyKnowledge(c, "test", "pregnancy", "guess").Pregnancy);
                AcceptPregnancyDisclosures(c, "test", "mother", "This is an illicit pregnancy.", Disclosure("illicit", "This is an illicit pregnancy."),
                    new[] { "legacy_illicit" }, "legacy_illicit_admission", 28);
                Check("legacy_confirmed_illicit_admission_needs_no_invented_marriage_history", ReadPregnancyKnowledge(c, "test", "pregnancy", "legacy_illicit").Illicit);
                AcceptPregnancyDisclosures(c, "test", "mother", "This is not an illicit pregnancy.", Disclosure("illicit", "This is not an illicit pregnancy."),
                    new[] { "illicit_denial" }, "illicit_denial_source", 28);
                Check("negated_illicit_admission_cannot_grant_knowledge", !ReadPregnancyKnowledge(c, "test", "pregnancy", "illicit_denial").Pregnancy);
                f.SpouseId = "husband";
                ExecuteSql(c, "UPDATE conception_contexts SET facts_json=$facts WHERE timeline_id='test' AND pregnancy_id='pregnancy';", TestDict("facts", Json.Serialize(f)));
                AcceptPregnancyDisclosures(c, "test", "mother", "I am unmarried.", Disclosure("illicit", "I am unmarried."), new[] { "false_marital_claim" }, "false_marriage", 28);
                Check("false_current_marriage_claim_cannot_grant_confirmed_illicit_knowledge", !ReadPregnancyKnowledge(c, "test", "pregnancy", "false_marital_claim").Illicit);
                string claim = "Husband is the father.";
                AcceptPregnancyDisclosures(c, "test", "mother", claim, Disclosure("paternity", claim), new[] { "player" }, "false_claim", 28);
                Check("false_legal_paternity_does_not_rewrite_truth", !ReadPregnancyKnowledge(c, "test", "pregnancy", "player").Paternity
                    && PregnancyFactsFrom(TryParseJsonObject(Json.Serialize(f))).BiologicalFatherId == "actual_father");
                AcceptPregnancyDisclosures(c, "test", "mother", "Actual Father says Husband is the father.",
                    Disclosure("paternity", "Actual Father says Husband is the father."), new[] { "incidental_name" }, "incidental_name_source", 28);
                Check("incidental_father_name_is_not_a_paternity_admission", !ReadPregnancyKnowledge(c, "test", "pregnancy", "incidental_name").Paternity);
                claim = "Actual Father is the father.";
                AcceptPregnancyDisclosures(c, "test", "mother", claim, Disclosure("paternity", claim), new[] { "player" }, "true_admission", 28);
                known = ReadPregnancyKnowledge(c, "test", "pregnancy", "player");
                Check("named_true_paternity_has_source_bound_knowledge", known.Paternity && known.Illicit && known.EvidenceId == "true_admission");
                AcceptPregnancyDisclosures(c, "test", "mother", "Nothing to tell.", Disclosure("paternity", claim), new[] { "draft_only" }, "draft", 28);
                Check("undelivered_quote_and_other_timeline_excluded", !ReadPregnancyKnowledge(c, "test", "pregnancy", "draft_only").Pregnancy
                    && !ReadPregnancyKnowledge(c, "other_branch", "pregnancy", "player").Pregnancy);
                AcceptPregnancyDisclosures(c, "test", "mother", claim, Disclosure("paternity", claim), new[] { "player" }, "true_admission", 28);
                Check("disclosure_retry_idempotent", QuerySql(c, "SELECT * FROM conception_knowledge WHERE timeline_id='test' AND observer_id='player';").Count == 1);
                Check("prompt_keeps_private_truth_and_deliberate_deception", PregnancyContextPolicy.SelfPrompt(f).Contains("Biological and legal father differ")
                    && PregnancyContextPolicy.SelfPrompt(f).Contains("claim never changes true parentage"));
                Check("save_sync_covers_new_tables", SaveSyncSubsystemForTable("conception_contexts") == "relationships" && SaveSyncSubsystemForTable("conception_knowledge") == "relationships");
                EnsureWorldHistorySchema(c);
                ExecuteSql(c, @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,created_utc)
VALUES('stale_conception',$campaign,'stale',1,28,'child_conceived','2026-10-10T00:00:00Z');
INSERT INTO world_history_knowledge_rules(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode)
VALUES('stale_conception_news','stale_conception','kingdom','realm',28,'realm_news');", TestDict("campaign", campaign));
                var staleSync = FamilyPregnancySyncApi(TestDict("campaignId", campaign, "timelineId", "stale", "worldDay", 28d,
                    "pregnancies", Array.Empty<Dictionary<string, object>>()));
                Check("stale_sync_cannot_repair_another_timeline", !ReadBool(staleSync, "ok", true)
                    && QuerySql(c, "SELECT rule_id FROM world_history_knowledge_rules WHERE rule_id='stale_conception_news';").Count == 1);
                var staleProfile = TestDict("heroStringId", "mother", "isPregnant", false);
                Check("delayed_letter_uses_current_canonical_state", BuildCurrentPregnancyPrompt(campaign, "test", "mother", 28, staleProfile).Contains("Actual Father"));
                Check("remote_observer_does_not_see_private_father", !BuildCurrentPregnancyPrompt(campaign, "test", "denial", 28, staleProfile).Contains("Actual Father"));
                GrantPregnancyKnowledge(c, "test", f, "future_letter_reader", true, true, "future_letter_discovery", "future_letter_hash", 29d);
                Check("dated_letter_excludes_later_paternity_discovery", !BuildCurrentPregnancyPrompt(campaign, "test", "future_letter_reader", 28, TestDict()).Contains("Actual Father"));
                ExecuteSql(c, "UPDATE conception_contexts SET world_day=40 WHERE timeline_id='test' AND pregnancy_id='pregnancy';");
                Check("dated_letter_excludes_future_canonical_snapshot", !BuildCurrentPregnancyPrompt(campaign, "test", "mother", 28, staleProfile).Contains("ARE pregnant"));
                ExecuteSql(c, "UPDATE conception_contexts SET world_day=28 WHERE timeline_id='test' AND pregnancy_id='pregnancy';");
                EnsureBanterSchema(c);
                var banterHeroes = new List<Dictionary<string, object>> { TestDict("heroStringId", "mother", "name", "Ingunde", "age", 30,
                    "isAlive", true, "isLord", true, "currentPartyId", "main_party"), TestDict("heroStringId", "banter_listener", "name", "Listener", "age", 30,
                    "isAlive", true, "isLord", true, "currentPartyId", "main_party") };
                var banterDraft = ValidateBanterDraft(Json.Serialize(TestDict("speakerId", "mother", "text", "I am pregnant.",
                    "pregnancyDisclosures", ReadDictionaryList(Disclosure("pregnancy", "I am pregnant."), "pregnancyDisclosures"))),
                    new List<string> { "mother", "banter_listener" }, TestDict("maxCharacters", 360), new());
                string generation = BanterGeneration(c), session = "pregnancy_banter";
                WriteBanter(c, TestDict("sessionId", session, "timelineId", "test", "generation", generation, "state", "ready", "revision", 0,
                    "accepted", 0, "playerHeroStringId", "player", "playerName", "Player", "heroIds", new[] { "mother", "banter_listener" },
                    "heroes", banterHeroes, "drafts", new[] { banterDraft }, "options", TestDict("maxCharacters", 360)));
                Check("banter_draft_disclosure_grants_nothing", !ReadPregnancyKnowledge(c, "test", "pregnancy", "banter_listener").Pregnancy);
                var acceptedBanter = PartyBanterApi("accept", TestDict("campaignId", campaign, "timelineId", "test", "worldDay", 28d,
                    "generation", generation, "sessionId", session, "expectedRevision", 0, "sequence", 1, "partyId", "main_party", "heroes", banterHeroes));
                Check("accepted_banter_grants_listener_knowledge_without_gameplay_effects", ReadInt(acceptedBanter, "accepted", 0) == 1
                    && ReadPregnancyKnowledge(c, "test", "pregnancy", "banter_listener").Pregnancy, acceptedBanter);
                f.MotherExposed = true;
                ApplyConfirmedPregnancyExposure(c, campaign, "test", f, 28);
                var reputations = QuerySql(c, "SELECT subject_id,reputation_value FROM character_reputations WHERE timeline_id='test' AND tag_id='the_unchaste' AND status='active';");
                Check("mother_only_immediate_penalty", reputations.Count == 1 && ReadString(reputations[0], "subject_id", "") == "mother" && ReadInt(reputations[0], "reputation_value", 0) == -50, reputations);
                int activations = QuerySql(c, "SELECT * FROM reputation_lifecycle_events WHERE timeline_id='test';").Count;
                ApplyConfirmedPregnancyExposure(c, campaign, "test", f, 28);
                Check("exposure_retry_has_no_duplicate_activation", QuerySql(c, "SELECT * FROM reputation_lifecycle_events WHERE timeline_id='test';").Count == activations);
                f.FatherExposed = true;
                ApplyConfirmedPregnancyExposure(c, campaign, "test", f, 28);
                reputations = QuerySql(c, "SELECT subject_id,reputation_value FROM character_reputations WHERE timeline_id='test' AND tag_id='the_unchaste' AND status='active';");
                Check("later_named_father_only_adds_minus_five", reputations.Count == 2 && reputations.Any(row => ReadString(row, "subject_id", "") == "actual_father" && ReadInt(row, "reputation_value", 0) == -5)
                    && reputations.All(row => ReadString(row, "subject_id", "") != "husband"), reputations);
                var rules = BuildWorldHistoryKnowledgeRules("child_conceived", 28, "ordinary", new List<Dictionary<string, object>> {
                    TestDict("entityId", "mother", "entityType", "hero", "role", "pregnant mother participant", "kingdomId", "realm") }, null);
                Check("conception_has_no_automatic_realm_news", rules.All(rule => ReadString(rule, "audienceType", "") == "entity")
                    && rules.Any(rule => ReadString(rule, "audienceId", "") == "mother"));
                foreach (string mode in new[] { "in_person", "event", "correspondence" })
                {
                    var profile = PromptParityProfile(); profile["heroStringId"] = "mother";
                    profile["isPregnant"] = true; profile["pregnancyContexts"] = new[] { TryParseJsonObject(Json.Serialize(f)) };
                    var empty = TestDict(); var lines = new List<Dictionary<string, object>>();
                    var payload = PromptParityPayload(mode, false); payload["timelineId"] = "test";
                    payload["playerHeroStringId"] = "player"; payload["worldDay"] = 28d; payload["mode"] = mode;
                    var motive = TestDict("sanitizedState", empty, "prompt", "", "relationshipNpcToTarget", empty, "relationshipNpcToSpouse", empty);
                    PromptEnvelope envelope = mode == "correspondence"
                        ? BuildCorrespondencePromptEnvelope(campaign, "mother", "Ingunde", "player", "Player", 28d, "How are you?", profile, PromptParityCharacteristics(), empty, "", payload)
                        : mode == "in_person"
                            ? BuildDialoguePromptEnvelope(campaign, "mother", "Ingunde", "Player", "Player", "How are you?", "At court.", profile, PromptParityCharacteristics(), empty, empty, empty, lines, lines, lines, lines, PromptParityIdentity(), payload, "", "")
                            : BuildEventPromptEnvelope(campaign, "fixture_event", "mother", "Ingunde", "Player", "Player", "How are you?", "At court.", payload, profile, PromptParityCharacteristics(), empty, empty, empty, lines, lines, lines, lines, PromptParityIdentity(), "", "", motive);
                    string final = Json.Serialize(envelope.Messages);
                    Check("final_envelope_" + mode, final.Contains("ARE pregnant") && final.Contains("Actual Father") && final.Contains("pregnancyDisclosures"));
                }
                // Exercise the actual PostgreSQL snapshot route, not a fabricated timeline prefix.
                GrantPregnancyKnowledge(c, "test", f, "fractional_observer", false, false, "fractional", "fractional_hash", 28.123456789d);
                c.Dispose();
                var point = TestDict("savePointId", "pregnancy_before", "timelineId", "test", "campaignTimeDays", 28.123456789d,
                    "worldHistorySequence", 8L, "worldHistoryHeadEventId", "pregnancy_head");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "pregnancy_before");
                using (var future = OpenCampaignConnection(campaign))
                    GrantPregnancyKnowledge(future, "test", f, "future_observer", true, true, "future", "future_hash", 29d);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "pregnancy_before");
                AdvanceMemoryRestoreGeneration(campaign, point);
                using (var restored = OpenCampaignConnection(campaign))
                {
                    BindRecoveredPregnancyKnowledge(restored, "test", "sibling", 8, "pregnancy_head");
                    Check("unverified_sibling_has_no_knowledge", !ReadPregnancyKnowledge(restored, "sibling", "pregnancy", "player").Pregnancy);
                    BindRecoveredLetterSources(restored, "test", "recovered", 8, "pregnancy_head");
                    BindRecoveredPregnancyKnowledge(restored, "test", "recovered", 8, "pregnancy_head");
                    Check("actual_snapshot_recovery_retains_known_paternity", ReadPregnancyKnowledge(restored, "recovered", "pregnancy", "player").Paternity);
                    Check("snapshot_retains_fractional_day_discovery", ReadPregnancyKnowledge(restored, "recovered", "pregnancy", "fractional_observer").Pregnancy);
                    Check("snapshot_excludes_future_discoveries", !ReadPregnancyKnowledge(restored, "recovered", "pregnancy", "future_observer").Pregnancy);
                }
            }
            catch (Exception ex) { Check("fixture", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
