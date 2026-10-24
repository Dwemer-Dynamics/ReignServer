using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void AddGovernmentParticipationSelfTests(Action<string, bool, string, object> add)
        {
            var traits = CoreTraitKeys.ToDictionary(x => x, x => 50);
            var profile = GovernmentPoliticalBeliefs.Seed("participation_test", "merchant_a", "timeline_a", traits,
                new Dictionary<ReignGovernmentPlank, double> { [ReignGovernmentPlank.Trade] = 1 }, false);
            var repeated = GovernmentPoliticalBeliefs.Seed("participation_test", "merchant_a", "timeline_a", traits,
                new Dictionary<ReignGovernmentPlank, double> { [ReignGovernmentPlank.Trade] = 1 }, false);
            add("government.participation_beliefs_persist_all_values", GovernmentPoliticalBeliefs.Valid(profile)
                && JsonSerializer.Serialize(profile) == JsonSerializer.Serialize(repeated)
                && GovernmentPoliticalBeliefs.Priorities(profile).Count == 16,
                "As a player, I reload and find the same character still holds the same complete political outlook.", profile);
            var peers = Enumerable.Range(0, 100).Select(i => GovernmentPoliticalBeliefs.Seed("participation_test", "peer_" + i, "timeline_a", traits,
                new Dictionary<ReignGovernmentPlank, double> { [ReignGovernmentPlank.NoblePrivilege] = 1 }, false)).ToList();
            add("government.participation_same_station_individual_variation", peers.Select(x => string.Join(",", x.Values.Select(v => v.Stance))).Distinct().Count() == 100
                && peers.Any(x => x.Values.Single(v => v.Value == ReignGovernmentPlank.NoblePrivilege).Stance < 0),
                "I meet nobles with different priorities; their social class does not dictate their opinions.", null);
            var neutral = JsonSerializer.Deserialize<GovernmentPoliticalProfile>(JsonSerializer.Serialize(profile));
            foreach (var value in neutral.Values) value.Stance = 0;
            var tradeParty = new[] { ReignGovernmentPlank.Trade, ReignGovernmentPlank.Peace };
            add("government.participation_party_signed_normalized_matching", GovernmentPoliticalBeliefs.PartyCompatibility(neutral, tradeParty) == 0
                && peers.All(x => Math.Abs(GovernmentPoliticalBeliefs.PartyCompatibility(x, tradeParty)) <= 100),
                "My neutral declaration is not automatically assigned to the longest party platform.", null);
            foreach (var value in neutral.Values) { value.Stance = value.Value == ReignGovernmentPlank.Trade ? 100 : 0; value.Importance = 100; }
            add("government.participation_ordered_party_priorities", GovernmentPoliticalBeliefs.PartyCompatibility(neutral, tradeParty)
                > GovernmentPoliticalBeliefs.PartyCompatibility(neutral, tradeParty.Reverse().ToArray()),
                "Someone whose defining concern is commerce prefers a party that prioritizes commerce over one listing it second.", null);
            bool edicts = GovernmentEdictRules.All.All(edict => {
                var enact = GovernmentPoliticalBeliefs.EdictImpact(edict.Id, false); var repeal = GovernmentPoliticalBeliefs.EdictImpact(edict.Id, true);
                return enact.Count > 0 && enact.All(x => repeal[x.Key] == -x.Value)
                    && Math.Abs(GovernmentPoliticalBeliefs.Alignment(profile, enact)) <= 40;
            });
            add("government.participation_all_real_edicts_have_inverse_impacts", edicts,
                "I propose each existing edict and its repeal; the political assessment changes direction without changing the edict's actual effects.", null);
            var native = GovernmentPoliticalBeliefs.NativePolicyImpact(2, -.5, 1, false);
            add("government.participation_native_effects_are_normalized", native[ReignGovernmentPlank.RoyalAuthority] == 100
                && native[ReignGovernmentPlank.NoblePrivilege] == -25 && native[ReignGovernmentPlank.PopularWelfare] == 50,
                "I propose a native law and the ideological assessment uses its actual native policy weights.", native);
            var before = JsonSerializer.Serialize(profile);
            add("government.participation_mandate_effects_follow_actual_office", GovernmentPoliticalBeliefs.MandateImpact("admission", false)[ReignGovernmentPlank.RepresentativeAuthority] == 25
                && GovernmentPoliticalBeliefs.MandateImpact("recall", true)[ReignGovernmentPlank.RepresentativeAuthority] == -25
                && GovernmentPoliticalBeliefs.MandateImpact("recall", false).Count == 0,
                "I seek a civic mandate, or recall a member. Removing an ordinary notable is not falsely treated as reversing a granted civic mandate.", null);
            bool forged = GovernmentPoliticalBeliefs.Evolve(profile, "forged", "rumor", 1, false, true,
                new[] { new GovernmentPoliticalValue { Value = ReignGovernmentPlank.Peace, Stance = 100 } });
            bool first = GovernmentPoliticalBeliefs.Evolve(profile, "discussion_1", "accepted_conversation", 1, true, true,
                new[] { new GovernmentPoliticalValue { Value = ReignGovernmentPlank.Peace, Stance = 100 } });
            int after = profile.Values.Single(x => x.Value == ReignGovernmentPlank.Peace).Stance;
            bool duplicate = GovernmentPoliticalBeliefs.Evolve(profile, "discussion_1", "accepted_conversation", 1, true, true,
                new[] { new GovernmentPoliticalValue { Value = ReignGovernmentPlank.Peace, Stance = 100 } });
            add("government.participation_belief_receipts_are_bounded_and_idempotent", !forged && first && !duplicate
                && profile.Changes.Count == 1 && Math.Abs(profile.Changes[0].Stance - profile.Changes[0].PreviousStance) == 1
                && profile.Values.Single(x => x.Value == ReignGovernmentPlank.Peace).Stance == after,
                "One real conversation can make a small change. Repeating its text or replaying its receipt cannot farm conviction.", null);
            for (int i = 0; i < 30; i++) GovernmentPoliticalBeliefs.Evolve(profile, "war_" + i, "verified_war", 1, true, false,
                new[] { new GovernmentPoliticalValue { Value = ReignGovernmentPlank.Peace, Stance = 3 },
                    new GovernmentPoliticalValue { Value = ReignGovernmentPlank.Security, Importance = 3 } });
            add("government.participation_evolution_interval_limits", profile.Changes.Sum(x => Math.Abs(x.Stance - x.PreviousStance) + Math.Abs(x.Importance - x.PreviousImportance)) <= 10
                && profile.Changes.GroupBy(x => x.Value).All(group => group.Sum(x => Math.Abs(x.Stance - x.PreviousStance) + Math.Abs(x.Importance - x.PreviousImportance)) <= 5),
                "Even repeated consequential events cannot transform a person's entire belief system in one interval.", null);
            add("government.participation_saturated_events_leave_the_queue", profile.SeenEvidence.Contains("war_29")
                && !GovernmentPoliticalBeliefs.Evolve(profile, "war_29", "verified_war", 1, true, false,
                    new[] { new GovernmentPoliticalValue { Value = ReignGovernmentPlank.Peace, Stance = -3 } }),
                "After a busy session reaches a character's limit, completed events leave the queue instead of being retried forever.", null);
            var retained = JsonSerializer.Deserialize<GovernmentPoliticalProfile>(JsonSerializer.Serialize(profile));
            retained.Changes.Clear();
            string beforeLate = JsonSerializer.Serialize(retained.Values);
            GovernmentPoliticalBeliefs.Evolve(retained, "late_old_interval", "verified_war", 1, true, false,
                new[] { new GovernmentPoliticalValue { Value = ReignGovernmentPlank.Peace, Stance = -3 },
                    new GovernmentPoliticalValue { Value = ReignGovernmentPlank.Security, Importance = -3 } });
            add("government.participation_old_limits_survive_detail_compaction", beforeLate == JsonSerializer.Serialize(retained.Values)
                && retained.SeenEvidence.Contains("late_old_interval"),
                "An older event arrives after detailed history was compacted. It cannot reopen an interval's spent political change budget.", null);
            var institutionMatrix = GovernmentParticipationRules.Institutions.SelectMany(institution => Enumerable.Range(1, 5).SelectMany(level =>
                new[] { 0, 1, 12, 120, 1200 }.Select(count => new { institution.Kind, level, count,
                    valid = GovernmentParticipationRules.CanConfirmCharter(level, count), crown = GovernmentSessionRules.CrownWeight(level), member = GovernmentSessionRules.MemberWeight(level, count) }))).ToList();
            add("government.participation_all_institutions_authorities_growth", institutionMatrix.Count == 200
                && institutionMatrix.All(x => x.valid == (x.level < 5 || x.count > 0) && (x.count == 0 ? x.member == 0 : Math.Abs(x.crown + x.member * x.count - 100) < .000001m)),
                "I found any of eight institutions, review all five authority levels, and grow from an empty chamber to a large empire without manufacturing or capping votes.", institutionMatrix);
            var petition = new GovernmentPetition { KingdomId = "realm", AuthorId = "player", ProposalId = "edict:free_market:poros", Title = "Free Market Charter — Poros", Terms = "real terms", Status = "seeking_sponsorship", SubmittedInterval = 1 };
            petition.DisclosedTo.Add("speaker");
            bool accepted = GovernmentParticipationRules.AddConsent(petition, "speaker", "sponsor", 1, "receipt", 1, "party", out _);
            bool stale = GovernmentParticipationRules.AddConsent(petition, "speaker", "sponsor", 2, "stale", 1, "party", out _);
            bool retry = GovernmentParticipationRules.AddConsent(petition, "speaker", "sponsor", 1, "receipt", 1, "party", out _);
            add("government.participation_sponsorship_exact_revision_receipt", accepted && !stale && retry && petition.Consents.Count == 1
                && !GovernmentParticipationRules.CanRead(petition, "unrelated_lord"),
                "I obtain personal sponsorship once. Another lord cannot read the private petition, and retrying does not duplicate the commitment or its payment.", null);
            bool revise = GovernmentParticipationRules.SemanticRevision(petition, "player", "edict:free_market:other", "Other target", "new terms", "");
            add("government.participation_changed_terms_invalidate_consent", revise && petition.Revision == 2 && petition.Consents.Count == 0 && petition.Status == "draft",
                "I change the named town; the old sponsor's consent no longer authorizes the revised petition.", null);
            var mandate = new GovernmentPetition { KingdomId = "realm", AuthorId = "player", Status = "seeking_sponsorship", PreviousIncumbentId = "incumbent" };
            foreach (string actor in new[] { "local_a", "local_b", "incumbent" }) mandate.DisclosedTo.Add(actor);
            GovernmentParticipationRules.AddConsent(mandate, "local_a", "endorse", 1, "a", 1, "", out _);
            GovernmentParticipationRules.AddConsent(mandate, "local_b", "endorse", 1, "b", 1, "", out _);
            var local = new[] { "local_a", "local_b", "local_c" };
            bool beforeTransfer = GovernmentParticipationRules.MandateSupported(mandate, local, 1, false);
            GovernmentParticipationRules.AddConsent(mandate, "incumbent", "transfer", 1, "transfer", 1, "", out _);
            add("government.participation_admission_endorsements_and_transfer", !beforeTransfer && GovernmentParticipationRules.MandateSupported(mandate, local, 1, false)
                && GovernmentParticipationRules.MandateSupported(mandate, local, 1, true) && !GovernmentParticipationRules.MandateSupported(mandate, local, 3, false),
                "I need both a local majority and the occupied seat's transfer consent. A recall needs two thirds; expired support is unusable.", null);
            var caucus = new GovernmentPartyCaucus { Roll = new List<string> { "speaker", "member", "opponent" }, Nominations = new List<string> { "petition" } };
            GovernmentParticipationRules.EndorseNomination(caucus, "speaker", "petition", caucus.Roll);
            bool newcomer = GovernmentParticipationRules.EndorseNomination(caucus, "newcomer", "petition", new[] { "speaker", "member", "opponent", "newcomer" });
            var copy = JsonSerializer.Deserialize<GovernmentPartyCaucus>(JsonSerializer.Serialize(caucus));
            string alone = GovernmentParticipationRules.SelectNomination(copy, copy.Roll, copy.Nominations);
            GovernmentParticipationRules.EndorseNomination(caucus, "member", "petition", caucus.Roll);
            add("government.participation_speaker_cannot_bypass_caucus", !newcomer && alone == "" && GovernmentParticipationRules.SelectNomination(caucus, caucus.Roll, caucus.Nominations) == "petition",
                "My speaker offers a proposal, but a second member must support it. A newly admitted member cannot change this frozen selection.", null);
            var election = new GovernmentPartyElection { IncumbentId = "old", Roll = new List<string> { "old", "new", "a", "b" },
                Nominations = new Dictionary<string, List<string>> { ["new"] = new List<string> { "new", "a" } },
                Ballots = new Dictionary<string, string> { ["new"] = "new", ["a"] = "new" } };
            add("government.participation_leadership_tie_retains_incumbent", GovernmentParticipationRules.CloseElection(election, election.Roll) == "old",
                "My challenge ties; I do not seize the speaker's office without a majority.", null);
            add("government.participation_docket_aliases_do_not_identify_options", new[] { 0, 25, 26, 27, 701, 702 }.Select(GovernmentParticipationRules.DocketAlias).SequenceEqual(new[] { "A", "Z", "AA", "AB", "ZZ", "AAA" }),
                "I choose docket AA, then choose its separate voting option. The sentence renderer names the actual proposal.", null);
            var stack = TestDict("traits", BuildTraitDocument(TestDict("heroStringId", "test_character")), "politicalProfile", TryParseJsonObject(JsonSerializer.Serialize(profile)));
            string prompt = BuildStableCharacterPrompt("merchant_a", "A merchant", TestDict("name", "A merchant"), stack);
            add("government.participation_required_prompt_is_complete_outside_motive_cap", prompt.Contains("REQUIRED PRIVATE POLITICAL BELIEFS")
                && Enum.GetValues(typeof(ReignGovernmentPlank)).Cast<ReignGovernmentPlank>().All(x => prompt.Contains(x + ":"))
                && prompt.Contains("PRIVATE NUMERIC MOTIVE EVIDENCE") && prompt.Contains("BASE PERSONALITY"),
                "I talk about an ordinary topic; the character's complete politics remain in their private foundation without removing personality or motive context.", null);
            add("government.participation_bargain_bounds_and_monotonicity",
                GovernmentParticipationBalance.ContributionReference(0, 0, false) == 2000
                && GovernmentParticipationBalance.ContributionReference(10000, 1000000, true) == 40000
                && GovernmentParticipationBalance.PoliticalServiceReference(0, 10000, 20, "government.transfer_mandate") == 90000
                && GovernmentParticipationBalance.PoliticalServiceReference(int.MaxValue, int.MaxValue, int.MaxValue, "government.transfer_mandate") == 180000
                && GovernmentParticipationBalance.Willingness(999, 999, 999, 999, 999, -999) == 90
                && GovernmentParticipationBalance.Willingness(-999, -999, -999, -999, -999, 999) == -90
                && Enumerable.Range(-40, 80).All(i => GovernmentParticipationBalance.Willingness(i + 1, 0, 0, 0, 0, 0)
                    > GovernmentParticipationBalance.Willingness(i, 0, 0, 0, 0, 0)),
                "I negotiate an actual service. Wealth has bounded reference prices, and a more compatible proposal never becomes less attractive merely from its score.", null);
            AddGovernmentPoliticalPersistenceTests(add);
        }

        private static void AddGovernmentPoliticalPersistenceTests(Action<string, bool, string, object> add)
        {
            string campaign = "government_participation_test_" + Guid.NewGuid().ToString("N");
            try
            {
                var native = TestDict("heroStringId", "local_notable", "name", "A local notable", "age", 35, "isChild", false,
                    "isAlive", true, "occupation", "Merchant", "isNotable", true, "politicalTimelineId", "original", "campaignId", campaign);
                var traits = BuildTraitDocument(native); EnsureTraitPercentageData(traits, "local_notable");
                WriteJsonObject(CharacterFile(campaign, "local_notable", "traits.json"), traits);
                WriteJsonObject(CharacterFile(campaign, "local_notable", "profile.json"), native);
                var first = EnsurePoliticalProfile(campaign, "local_notable", native, traits);
                native["appliedPoliticalProfile"] = TryParseJsonObject(JsonSerializer.Serialize(first));
                var evidence = new GovernmentPoliticalWorldEvidence { Id = "government:actual_law", CampaignId = campaign,
                    TimelineId = "original", KingdomId = "realm", Kind = "edict", TargetId = "public_granary",
                    OptionId = "accept", Applied = true, Interval = 2, WorldDay = 20, AffectedHeroes = new List<string> { "local_notable" } };
                var record = TryParseJsonObject(JsonSerializer.Serialize(evidence));
                var evolved = ApplyPoliticalWorldEvidence(campaign, "local_notable", first, new List<Dictionary<string, object>> { record }, 20);
                var replay = ApplyPoliticalWorldEvidence(campaign, "local_notable", first, new List<Dictionary<string, object>> { record }, 20);
                add("government.participation_world_effect_replay_reuses_revision", evolved.Revision > first.Revision
                    && JsonSerializer.Serialize(evolved) == JsonSerializer.Serialize(replay)
                    && evolved.Values.Zip(first.Values, (a, b) => a.Stance == b.Stance).All(x => x)
                    && evolved.Changes.Count == 2 && evolved.Changes.All(x => x.Importance - x.PreviousImportance == 1),
                    "I live through a verified law taking effect. Its concerns gain slight importance; replaying synchronization neither changes my stance nor issues another revision.", null);
                add("government.participation_future_and_unaffected_events_do_nothing",
                    ApplyPoliticalWorldEvidence(campaign, "local_notable", first, new List<Dictionary<string, object>> { record }, 19).Revision == first.Revision
                    && GovernmentPoliticalBeliefs.WorldEventChanges(evidence, "distant_stranger").Count == 0
                    && EnsurePoliticalProfile(campaign, "local_notable", native, traits).Revision == first.Revision,
                    "I reload an older applied snapshot. Future effects and another person's experience do not leak into my current beliefs.", null);
                var forged = CloneDictionary(native);
                var forgedProfile = JsonSerializer.Deserialize<GovernmentPoliticalProfile>(JsonSerializer.Serialize(first));
                forgedProfile.Values[0].Stance = forgedProfile.Values[0].Stance == 100 ? 99 : 100;
                forged["appliedPoliticalProfile"] = TryParseJsonObject(JsonSerializer.Serialize(forgedProfile));
                bool refused = false;
                try { EnsurePoliticalProfile(campaign, "local_notable", forged, traits); } catch (InvalidOperationException) { refused = true; }
                add("government.participation_unissued_profile_refused", refused,
                    "An invented private profile cannot replace the character's issued political record.", null);
                var fork = CloneDictionary(native); fork["politicalTimelineId"] = "restored_branch";
                var forked = EnsurePoliticalProfile(campaign, "local_notable", fork, traits);
                add("government.participation_branch_keeps_applied_history", forked.TimelineId == "restored_branch"
                    && forked.Values.Zip(first.Values, (a, b) => a.Stance == b.Stance && a.Importance == b.Importance).All(x => x)
                    && forked.Changes.Count == first.Changes.Count,
                    "I branch from a saved game; the new timeline starts with that save's actual beliefs, not the latest future file.", null);

                string history = string.Join("\n", Enumerable.Range(0, 180).Select(i => "Earlier conversation " + i + ": the character remembers this complete exchange."));
                var messages = new List<Dictionary<string, object>> { TestDict("role", "system", "content", "Keep the complete character context."),
                    TestDict("role", "user", "content", history) };
                var routes = new[] { "dialogue", "correspondence", "party_chat", "social_event", "castle_chat", "royal_council", "government_hearing", "government_statement", "continuity_repair" };
                foreach (string route in routes)
                {
                    var request = TestDict("campaignId", campaign, "heroStringId", "local_notable", "hero", native, "requestType", route,
                        "politicalAuthors", new[] { native }, "messages", messages);
                    var outbound = BuildChatRequestBody(TestDict(), request, messages, "offline-capture");
                    var actual = ReadDictionaryList(outbound, "messages");
                    string joined = string.Join("\n", actual.Select(x => ReadString(x, "content", "")));
                    add("government.participation_final_provider_" + route, actual.Count == 3
                        && ReadString(actual[1], "content", "") == history
                        && Enum.GetValues(typeof(ReignGovernmentPlank)).Cast<ReignGovernmentPlank>().All(x => joined.Contains(x + ":"))
                        && joined.Contains("revision " + first.Revision + " — timeline original")
                        && ReadDictionaryList(BuildChatRequestBody(TestDict(), request, actual, "offline-capture"), "messages").Count == 3,
                        "I use " + route + "; the outgoing provider request retains all 16 beliefs and all earlier conversation, with one private block.", null);
                }
                var other = TestDict("heroStringId", "companion", "campaignId", campaign, "politicalTimelineId", "original",
                    "name", "A companion", "age", 25, "isAlive", true);
                var combined = RequiredPoliticalMessages(TestDict("campaignId", campaign, "politicalAuthors", new[] { native, other }), messages);
                add("government.participation_multispeaker_private_ownership", combined.Count == 4
                    && ReadString(combined[2], "content", "").Contains("— local_notable —")
                    && ReadString(combined[3], "content", "").Contains("— companion —")
                    && combined.Skip(2).All(x => ReadString(x, "content", "").Contains("only to this character")),
                    "Two people converse; each authored character has their own complete private foundation, without treating either profile as the other's knowledge.", null);
                var unit = new AcceptedConversationUnit { Hero = "local_notable", EventId = "accepted_fixture",
                    Profile = TestDict("politicalInterval", 2), Payload = TestDict("timelineId", "original", "worldDay", 20d),
                    PlayerText = "The public granary kept our families alive through winter.",
                    Reply = "That winter has convinced me to care more about public welfare.",
                    Parsed = TestDict("politicalReflection", new[] { TestDict("value", "PopularWelfare", "stanceChange", 0, "importanceChange", 1,
                        "sourceQuote", "The public granary kept our families alive through winter.",
                        "evidenceQuote", "That winter has convinced me to care more about public welfare.") }) };
                var reflection = AcceptedPoliticalReflection(unit, false);
                bool good = reflection.Count > 0;
                unit.CommitEffects = false;
                bool draftIgnored = AcceptedPoliticalReflection(unit, false).Count == 0;
                unit.CommitEffects = true; unit.Reply = "Perhaps, but I am not convinced.";
                add("government.participation_discussion_requires_accepted_visible_evidence", good && draftIgnored
                    && AcceptedPoliticalReflection(unit, false).Count == 0 && AcceptedPoliticalReflection(unit, true).Count == 0,
                    "My substantive argument can matter only when the accepted reply actually acknowledges it. Unspoken drafts, technical notices and fabricated quotes do nothing.", null);
                var subject = TestDict("heroStringId", "player", "kingdomId", "realm", "isAlive", true, "isChild", false, "isRuler", false,
                    "governmentOfficeTitle", "Senator and party speaker", "governmentOfficeKingdomId", "realm");
                var context = TestDict("nativePoliticalContext", TestDict("authoritative", true, "subject", subject,
                    "observer", TestDict("heroStringId", "local_notable", "kingdomId", "realm")));
                var known = BuildCurrentPoliticalAuthorityView(TestDict("canonicalNameAllowed", true), context);
                var unknown = BuildCurrentPoliticalAuthorityView(TestDict("canonicalNameAllowed", false), context);
                add("government.participation_title_follows_verified_current_office", ReadBool(known, "governmentMemberOfficeKnown", false)
                    && ReadStringList(known, "validFormalAddresses").Contains("Senator")
                    && !ReadBool(known, "subjectIsObserverSovereign", true) && !ReadBool(unknown, "governmentMemberOfficeKnown", true),
                    "A character who recognizes my current office can call me Senator. Membership never makes me their sovereign or reveals my identity to strangers.", null);
                subject["governmentOfficeTitle"] = ""; subject["governmentOfficeKingdomId"] = "";
                add("government.participation_revoked_title_is_not_reused", !ReadBool(BuildCurrentPoliticalAuthorityView(TestDict("canonicalNameAllowed", true), context), "governmentMemberOfficeKnown", true),
                    "After losing the office, my former title stops being supplied as current authority.", null);
            }
            catch (Exception ex)
            {
                add("government.participation_persistence_fixture", false, "The isolated production persistence and final-provider fixture must complete.", ex.ToString());
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }
    }
}
