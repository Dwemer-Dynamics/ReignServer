using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> GossipTestTraits(params object[] overrides)
        {
            var values = GossipFields("sociability", 50, "impulsiveness", 50, "pride", 50, "curiosity", 50,
                "honesty", 50, "vengefulness", 50, "compassion", 50, "loyalty", 50, "tact", 50,
                "discipline", 50, "empathy", 50, "shame", 50, "familyMotivation", 50, "socialTrust", 50);
            foreach (var pair in GossipFields(overrides)) values[pair.Key] = pair.Value;
            return GossipFields("traitPercentages", values);
        }
        private static Dictionary<string, object> GossipTestHero(string id, string town = "town_a", string party = "") =>
            GossipFields("heroStringId", id, "name", id, "settlementId", town, "partyId", party, "kingdomId", "empire",
                "isAlive", true, "isPrisoner", false, "available", true, "positionKnown", true, "positionX", 0d, "positionY", 0d);
        private static void GossipTestPair(ReignDbConnection c, string a, string b, int aToB, int bToA)
        {
            ExecuteSql(c, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($key,$a,$b,$ab,$ba,1,1,1) ON CONFLICT(pair_key) DO UPDATE SET affinity_a_to_b=excluded.affinity_a_to_b,affinity_b_to_a=excluded.affinity_b_to_a;",
                GossipFields("key", AmbientPairKey(a, b), "a", a, "b", b, "ab", aToB, "ba", bToA));
        }
        private static string GossipTestSeed(string prefix, Func<string, bool> predicate)
        {
            for (int i = 0; i < 100000; i++) if (predicate(prefix + i)) return prefix + i;
            throw new InvalidOperationException("No deterministic gossip fixture seed found.");
        }

        private static List<Dictionary<string, object>> RunPersonalGossipSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> check = (id, passed, detail) => results.Add(GossipFields("ok", true, "passed", passed,
                "suite", "personal_gossip", "caseId", id, "name", id, "summary", passed || detail == null ? id : id + ": " + LimitText(Json.Serialize(detail), 6000), "detail", detail, "durationMs", 0));
            var policy = DefaultPersonalGossipSettings();
            var leaseNow = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
            (Dictionary<string, object> Run, Dictionary<string, object> Runtime) ConversationLeaseFixture()
            {
                var enrollment = GossipFields("source", "reign_mcp_personal_gossip", "campaignId", "lease_campaign",
                    "campaignTestRunId", "owned", "timelineId", "timeline", "disposableSaveName", "Current",
                    "protectedBaselineSaveName", "test", "recoveryCheckpointVerified", true);
                var command = GossipFields("operation", "personal_gossip_test", "status", "accepted", "leaseId", "token",
                    "leaseStartedUtc", leaseNow.AddSeconds(-120).ToString("o"), "leaseExpiresUtc", leaseNow.AddSeconds(10).ToString("o"),
                    "timeoutSeconds", 1800, "testCase", GossipFields("phase", "conversation"), "enrollment", enrollment);
                return (GossipFields("mode", "personal_gossip", "status", "running", "runId", "run", "campaignId", "lease_campaign",
                        "gameInstanceId", "instance", "enrollment", enrollment, "commands", new[] { command }),
                    GossipFields("busy", true, "activeRunId", "run", "activeMode", "party_chat", "campaignId", "lease_campaign",
                        "gameInstanceId", "instance", "receivedUtc", leaseNow.ToString("o"), "activeSaveName", "Current",
                        "saveSync", GossipFields("ready", true, "alignmentPending", false, "timelineId", "timeline"),
                        "nativeSave", GossipFields("isSaving", false)));
            }
            bool LeaseRejected(Action<Dictionary<string, object>, Dictionary<string, object>> change, bool armed = true)
            {
                var f = ConversationLeaseFixture(); change(f.Run, f.Runtime); string before = CanonicalJson(f.Run);
                return !TryRenewPersonalGossipConversationLease(f.Run, f.Runtime, leaseNow, armed) && CanonicalJson(f.Run) == before;
            }
            var liveLease = ConversationLeaseFixture();
            check("conversation_lease_live_heartbeat_renews_exact_command", TryRenewPersonalGossipConversationLease(liveLease.Run, liveLease.Runtime, leaseNow, true)
                && ParseLiveTestUtc(ReadString(FirstLiveTestCommandInSequence(liveLease.Run), "leaseExpiresUtc", "")) == leaseNow.AddSeconds(45), null);
            check("conversation_lease_requires_arm_and_exact_native_owner", LeaseRejected((r, t) => { }, false)
                && LeaseRejected((r, t) => t["gameInstanceId"] = "other") && LeaseRejected((r, t) => t["activeRunId"] = "other")
                && LeaseRejected((r, t) => t["campaignId"] = "other") && LeaseRejected((r, t) => t["busy"] = false)
                && LeaseRejected((r, t) => t["receivedUtc"] = leaseNow.AddSeconds(-91).ToString("o")), null);
            check("conversation_lease_requires_current_and_aligned_recovery", LeaseRejected((r, t) => t["activeSaveName"] = "test")
                && LeaseRejected((r, t) => ReadDictionary(t, "saveSync")["ready"] = false)
                && LeaseRejected((r, t) => ReadDictionary(t, "saveSync")["alignmentPending"] = true)
                && LeaseRejected((r, t) => ReadDictionary(t, "saveSync")["timelineId"] = "other")
                && LeaseRejected((r, t) => ReadDictionary(t, "nativeSave")["isSaving"] = true)
                && LeaseRejected((r, t) => ReadDictionary(r, "enrollment")["recoveryCheckpointVerified"] = false), null);
            check("conversation_lease_never_revives_expiry_or_other_work", LeaseRejected((r, t) => FirstLiveTestCommandInSequence(r)["leaseExpiresUtc"] = leaseNow.ToString("o"))
                && LeaseRejected((r, t) => r["status"] = "cancelled") && LeaseRejected((r, t) => r["mode"] = "individual_chat")
                && LeaseRejected((r, t) => ReadDictionary(FirstLiveTestCommandInSequence(r), "testCase")["phase"] = "prepare")
                && LeaseRejected((r, t) => FirstLiveTestCommandInSequence(r).Remove("leaseStartedUtc"))
                && LeaseRejected((r, t) => FirstLiveTestCommandInSequence(r)["leaseId"] = ""), null);
            var deadlineLease = ConversationLeaseFixture();
            FirstLiveTestCommandInSequence(deadlineLease.Run)["timeoutSeconds"] = 140;
            check("conversation_lease_original_deadline_caps_renewal", TryRenewPersonalGossipConversationLease(deadlineLease.Run, deadlineLease.Runtime, leaseNow, true)
                && ParseLiveTestUtc(ReadString(FirstLiveTestCommandInSequence(deadlineLease.Run), "leaseExpiresUtc", "")) == leaseNow.AddSeconds(20)
                && LeaseRejected((r, t) => FirstLiveTestCommandInSequence(r)["timeoutSeconds"] = 120), null);
            check("conversation_lease_avoids_unnecessary_writes", LeaseRejected((r, t) => FirstLiveTestCommandInSequence(r)["leaseExpiresUtc"] = leaseNow.AddSeconds(30).ToString("o")), null);
            var probeLease = ConversationLeaseFixture();
            ReadDictionary(FirstLiveTestCommandInSequence(probeLease.Run), "testCase")["phase"] = "composition_probe";
            probeLease.Runtime["activeMode"] = "personal_gossip";
            ReadDictionary(probeLease.Run, "enrollment")["allowProviderCalls"] = true;
            check("composition_probe_lease_requires_exact_owned_mode", TryRenewPersonalGossipConversationLease(probeLease.Run, probeLease.Runtime, leaseNow, true)
                && LeaseRejected((r, t) => ReadDictionary(FirstLiveTestCommandInSequence(r), "testCase")["phase"] = "composition_probe"), null);
            bool AcceptsGossipCase(Dictionary<string, object> value)
            {
                try { ValidatePersonalGossipTestCase(value); return true; }
                catch (ArgumentException) { return false; }
            }
            var groupCase = GossipFields("caseId", "group", "phase", "conversation", "actorHeroId", "source",
                "conversationMode", "party_chat", "participantHeroIds", new[] { "source", "target" }, "playerTurns", new[] { "Hello." });
            check("guarded_group_case_accepts_exact_participants", AcceptsGossipCase(groupCase), null);
            check("guarded_group_case_rejects_duplicate_or_missing_actor", !AcceptsGossipCase(new Dictionary<string, object>(groupCase) {
                    ["participantHeroIds"] = new[] { "source", "source" } })
                && !AcceptsGossipCase(new Dictionary<string, object>(groupCase) { ["participantHeroIds"] = new[] { "target", "other" } }), null);
            check("guarded_group_case_rejects_single_individual_and_oversized_group", !AcceptsGossipCase(new Dictionary<string, object>(groupCase) {
                    ["participantHeroIds"] = new[] { "source" } })
                && !AcceptsGossipCase(new Dictionary<string, object>(groupCase) { ["conversationMode"] = "individual_chat" })
                && !AcceptsGossipCase(new Dictionary<string, object>(groupCase) { ["participantHeroIds"] = Enumerable.Range(0, 9).Select(i => i == 0 ? "source" : "other" + i).ToArray() }), null);
            var missingGroup = new Dictionary<string, object>(groupCase); missingGroup.Remove("participantHeroIds");
            check("guarded_party_case_requires_explicit_group", !AcceptsGossipCase(missingGroup), null);
            var activationCase = GossipFields("caseId", "activate", "phase", "prepare", "actorHeroId", "source",
                "activateResidents", new[] { "source" }, "placements", new[] { GossipFields("heroId", "source", "settlementId", "town_a") });
            check("guarded_activation_requires_single_explicit_destination", AcceptsGossipCase(activationCase)
                && !AcceptsGossipCase(new Dictionary<string, object>(activationCase) { ["placements"] = Array.Empty<object>() })
                && !AcceptsGossipCase(new Dictionary<string, object>(activationCase) { ["placements"] = new[] {
                    GossipFields("heroId", "source", "settlementId", "town_a"), GossipFields("heroId", "source", "party", "main") } }), null);
            check("guarded_activation_cannot_expand_a_conversation_or_duplicate_identity", !AcceptsGossipCase(new Dictionary<string, object>(groupCase) {
                    ["activateResidents"] = new[] { "source" } })
                && !AcceptsGossipCase(new Dictionary<string, object>(activationCase) { ["activateResidents"] = new[] { "source", "source" } }), null);
            check("known_gossip_speaker_uses_native_male_fact", GossipIdentityHint(GossipFields("isFemale", false), "Known contact") == "Known contact (man; he/him)", null);
            check("known_gossip_speaker_uses_native_female_fact", GossipIdentityHint(GossipFields("isFemale", true), "Known contact") == "Known contact (woman; she/her)", null);
            check("missing_gossip_identity_does_not_guess_gender", GossipIdentityHint(GossipFields(), "Known contact") == "Known contact"
                && GossipIdentityHint(null, "Known contact") == "Known contact", null);
            check("malformed_gossip_identity_does_not_guess_gender", GossipIdentityHint(GossipFields("isFemale", "false"), "Known contact") == "Known contact", null);
            check("gossip_identity_hint_preserves_communicated_label", !GossipIdentityHint(GossipFields("isFemale", false, "name", "HiddenCanonicalName"), "The contact").Contains("HiddenCanonicalName"), null);
            foreach (string stage in PersonalGossipChanceLabels.Keys)
            {
                string key = GossipAdjustmentKey(stage);
                check("options_" + stage + "_bounds", new object[] { -100, 0, 100 }.All(v => ValidatePersonalGossipOptions(GossipFields(key, v)) == "")
                    && new object[] { -101, 101, .5, "50", true, null, double.NaN, double.PositiveInfinity }.All(v => ValidatePersonalGossipOptions(GossipFields(key, v)).Length > 0), null);
                check("options_" + stage + "_chance_endpoints", Enumerable.Range(0, 101).All(basis =>
                    GossipAdjustedChance(basis, stage, PersonalGossipSettings(GossipFields(key, -100))) == 0
                    && GossipAdjustedChance(basis, stage, PersonalGossipSettings(GossipFields(key, 100))) == 100
                    && GossipAdjustedChance(basis, stage, policy) == basis), null);
            }
            check("options_invalid_master_and_unknown_rejected", ValidatePersonalGossipOptions(GossipFields("personalGossipEnabled", "false")).Length > 0
                && ValidatePersonalGossipOptions(GossipFields("personalGossipInvented", 20)).Length > 0, null);
            var optionSettings = DefaultSettings(); optionSettings["unrelated_gossip_fixture"] = "retain";
            MergeProviderSettings(optionSettings, GossipFields("personalGossipShareAdjustment", 100));
            MergeProviderSettings(optionSettings, GossipFields("personalGossipDisclosureAdjustment", -100, "personalGossipEnabled", false));
            var optionRestored = TryParseJsonObject(Json.Serialize(optionSettings));
            var frozenOptions = PersonalGossipSettings(optionRestored);
            MergeProviderSettings(optionRestored, GossipFields("personalGossipShareAdjustment", -100));
            check("options_partial_merge_persist_and_freeze", !ReadBool(frozenOptions, "enabled", true)
                && ReadInt(ReadDictionary(frozenOptions, "chanceAdjustments"), "share", 0) == 100
                && ReadInt(ReadDictionary(frozenOptions, "chanceAdjustments"), "disclosure", 0) == -100
                && GossipAdjustedChance(40, "share", PersonalGossipSettings(optionRestored)) == 0
                && ReadString(optionRestored, "unrelated_gossip_fixture", "") == "retain", null);
            check("options_ui_default_and_registration", PersonalGossipChanceLabels.Keys.All(stage => ReadInt(SettingsForUi(DefaultSettings()), GossipAdjustmentKey(stage), 999) == 0)
                && ReadBool(SettingsForUi(DefaultSettings()), "personalGossipEnabled", false)
                && ReignXpOptionsHtml().Contains("id='personalGossipOptions'") && ReignXpOptionsScript().Contains("loadPersonalGossipOptions(settings)"), null);
            WriteJsonObject(System.IO.Path.Combine(VerificationDir, "personal-gossip-options-fixture.json"),
                GossipFields("markup", PersonalGossipOptionsHtml(), "script", PersonalGossipOptionsScript(),
                    "defaults", PersonalGossipChanceLabels.Keys.ToDictionary(GossipAdjustmentKey, _ => (object)0)));
            var delayed = GossipFields("operatorTestControls", GossipFields("compositionMode", "delay_past_watch_once"), "operatorAttempt", 1);
            check("fixed_post_watch_delay_is_bounded_and_first_attempt_only", PersonalGossipCompositionDelaySeconds(delayed) == 135
                && PersonalGossipCompositionDelaySeconds(GossipFields("operatorTestControls", GossipFields("compositionMode", "delay_once"), "operatorAttempt", 1)) == 15
                && PersonalGossipCompositionDelaySeconds(GossipFields("operatorTestControls", GossipFields("compositionMode", "delay_past_watch_once"), "operatorAttempt", 2)) == 0
                && PersonalGossipCompositionDelaySeconds(GossipFields()) == 0, null);
            check("post_watch_fault_does_not_delay_the_return_reply", PersonalGossipCompositionDelaySeconds(
                GossipFields("operatorTestControls", GossipFields("compositionMode", "delay_past_watch_once"), "operatorAttempt", 1, "reply", true)) == 0, null);
            check("post_watch_fault_does_not_delay_retelling", PersonalGossipCompositionDelaySeconds(
                GossipFields("operatorTestControls", GossipFields("compositionMode", "delay_past_watch_once"), "operatorAttempt", 1, "hop", 2)) == 0, null);
            Func<string, double, bool, bool, bool, GossipCandidate> candidate = (id, relation, family, named, local) =>
                new GossipCandidate { Id = id, Affinity = relation, Family = family, Mentioned = named, Local = local, Hurt = 75, Person = GossipTestHero(id) };
            Func<double, bool, string, bool> eligible = (r, family, type) =>
                GossipRecipients(new[] { candidate("npc", r, family, false, true) }, type, 50, policy).Count == 1;
            check("friendship_boundary", !eligible(29, false, "positive") && eligible(30, false, "positive"), null);
            check("family_boundary", !eligible(-1, true, "positive") && eligible(0, true, "positive"), null);
            check("dislike_boundary", !eligible(-9, false, "negative") && eligible(-10, false, "negative"), null);
            check("negative_impression_friendly_audience", eligible(30, false, "impression") && !eligible(-10, false, "impression"), null);
            var namedNeutral = candidate("named", 0, false, true, false);
            check("name_never_bypasses_relationship", GossipRecipients(new[] { namedNeutral }, "positive", 50, policy).Count == 0, namedNeutral.Audit());
            check("distant_unnamed_best_friend_excluded", GossipRecipients(new[] { candidate("friend", 100, false, false, false) }, "positive", 50, policy).Count == 0, null);
            check("named_eligible_remote_contact", GossipRecipients(new[] { candidate("friend", 30, false, true, false) }, "positive", 50, policy).Count == 1, null);
            var mildEnemy = candidate("enemy", -80, false, true, true); mildEnemy.Hurt = 25;
            check("malice_requires_motive", GossipRecipients(new[] { mildEnemy }, "negative", 50, policy).Count == 0, null);
            var names = GossipRecipients(new[] { candidate("named", 30, false, true, false), candidate("local1", 40, false, false, true), candidate("local2", 40, false, false, true) }, "positive", 50, policy);
            double sum = names.Sum(x => x.Weight);
            check("named_80_10_10", Math.Abs(names.Single(x => x.Id == "named").Weight / sum - .8) < 1e-12
                && names.Where(x => !x.Mentioned).All(x => Math.Abs(x.Weight / sum - .1) < 1e-12), names.Select(x => x.Audit()).ToList());
            var multiple = GossipRecipients(new[] { candidate("named1", 40, false, true, false), candidate("named2", 40, false, true, false), candidate("local", 40, false, false, true) }, "positive", 50, policy);
            check("two_names_44_44_11", multiple.Where(x => x.Mentioned).All(x => Math.Abs(x.Weight / multiple.Sum(y => y.Weight) - 4d / 9) < 1e-12), null);
            var rivals = GossipRecipients(new[] { candidate("named", -20, false, true, false), candidate("rival", -60, false, false, true) }, "negative", 50, policy);
            check("named_enemy_over_stronger_local_rival", Math.Abs(rivals.Single(x => x.Mentioned).Weight / rivals.Sum(x => x.Weight) - .8) < 1e-12, null);
            var regular = GossipRecipients(new[] { candidate("strong", 70, false, false, true), candidate("friend", 30, false, false, true) }, "positive", 50, policy);
            check("ordinary_weighted_2_to_1", Math.Abs(regular.Single(x => x.Id == "strong").Weight / regular.Sum(x => x.Weight) - 2d / 3) < 1e-12, null);
            var capped = GossipRecipients(Enumerable.Range(0, 20).Select(i => candidate("local" + i, 100, false, false, true))
                .Concat(new[] { candidate("named_low", 30, false, true, false) }), "positive", 50, policy);
            check("candidate_cap_keeps_named", capped.Count == 8 && capped.Any(x => x.Id == "named_low"), null);
            var unequalNames = GossipRecipients(new[] { candidate("big", 90, false, true, false), candidate("small", 30, false, true, false), candidate("local", 60, false, false, true) }, "positive", 50, policy);
            check("each_named_outweighs_ordinary_group", unequalNames.Where(x => x.Mentioned).All(x => x.Weight >= 4 * unequalNames.Where(y => !y.Mentioned).Sum(y => y.Weight)), null);
            var sole = GossipRecipients(new[] { candidate("only", 30, false, false, true) }, "positive", 50, policy);
            check("sole_candidate_selection_not_sharing_guarantee", Enumerable.Range(0, 100).All(i => GossipChooseRecipient(sole, i / 100d).Id == "only")
                && Enumerable.Range(0, 100).Any(i => GossipRoll("sole" + i, "share") > 85), null);
            check("same_settlement", GossipLocal(GossipTestHero("a"), GossipTestHero("b")), null);
            check("same_party", GossipLocal(GossipTestHero("a", "", "party"), GossipTestHero("b", "", "party")), null);
            var nowhere = GossipTestHero("a", ""); nowhere["armyId"] = "army";
            var elsewhere = GossipTestHero("b", ""); elsewhere["armyId"] = "army";
            check("empty_locations_and_shared_army_do_not_qualify", !GossipLocal(nowhere, elsewhere), null);
            var child = GossipTestHero("Ira"); var mother = GossipTestHero("Rhagaea"); child["motherId"] = "Rhagaea";
            var player = GossipTestHero("Traveler");
            check("kin_reference_resolves_without_omniscient_mentions", GossipMentionedIds("Did your mother speak to me?", player, child, new[] { child, mother, player }).SequenceEqual(new[] { "Rhagaea" })
                && GossipMentionedIds("I enjoyed that discussion.", child, player, new[] { mother }).Count == 0, null);
            var duplicateName = GossipTestHero("other_rhagaea"); duplicateName["name"] = "Rhagaea";
            check("ambiguous_name_cannot_unlock_travel", GossipMentionedIds("Rhagaea told me about it.", child, player, new[] { mother, duplicateName }).Count == 0, null);
            check("kin_reference_stays_unambiguous", GossipMentionedIds("My mother told me about it.", child, player, new[] { mother, duplicateName }).SequenceEqual(new[] { "Rhagaea" }), null);
            var otherIra = GossipTestHero("other_ira"); otherIra["name"] = "Ira";
            var anotherIra = GossipTestHero("another_ira"); anotherIra["name"] = "Ira"; anotherIra["motherId"] = "someone_else";
            var threeIras = new[] { child, otherIra, anotherIra, mother, player };
            check("native_parent_disambiguates_named_daughter", GossipMentionedIds("Please write to your daughter Ira after we finish.", player, mother, threeIras).SequenceEqual(new[] { "Ira" })
                && GossipMentionedIds("I will tell my daughter Ira about the school.", mother, player, threeIras).SequenceEqual(new[] { "Ira" }), null);
            check("bare_duplicate_child_name_still_requires_disambiguation", GossipMentionedIds("Please tell Ira about the school.", player, mother, threeIras).Count == 0, null);
            check("only_native_child_can_be_resolved_without_name", GossipMentionedIds("I will tell my daughter about it.", mother, player, threeIras).SequenceEqual(new[] { "Ira" }), null);
            var nativeChildSibling = GossipTestHero("native_child_sibling"); nativeChildSibling["motherId"] = "Rhagaea";
            check("multiple_native_children_do_not_grant_unnamed_identity", GossipMentionedIds("I will tell my child about it.", mother, player,
                threeIras.Concat(new[] { nativeChildSibling })).Count == 0, null);
            var vintner = GossipTestHero("CharacterObject_2623"); vintner["name"] = "Eupitor the Vintner";
            var otherEupitor = GossipTestHero("CharacterObject_4911"); otherEupitor["name"] = "Eupitor";
            var eupitors = new[] { vintner, otherEupitor, child, player };
            check("narrated_speaker_short_name_cannot_select_other_person", GossipMentionedIds(
                "Eupitor is quiet for a moment. He sets his hat down before he answers.", vintner, player, eupitors).Count == 0, null);
            check("complete_titled_name_does_not_mention_embedded_other_name", GossipMentionedIds(
                "Eupitor the Vintner told me about the harvest.", child, player, eupitors).SequenceEqual(new[] { "CharacterObject_2623" }), null);
            check("short_name_collision_is_ambiguous_outside_current_participants", GossipMentionedIds(
                "Did Eupitor speak to you?", child, player, eupitors).Count == 0, null);
            check("participant_alias_blocks_an_incomplete_operator_roster", GossipMentionedIds(
                "Eupitor turns his ledger face-down.", vintner, player, new[] { otherEupitor }).Count == 0, null);
            var ofTown = GossipTestHero("town_namesake"); ofTown["name"] = "Rhagaea of Zeonica";
            check("longest_explicit_name_and_kin_resolution_remain_distinct", GossipMentionedIds(
                "Rhagaea of Zeonica told me about it.", child, player, new[] { mother, ofTown }).SequenceEqual(new[] { "town_namesake" })
                && GossipMentionedIds("My mother told me about it.", child, player, new[] { mother, ofTown }).SequenceEqual(new[] { "Rhagaea" }), null);
            var sibling = GossipTestHero("Sibling"); sibling["motherId"] = "Rhagaea";
            var cousin = GossipTestHero("Cousin"); cousin["motherId"] = "aunt";
            check("immediate_family_only", GossipFamily(child, mother) && GossipFamily(child, sibling) && !GossipFamily(child, cousin), null);
            check("local_one_hour", Math.Abs(GossipTransitDays(child, mother, policy, true) - 1d / 24) < 1e-12, null);
            mother["positionX"] = 80d;
            check("same_kingdom_distance", GossipTransitDays(child, mother, policy, false) == 2, null);
            mother["kingdomId"] = "foreign"; mother["positionX"] = 55d;
            check("foreign_distance", GossipTransitDays(child, mother, policy, false) == 4, null);
            mother["positionKnown"] = false;
            check("unknown_route_slower", GossipTransitDays(child, mother, policy, false) == 5, null);
            var ordinary = GossipTestTraits();
            var excitable = GossipTestTraits("sociability", 85, "impulsiveness", 80, "pride", 65, "curiosity", 70,
                "honesty", 65, "vengefulness", 30, "compassion", 65, "tact", 25, "discipline", 30, "empathy", 50, "shame", 25);
            var spiteful = GossipTestTraits("sociability", 75, "impulsiveness", 75, "pride", 85, "curiosity", 65,
                "honesty", 15, "vengefulness", 90, "compassion", 15, "loyalty", 25, "tact", 40, "discipline", 45, "empathy", 20, "shame", 20);
            check("documented_share_examples", GossipShareChance(75, ordinary) == 44 && GossipShareChance(75, excitable) == 62 && GossipShareChance(75, spiteful) == 60, null);
            check("documented_disclosure_examples", GossipDetailChance(excitable, "personal", 75, 60, 60, false, false, 0) == 78
                && GossipDetailChance(excitable, "secret", 75, 60, 60, false, true, 0) == 8, null);
            check("documented_negative_and_distortion", GossipNegativeChance(spiteful, 75, -60, 0, false, false) == 57 && GossipDistortionChance(spiteful, 75) == 71, null);
            var honest = GossipTestTraits("honesty", 90, "vengefulness", 90, "pride", 85, "impulsiveness", 75);
            check("honest_spite_can_hurt_but_rarely_distorts", GossipDistortionChance(honest, 75) == 8, null);
            check("categorical_distortion_boundaries", GossipDistortionSeverity(39, 80) == "loaded_exaggeration" && GossipDistortionSeverity(39, 81) == "false_escalation"
                && GossipDistortionSeverity(69, 90) == "false_escalation" && GossipDistortionSeverity(69, 91) == "invented_detail"
                && GossipDistortionSeverity(70, 66) == "invented_detail", null);
            check("no_minimum_secret_leak", GossipDetailChance(GossipTestTraits("tact", 100, "discipline", 100, "empathy", 100, "loyalty", 100, "honesty", 100, "impulsiveness", 0),
                "secret", 75, 60, 100, false, true, 0) == 0, null);
            check("promise_cannot_be_relabelled_public", GossipDetailChance(excitable, "public", 75, 60, 60, false, true, 0) == 8, null);
            check("trait_monotonicity", Enumerable.Range(0, 100).All(i => GossipShareChance(75, GossipTestTraits("sociability", i))
                    <= GossipShareChance(75, GossipTestTraits("sociability", i + 1)))
                && Enumerable.Range(0, 100).All(i => GossipDistortionChance(GossipTestTraits("honesty", i), 75)
                    >= GossipDistortionChance(GossipTestTraits("honesty", i + 1), 75)), null);
            check("credibility_and_retelling_examples", GossipCredibility(60, 60, "firsthand", 0, false, false, false) == 67
                && GossipRetellChance(62, 67) == 19 && GossipRaiseChance(70, 75, 33, 75) == 68, null);
            check("recipient_proof_and_speculation", GossipCredibility(80, 80, "firsthand", 0, false, false, false, "disproved") == 0
                && GossipCredibility(-80, 20, "secondhand", 1, false, false, false, "confirmed") == 100
                && GossipCredibility(0, 50, "speculation", 0, false, false, false) == 25, null);
            check("strong_feelings_gate", !GossipEmotionEligible(0, excitable) && !GossipEmotionEligible(25, ordinary) && GossipEmotionEligible(25, excitable), null);
            check("rounding_and_roll_endpoints", GossipPercent(.5) == 1 && GossipPercent(-1) == 0 && GossipPercent(120, 85) == 85
                && Enumerable.Range(0, 500).All(i => GossipRoll("endpoint" + i, "share") is >= 1 and <= 100), null);
            check("stable_stage_rolls", Enumerable.Range(0, 100).All(_ => GossipRoll("same_topic", "share") == GossipRoll("same_topic", "share"))
                && GossipTopicId("main", "a", "player", 20.1, "family", "mother") == GossipTopicId("main", "a", "player", 20.9, "family", "mother"), null);
            check("unapproved_fabrication_rejected", !GossipBodyAllowed("We spent the night together.", "We talked privately.", "none")
                && GossipBodyAllowed("We spent the night together.", "We talked privately.", "invented_detail"), null);
            const string entrustedPlan = "Keep this between us. I want to pay for the quarter's well anonymously.";
            var ownerSignal = GossipFields("privacy", "secret", "existencePrivacy", "secret", "promisedSecrecy", true,
                "ownCompromising", false, "evidenceQuote", entrustedPlan, "confidenceOwnerIds", new[] { "self", "unmentioned_background_person" });
            var resolvedOwners = GossipResolveConfidenceOwners(ownerSignal, "confidant_id", "current_heir_id", Array.Empty<string>(), entrustedPlan);
            check("confidence_owner_captured_self_only_adds_entrusting_player", resolvedOwners.SequenceEqual(new[] { "confidant_id", "current_heir_id" }), resolvedOwners);
            var aliasOwners = new Dictionary<string, object>(ownerSignal); aliasOwners["confidenceOwnerIds"] = new[] { " PLAYER ", " self ", "mentioned_owner" };
            check("confidence_owner_aliases_resolve_without_exposing_player_identity", GossipResolveConfidenceOwners(aliasOwners,
                "confidant_id", "current_heir_id", new[] { "mentioned_owner" }, entrustedPlan)
                .SequenceEqual(new[] { "confidant_id", "current_heir_id", "mentioned_owner" }), null);
            var npcConfidence = new Dictionary<string, object>(ownerSignal); npcConfidence["evidenceQuote"] = "This is my own private concern.";
            check("npc_only_confidence_does_not_invent_player_ownership", GossipResolveConfidenceOwners(npcConfidence,
                "confidant_id", "current_heir_id", Array.Empty<string>(), "What would you like to discuss?").SequenceEqual(new[] { "confidant_id" }), null);
            var publicSource = new Dictionary<string, object>(ownerSignal); publicSource["privacy"] = "public";
            publicSource["existencePrivacy"] = "public"; publicSource["promisedSecrecy"] = false; publicSource["confidenceOwnerIds"] = Array.Empty<string>();
            check("public_source_does_not_gain_an_automatic_confidence_owner", GossipResolveConfidenceOwners(publicSource,
                "confidant_id", "current_heir_id", Array.Empty<string>(), entrustedPlan).Count == 0, null);
            RunPersonalGossipCompositionReviewTests(check);
            try { RunPersonalGossipPersistenceTests(check, spiteful); }
            catch (Exception ex) { check("persistence_fixture", false, ex.ToString()); }
            return results;
        }

        private static void RunPersonalGossipCompositionReviewTests(Action<string, bool, object> check)
        {
            const string playerKey = "uncommunicated_player_identity_key";
            const string quote = "I want him to understand what his hiring of my carts has bought him.";
            var data = GossipFields("speakerId", "source", "playerId", playerKey, "recipientId", "rival",
                "speaker", GossipFields("name", "Chara"), "recipient", GossipFields("name", "Eupitor"),
                "playerReference", GossipFields("label", "the armed stranger"), "sourceQuoteSpeakerId", "source",
                "sourceQuote", quote, "type", "negative", "distortion", "invented_detail", "claimKind", "assertion", "hop", 1,
                "sourceEncounterTurns", new[] {
                    GossipFields("speakerId", playerKey, "text", "When Eupitor hears about our quiet conversation, what should he understand?"),
                    GossipFields("speakerId", "source", "text", quote) });
            Dictionary<string, object> Draft(string body = "Eupitor, I spent the night with the armed stranger.") =>
                GossipFields("body", body, "sourceQuote", quote, "distortionSeverity", "invented_detail", "account", "firsthand");
            Dictionary<string, object> Verdict(string body, string failed = "") => GossipFields(
                "participantsPreserved", failed != "participantsPreserved", "aboutPlayerEncounter", failed != "aboutPlayerEncounter",
                "severityRealized", failed != "severityRealized", "claimQuote", body, "reason", "Bounded isolated reviewer fixture.");
            Dictionary<string, object> Response(Dictionary<string, object> review) => GossipFields("ok", true, "content", Json.Serialize(review));
            var draft = Draft(); int calls = 0; Dictionary<string, object> captured = null;
            var approved = ReviewPersonalGossipComposition(data, draft, request => {
                calls++; captured = request; return Response(Verdict(ReadString(draft, "body", "")));
            });
            string sent = Json.Serialize(captured);
            check("malicious_review_keeps_false_night_permission", calls == 1 && PersonalGossipCompositionApproved(data, approved)
                && ReadString(data, "sourceQuote", "") == quote && !quote.Contains("night"), null);
            check("malicious_review_receives_actual_role_grounded_encounter", sent.Contains("quiet conversation")
                && sent.Contains("gossip_speaker") && sent.Contains("accepted_spoken_exchange") && !sent.Contains(playerKey), null);
            var probeSource = new Dictionary<string, object>(data) { ["id"] = "root", ["rootId"] = "root", ["timelineId"] = "timeline", ["sourceTurnIds"] = new[] { "turn_player", "turn_source" } };
            var probeRow = GossipFields("message_id", "root_out", "topic_id", "root", "root_id", "root", "timeline_id", "timeline", "speaker_id", "source", "recipient_id", "rival", "player_id", playerKey,
                "status", "generation_failed", "is_reply", 0, "hop", 1, "attempts", 3, "payload_json", Json.Serialize(probeSource));
            bool ProbeAccepts(Action<Dictionary<string, object>> change, string actor = "source", string timeline = "timeline")
            {
                var row = new Dictionary<string, object>(probeRow); change(row);
                try { PersonalGossipProbeSource(row, actor, timeline); return true; }
                catch (InvalidOperationException) { return false; }
            }
            check("composition_probe_requires_terminal_actor_timeline_original", ProbeAccepts(_ => { })
                && !ProbeAccepts(_ => { }, "other") && !ProbeAccepts(_ => { }, timeline: "other")
                && !ProbeAccepts(row => row["status"] = "in_transit") && !ProbeAccepts(row => row["is_reply"] = 1)
                && !ProbeAccepts(row => row["hop"] = 2) && !ProbeAccepts(row => row["root_id"] = ""), null);
            check("composition_probe_refuses_recipient_and_source_rebinding", !ProbeAccepts(row => row["recipient_id"] = "other")
                && !ProbeAccepts(row => row["player_id"] = "other")
                && !ProbeAccepts(row => row["payload_json"] = Json.Serialize(new Dictionary<string, object>(probeSource) { ["sourceEncounterTurns"] = new object[0] }))
                && !ProbeAccepts(row => row["payload_json"] = Json.Serialize(new Dictionary<string, object>(probeSource) { ["operatorTestControls"] = GossipFields("compositionMode", "fail_once") })), null);
            string originalProbe = CanonicalJson(probeSource), originalRow = CanonicalJson(probeRow);
            var proposal = PersonalGossipProbeDraft(probeSource, request => ReadString(request, "requestType", "") == "personal_gossip"
                ? Response(Draft()) : Response(Verdict(ReadString(Draft(), "body", ""))));
            check("composition_probe_uses_real_writer_review_validation_without_live_attempts", ReadBool(proposal, "accepted", false)
                && ReadInt(proposal, "providerCalls", 0) == 2 && ReadInt(proposal, "liveAttemptsConsumed", -1) == 0
                && ReadBool(proposal, "sourceUnchanged", false) && CanonicalJson(probeSource) == originalProbe && CanonicalJson(probeRow) == originalRow
                && !Json.Serialize(proposal).Contains(GossipCompositionApprovalKey), null);
            var refusedProbe = PersonalGossipProbeDraft(probeSource, request => ReadString(request, "requestType", "") == "personal_gossip"
                ? Response(Draft()) : Response(Verdict(ReadString(Draft(), "body", ""), "participantsPreserved")));
            check("composition_probe_retains_rejected_proposal_without_retry_or_publication", !ReadBool(refusedProbe, "accepted", true)
                && ReadInt(refusedProbe, "providerCalls", 0) == 2 && ReadString(refusedProbe, "proposedBody", "") == ReadString(Draft(), "body", "")
                && ReadString(refusedProbe, "validatedBody", "").Length == 0 && CanonicalJson(probeSource) == originalProbe, null);
            foreach (string criterion in new[] { "participantsPreserved", "aboutPlayerEncounter", "severityRealized" })
            {
                var rejected = ReviewPersonalGossipComposition(data, Draft(), _ => Response(Verdict(ReadString(Draft(), "body", ""), criterion)));
                check("malicious_review_rejects_" + criterion, rejected.ContainsKey("error") && !PersonalGossipCompositionApproved(data, rejected), null);
            }
            var wrongRoles = Draft("Eupitor, that armed stranger hired my carts six years. I told him so myself.");
            var wrongRoleResult = ReviewPersonalGossipComposition(data, wrongRoles, _ => Response(Verdict(ReadString(wrongRoles, "body", ""), "participantsPreserved")));
            check("native_malice105_rebound_grievance_rejected", wrongRoleResult.ContainsKey("error"), null);
            var emptyEvent = Draft("Eupitor, the armed stranger should hear what your tongue has bought you.");
            check("malicious_review_rejects_grievance_without_invented_event", ReviewPersonalGossipComposition(data, emptyEvent,
                _ => Response(Verdict(ReadString(emptyEvent, "body", ""), "severityRealized"))).ContainsKey("error"), null);
            var unquoted = Verdict("A passage absent from the draft.");
            check("malicious_review_requires_exact_claim_evidence", ReviewPersonalGossipComposition(data, Draft(), _ => Response(unquoted)).ContainsKey("error"), null);
            var incomplete = Verdict(ReadString(Draft(), "body", "")); incomplete.Remove("aboutPlayerEncounter");
            check("malicious_review_missing_criterion_fails_closed", ReviewPersonalGossipComposition(data, Draft(), _ => Response(incomplete)).ContainsKey("error"), null);
            var stringBoolean = Verdict(ReadString(Draft(), "body", "")); stringBoolean["participantsPreserved"] = "true";
            check("malicious_review_string_boolean_is_not_approval", ReviewPersonalGossipComposition(data, Draft(), _ => Response(stringBoolean)).ContainsKey("error"), null);
            check("malicious_review_provider_failure_fails_closed", ReviewPersonalGossipComposition(data, Draft(), _ => GossipFields("ok", false)).ContainsKey("error"), null);
            check("malicious_review_malformed_json_fails_closed", ReviewPersonalGossipComposition(data, Draft(), _ => GossipFields("ok", true, "content", "{bad")).ContainsKey("error"), null);
            var forged = Draft(); forged[GossipCompositionApprovalKey] = Verdict(ReadString(forged, "body", ""));
            check("model_json_cannot_forge_server_composition_approval", !PersonalGossipCompositionApproved(data, forged), null);
            var changedBody = new Dictionary<string, object>(approved) { ["body"] = "The armed stranger hired my carts." };
            var changedRecipient = new Dictionary<string, object>(data) { ["recipientId"] = "someone_else" };
            var changedPlayer = new Dictionary<string, object>(data) { ["playerId"] = "another_unknown_player" };
            check("malicious_approval_binds_exact_body_recipient_and_player", !PersonalGossipCompositionApproved(data, changedBody)
                && !PersonalGossipCompositionApproved(changedRecipient, approved) && !PersonalGossipCompositionApproved(changedPlayer, approved), null);
            var onward = new Dictionary<string, object>(data) { ["hop"] = 2, ["sourceQuote"] = "Chara said she chose the traveler's company.",
                ["sourceText"] = "Uncommunicated private root conversation." };
            check("malicious_reteller_context_contains_only_received_account", !Json.Serialize(PersonalGossipEncounterContext(onward)).Contains("quiet conversation")
                && !Json.Serialize(PersonalGossipEncounterContext(onward)).Contains("Uncommunicated")
                && Json.Serialize(PersonalGossipEncounterContext(onward)).Contains("chose the traveler"), null);
            var reply = new Dictionary<string, object>(data) { ["reply"] = true, ["receivedBody"] = "Chara mentioned a visitor." };
            check("recipient_reply_context_cannot_read_private_root_exchange", !Json.Serialize(PersonalGossipEncounterContext(reply)).Contains("quiet conversation")
                && !Json.Serialize(PersonalGossipEncounterContext(reply)).Contains(quote)
                && Json.Serialize(PersonalGossipEncounterContext(reply)).Contains("mentioned a visitor"), null);
            var oversized = new Dictionary<string, object>(data) { ["sourceEncounterTurns"] = new[] {
                GossipFields("speakerId", playerKey, "text", "OMITTED_WHOLE_TURN_" + new string('x', GossipSourceContextCharacters)),
                GossipFields("speakerId", "source", "text", quote) } };
            var bounded = PersonalGossipEncounterContext(oversized);
            check("malicious_context_budget_preserves_whole_records", ReadInt(bounded, "omittedWholeTurns", 0) == 1
                && !Json.Serialize(bounded).Contains("OMITTED_WHOLE_TURN") && Json.Serialize(bounded).Contains(quote), null);
            var persisted = new Dictionary<string, object>(data);
            BindPersonalGossipEncounterTurns(persisted, new[] {
                GossipFields("speaker_id", playerKey, "text", "OMITTED_WHOLE_TURN_" + new string('x', GossipSourceContextCharacters)),
                GossipFields("speaker_id", "source", "text", quote) });
            check("malicious_job_storage_obeys_source_context_budget", ReadDictionaryList(persisted, "sourceEncounterTurns")
                    .Sum(turn => ReadString(turn, "text", "").Length) <= GossipSourceContextCharacters
                && !Json.Serialize(persisted["sourceEncounterTurns"]).Contains("OMITTED_WHOLE_TURN")
                && ReadInt(PersonalGossipEncounterContext(persisted), "omittedWholeTurns", 0) == 1, null);
            persisted["reply"] = true;
            BindPersonalGossipEncounterTurns(persisted, new[] { GossipFields("speaker_id", "source", "text", quote) });
            check("recipient_context_binding_cannot_retain_root_turns", !persisted.ContainsKey("sourceEncounterTurns")
                && !persisted.ContainsKey("sourceContextOmittedWholeTurns"), null);
            var truthful = new Dictionary<string, object>(data) { ["distortion"] = "none" };
            int unnecessaryCalls = 0;
            ReviewPersonalGossipComposition(truthful, Draft(), _ => { unnecessaryCalls++; throw new InvalidOperationException("No review is required."); });
            check("truthful_source_path_adds_no_review_provider_call", unnecessaryCalls == 0, null);
        }

        private static void RunPersonalGossipPersistenceTests(Action<string, bool, object> check, Dictionary<string, object> spiteful)
        {
            const string entrustedPlan = "Keep this between us. I want to pay for the quarter's well anonymously.";
            string campaign = "__gossip_test_" + Guid.NewGuid().ToString("N");
            string snapshotId = "gossip_before_delivery";
            var source = GossipTestHero("source"); var target = GossipTestHero("target"); var player = GossipTestHero("player");
            var people = new List<Dictionary<string, object>> { source, target, player };
            var policy = DefaultPersonalGossipSettings();
            var excitable = GossipTestTraits("sociability", 100, "impulsiveness", 100, "pride", 100, "curiosity", 100);
            WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), excitable);
            Func<double, Dictionary<string, object>> presence = day => GossipFields("campaignId", campaign, "timelineId", "main", "worldDay", day,
                "gossipPresence", GossipFields("schema", "reign-gossip-presence-v1", "timelineId", "main", "worldDay", day, "rosterComplete", true, "people", people));
            Dictionary<string, object> NewTopic(ReignDbConnection c, string id, double day, bool named, bool malicious = false,
                string sourceText = null, string npcReply = null)
            {
                string text = sourceText ?? "Traveler and I talked privately about the tournament.";
                // Seed a closed fixture session without scheduling normal consolidation/providers.
                ExecuteSql(c, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,participants_json,payload_json)
VALUES($id,$campaign,'source','player','in_person','closed',1,1,$day,$day,'[""source"",""player""]','{}') ON CONFLICT(session_id) DO NOTHING;",
                    GossipFields("id", id + "_conversation", "campaign", campaign, "day", day));
                var request = GossipFields("campaignId", campaign, "conversationSessionId", id + "_conversation", "timelineId", "main", "worldDay", day, "channel", "in_person");
                var exchange = StoreConversationExchange(campaign, request, "source", "player", "Traveler", "Source", text, npcReply ?? "That was an interesting conversation.", id + "_exchange", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var signal = GossipFields("topic", "conversation", "confidence", 1d, "evidenceQuote", text, "excitement", malicious ? 0 : 100,
                    "curiosity", 0, "hurt", malicious ? 75 : 0, "privacy", "public", "existencePrivacy", "public", "question", !malicious,
                    "promisedSecrecy", false, "ownCompromising", false,
                    "targets", malicious ? new[] { GossipFields("heroId", "target", "evidenceQuote", text, "hurt", 75, "grievance", 0) } : Array.Empty<Dictionary<string, object>>());
                return GossipFields("id", id, "timelineId", "main", "speakerId", "source", "playerId", "player", "sessionId", ReadString(exchange, "sessionId", ""),
                    "createdDay", day, "worldDay", day, "category", "conversation", "signal", signal, "speaker", source, "policy", policy,
                    "mentionedIds", named ? new[] { "target" } : Array.Empty<string>(), "sourceText", text, "sourceQuote", text,
                    "sourceTurnIds", ReadStringList(exchange, "turnIds"), "sourceExchangeId", ReadString(exchange, "exchangeId", ""), "rootId", id, "hop", 1, "holders", new[] { "source" });
            }
            Dictionary<string, object> ReviewedFixture(Dictionary<string, object> data, Dictionary<string, object> draft) =>
                ReviewPersonalGossipComposition(data, draft, _ => GossipFields("ok", true, "content", Json.Serialize(GossipFields(
                    "participantsPreserved", true, "aboutPlayerEncounter", true, "severityRealized", true,
                    "questionPreserved", true, "factsSupported", true, "noAnswerInvented", true,
                    "claimQuote", ReadString(draft, "body", ""), "reason", "Isolated fixture reviewer outcome; no provider call."))));
            Dictionary<string, object> Compose(Dictionary<string, object> data, bool reply)
            {
                var draft = reply
                    ? GossipFields("body", "I will think about that.", "sourceQuote", "", "privacy", "public", "answerKind", "acknowledged", "gossipSignals", Array.Empty<object>())
                    : PersonalGossipFramedSource(data) ?? GossipFields("body", ReadString(data, "distortion", "none") == "invented_detail" ? "Traveler spent the night with me." : "Did you speak with Traveler about the tournament?",
                        "sourceQuote", ReadString(data, "sourceQuote", ""), "distortionSeverity", ReadString(data, "distortion", "none"), "account", "firsthand");
                if (!reply && PersonalGossipNeedsQuestionComposition(data))
                    draft["body"] = ReadString(draft, "body", "").Replace(" What do you make of that?", "") + " Why did you discuss the tournament with Traveler?";
                return reply ? draft : ReviewedFixture(data, draft);
            }
            Dictionary<string, object> FreshClock(double day)
            {
                var ready = PersonalGossipStatusApi(new Dictionary<string, string> { ["campaignId"] = campaign, ["deliveryOnly"] = "true" });
                var payload = presence(day);
                payload["gossipClockEpoch"] = ReadString(ready, "clockEpoch", "");
                payload["gossipReadyNonces"] = ReadStringList(ready, "readyNonces");
                return payload;
            }
            void ComposeAndCapture(Func<Dictionary<string, object>, bool, Dictionary<string, object>> composer)
            {
                RunPersonalGossipWork(campaign, composer);
                double day;
                using (var c = OpenCampaignConnection(campaign)) day = ReadDouble(GossipClock(c, "main"), "world_day", 0);
                PersonalGossipTickApi(FreshClock(day));
            }
            try
            {
                string localId = GossipTestSeed("local_", id => GossipRoll(id, "share") <= 85);
                using (var c = OpenCampaignConnection(campaign))
                {
                    bool hadSchema = TableExists(c, "personal_gossip_messages");
                    var emptyReadiness = PersonalGossipStatusApi(new Dictionary<string, string> { ["campaignId"] = campaign, ["deliveryOnly"] = "true" });
                    check("readiness_is_readonly_and_handles_uninitialized_campaign", ReadBool(emptyReadiness, "ok", false)
                        && ReadInt(emptyReadiness, "pendingComposition", -1) == 0 && TableExists(c, "personal_gossip_messages") == hadSchema, null);
                    EnsurePersonalGossipSchema(c); EnsureMbtiRelationshipSchema(c); EnsureCategorizedMemorySchema(c); EnsureMemoryPrecisionSchema(c);
                    EnsureGossipTestSchema(c);
                    using (var activationTransaction = c.BeginTransaction())
                    {
                        var child = GossipTestHero("activation_child"); child["motherId"] = "activation_mother"; child["available"] = false;
                        var mother = GossipTestHero("activation_mother"); mother["available"] = false;
                        var activationPeople = new List<Dictionary<string, object>> { child, mother, player };
                        Dictionary<string, object> State(string id, int age) => GossipFields("heroId", id, "heroState", "Disabled", "isActive", false,
                            "isAlive", true, "age", age, "isPrisoner", false, "partyId", "", "hasPrisonerParty", false, "governorOfSettlementId", "",
                            "isTraveling", false, "settlementUnderSiege", false, "hasQuestDuty", false);
                        var activationStates = new List<Dictionary<string, object>> { State("activation_child", 25), State("activation_mother", 50) };
                        var activationTest = GossipFields("caseId", "activation", "phase", "prepare", "actorHeroId", "activation_child",
                            "activateResidents", new[] { "activation_child", "activation_mother" }, "placements", new[] {
                                GossipFields("heroId", "activation_child", "settlementId", "town_a"), GossipFields("heroId", "activation_mother", "settlementId", "town_a") });
                        var activationRequest = GossipFields("runId", "activation_contract", "commandId", "valid", "timelineId", "main",
                            "playerId", "player", "gameInstanceId", "isolated_instance", "activeSaveName", "owned_current", "nativeActivationStates", activationStates);
                        var activationResult = GossipFields();
                        PreparePersonalGossipTest(c, campaign, activationRequest, activationTest, activationPeople, activationResult);
                        var journal = GossipJson(QuerySql(c, "SELECT payload_json FROM personal_gossip_test_fixtures WHERE fixture_id='activation_contract:valid';").Single());
                        check("activation_journals_exact_native_before_state_without_gossip", CanonicalJson(ReadDictionaryList(journal, "beforeNativeActivationStates")) == CanonicalJson(activationStates)
                            && ReadDictionaryList(journal, "beforePresence").All(person => !ReadBool(person, "available", true))
                            && QuerySql(c, "SELECT topic_id FROM personal_gossip_topics;").Count == 0
                            && QuerySql(c, "SELECT message_id FROM personal_gossip_messages;").Count == 0, null);
                        bool rejectsMissing = false;
                        try { PreparePersonalGossipTest(c, campaign, new Dictionary<string, object>(activationRequest) {
                            ["commandId"] = "missing", ["nativeActivationStates"] = Array.Empty<object>() }, activationTest, activationPeople, GossipFields()); }
                        catch (ArgumentException) { rejectsMissing = true; }
                        check("activation_requires_native_state_evidence", rejectsMissing, null);
                        bool rejectsUnsafe = true;
                        foreach (var unsafeField in new[] {
                            new KeyValuePair<string, object>("isActive", true), new KeyValuePair<string, object>("isPrisoner", true),
                            new KeyValuePair<string, object>("hasQuestDuty", true), new KeyValuePair<string, object>("isTraveling", true),
                            new KeyValuePair<string, object>("settlementUnderSiege", true), new KeyValuePair<string, object>("age", 17),
                            new KeyValuePair<string, object>("partyId", "party"), new KeyValuePair<string, object>("hasPrisonerParty", true),
                            new KeyValuePair<string, object>("governorOfSettlementId", "town_a") })
                        {
                            var unsafeStates = activationStates.Select(state => new Dictionary<string, object>(state)).ToList();
                            unsafeStates[1][unsafeField.Key] = unsafeField.Value;
                            bool rejected = false;
                            try { PreparePersonalGossipTest(c, campaign, new Dictionary<string, object>(activationRequest) {
                                ["commandId"] = "unsafe_" + unsafeField.Key, ["nativeActivationStates"] = unsafeStates }, activationTest, activationPeople, GossipFields()); }
                            catch (ArgumentException) { rejected = true; }
                            rejectsUnsafe &= rejected;
                        }
                        var unrelated = GossipTestHero("activation_stranger"); unrelated["available"] = false;
                        bool rejectsUnrelated = false;
                        try { PreparePersonalGossipTest(c, campaign, new Dictionary<string, object>(activationRequest) {
                                ["commandId"] = "unrelated", ["nativeActivationStates"] = new[] { State("activation_stranger", 30) } },
                            GossipFields("caseId", "unrelated", "phase", "prepare", "actorHeroId", "activation_child", "activateResidents", new[] { "activation_stranger" },
                                "placements", new[] { GossipFields("heroId", "activation_stranger", "settlementId", "town_a") }),
                            activationPeople.Concat(new[] { unrelated }).ToList(), GossipFields()); }
                        catch (ArgumentException) { rejectsUnrelated = true; }
                        check("activation_rejects_unsafe_or_unrelated_residents_before_journaling", rejectsUnsafe && rejectsUnrelated
                            && QuerySql(c, "SELECT fixture_id FROM personal_gossip_test_fixtures;").Count == 1, null);
                        activationTransaction.Rollback();
                    }
                    GossipTestPair(c, "source", "target", 50, -60);
                    check("directional_authority_not_native_average", GossipAffinity(c, "source", "target", source, target) == 50
                        && GossipAffinity(c, "target", "source", target, source) == -60, null);
                    var readinessWrongTimeline = PersonalGossipStatusApi(new Dictionary<string, string> { ["campaignId"] = campaign, ["deliveryOnly"] = "true", ["timelineId"] = "inactive" });
                    check("readiness_rejects_wrong_timeline", !ReadBool(readinessWrongTimeline, "ok", true), null);
                    GossipAcceptPresence(c, presence(20));
                    var clockBeforeIncomplete = Json.Serialize(GossipClock(c, "main"));
                    var incompletePresence = presence(20);
                    ReadDictionary(incompletePresence, "gossipPresence")["rosterComplete"] = false;
                    check("production_incomplete_presence_rejected_without_clock_write", !GossipAcceptPresence(c, incompletePresence)
                        && Json.Serialize(GossipClock(c, "main")) == clockBeforeIncomplete, null);
                    ReadDictionary(incompletePresence, "gossipPresence").Remove("rosterComplete");
                    check("production_missing_completeness_rejected_without_clock_write", !GossipAcceptPresence(c, incompletePresence)
                        && Json.Serialize(GossipClock(c, "main")) == clockBeforeIncomplete, null);
                    var presenceContract = GossipProductionPresenceContract(c, presence(20));
                    check("production_presence_contract_rolls_back_all_fixed_probes", new[] { "controlAccepted", "incompleteRejected",
                        "missingCompletenessRejected", "overCapRejected", "clockUnchangedOnRejection", "rollbackRestoredClock" }
                        .All(key => ReadBool(presenceContract, key, false)), presenceContract);
                    var stale = presence(20); ReadDictionary(stale, "gossipPresence")["worldDay"] = 19d;
                    check("stale_native_snapshot_rejected", !GossipAcceptPresence(c, stale), null);
                    var partial = NewTopic(c, "incomplete_roster", 20, true); partial["presenceComplete"] = false;
                    EvaluatePersonalGossip(c, campaign, partial, people);
                    check("partial_roster_cannot_select_audience", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='incomplete_roster';").Count == 0, null);
                    var topic = NewTopic(c, localId, 20, false);
                    EvaluatePersonalGossip(c, campaign, topic, people);
                    for (int i = 0; i < 100; i++) EvaluatePersonalGossip(c, campaign, topic, people);
                    check("one_queue_item_per_topic", QuerySql(c, "SELECT * FROM personal_gossip_messages;").Count == 1, null);
                    check("queue_has_no_recipient_history", QuerySql(c, "SELECT * FROM personal_gossip_history;").Count == 0, null);
                    var sourceObservation = PersonalGossipConversationScheduling(c, "main", 20, new List<string> { "source" });
                    var observedTopics = ReadDictionaryList(sourceObservation, "topics");
                    check("open_conversation_observation_references_actual_accepted_source", ReadInt(sourceObservation, "messageCount", 0) == 1
                        && observedTopics.Any(row => ReadString(row, "sourceExchangeId", "") == ReadString(topic, "sourceExchangeId", "")
                            && ReadString(row, "sessionId", "") == ReadString(topic, "sessionId", ""))
                        && !ReadBool(sourceObservation, "npcKnowledgeAuthority", true) && ReadInt(sourceObservation, "providerCalls", -1) == 0
                        && observedTopics.All(row => !row.ContainsKey("payload_json") && !row.ContainsKey("sourceText")), null);
                    check("open_conversation_observation_filters_other_observers_and_timelines", ReadInt(PersonalGossipConversationScheduling(c, "main", 20, new List<string> { "player" }), "messageCount", -1) == 0
                        && ReadInt(PersonalGossipConversationScheduling(c, "other", 20, new List<string> { "source" }), "topicCount", -1) == 0, null);
                    check("open_conversation_observation_never_exposes_future_sources", ReadInt(PersonalGossipConversationScheduling(c, "main", 19, new List<string> { "source" }), "messageCount", -1) == 0
                        && ReadInt(PersonalGossipConversationScheduling(c, "main", 19, new List<string> { "source" }), "topicCount", -1) == 0, null);
                    string unchangedMessage = Json.Serialize(QuerySql(c, "SELECT * FROM personal_gossip_messages;"));
                    using (var observationTransaction = c.BeginTransaction())
                    {
                        string originalMessage = ReadString(QuerySql(c, "SELECT message_id FROM personal_gossip_messages;").Single(), "message_id", "");
                        for (int i = 0; i < 65; i++) ExecuteSql(c, @"INSERT INTO personal_gossip_messages
(message_id,topic_id,root_id,timeline_id,speaker_id,recipient_id,player_id,is_reply,hop,created_day,due_day,expires_day,status,attempts,lease_token,lease_until,payload_json)
SELECT $copy,topic_id,root_id,timeline_id,speaker_id,recipient_id,player_id,is_reply,hop,created_day,due_day,expires_day,status,attempts,lease_token,lease_until,payload_json
FROM personal_gossip_messages WHERE message_id=$original;", GossipFields("copy", "observation_copy_" + i, "original", originalMessage));
                        var bounded = PersonalGossipConversationScheduling(c, "main", 20, new List<string> { "source" });
                        check("open_conversation_observation_bounds_rows_and_reports_truncation", ReadDictionaryList(bounded, "messages").Count == 64
                            && ReadInt(bounded, "messageCount", 0) == 66 && ReadBool(bounded, "truncated", false), null);
                        observationTransaction.Rollback();
                    }
                    check("open_conversation_observation_is_readonly_and_fixture_rolls_back", unchangedMessage == Json.Serialize(QuerySql(c, "SELECT * FROM personal_gossip_messages;"))
                        && QuerySql(c, "SELECT * FROM personal_gossip_history;").Count == 0, null);
                }
                var preComposition = FreshClock(20);
                RunPersonalGossipWork(campaign, Compose);
                PersonalGossipTickApi(preComposition);
                using (var c = OpenCampaignConnection(campaign))
                    check("completed_wording_requires_post_ready_native_capture", QuerySql(c, "SELECT receipt_id FROM personal_gossip_history;").Count == 0
                        && ReadString(QuerySql(c, "SELECT status FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", localId + "_out")).Single(), "status", "") == "awaiting_native_clock", null);
                var wrongNonce = FreshClock(20); wrongNonce["gossipReadyNonces"] = new[] { "unobserved_nonce" };
                PersonalGossipTickApi(wrongNonce);
                using (var c = OpenCampaignConnection(campaign))
                    check("unobserved_ready_nonce_cannot_publish", ReadString(QuerySql(c, "SELECT status FROM personal_gossip_messages WHERE message_id=$id;",
                        GossipFields("id", localId + "_out")).Single(), "status", "") == "awaiting_native_clock", null);
                var previousSaveProof = FreshClock(20.01);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, snapshotId);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, snapshotId);
                AdvanceMemoryRestoreGeneration(campaign);
                check("ready_result_restore_rejects_old_clock_proof", !ReadBool(PersonalGossipTickApi(previousSaveProof), "ok", true), null);
                using (var c = OpenCampaignConnection(campaign))
                    check("restored_wording_waits_without_reroll_or_recompose", ReadDouble(GossipClock(c, "main"), "world_day", 0) == 20
                        && QuerySql(c, "SELECT receipt_id FROM personal_gossip_history;").Count == 0
                        && ReadInt(QuerySql(c, "SELECT attempts FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", localId + "_out")).Single(), "attempts", 0) == 1, null);
                ReignPostgreSqlStorage.DropSnapshot(campaign, snapshotId);
                PersonalGossipTickApi(FreshClock(20));
                PersonalGossipTickApi(presence(20 + .9 / 24));
                using (var c = OpenCampaignConnection(campaign))
                    check("before_one_hour_no_history", QuerySql(c, "SELECT * FROM personal_gossip_history;").Count == 0, null);
                source["settlementId"] = "town_left"; target["settlementId"] = "town_other";
                var arrived = PersonalGossipTickApi(presence(20 + 1d / 24));
                check("departure_keeps_local_delivery", ReadInt(arrived, "delivered", 0) == 1, arrived);
                using (var c = OpenCampaignConnection(campaign))
                {
                    var receipt = QuerySql(c, "SELECT * FROM personal_gossip_history WHERE owner_id='target';").Single();
                    check("delivered_claim_not_private_source", !ReadString(receipt, "payload_json", "").Contains("sourceText")
                        && !ReadString(receipt, "payload_json", "").Contains("distortion") && ReadDouble(receipt, "acquired_day", 0) >= 20 + 1d / 24, receipt);
                    check("return_not_known_before_composed", !QuerySql(c, "SELECT * FROM personal_gossip_history WHERE owner_id='source';")
                        .Any(r => ReadString(GossipJson(r), "claimKind", "") == "reply"), null);
                }
                RunPersonalGossipWork(campaign, (data, reply) =>
                {
                    // Counterexample: native has advanced and paused before the provider completes.
                    using (var c = OpenCampaignConnection(campaign)) GossipAcceptPresence(c, presence(20 + 2d / 24));
                    return Compose(data, reply);
                });
                using (var c = OpenCampaignConnection(campaign))
                    check("late_composer_never_publishes_using_previous_clock", QuerySql(c, "SELECT receipt_id FROM personal_gossip_history WHERE message_id=$id;",
                        GossipFields("id", localId + "_out_reply")).Count == 0, null);
                var lateProof = FreshClock(20 + 2d / 24);
                var staleEpoch = FreshClock(20 + 3d / 24); staleEpoch["gossipClockEpoch"] = "previous_process_or_restore";
                check("obsolete_epoch_cannot_advance_native_clock", !ReadBool(PersonalGossipTickApi(staleEpoch), "ok", true), null);
                PersonalGossipTickApi(lateProof);
                using (var c = OpenCampaignConnection(campaign))
                {
                    check("local_reply_and_idempotent_receipts", QuerySql(c, "SELECT * FROM personal_gossip_history;").Count == 4, null);
                    check("late_reply_acquisition_uses_fresh_native_day", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id;",
                        GossipFields("id", localId + "_out_reply")).All(r => ReadDouble(r, "acquired_day", 0) == 20 + 2d / 24), null);
                    var payload = presence(20 + 2d / 24); payload["playerHeroStringId"] = "player";
                    string receivedBodyBeforeIdentityHint = ReadString(GossipJson(QuerySql(c,
                        "SELECT * FROM personal_gossip_history WHERE message_id=$message AND owner_id='target';",
                        GossipFields("message", localId + "_out")).Single()), "body", "");
                    var sourceIdentityProfile = ReadJsonObject(CharacterFile(campaign, "source", "profile.json")) ?? new Dictionary<string, object>();
                    sourceIdentityProfile["isFemale"] = false;
                    WriteJsonObject(CharacterFile(campaign, "source", "profile.json"), sourceIdentityProfile);
                    string own = BuildPersonalGossipPrompt(campaign, "target", payload, new List<string>());
                    string outsider = BuildPersonalGossipPrompt(campaign, "outsider", payload, new List<string>());
                    check("observer_private_recent_history", own.Contains("talked privately about the tournament") && !outsider.Contains("talked privately about the tournament"), null);
                    check("received_gossip_context_uses_native_identity_without_rewriting_history", own.Contains("source (man; he/him) told you")
                        && !outsider.Contains("source (man; he/him) told you")
                        && ReadString(GossipJson(QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$message AND owner_id='target';",
                            GossipFields("message", localId + "_out")).Single()), "body", "") == receivedBodyBeforeIdentityHint, null);
                    PromptEnvelope IdentityEnvelope(Dictionary<string, object> context, string observer, string suppliedContext, bool eventMode = false, bool finalize = true)
                    {
                        var rows = new List<Dictionary<string, object>>();
                        string canonical = FormatContinuityTranscript(rows, FormatDialogueForPrompt);
                        var slots = CreateConversationTemplateSlots("{context}\n{priorDialogueText}\n{playerText}", "priorDialogueText");
                        const string latest = "How credible is that story?";
                        slots.MarkedLiveTurn = RenderConversationTemplate(slots.Template, new Dictionary<string, string> {
                            ["context"] = suppliedContext, ["priorDialogueText"] = canonical, ["playerText"] = latest });
                        var envelope = CreatePromptEnvelope(eventMode ? "social_event" : "dialogue", "gossip_identity_fixture",
                            "Current engine identity controls narration; memories remain fallible attributed records.", "Known character", slots.WithoutMarkers(slots.MarkedLiveTurn));
                        AttachPersonalGossipIdentityPrompt(envelope, campaign, observer, context);
                        AttachPersonalGossipIdentityPrompt(envelope, campaign, observer, context);
                        StructureConversationMessages(envelope, slots, canonical, rows, FormatDialogueForPrompt, observer, "Traveler", latest, eventMode, null);
                        if (finalize) FinalizeContinuityPromptBudget(envelope, context, GossipFields("inputAllowance", 1000000, "outputFits", true));
                        return envelope;
                    }
                    var identityEnvelope = IdentityEnvelope(payload, "target", own + "\nEarlier mistaken recollection: source called herself a woman.");
                    var identityMessages = identityEnvelope.Messages.Where(m => ReadString(m, "role", "") == "system"
                        && ReadString(m, "content", "").Contains("AUTHORITATIVE KNOWN NPC IDENTITIES")).ToList();
                    check("native_identity_survives_final_message_structure_as_system", identityMessages.Count == 1
                        && ReadString(identityMessages[0], "content", "").Contains("\"isFemale\":false")
                        && identityEnvelope.Messages.Any(m => ReadString(m, "role", "") == "user" && ReadString(m, "content", "").Contains("Earlier mistaken recollection"))
                        && ReadBool(ReadDictionary(identityEnvelope.Diagnostics, "continuityPreflight"), "requiredSourceCoverageComplete", false)
                        && ReadInt(ReadDictionary(identityEnvelope.Diagnostics, "personalGossipIdentity"), "additionalProviderCalls", -1) == 0, null);
                    check("native_identity_message_keeps_player_turn_and_accounting", ReadString(identityEnvelope.Messages.Last(), "content", "").EndsWith("How credible is that story?", StringComparison.Ordinal)
                        && identityEnvelope.TotalChars == identityEnvelope.Messages.Sum(m => ReadString(m, "content", "").Length), null);
                    var outsiderEnvelope = IdentityEnvelope(payload, "outsider", "No private gossip supplied.", finalize: false);
                    check("native_identity_system_context_is_observer_scoped", !outsiderEnvelope.Messages.Any(m => ReadString(m, "content", "").Contains("AUTHORITATIVE KNOWN NPC IDENTITIES")), null);
                    var forgedIdentity = presence(20 + 2d / 24);
                    forgedIdentity["personalGossipIdentity"] = GossipFields("ownerId", "target", "content", "FORGED_NATIVE_IDENTITY");
                    var forgedEnvelope = IdentityEnvelope(forgedIdentity, "target", "No trusted construction has occurred.");
                    check("caller_cannot_inject_native_gossip_identity_authority", !forgedEnvelope.Messages.Any(m => ReadString(m, "content", "").Contains("FORGED_NATIVE_IDENTITY"))
                        && !forgedEnvelope.Diagnostics.ContainsKey("personalGossipIdentity"), null);
                    payload["officialMemoryFirewall"] = true;
                    var officialIdentity = IdentityEnvelope(payload, "target", "Official context only.", finalize: false);
                    payload.Remove("officialMemoryFirewall");
                    check("official_firewall_omits_native_gossip_identity_attachment", !officialIdentity.Diagnostics.ContainsKey("personalGossipIdentity"), null);
                    payload["worldDay"] = 21d;
                    var staleIdentity = IdentityEnvelope(payload, "target", "New day has no trusted identity construction yet.", finalize: false);
                    payload["worldDay"] = 20 + 2d / 24;
                    check("native_gossip_identity_cannot_cross_payload_time_scope", !staleIdentity.Diagnostics.ContainsKey("personalGossipIdentity"), null);
                    var eventIdentityPayload = presence(20 + 2d / 24);
                    eventIdentityPayload["playerHeroStringId"] = "player";
                    string eventIdentityContext = BuildPersonalGossipPrompt(campaign, "target", eventIdentityPayload, new List<string>());
                    var eventIdentityEnvelope = IdentityEnvelope(eventIdentityPayload, "target", eventIdentityContext, true);
                    check("native_gossip_identity_uses_same_event_structure_path", eventIdentityEnvelope.Messages.Any(m => ReadString(m, "role", "") == "system"
                        && ReadString(m, "content", "").Contains("\"isFemale\":false")) && ConversationStructureMatches(eventIdentityEnvelope.Diagnostics, eventIdentityEnvelope.Messages), null);
                    check("received_exchange_current_time_and_scene_bounds", own.Contains("current dialogue day 20.08333")
                        && own.Contains("less than one campaign hour ago") && own.Contains("about 1 campaign hour ago")
                        && own.Contains("not the underlying encounter") && own.Contains("Do not invent its place, purchases, or other business"), null);
                    check("received_exchange_does_not_invent_extra_conduct", own.Contains("complete evidence for that NPC exchange")
                        && own.Contains("Do not add unrecorded remarks, repetitions, actions, reactions, or quantities")
                        && own.Contains("interpretation as an interpretation"), null);
                    var older = presence(22 + 2d / 24); older["playerHeroStringId"] = "player";
                    string olderPrompt = BuildPersonalGossipPrompt(campaign, "target", older, new List<string>());
                    check("older_received_exchange_uses_acquisition_age", olderPrompt.Contains("current dialogue day 22.08333")
                        && olderPrompt.Contains("about 48 campaign hours ago") && olderPrompt.Contains("about 49 campaign hours ago"), null);
                    var rewind = presence(19); rewind["playerHeroStringId"] = "player";
                    check("future_history_filtered", !BuildPersonalGossipPrompt(campaign, "target", rewind, new List<string>()).Contains("talked privately about the tournament"), null);
                }
                string remoteId = GossipTestSeed("remote_", id => GossipRoll(id, "share") <= 85);
                target["positionX"] = 160d;
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipAcceptPresence(c, presence(30));
                    var noAudience = NewTopic(c, "no_audience", 30, false);
                    EvaluatePersonalGossip(c, campaign, noAudience, people);
                    check("no_audience_no_job", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='no_audience';").Count == 0
                        && ReadString(QuerySql(c, "SELECT status FROM personal_gossip_topics WHERE topic_id='no_audience';").Single(), "status", "") == "no_audience", null);
                    EvaluatePersonalGossip(c, campaign, NewTopic(c, remoteId, 30, true), people);
                }
                ComposeAndCapture(Compose);
                target["settlementId"] = source["settlementId"];
                PersonalGossipTickApi(presence(31));
                using (var c = OpenCampaignConnection(campaign))
                    check("arrival_does_not_retry_no_audience_story", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='no_audience';").Count == 0, null);
                target["settlementId"] = "town_other";
                PersonalGossipTickApi(presence(32.99));
                using (var c = OpenCampaignConnection(campaign))
                    check("remote_not_early", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE root_id=$root AND owner_id='target';", GossipFields("root", remoteId)).Count == 0, null);
                PersonalGossipTickApi(presence(33));
                ComposeAndCapture(Compose);
                using (var c = OpenCampaignConnection(campaign))
                    check("remote_answer_waits_for_return", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='source';",
                        GossipFields("id", remoteId + "_out_reply")).Count == 0, null);
                PersonalGossipTickApi(presence(36));
                using (var c = OpenCampaignConnection(campaign))
                    check("remote_reply_arrives_once", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='source';",
                        GossipFields("id", remoteId + "_out_reply")).Count == 1, null);

                WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), spiteful);
                source["settlementId"] = "town_a"; target["settlementId"] = "town_a";
                string maliciousId = GossipTestSeed("malice_", id => GossipRoll(id, "share") <= 60 && GossipRoll(id, "negative") <= 57
                    && GossipRoll(id, "distortion") <= 71 && GossipRoll(id, "severity") >= 66);
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipTestPair(c, "source", "target", -60, -60);
                    GossipAcceptPresence(c, presence(40));
                    EvaluatePersonalGossip(c, campaign, NewTopic(c, maliciousId, 40, false, true), people);
                }
                ComposeAndCapture(Compose);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, snapshotId);
                PersonalGossipTickApi(presence(40.05));
                using (var c = OpenCampaignConnection(campaign))
                {
                    var recipientReceipt = GossipJson(QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='target';", GossipFields("id", maliciousId + "_out")).Single());
                    var authorReceipt = GossipJson(QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='source';", GossipFields("id", maliciousId + "_out")).Single());
                    check("intentional_false_event_separate_from_truth", ReadString(recipientReceipt, "body", "").Contains("spent the night")
                        && !recipientReceipt.ContainsKey("authorKnowsDistortion") && ReadBool(authorReceipt, "authorKnowsDistortion", false)
                        && QuerySql(c, "SELECT text FROM conversation_turns WHERE exchange_id=$id AND role='player';", GossipFields("id", maliciousId + "_exchange"))
                            .All(r => ReadString(r, "text", "").Contains("talked privately")), null);
                    check("gossip_never_changes_relationship", GossipAffinity(c, "target", "source", target, source) == -60, null);
                }
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, snapshotId);
                AdvanceMemoryRestoreGeneration(campaign);
                using (var c = OpenCampaignConnection(campaign))
                {
                    check("save_restore_rewinds_receipts_retains_route", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE root_id=$root;", GossipFields("root", maliciousId)).Count == 0
                        && ReadString(QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", maliciousId + "_out")).Single(), "status", "") == "in_transit", null);
                    ExecuteSql(c, "UPDATE conversation_turns SET status='invalidated' WHERE exchange_id=$id;", GossipFields("id", maliciousId + "_exchange"));
                }
                PersonalGossipTickApi(presence(40.05));
                using (var c = OpenCampaignConnection(campaign))
                    check("invalidated_source_cancels_undelivered", ReadString(QuerySql(c, "SELECT status FROM personal_gossip_messages WHERE message_id=$id;",
                        GossipFields("id", maliciousId + "_out")).Single(), "status", "") == "cancelled_unavailable", null);

                WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), excitable);
                var availabilitySourceBefore = new Dictionary<string, object>(source);
                var availabilityTargetBefore = new Dictionary<string, object>(target);
                int availabilityIndex = 0;
                foreach (string mode in new[] { "inactive", "prisoner", "dead", "active_departure", "reply_before_composition", "reply_before_publication" })
                {
                    double availabilityDay = 44 + availabilityIndex++;
                    source["settlementId"] = "availability_town";
                    target["settlementId"] = "availability_town";
                    target["isAlive"] = true; target["isPrisoner"] = false; target["available"] = true;
                    string availabilityId = GossipTestSeed("availability_" + mode + "_", candidate => GossipRoll(candidate, "share") <= 85);
                    using (var c = OpenCampaignConnection(campaign))
                    {
                        GossipTestPair(c, "source", "target", 60, 60);
                        GossipAcceptPresence(c, presence(availabilityDay));
                        EvaluatePersonalGossip(c, campaign, NewTopic(c, availabilityId, availabilityDay, false), people);
                    }
                    ComposeAndCapture(Compose);
                    if (mode.StartsWith("reply_", StringComparison.Ordinal))
                    {
                        PersonalGossipTickApi(presence(availabilityDay + .05));
                        if (mode == "reply_before_composition")
                        {
                            target["available"] = false;
                            using (var c = OpenCampaignConnection(campaign)) GossipAcceptPresence(c, presence(availabilityDay + .05));
                        }
                        int replyComposerCalls = 0;
                        RunPersonalGossipWork(campaign, (data, reply) => { if (reply) replyComposerCalls++; return Compose(data, reply); });
                        target["available"] = false;
                        PersonalGossipTickApi(FreshClock(availabilityDay + .05));
                        using (var c = OpenCampaignConnection(campaign))
                        {
                            var replyRow = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", availabilityId + "_out_reply")).Single();
                            check("unavailable_reply_speaker_" + mode, ReadString(replyRow, "status", "") == "cancelled_unavailable"
                                && ReadString(GossipJson(replyRow), "cancellationReason", "") == "reply_speaker_unavailable"
                                && replyComposerCalls == (mode == "reply_before_composition" ? 0 : 1)
                                && QuerySql(c, "SELECT receipt_id FROM personal_gossip_history WHERE message_id=$id;", GossipFields("id", availabilityId + "_out_reply")).Count == 0
                                && QuerySql(c, "SELECT receipt_id FROM personal_gossip_history WHERE message_id=$id;", GossipFields("id", availabilityId + "_out")).Count == 2, null);
                        }
                    }
                    else
                    {
                        if (mode == "inactive") target["available"] = false;
                        if (mode == "prisoner") target["isPrisoner"] = true;
                        if (mode == "dead") target["isAlive"] = false;
                        if (mode == "active_departure")
                        {
                            target["settlementId"] = ""; target["partyId"] = ""; target["positionKnown"] = false;
                        }
                        PersonalGossipTickApi(presence(availabilityDay + .05));
                        using (var c = OpenCampaignConnection(campaign))
                        {
                            var outgoing = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", availabilityId + "_out")).Single();
                            bool remainsAvailable = mode == "active_departure";
                            check("delivery_rechecks_communication_" + mode, ReadString(outgoing, "status", "") == (remainsAvailable ? "delivered" : "cancelled_unavailable")
                                && QuerySql(c, "SELECT receipt_id FROM personal_gossip_history WHERE message_id=$id;", GossipFields("id", availabilityId + "_out")).Count == (remainsAvailable ? 2 : 0)
                                && QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", availabilityId + "_out_reply")).Count == (remainsAvailable ? 1 : 0), null);
                        }
                        if (mode == "active_departure") ComposeAndCapture(Compose);
                    }
                }
                source.Clear(); foreach (var pair in availabilitySourceBefore) source[pair.Key] = pair.Value;
                target.Clear(); foreach (var pair in availabilityTargetBefore) target[pair.Key] = pair.Value;
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipTestPair(c, "source", "target", 60, 60);
                    EnsureConversationRelationshipSchema(c);
                    GossipAcceptPresence(c, presence(50));
                    foreach (int sign in new[] { -1, 0, 1 })
                    {
                        string impressionId = GossipTestSeed("impression_" + sign + "_", id => GossipRoll(id, "share") <= 85 && GossipRoll(id, "disclosure") <= 50);
                        var impression = NewTopic(c, impressionId, 50, false);
                        impression["category"] = "impression";
                        impression["signal"] = GossipFields("privacy", "personal", "existencePrivacy", "public");
                        ExecuteSql(c, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,native_application_status,created_ts)
VALUES($id,$campaign,'main',$exchange,'source','player',$delta,'applied','pending',1);",
                            GossipFields("id", impressionId, "campaign", campaign, "exchange", impressionId + "_exchange", "delta", 5 * sign));
                        EvaluatePersonalGossip(c, campaign, impression, people);
                        var queued = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", impressionId));
                        check("applied_delta_impression_" + sign, sign == 0 ? queued.Count == 0 : queued.Count == 1
                            && ReadString(GossipJson(queued[0]), "type", "") == "impression", null);
                        if (sign != 0)
                        {
                            var composed = GossipFields("body", "We kissed at the castle and exchanged gifts.");
                            bool valid = ValidatePersonalGossipComposition(c, GossipJson(queued[0]), composed, false);
                            check("impression_contains_no_private_detail_" + sign, valid && ReadString(composed, "body", "").Contains(sign > 0 ? "good impression" : "bad impression")
                                && !ReadString(composed, "body", "").Contains("castle"), composed);
                        }
                    }
                    var mixed = NewTopic(c, "mixed_zero", 50, false);
                    mixed["category"] = "impression";
                    ExecuteSql(c, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,created_ts)
VALUES('mixed_receipt',$campaign,'main','mixed_zero_exchange','source','player',0,'applied',1);", GossipFields("campaign", campaign));
                    EvaluatePersonalGossip(c, campaign, mixed, people);
                    check("capped_to_zero_not_positive_prose", QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id='mixed_zero';").Count == 0, null);
                    var secretTraits = GossipTestTraits("sociability", 100, "curiosity", 100, "impulsiveness", 0, "tact", 100, "discipline", 100, "empathy", 100, "honesty", 100, "loyalty", 100);
                    WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), secretTraits);
                    GossipTestPair(c, "source", "player", 100, 0);
                    string secretId = GossipTestSeed("private_", id => GossipRoll(id, "share") <= 65);
                    var secret = NewTopic(c, secretId, 51, false);
                    var privateSignal = ReadDictionary(secret, "signal");
                    privateSignal["privacy"] = "secret"; privateSignal["existencePrivacy"] = "secret";
                    privateSignal["promisedSecrecy"] = true; privateSignal["confidenceOwnerIds"] = new[] { "player" };
                    ExecuteSql(c, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,created_ts)
VALUES($id,$campaign,'main',$exchange,'source','player',5,'applied',1);", GossipFields("id", secretId, "campaign", campaign, "exchange", secretId + "_exchange"));
                    EvaluatePersonalGossip(c, campaign, secret, people);
                    check("discretion_refuses_private_details", QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", secretId)).Count == 0
                        && ReadString(QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", secretId)).Single(), "status", "") == "privacy_declined", null);
                    var failure = GossipFields("error", "Injected provider failure");
                    const string reportedTrade = "Rhagaea spoke with me earlier about blue-dyed wool and Zeonica's traders.";
                    var consultation = GossipFields("speakerId", "ira", "playerId", "tester", "playerName", "Tester",
                        "playerReference", GossipFields("label", "Tester", "named", true, "identityState", "verified"),
                        "recipientId", "rhagaea", "speaker", GossipFields("name", "Ira"), "recipient", GossipFields("name", "Rhagaea"),
                        "sourceQuoteSpeakerId", "tester", "sourceQuote", reportedTrade, "type", "positive", "claimKind", "question",
                        "distortion", "none", "hop", 1, "delta", 0,
                        "traits", GossipFields("traitPercentages", GossipFields("curiosity", 50), "nativeDescriptionEvidence", "unrelated private background"));
                    var brief = PersonalGossipCompositionBrief(consultation);
                    check("consultation_preserves_original_quote_perspective", ReadString(brief, "sourceQuoteSpeaker", "") == "Tester"
                        && ReadString(brief, "player", "") == "Tester" && ReadString(brief, "recipient", "") == "Rhagaea"
                        && !brief.ContainsKey("impression") && !Json.Serialize(brief).Contains("unrelated private background"), null);
                    var observedBadDraft = GossipFields("body", "Did you hear? The player spoke with your mother about blue-dyed wool and the traders of Zeonica, and it seems the encounter went badly.",
                        "sourceQuote", reportedTrade, "distortionSeverity", "none", "account", "secondhand");
                    check("native_consultation_bad_draft_rejected", !ValidatePersonalGossipComposition(c, consultation, observedBadDraft, false), null);
                    check("rhetorical_question_does_not_assert_unknown_answer", !PersonalGossipSubstantiveQuestion("Did you hear? He spoke with her.")
                        && PersonalGossipSubstantiveQuestion("He says you spoke about trade. Why did you seek him out?"), null);
                    check("ordinary_consultation_cannot_invent_encounter_outcome", !PersonalGossipOutcomeSupported("Why did the encounter go on? It went badly.", reportedTrade, "none")
                        && PersonalGossipOutcomeSupported("Was the conversation pleasant?", reportedTrade, "none")
                        && PersonalGossipOutcomeSupported("Our conversation went well.", "I enjoyed our conversation.", "none")
                        && PersonalGossipOutcomeSupported("It went badly.", reportedTrade, "false_escalation"), null);
                    var supportedQuestion = GossipFields("body", "Tester told me you spoke about blue-dyed wool and Zeonica's traders. Why did you seek him out?",
                        "sourceQuote", reportedTrade, "distortionSeverity", "none", "account", "secondhand");
                    check("pronoun_paraphrase_requires_explicit_source_frame", !ValidatePersonalGossipComposition(c, consultation, supportedQuestion, false), null);
                    Dictionary<string, object> QuestionFixture(Dictionary<string, object> data, string body = null, string reject = "")
                    {
                        string words = body ?? ReadString(PersonalGossipFramedSource(data), "body", "").Replace(" What do you make of that?", "")
                            + " Why did you discuss blue-dyed wool with " + ReadString(ReadDictionary(data, "playerReference"), "label", "the traveler") + "?";
                        return ComposePersonalGossip(data, false, request => ReadString(request, "requestType", "") == "personal_gossip_composition_review"
                            ? GossipFields("ok", true, "content", Json.Serialize(GossipFields("participantsPreserved", reject != "participants",
                                "questionPreserved", reject != "question", "factsSupported", reject != "facts", "noAnswerInvented", reject != "answer",
                                "claimQuote", words, "reason", "Controlled reviewer result, no provider call.")))
                            : GossipFields("ok", true, "content", Json.Serialize(GossipFields("body", words, "sourceQuote", ReadString(data, "sourceQuote", ""),
                                "distortionSeverity", "none", "account", "secondhand"))));
                    }
                    var framedQuestion = QuestionFixture(consultation);
                    check("attributed_consultation_question_remains_eligible", ValidatePersonalGossipComposition(c, consultation, framedQuestion, false)
                        && ReadString(framedQuestion, "body", "").Contains("Tester told me this:")
                        && ReadString(framedQuestion, "body", "").Contains(reportedTrade)
                        && PersonalGossipSubstantiveQuestion(ReadString(framedQuestion, "body", "")), null);
                    check("consultation_generic_adjacent_opinion_is_not_substantive", !PersonalGossipSubstantiveQuestion("What do you make of that?")
                        && ReadString(QuestionFixture(consultation, "Tester spoke with me. What do you make of that?"), "error", "").Length > 0, null);
                    foreach (string failureKind in new[] { "participants", "question", "facts", "answer" })
                        check("consultation_review_rejects_" + failureKind,
                            ReadString(QuestionFixture(consultation, reject: failureKind), "error", "").Length > 0, null);
                    var editedQuestion = new Dictionary<string, object>(framedQuestion) { ["body"] = "Tester said you already agreed. Why hide your answer?" };
                    check("consultation_review_is_bound_to_draft", !ValidatePersonalGossipComposition(c, consultation, editedQuestion, false), null);
                    var changedQuestionSource = new Dictionary<string, object>(consultation) { ["sourceQuote"] = "A different exchange." };
                    check("consultation_review_is_bound_to_source", !ValidatePersonalGossipComposition(c, changedQuestionSource, framedQuestion, false), null);
                    const string successionQuote = "Rhagaea asked whether I thought you were fit to inherit the throne, then changed the subject before I could answer.";
                    var succession = new Dictionary<string, object>(consultation) { ["sourceQuote"] = successionQuote, ["claimKind"] = "assertion" };
                    var swappedActors = GossipFields("body", "Rhagaea, you asked me whether I thought Tester fit to inherit the throne.",
                        "sourceQuote", successionQuote, "distortionSeverity", "none", "account", "firsthand");
                    check("native_succession_actor_swap_rejected", !ValidatePersonalGossipComposition(c, succession, swappedActors, false), null);
                    var successionReport = ComposePersonalGossip(succession, false);
                    check("native_succession_roles_preserved_without_provider", ValidatePersonalGossipComposition(c, succession, successionReport, false)
                        && ReadString(successionReport, "body", "") == "Tester told me this: \u201c" + successionQuote + "\u201d"
                        && ReadString(successionReport, "account", "") == "secondhand", null);
                    var ownStatement = new Dictionary<string, object>(succession) { ["sourceQuoteSpeakerId"] = "ira", ["sourceQuote"] = "My mother spoke with you." };
                    var ownReport = ComposePersonalGossip(ownStatement, false);
                    check("npc_source_preserves_original_addressee", ValidatePersonalGossipComposition(c, ownStatement, ownReport, false)
                        && ReadString(ownReport, "body", "") == "I told Tester: \u201cMy mother spoke with you.\u201d", null);
                    const string earnedCup = "A man who speaks well of honest scars has earned a cup";
                    var cup = new Dictionary<string, object>(ownStatement) { ["sourceQuote"] = earnedCup };
                    var cupReport = ComposePersonalGossip(cup, false);
                    check("native_unpronominal_npc_quote_retains_author", ValidatePersonalGossipComposition(c, cup, cupReport, false)
                        && ReadString(cupReport, "body", "") == "I told Tester: \u201c" + earnedCup + "\u201d", null);
                    check("native_unpronominal_npc_quote_cannot_become_player_statement", !ValidatePersonalGossipComposition(c, cup,
                        GossipFields("body", "Tester said a man who speaks well of honest scars has earned a cup.", "sourceQuote", earnedCup,
                            "distortionSeverity", "none", "account", "firsthand"), false), null);
                    cup["sourceQuoteSpeakerId"] = "tester";
                    var playerCupReport = ComposePersonalGossip(cup, false);
                    check("unpronominal_player_quote_retains_author", ValidatePersonalGossipComposition(c, cup, playerCupReport, false)
                        && ReadString(playerCupReport, "body", "") == "Tester told me this: \u201c" + earnedCup + "\u201d", null);
                    cup["sourceQuoteSpeakerId"] = "";
                    check("ambiguous_unpronominal_quote_cannot_invent_author", ReadString(ComposePersonalGossip(cup, false), "error", "").Length > 0, null);
                    var onward = new Dictionary<string, object>(succession) { ["hop"] = 2, ["speakerId"] = "rhagaea",
                        ["sourceQuoteSpeakerId"] = "ira", ["sourceQuoteSpeakerName"] = "Ira", ["sourceQuoteAddresseeId"] = "rhagaea", ["sourceQuote"] = ReadString(successionReport, "body", "") };
                    var onwardReport = ComposePersonalGossip(onward, false);
                    check("retelling_quotes_immediate_author_without_rebinding_root_roles", ValidatePersonalGossipComposition(c, onward, onwardReport, false)
                        && ReadString(onwardReport, "body", "").StartsWith("Ira told me this:", StringComparison.Ordinal)
                        && ReadString(onwardReport, "body", "").Contains(ReadString(successionReport, "body", ""))
                        && ReadString(onwardReport, "account", "") == "secondhand", null);
                    succession["sourceQuoteSpeakerId"] = "";
                    check("ambiguous_pronoun_source_never_invents_author", ReadString(ComposePersonalGossip(succession, false), "error", "").Length > 0, null);
                    succession["sourceQuoteSpeakerId"] = "tester";
                    succession["distortion"] = "invented_detail";
                    check("deliberate_distortion_retains_existing_generation_permission", !PersonalGossipNeedsSourceFraming(succession), null);
                    const string privateTalk = "I spoke privately with you about the market.";
                    var malicious = new Dictionary<string, object>(ownStatement) { ["sourceQuote"] = privateTalk,
                        ["type"] = "negative", ["claimKind"] = "assertion", ["distortion"] = "invented_detail" };
                    var maliciousBrief = PersonalGossipCompositionBrief(malicious);
                    check("malicious_source_frame_keeps_original_player_addressee",
                        ReadString(maliciousBrief, "originalSourceFrame", "") == "I told Tester: \u201c" + privateTalk + "\u201d"
                        && ReadString(maliciousBrief, "sourceQuoteAddressee", "") == "Tester", null);
                    var reboundDraft = GossipFields("body", "Rhagaea, then let him hear whose table you chose.",
                        "sourceQuote", privateTalk, "distortionSeverity", "invented_detail", "account", "firsthand");
                    check("malicious_recipient_cannot_replace_omitted_player", !ValidatePersonalGossipComposition(c, malicious, ReviewedFixture(malicious, reboundDraft), false), null);
                    var namedReboundDraft = new Dictionary<string, object>(reboundDraft) { ["body"] = "Tester heard me say I spoke privately with you about the market." };
                    check("malicious_player_name_does_not_license_rebound_source_you", !ValidatePersonalGossipComposition(c, malicious, ReviewedFixture(malicious, namedReboundDraft), false), null);
                    var falseNight = GossipFields("body", "Tester spent the night with me, Rhagaea.", "sourceQuote", privateTalk,
                        "distortionSeverity", "invented_detail", "account", "firsthand");
                    check("malicious_false_night_keeps_people_and_source_truth", ValidatePersonalGossipComposition(c, malicious, ReviewedFixture(malicious, falseNight), false)
                        && ReadString(malicious, "sourceQuote", "") == privateTalk && !privateTalk.Contains("night"), null);
                    var attributedFalseNight = new Dictionary<string, object>(falseNight) { ["body"] = "I told Tester: \u201c" + privateTalk + "\u201d Tester spent the night with me." };
                    check("malicious_explicit_original_quote_keeps_its_addressee", ValidatePersonalGossipComposition(c, malicious, ReviewedFixture(malicious, attributedFalseNight), false), null);
                    var substringDraft = new Dictionary<string, object>(falseNight) { ["body"] = "Detester spent the night with me." };
                    check("malicious_subject_reference_requires_whole_label", !ValidatePersonalGossipComposition(c, malicious, ReviewedFixture(malicious, substringDraft), false), null);
                    var ambiguousMalice = new Dictionary<string, object>(malicious) { ["sourceQuoteSpeakerId"] = "" };
                    check("ambiguous_malicious_source_refuses_without_provider", ReadString(ComposePersonalGossip(ambiguousMalice, false), "error", "").Length > 0
                        && !ValidatePersonalGossipComposition(c, ambiguousMalice, falseNight, false), null);
                    var playerMalice = new Dictionary<string, object>(malicious) { ["sourceQuoteSpeakerId"] = "tester" };
                    var playerMaliceBrief = PersonalGossipCompositionBrief(playerMalice);
                    check("malicious_player_source_keeps_npc_addressee", ReadString(playerMaliceBrief, "originalSourceFrame", "").StartsWith("Tester told me this:", StringComparison.Ordinal)
                        && ReadString(playerMaliceBrief, "sourceQuoteAddressee", "") == "Ira", null);
                    var anonymousMalice = new Dictionary<string, object>(malicious) { ["playerName"] = "HiddenCanonicalName",
                        ["playerReference"] = GossipFields("label", "the armed stranger", "named", false, "identityState", "encountered_unknown") };
                    var anonymousNight = new Dictionary<string, object>(falseNight) { ["body"] = "The armed stranger spent the night with me, Rhagaea." };
                    check("malicious_unknown_player_uses_only_accessible_label", ValidatePersonalGossipComposition(c, anonymousMalice, ReviewedFixture(anonymousMalice, anonymousNight), false)
                        && !Json.Serialize(PersonalGossipCompositionBrief(anonymousMalice)).Contains("HiddenCanonicalName"), null);
                    EnsureIdentitySchema(c);
                    var hiddenCanonical = GossipFields("heroStringId", "player", "name", "SecretCanonicalName");
                    var unidentified = GossipPlayerReference(c, "source", "player", hiddenCanonical, GossipFields());
                    consultation["playerName"] = ReadString(unidentified, "label", "");
                    consultation["playerReference"] = unidentified;
                    var unknownBrief = PersonalGossipCompositionBrief(consultation);
                    check("unknown_subject_reference_does_not_leak_canonical_name_or_key", !ReadBool(unidentified, "named", true)
                        && !Json.Serialize(unknownBrief).Contains("SecretCanonicalName") && !unknownBrief.ContainsKey("playerId")
                        && !PersonalGossipImpressionBody(consultation).Contains("SecretCanonicalName"), null);
                    var anonymousQuote = QuestionFixture(consultation);
                    check("source_frame_uses_only_communicated_anonymous_label", !Json.Serialize(anonymousQuote).Contains("SecretCanonicalName")
                        && ReadString(anonymousQuote, "body", "").StartsWith(ReadString(unidentified, "label", "") + " told me this:", StringComparison.Ordinal), null);
                    check("legacy_raw_name_is_not_accessible_identity", !Json.Serialize(PersonalGossipCompositionBrief(GossipFields("playerName", "SecretCanonicalName"))).Contains("SecretCanonicalName")
                        && !Json.Serialize(PersonalGossipReplyEvidenceBrief(consultation, GossipFields("speakerId", "tester", "text", "We discussed wool."))).Contains("tester"), null);
                    check("unidentified_report_does_not_identify_current_player", !GossipSubjectRecognized(c, unidentified, "target", "player", false)
                        && GossipSubjectRecognized(c, unidentified, "source", "player", true), null);
                    ExecuteSql(c, @"INSERT INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,claimed_name)
VALUES('source','player','claimed','SecretCanonicalName','TravelingAlias'),('target','player','claimed','SecretCanonicalName','DifferentAlias');");
                    var claimed = GossipPlayerReference(c, "source", "player", hiddenCanonical, GossipFields());
                    check("claimed_name_is_not_upgraded_to_canonical", ReadString(claimed, "label", "") == "TravelingAlias"
                        && ReadString(claimed, "identityState", "") == "claimed" && !Json.Serialize(claimed).Contains("SecretCanonicalName"), null);
                    check("unmatched_alias_does_not_nominate_current_player", !GossipSubjectRecognized(c, claimed, "target", "player", false), null);
                    ExecuteSql(c, "UPDATE acquaintances SET claimed_name='TravelingAlias' WHERE observer_id='target' AND subject_id='player';");
                    check("communicated_alias_matches_accessible_recipient_identity", GossipSubjectRecognized(c, claimed, "target", "player", false), null);
                    ExecuteSql(c, "UPDATE acquaintances SET identity_state='verified' WHERE observer_id='source' AND subject_id='player';");
                    var verified = GossipPlayerReference(c, "source", "player", hiddenCanonical, GossipFields());
                    check("verified_speaker_can_use_canonical_name", ReadString(verified, "label", "") == "SecretCanonicalName", null);
                    ExecuteSql(c, "DELETE FROM acquaintances WHERE subject_id='player';");
                    foreach (string profile in new[] { "excitable", "reserved", "honest_loyal", "vengeful" })
                    {
                        var foundation = CoreTraitKeys.ToDictionary(k => k, _ => (object)0);
                        foundation["courage"] = 1; foundation["mercy"] = -1; foundation["generosity"] = 2;
                        var profileTraits = GossipFields("foundationTraits", foundation);
                        EnsureTraitPercentageData(profileTraits, "profile_fixture");
                        ApplyPersonalGossipTraitProfile(profileTraits, profile);
                        var expected = new Dictionary<string, object>(ReadDictionary(profileTraits, "traitPercentages"));
                        MarkEditorFoundationTraitsCurrent(profileTraits);
                        EnsureTraitPercentageData(profileTraits, "profile_fixture");
                        EnsureTraitPercentageData(profileTraits, "profile_fixture");
                        check("fixed_profile_survives_normalization_" + profile, expected.All(p => ReadInt(ReadDictionary(profileTraits, "traitPercentages"), p.Key, -1) == Convert.ToInt32(p.Value))
                            && ReadInt(ReadDictionary(profileTraits, "foundationTraits"), "courage", 0) == 1
                            && ReadInt(ReadDictionary(profileTraits, "foundationTraits"), "mercy", 0) == -1
                            && ReadInt(ReadDictionary(profileTraits, "foundationTraits"), "generosity", 0) == 2, null);
                        SavePersonalGossipTraitProfile(c, campaign, "profile_fixture", profileTraits);
                        var openedProfile = MaterializeCanonicalProfile(campaign, "profile_fixture", GossipFields("heroStringId", "profile_fixture", "name", "Profile Fixture"), GossipFields(), false, false);
                        var afterOpen = ReadJsonObject(CharacterFile(campaign, "profile_fixture", "traits.json"));
                        check("fixed_profile_survives_canonical_open_" + profile, ReadString(openedProfile, "status", "") == "editor_authoritative"
                            && expected.All(p => ReadInt(ReadDictionary(afterOpen, "traitPercentages"), p.Key, -1) == Convert.ToInt32(p.Value))
                            && QuerySql(c, "SELECT command_id FROM character_editor_native_commands WHERE hero_id='profile_fixture';").Count == 0, openedProfile);
                    }
                    // Reproduce the native failure: a sparse shipped character has
                    // constructed/trait files but no expanded narrative before editing.
                    string editedHero = "lord_1_37";
                    var shippedEditorProfile = LoadCharacterProfileLibrary()[editedHero];
                    var editorRuntime = DeepCloneProfileDictionary(ReadDictionary(shippedEditorProfile, "sourceFacts"));
                    editorRuntime["heroStringId"] = editedHero;
                    editorRuntime["narrativeVersion"] = 5;
                    WriteJsonObject(CharacterFile(campaign, editedHero, "profile.json"), editorRuntime);
                    WriteJsonObject(CharacterFile(campaign, editedHero, "constructed.json"), GossipFields("ok", true, "status", "canonical_ready"));
                    foreach (string profile in new[] { "excitable", "reserved", "honest_loyal", "vengeful" })
                    {
                        var editedTraits = GossipFields("foundationTraits", CoreTraitKeys.ToDictionary(k => k, _ => (object)0));
                        EnsureTraitPercentageData(editedTraits, editedHero);
                        ApplyPersonalGossipTraitProfile(editedTraits, profile);
                        var expected = new Dictionary<string, object>(ReadDictionary(editedTraits, "traitPercentages"));
                        SavePersonalGossipTraitProfile(c, campaign, editedHero, editedTraits);
                        var ready = EnsureCharacterConstructed(campaign, editedHero, editorRuntime, "gossip_fixture_first_contact_test");
                        var afterContact = ReadJsonObject(CharacterFile(campaign, editedHero, "traits.json"));
                        check("fixed_profile_full_first_contact_ready_" + profile, IsCharacterConstructionReady(ready)
                            && NarrativeDocumentReady(ReadJsonObject(CharacterFile(campaign, editedHero, "narrative.json")))
                            && expected.All(p => ReadInt(ReadDictionary(afterContact, "traitPercentages"), p.Key, -1) == Convert.ToInt32(p.Value))
                            && QuerySql(c, "SELECT command_id FROM character_editor_native_commands WHERE hero_id=$hero;", GossipFields("hero", editedHero)).Count == 0, ready);
                    }
                    check("provider_failure_never_becomes_placeholder_reply", !ValidatePersonalGossipComposition(c, GossipFields(), failure, true), null);
                    var inventedReply = GossipFields("body", "Yes, I spent the night with Traveler.", "sourceQuote", "", "answerKind", "acknowledged");
                    check("unsupported_reply_cannot_invent_answer", ValidatePersonalGossipComposition(c, GossipFields(), inventedReply, true)
                        && ReadString(inventedReply, "body", "") == "I will think about what you said.", null);
                }
                WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), excitable);
                string retryId = GossipTestSeed("retry_", id => GossipRoll(id, "share") <= 85);
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipAcceptPresence(c, presence(60));
                    EvaluatePersonalGossip(c, campaign, NewTopic(c, retryId, 60, false), people);
                }
                ComposeAndCapture((data, reply) => GossipFields("error", "Injected failure"));
                ComposeAndCapture((data, reply) => GossipFields("error", "Injected failure"));
                using (var c = OpenCampaignConnection(campaign))
                {
                    var failed = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", retryId + "_out")).Single();
                    check("three_attempts_keep_recipient_and_no_history", ReadInt(failed, "attempts", 0) == 3 && ReadString(failed, "status", "") == "generation_failed"
                        && ReadString(failed, "recipient_id", "") == "target" && QuerySql(c, "SELECT * FROM personal_gossip_history WHERE root_id=$id;", GossipFields("id", retryId)).Count == 0, null);
                }
                string generationId = GossipTestSeed("generation_", id => GossipRoll(id, "share") <= 85);
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipAcceptPresence(c, presence(65));
                    EvaluatePersonalGossip(c, campaign, NewTopic(c, generationId, 65, false), people);
                }
                int compositions = 0;
                ComposeAndCapture((data, reply) =>
                {
                    if (ReadString(data, "id", "") == generationId && ++compositions == 1) AdvanceMemoryRestoreGeneration(campaign);
                    return Compose(data, reply);
                });
                using (var c = OpenCampaignConnection(campaign))
                {
                    var generated = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", generationId + "_out")).Single();
                    check("restore_generation_discards_stale_result", compositions == 2 && ReadInt(generated, "attempts", 0) == 2
                        && ReadString(generated, "status", "") == "in_transit", null);
                }

                var third = GossipTestHero("third"); people.Add(third);
                WriteJsonObject(CharacterFile(campaign, "target", "traits.json"), excitable);
                string retellId = GossipTestSeed("retell_", id => GossipRoll(id, "share") <= 85
                    && GossipRoll(id + "_via_" + PromptHash("target").Substring(0, 12).ToLowerInvariant(), "share") <= 25);
                using (var c = OpenCampaignConnection(campaign))
                {
                    GossipTestPair(c, "target", "third", 60, 30);
                    GossipAcceptPresence(c, presence(80));
                    var topic = NewTopic(c, retellId, 80, false);
                    ReadDictionary(topic, "signal")["question"] = false;
                    EvaluatePersonalGossip(c, campaign, topic, people);
                }
                Dictionary<string, object> ComposeRetell(Dictionary<string, object> data, bool reply)
                {
                    if (!reply) return PersonalGossipFramedSource(data) ?? GossipFields("body", (ReadInt(data, "hop", 1) > 1 ? "Source said: " : "") + ReadString(data, "sourceQuote", ""), "sourceQuote", ReadString(data, "sourceQuote", ""),
                        "distortionSeverity", "none", "account", ReadInt(data, "hop", 1) > 1 ? "secondhand" : "firsthand");
                    return GossipFields("body", "That is interesting news.", "sourceQuote", "", "answerKind", "acknowledged", "gossipSignals", new[] {
                        GossipFields("confidence", 1d, "evidenceQuote", ReadString(data, "receivedBody", ""), "excitement", 100, "privacy", "public", "existencePrivacy", "public") });
                }
                ComposeAndCapture(ComposeRetell);
                PersonalGossipTickApi(presence(80.05));
                ComposeAndCapture(ComposeRetell);
                PersonalGossipTickApi(presence(80.10));
                ComposeAndCapture(ComposeRetell);
                using (var c = OpenCampaignConnection(campaign))
                {
                    var outward = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE root_id=$root AND is_reply=0;", GossipFields("root", retellId));
                    check("one_onward_hop_and_no_loop", outward.Count == 2 && outward.Any(r => ReadString(r, "recipient_id", "") == "third" && ReadInt(r, "hop", 0) == 2)
                        && outward.All(r => ReadString(r, "recipient_id", "") != "source"), outward.Select(r => ReadString(r, "recipient_id", "")).ToList());
                    var pastLimit = NewTopic(c, "past_hop_limit", 80, true); pastLimit["hop"] = 3;
                    EvaluatePersonalGossip(c, campaign, pastLimit, people);
                    var expired = NewTopic(c, "expired_root", 80, true); expired["rootCreatedDay"] = 60;
                    EvaluatePersonalGossip(c, campaign, expired, people);
                    check("hop_and_root_expiry_prevent_new_jobs", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id IN ('past_hop_limit','expired_root');").Count == 0, null);
                    string sharedSession = "capacity_conversation";
                    for (int i = 0; i < 4; i++)
                    {
                        var cappedTopic = NewTopic(c, GossipTestSeed("capacity" + i + "_", id => GossipRoll(id, "share") <= 85), 90, false);
                        if (i < 3) cappedTopic["sessionId"] = sharedSession;
                        EvaluatePersonalGossip(c, campaign, cappedTopic, people);
                    }
                    var daily = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE speaker_id='source' AND is_reply=0 AND created_day=90;");
                    check("two_per_conversation_three_per_day", daily.Count == 3 && QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE session_id=$session AND status='queued';", GossipFields("session", sharedSession)).Count == 2, null);

                    double acceptedDay = 100;
                    while (GossipRoll(GossipTopicId("main", "source", "player", acceptedDay, "conversation", ""), "share") > 85) acceptedDay++;
                    var acceptedTopic = NewTopic(c, "accepted_hook", acceptedDay, false);
                    var acceptedPayload = presence(acceptedDay);
                    acceptedPayload["playerHeroStringId"] = "player";
                    var acceptedExchange = GossipFields("sessionId", ReadString(acceptedTopic, "sessionId", ""),
                        "exchangeId", ReadString(acceptedTopic, "sourceExchangeId", ""), "turnIds", ReadStringList(acceptedTopic, "sourceTurnIds"));
                    var acceptedParsed = GossipFields("gossipSignals", new[] { ReadDictionary(acceptedTopic, "signal") });
                    for (int i = 0; i < 3; i++) AcceptPersonalGossip(campaign, acceptedPayload, acceptedParsed, acceptedExchange, "source", "player",
                        ReadString(acceptedTopic, "sourceText", ""), "That was an interesting conversation.");
                    string acceptedId = GossipTopicId("main", "source", "player", acceptedDay, "conversation", "");
                    check("accepted_exchange_hook_is_idempotent", QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", acceptedId)).Count == 1, null);

                    // Private storage identities protect the source. The enclosing conversation
                    // still owns its capacity and the native adjudication's exchange identity.
                    string[] categories = { "family", "politics", "gift" };
                    double whisperDay = 140;
                    while (categories.Any(category => GossipRoll(GossipTopicId("main", "source", "player", whisperDay, category, ""), "share") > 85)) whisperDay++;
                    var parent = NewTopic(c, "whisper_parent", whisperDay, false);
                    string parentSession = ReadString(parent, "sessionId", "");
                    bool privateReferencesPreserved = true;
                    for (int i = 0; i < categories.Length; i++)
                    {
                        var privateTopic = NewTopic(c, "private_turn_" + i, whisperDay, false);
                        ReadDictionary(privateTopic, "signal")["topic"] = categories[i];
                        var publicExchange = StoreConversationExchange(campaign,
                            GossipFields("conversationSessionId", parentSession, "worldDay", whisperDay, "timelineId", "main", "channel", "party_chat"),
                            "source", "player", "Traveler", "Source", "The player speaks quietly to Source.", "Source replies privately.",
                            "parent_whisper_exchange_" + i, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        var privateExchange = GossipFields("sessionId", ReadString(privateTopic, "sessionId", ""),
                            "exchangeId", ReadString(privateTopic, "sourceExchangeId", ""), "turnIds", ReadStringList(privateTopic, "sourceTurnIds"));
                        AcceptPersonalGossip(campaign, presence(whisperDay), GossipFields("gossipSignals", new[] { ReadDictionary(privateTopic, "signal") }),
                            privateExchange, "source", "player", ReadString(privateTopic, "sourceText", ""), "That was an interesting conversation.", publicExchange);
                        var decision = GossipJson(QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;",
                            GossipFields("id", GossipTopicId("main", "source", "player", whisperDay, categories[i], ""))).Single());
                        privateReferencesPreserved &= ReadString(decision, "sourceSessionId", "") == ReadString(privateTopic, "sessionId", "")
                            && ReadString(decision, "sessionId", "") == parentSession
                            && ReadStringList(decision, "sourceTurnIds").SequenceEqual(ReadStringList(privateTopic, "sourceTurnIds"));
                    }
                    check("private_whispers_share_parent_conversation_cap", QuerySql(c,
                        "SELECT topic_id FROM personal_gossip_topics WHERE session_id=$session AND status='queued';", GossipFields("session", parentSession)).Count == 2,
                        QuerySql(c, "SELECT topic_id,status FROM personal_gossip_topics WHERE session_id=$session;", GossipFields("session", parentSession)));
                    check("private_whisper_source_ids_remain_private", privateReferencesPreserved, null);

                    double receiptDay = 160;
                    while (GossipRoll(GossipTopicId("main", "source", "player", receiptDay, "impression", ""), "share") > 85
                        || GossipRoll(GossipTopicId("main", "source", "player", receiptDay, "impression", ""), "disclosure") > 80) receiptDay++;
                    var receiptParent = NewTopic(c, "receipt_parent", receiptDay, false);
                    var receiptPrivate = NewTopic(c, "receipt_private", receiptDay, false);
                    var receiptEpisode = GossipFields("sessionId", ReadString(receiptParent, "sessionId", ""),
                        "exchangeId", ReadString(receiptParent, "sourceExchangeId", ""));
                    var receiptSource = GossipFields("sessionId", ReadString(receiptPrivate, "sessionId", ""),
                        "exchangeId", ReadString(receiptPrivate, "sourceExchangeId", ""), "turnIds", ReadStringList(receiptPrivate, "sourceTurnIds"));
                    GossipTestPair(c, "source", "player", 0, 0);
                    var receiptPrivacy = GossipFields("topic", "impression", "evidenceQuote", ReadString(receiptPrivate, "sourceText", ""),
                        "confidence", 1d, "privacy", "public", "existencePrivacy", "public", "promisedSecrecy", false, "ownCompromising", false);
                    AcceptPersonalGossip(campaign, presence(receiptDay), GossipFields("gossipSignals", new[] { receiptPrivacy }), receiptSource, "source", "player",
                        ReadString(receiptPrivate, "sourceText", ""), "That was an interesting conversation.", receiptEpisode);
                    string receiptTopicId = GossipTopicId("main", "source", "player", receiptDay, "impression", "");
                    bool noProposal = QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", receiptTopicId)).Count == 0;
                    ExecuteSql(c, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,native_application_status,created_ts)
VALUES('whisper_receipt',$campaign,'main',$exchange,'source','player',5,'applied','pending',1);",
                        GossipFields("campaign", campaign, "exchange", ReadString(receiptParent, "sourceExchangeId", "")));
                    PersonalGossipRelationshipApplied(campaign, GossipFields("exchangeId", ReadString(receiptParent, "sourceExchangeId", ""), "worldDay", receiptDay));
                    var receiptJobs = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", receiptTopicId));
                    check("parent_receipt_activates_private_impression", noProposal && receiptJobs.Count == 1
                        && ReadString(GossipJson(receiptJobs[0]), "type", "") == "impression"
                        && ReadStringList(GossipJson(receiptJobs[0]), "sourceTurnIds").SequenceEqual(ReadStringList(receiptPrivate, "sourceTurnIds")),
                        QuerySql(c, "SELECT status,payload_json FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", receiptTopicId)));

                    // Native source014: visible confidentiality was promised, optional
                    // metadata was absent, and an applied +3 receipt leaked an impression.
                    // Use the accepted-source path with no provider or live campaign.
                    double privacyDay = receiptDay + 20;
                    var discreet = GossipTestTraits("sociability", 75, "curiosity", 75, "impulsiveness", 75, "pride", 75,
                        "honesty", 100, "loyalty", 100, "tact", 100, "discipline", 100, "empathy", 100);
                    WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), discreet);
                    GossipTestPair(c, "source", "target", 60, 60);
                    GossipTestPair(c, "source", "player", 100, 100);
                    while (GossipRoll(GossipTopicId("main", "source", "player", privacyDay, "impression", ""), "share") > 55
                        || GossipRoll(GossipTopicId("main", "source", "player", privacyDay, "impression", ""), "disclosure") > 53) privacyDay++;
                    const string confidenceText = "I am sometimes sick with fear before a battle. Please promise to tell nobody, even that we had this conversation.";
                    const string promiseText = "Not a word of it leaves this yard. That I swear.";
                    var confidenceTopic = NewTopic(c, "source014_confidence", privacyDay, false, sourceText: confidenceText, npcReply: promiseText);
                    var confidenceExchange = GossipFields("sessionId", ReadString(confidenceTopic, "sessionId", ""),
                        "exchangeId", ReadString(confidenceTopic, "sourceExchangeId", ""), "turnIds", ReadStringList(confidenceTopic, "sourceTurnIds"));
                    ExecuteSql(c, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,native_application_status,created_ts)
VALUES('source014_receipt',$campaign,'main',$exchange,'source','player',3,'applied','pending',1);",
                        GossipFields("campaign", campaign, "exchange", ReadString(confidenceTopic, "sourceExchangeId", "")));
                    AcceptPersonalGossip(campaign, presence(privacyDay), GossipFields(), confidenceExchange, "source", "player", confidenceText, promiseText);
                    string confidenceId = GossipTopicId("main", "source", "player", privacyDay, "impression", "");
                    var unclassifiedRow = QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", confidenceId)).Single();
                    var unclassified = GossipJson(unclassifiedRow);
                    check("missing_privacy_cannot_leak_receipt_impression", ReadString(unclassifiedRow, "status", "") == "privacy_unclassified"
                        && QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", confidenceId)).Count == 0,
                        unclassifiedRow);
                    PersonalGossipRelationshipApplied(campaign, GossipFields("exchangeId", ReadString(confidenceTopic, "sourceExchangeId", ""), "worldDay", privacyDay));
                    check("relationship_ack_cannot_bypass_missing_privacy", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id=$id;",
                        GossipFields("id", confidenceId)).Count == 0, null);
                    var secretPrivacy = GossipFields("topic", "impression", "evidenceQuote", confidenceText, "confidence", 1d,
                        "privacy", "secret", "existencePrivacy", "secret", "promisedSecrecy", true, "ownCompromising", false, "confidenceOwnerIds", new[] { "player" });
                    bool malformedRejected = true;
                    foreach (string field in new[] { "privacy", "existencePrivacy", "promisedSecrecy", "ownCompromising" })
                    {
                        var missing = new Dictionary<string, object>(secretPrivacy); missing.Remove(field);
                        malformedRejected &= !GossipSourcePrivacyClassified(missing);
                    }
                    var invalid = new Dictionary<string, object>(secretPrivacy); invalid["existencePrivacy"] = "unclassified";
                    var stringPromise = new Dictionary<string, object>(secretPrivacy); stringPromise["promisedSecrecy"] = "true";
                    check("privacy_requires_valid_categories_and_explicit_booleans", malformedRejected
                        && !GossipSourcePrivacyClassified(invalid) && !GossipSourcePrivacyClassified(stringPromise), null);
                    AcceptPersonalGossip(campaign, presence(privacyDay), GossipFields("gossipSignals", new[] { secretPrivacy }),
                        confidenceExchange, "source", "player", confidenceText, promiseText);
                    var classifiedRow = QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", confidenceId)).Single();
                    var classified = GossipJson(classifiedRow);
                    check("classified_confidence_keeps_zero_disclosure_and_saved_draws", ReadString(classifiedRow, "status", "") == "privacy_declined"
                        && ReadInt(classified, "detailChance", -1) == 0
                        && Json.Serialize(ReadDictionary(unclassified, "savedRolls")) == Json.Serialize(ReadDictionary(classified, "savedRolls"))
                        && QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", confidenceId)).Count == 0,
                        classifiedRow);

                    double ownerDay = privacyDay + 10;
                    var loyalConfidant = GossipTestTraits("honesty", 0, "loyalty", 100, "tact", 0, "discipline", 0,
                        "empathy", 0, "impulsiveness", 100, "sociability", 100, "pride", 100, "curiosity", 100);
                    WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), loyalConfidant);
                    string ownerId = GossipTopicId("main", "source", "player", ownerDay, "gift", "");
                    var ownerSeed = NewTopic(c, ownerId, ownerDay, false, sourceText: entrustedPlan, npcReply: promiseText);
                    var ownerMetadata = GossipFields("topic", "gift", "evidenceQuote", entrustedPlan, "confidence", 1d,
                        "excitement", 50, "curiosity", 0, "hurt", 0, "privacy", "secret", "existencePrivacy", "secret",
                        "promisedSecrecy", true, "ownCompromising", false, "question", false, "confidenceOwnerIds", new[] { "source" });
                    ownerSeed["signal"] = ownerMetadata;
                    ownerSeed["operatorTestControls"] = GossipFields("fixtureId", "isolated_confidence_owner",
                        "draws", GossipFields("share", 1, "negative", 100, "disclosure", 30, "distortion", 100,
                            "severity", 100, "negative_recipient", 0d, "friendly_recipient", 0d), "compositionMode", "production");
                    SavePersonalGossipTopic(c, ownerSeed, "awaiting_source_classification");
                    var ownerExchange = GossipFields("sessionId", ReadString(ownerSeed, "sessionId", ""),
                        "exchangeId", ReadString(ownerSeed, "sourceExchangeId", ""), "turnIds", ReadStringList(ownerSeed, "sourceTurnIds"));
                    AcceptPersonalGossip(campaign, presence(ownerDay), GossipFields("gossipSignals", new[] { ownerMetadata }),
                        ownerExchange, "source", "player", entrustedPlan, promiseText);
                    var ownerRow = QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", ownerId)).Single();
                    var ownerDecision = GossipJson(ownerRow);
                    check("loyalty_protects_actual_player_confidence_through_acceptance", ReadString(ownerRow, "status", "") == "privacy_declined"
                        && ReadInt(ownerDecision, "detailChance", -1) == 22 && ReadInt(ownerDecision, "disclosureRoll", -1) == 30
                        && ReadStringList(ReadDictionary(ownerDecision, "signal"), "confidenceOwnerIds").Contains("player")
                        && GossipDetailChance(loyalConfidant, "secret", 50, 60, 0, false, true, 0) == 52
                        && QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id=$id;", GossipFields("id", ownerId)).Count == 0,
                        GossipFields("status", ReadString(ownerRow, "status", ""), "detailChance", ReadInt(ownerDecision, "detailChance", -1),
                            "disclosureRoll", ReadInt(ownerDecision, "disclosureRoll", -1),
                            "owners", ReadStringList(ReadDictionary(ownerDecision, "signal"), "confidenceOwnerIds")));
                    string retainedOwnerRolls = Json.Serialize(ReadDictionary(ownerDecision, "savedRolls"));
                    AcceptPersonalGossip(campaign, presence(ownerDay), GossipFields("gossipSignals", new[] { ownerMetadata }),
                        ownerExchange, "source", "player", entrustedPlan, promiseText);
                    var ownerRetry = GossipJson(QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", ownerId)).Single());
                    check("confidence_owner_retry_preserves_source_and_saved_decision", retainedOwnerRolls == Json.Serialize(ReadDictionary(ownerRetry, "savedRolls"))
                        && ReadString(ownerRetry, "sourceQuote", "") == entrustedPlan
                        && ReadString(ownerRetry, "sourceText", "") == entrustedPlan + "\n" + promiseText
                        && ReadStringList(ownerRetry, "sourceTurnIds").SequenceEqual(ReadStringList(ownerSeed, "sourceTurnIds")), null);
                    var aliasPlayer = GossipTestHero("confidence_player_id");
                    GossipTestPair(c, "source", "confidence_player_id", 80, -10);
                    check("reply_confidence_alias_uses_contextual_player_and_directional_bond", GossipConfidenceBond(c, source,
                        people.Concat(new[] { aliasPlayer }).ToList(), new List<string> { "player", "self" }, "confidence_player_id") == 80, null);
                    check("confidence_owner_absence_does_not_erase_persisted_loyalty", GossipConfidenceBond(c, source,
                        people, new List<string> { "confidence_player_id" }, "player") == 80, null);
                    string ownerReplyId = GossipTestSeed("owner_reply_", id => GossipRoll(id, "reply_disclosure") > 22 && GossipRoll(id, "reply_disclosure") <= 52);
                    var ownerReplyData = GossipFields("messageId", ownerReplyId, "playerId", "player", "speaker", source,
                        "recipient", target, "traits", loyalConfidant, "timelineId", "main", "policy", DefaultPersonalGossipSettings(),
                        "recipientMemory", entrustedPlan, "replyEvidence", new[] { GossipFields("speakerId", "player", "text", entrustedPlan) });
                    var ownerReply = GossipFields("body", entrustedPlan, "sourceQuote", entrustedPlan, "answerKind", "answered",
                        "privacy", "secret", "existencePrivacy", "secret", "promisedSecrecy", true, "ownCompromising", false,
                        "feeling", 50, "confidenceOwnerIds", new[] { "source" });
                    check("reply_source_provenance_restores_player_loyalty_before_disclosure", ValidatePersonalGossipComposition(c, ownerReplyData, ownerReply, true)
                        && ReadString(ownerReply, "answerKind", "") == "refused"
                        && ReadString(ownerReply, "body", "") == "I would rather keep that to myself."
                        && ReadStringList(ownerReply, "confidenceOwnerIds").Contains("player"), ownerReply);

                    double optionsDay = privacyDay + 100;
                    WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), excitable);
                    GossipTestPair(c, "source", "target", 60, 60);
                    Dictionary<string, object> TunedTopic(string id, double day, int adjustment, bool malicious = false)
                    {
                        var value = NewTopic(c, id, day, false, malicious);
                        var tunedPolicy = DefaultPersonalGossipSettings();
                        tunedPolicy["chanceAdjustments"] = PersonalGossipChanceLabels.Keys.ToDictionary(k => k, _ => (object)adjustment);
                        value["policy"] = tunedPolicy;
                        return value;
                    }
                    var guaranteed = TunedTopic("options_guaranteed", optionsDay, 100);
                    EvaluatePersonalGossip(c, campaign, guaranteed, people);
                    check("options_guarantee_eligible_original", ReadInt(guaranteed, "shareChance", 0) == 100
                        && ReadInt(guaranteed, "detailChance", 0) == 100 && ReadString(guaranteed, "type", "") == "positive"
                        && QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='options_guaranteed';").Count == 1, null);
                    var blocked = TunedTopic("options_suppressed", optionsDay + 2, -100);
                    EvaluatePersonalGossip(c, campaign, blocked, people);
                    check("options_zero_prevents_original", ReadInt(blocked, "shareChance", -1) == 0
                        && QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='options_suppressed';").Count == 0, null);
                    var unclassifiedTuned = TunedTopic("options_unclassified", optionsDay + 4, 100);
                    ReadDictionary(unclassifiedTuned, "signal")["privacyUnclassified"] = true;
                    EvaluatePersonalGossip(c, campaign, unclassifiedTuned, people);
                    var noAudienceTuned = TunedTopic("options_no_audience", optionsDay + 6, 100);
                    EvaluatePersonalGossip(c, campaign, noAudienceTuned, new List<Dictionary<string, object>> { source, player });
                    check("options_cannot_bypass_privacy_or_audience", QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id IN ('options_unclassified','options_no_audience');").Count == 0, null);
                    GossipTestPair(c, "source", "target", -60, -60);
                    WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), GossipTestTraits("honesty", 0, "vengefulness", 100));
                    var tunedMalice = TunedTopic("options_malicious", optionsDay + 8, 100, true);
                    EvaluatePersonalGossip(c, campaign, tunedMalice, people);
                    check("options_guarantee_supported_malice_and_distortion", ReadString(tunedMalice, "type", "") == "negative"
                        && ReadInt(tunedMalice, "negativeChance", 0) == 100 && ReadInt(tunedMalice, "distortionChance", 0) == 100
                        && ReadString(tunedMalice, "distortion", "none") != "none", null);
                    WriteJsonObject(CharacterFile(campaign, "source", "traits.json"), GossipTestTraits("honesty", 100));
                    var honestTuned = TunedTopic("options_honest", optionsDay + 10, 100, true);
                    EvaluatePersonalGossip(c, campaign, honestTuned, people);
                    check("options_cannot_make_completely_honest_npc_fabricate", ReadString(honestTuned, "type", "") == "negative"
                        && ReadInt(honestTuned, "distortionChance", -1) == 0 && ReadString(honestTuned, "distortion", "") == "none", null);
                }
            }
            finally
            {
                ReignPostgreSqlStorage.DropSnapshot(campaign, snapshotId);
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
        }
    }
}
