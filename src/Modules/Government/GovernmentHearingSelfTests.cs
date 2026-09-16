using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void AddGovernmentHearingSelfTests(Action<string, bool, string, object> add)
        {
            var constitutional = new Dictionary<string, ReignGovernmentActionKind>
            {
                ["DiplomacyDeclareWar"] = ReignGovernmentActionKind.War,
                ["DiplomacyPayToJoinWar"] = ReignGovernmentActionKind.War,
                ["DiplomacyMakePeace"] = ReignGovernmentActionKind.Peace,
                ["DiplomacyDemandSettlementPeace"] = ReignGovernmentActionKind.Peace,
                ["DiplomacySignAlliance"] = ReignGovernmentActionKind.Treaty,
                ["DiplomacySignTradeAgreement"] = ReignGovernmentActionKind.Treaty,
                ["DiplomacyExchangePrisoners"] = ReignGovernmentActionKind.Treaty,
                ["DiplomacyPackage"] = ReignGovernmentActionKind.Treaty,
                ["DiplomacyLoanOrSubsidy"] = ReignGovernmentActionKind.MajorSpending,
                ["DiplomacyPayToStayNeutral"] = ReignGovernmentActionKind.MajorSpending,
                ["DiplomacyWarIndemnity"] = ReignGovernmentActionKind.MajorSpending,
                ["DiplomacyRansomPackage"] = ReignGovernmentActionKind.MajorSpending,
                ["DiplomacyReturnOccupiedSettlement"] = ReignGovernmentActionKind.FiefTransfer,
                ["PoliticsExileClan"] = ReignGovernmentActionKind.MajorJustice,
                ["PoliticsRestoreExiledClan"] = ReignGovernmentActionKind.MajorJustice,
                ["RegularConfirmArrest"] = ReignGovernmentActionKind.MajorJustice,
                ["DiplomacyBackRebellion"] = ReignGovernmentActionKind.Policy,
                ["RegularDismissPlayerVassal"] = ReignGovernmentActionKind.Policy
            };
            add("government.constitutional_action_classification",
                constitutional.All(x => GovernmentBusinessRules.ClassifyWorldAction(x.Key) == x.Value
                    && GovernmentBusinessRules.IsConstitutionalWorldAction(x.Key)),
                "Explicit war, peace, treaty, state expenditure, fief and major justice actions enter the correct government authority category.", constitutional);
            string[] personalOrTactical = { "StrategyFormArmy", "StrategyCaptureSettlement", "RegularRaidVillage",
                "RegularBesiegeSettlement", "RegularIssueCampaignOrder", "RegularReviseCampaignOrder", "RegularCancelCampaignOrder",
                "RegularTransferWorkshop", "RegularOfferPlayerVassalage", "PoliticsStartRulingClanRebellion", "PoliticsInstallRulingClan",
                "PoliticsResolveRebellionPledge", "PoliticsResolveRebellionSummons", "PoliticsMarriageAlliance",
                "PoliticsEncourageClanDefection", "PoliticsMediateClanDispute", "PoliticsSupportClaimant", "PoliticsConsentGovernmentReduction",
                "DiplomacyDeclareWarExtra", "diplomacydeclarewar", "", "unknown" };
            add("government.personal_tactical_and_rebellion_lifecycle_not_gated",
                personalOrTactical.All(x => GovernmentBusinessRules.ClassifyWorldAction(x) == ReignGovernmentActionKind.Advice
                    && !GovernmentBusinessRules.IsConstitutionalWorldAction(x)),
                "Routine military orders, personal transactions, internal rebellion lifecycle and consent recording do not stall behind constitutional hearings; unknown or partially matching names never gain authority.", personalOrTactical);
            string[] bilateral = { "DiplomacyMakePeace", "DiplomacySignAlliance", "DiplomacyPayToJoinWar", "DiplomacyRansomPackage", "DiplomacyPackage" };
            string[] unilateral = { "DiplomacyDeclareWar", "DiplomacyBreakTreaty", "DiplomacyTradeEmbargo", "DiplomacyBackRebellion",
                "DiplomacyRecordPromise", "DiplomacyGuaranteeIndependence", "RegularTransferWorkshop", "unknown", "" };
            add("government.bilateral_approval_scope",
                bilateral.All(GovernmentBusinessRules.RequiresCounterpartWorldApproval)
                && unilateral.All(x => !GovernmentBusinessRules.RequiresCounterpartWorldApproval(x)),
                "Peace, negotiated treaties and paid war commitments require the counterpart's approval; unilateral declarations and unrelated actions do not invent foreign consent.",
                new { bilateral, unilateral });
            string[] privateMeasurements = { "relation +50", "relationship: -20", "current relation value: 65", "relationship delta +10",
                "Current relationship value: 42.5", "party loyalty: 80", "support score 90" };
            string publicTerms = "Pay 2500 gold within 7 days. Improve relations with the petitioner.";
            string[] sanitizedMeasurements = privateMeasurements.Select(text => GovernmentBusinessRules.PublicText(text)).ToArray();
            string mixedPublicText = GovernmentBusinessRules.PublicText("relation +50; " + publicTerms + "\nrelationship: -20");
            add("government.public_business_text_hides_measurements",
                sanitizedMeasurements.All(text => text.Length > 0 && !text.Any(char.IsDigit))
                && GovernmentBusinessRules.PublicText(publicTerms) == publicTerms
                && mixedPublicText.Contains(publicTerms, StringComparison.Ordinal)
                && !mixedPublicText.Contains("+50", StringComparison.Ordinal) && !mixedPublicText.Contains("-20", StringComparison.Ordinal)
                && GovernmentBusinessRules.PublicText(null) == string.Empty,
                "Production public-business text removes numeric relationship values and deltas while preserving actual payment amounts, deadlines and qualitative political wording in their own clauses.",
                new { privateMeasurements, sanitizedMeasurements, publicTerms, mixedPublicText });
            string[] stages = { "awaiting_sponsorship", "hearing", "postponed", "reconsideration", "awaiting_ruler", "voting", "decided", "invalidated", "expired" };
            var recessCases = (from stage in stages from urgent in new[] { false, true }
                from used in new[] { false, true } select new
                {
                    stage, urgent, used,
                    actual = GovernmentBusinessRules.CanPostpone(stage, urgent, used),
                    expected = stage == "hearing" && !urgent && !used
                }).ToArray();
            add("government.hearing_single_seven_day_recess",
                GovernmentBusinessRules.RecessDays == 7 && recessCases.All(x => x.actual == x.expected),
                "Only an unpostponed nonurgent hearing permits the single seven-day recess; closed, urgent and reconsideration cases cannot postpone.", recessCases);
            add("government.hearing_stage_permissions",
                stages.All(stage => GovernmentBusinessRules.CanVote(stage) == (stage == "hearing")
                    && GovernmentBusinessRules.CanRecommend(stage) == (stage == "hearing" || stage == "reconsideration" || stage == "awaiting_ruler")
                    && GovernmentBusinessRules.IsClosed(stage) == (stage == "decided" || stage == "invalidated" || stage == "expired"))
                && !GovernmentBusinessRules.CanVote(null) && !GovernmentBusinessRules.CanRecommend("unknown")
                && GovernmentBusinessRules.PetitionExpiryDays == 21,
                "Hearing state permissions reject premature, repeated and unknown-stage operations; unsponsored petitions have the explicit twenty-one-day limit.", stages);

            var candidates = new[]
            {
                new GovernmentSponsorCandidate { HeroId = "friend_opposes", Eligible = true, IssueSupport = -1, PetitionerRelation = 100 },
                new GovernmentSponsorCandidate { HeroId = "neutral_friend", Eligible = true, IssueSupport = 0, PetitionerRelation = 100 },
                new GovernmentSponsorCandidate { HeroId = "outside_institution", Eligible = false, IssueSupport = 100, PetitionerRelation = 100 },
                new GovernmentSponsorCandidate { HeroId = "willing_senator", Eligible = true, IssueSupport = 10, PetitionerRelation = -100 }
            };
            string sponsor = GovernmentBusinessRules.SelectSponsor(candidates);
            add("government.hearing_sponsor_requires_eligible_willing_member",
                sponsor == "willing_senator"
                && GovernmentBusinessRules.SelectSponsor(candidates.Take(3)) == string.Empty
                && GovernmentBusinessRules.SelectSponsor(null) == string.Empty
                && GovernmentBusinessRules.SelectSponsor(new GovernmentSponsorCandidate[] { null,
                    new GovernmentSponsorCandidate { HeroId = " ", Eligible = true, IssueSupport = 100 } }) == string.Empty,
                "Friendship cannot invent issue support or a government seat; absent sponsorship remains genuinely absent.",
                new { candidates, sponsor });
            var tiedSponsors = new[]
            {
                new GovernmentSponsorCandidate { HeroId = "senator_b", Eligible = true, IssueSupport = 20, PetitionerRelation = 0 },
                new GovernmentSponsorCandidate { HeroId = "senator_a", Eligible = true, IssueSupport = 20, PetitionerRelation = 0 }
            };
            string tieSponsor = GovernmentBusinessRules.SelectSponsor(tiedSponsors);
            tiedSponsors[0].PetitionerRelation = 100;
            string connectedSponsor = GovernmentBusinessRules.SelectSponsor(tiedSponsors);
            add("government.hearing_sponsor_stable_ranking",
                tieSponsor == "senator_a" && connectedSponsor == "senator_b"
                && GovernmentBusinessRules.SelectSponsor(tiedSponsors.AsEnumerable().Reverse()) == connectedSponsor,
                "Relationships rank already-willing representatives and stable identity breaks ties independently of collection order.",
                new { tieSponsor, connectedSponsor });

            int interestedEnemy = GovernmentBusinessRules.OptionScore(60, 40, -100, true, 0, 0, 0);
            int harmedFriend = GovernmentBusinessRules.OptionScore(-60, -40, 100, true, 0, 0, 0);
            int unendorsedHostile = GovernmentBusinessRules.OptionScore(10, 0, -100, false, 0, 0, 0);
            int unendorsedFriendly = GovernmentBusinessRules.OptionScore(10, 0, 100, false, 0, 0, 0);
            add("government.hearing_issue_specific_individual_ballots",
                interestedEnemy > 0 && harmedFriend < 0 && unendorsedHostile == unendorsedFriendly
                && GovernmentBusinessRules.OptionScore(0, 0, 100, true, 0, 0, 0)
                    > GovernmentBusinessRules.OptionScore(0, 0, -100, true, 0, 0, 0),
                "A ruler's friend can oppose a harmful proposal and an enemy can support a beneficial one; ruler relations weight only the ruler's actual recommendation.",
                new { interestedEnemy, harmedFriend, unendorsedHostile, unendorsedFriendly });
            int maximum = GovernmentBusinessRules.OptionScore(int.MaxValue, int.MaxValue, int.MaxValue, true,
                int.MaxValue, int.MaxValue, int.MaxValue);
            int minimum = GovernmentBusinessRules.OptionScore(int.MinValue, int.MinValue, int.MinValue, true,
                int.MinValue, int.MinValue, int.MinValue);
            add("government.hearing_private_commitments_bounded",
                maximum == 180 && minimum == -180
                && GovernmentBusinessRules.OptionScore(0, 0, 0, false, 1000, 0, 0) == 30
                && GovernmentBusinessRules.OptionScore(-60, -40, 0, false, 1000, 0, 0) < 0,
                "Accepted political commitments remain bounded inputs and cannot guarantee a vote against overwhelming issue opposition; extreme data cannot overflow the score.",
                new { maximum, minimum });

            var options = new[] { "enact", "reject" };
            string majority = GovernmentBusinessRules.SelectWinner(new[] { "enact", "reject", "reject" }, options, "reject");
            string equal = GovernmentBusinessRules.SelectWinner(new[] { "enact", "reject" }, options, "reject");
            add("government.hearing_ballot_majority_and_status_quo_tie",
                majority == "reject" && equal == "reject"
                && GovernmentBusinessRules.SelectWinner(new[] { "enact", "enact", "reject" }, options, "reject") == "enact",
                "Individual ballots decide the result and a tied policy vote retains the status quo; the ruler's recommendation is not an extra ballot.",
                new { majority, equal });
            var claimants = new[] { "clan_a", "clan_b", "clan_c" };
            var claimantVotes = new[] { "clan_a", "clan_b", "clan_c", "ineligible" };
            string allocation = GovernmentBusinessRules.SelectWinner(claimantVotes, claimants, "");
            add("government.hearing_allocation_stable_lot_and_valid_candidates",
                claimants.Contains(allocation)
                && GovernmentBusinessRules.SelectWinner(claimantVotes.AsEnumerable().Reverse(), claimants.AsEnumerable().Reverse(), "") == allocation
                && GovernmentBusinessRules.SelectWinner(new[] { "ineligible", "clan_b", "clan_b" }, claimants, "") == "clan_b"
                && GovernmentBusinessRules.SelectWinner(new[] { "ineligible" }, claimants, "") == string.Empty
                && GovernmentBusinessRules.SelectWinner(Array.Empty<string>(), claimants, "clan_a") == string.Empty
                && GovernmentBusinessRules.SelectWinner(new[] { "clan_a" }, Array.Empty<string>(), "clan_a") == string.Empty,
                "Tied essential allocations select an eligible stable lot; invalid ballots and empty institutions never manufacture authorization.",
                new { allocation, claimants, claimantVotes });

            int petitioner = GovernmentBusinessRules.ParticipantRelationDelta(true, true, true, true, false, false, false);
            int both = GovernmentBusinessRules.ParticipantRelationDelta(true, true, true, true, true, true, false);
            int rejected = GovernmentBusinessRules.ParticipantRelationDelta(false, false, false, true, false, false, false);
            int overruled = GovernmentBusinessRules.ParticipantRelationDelta(false, true, true, true, false, false, false);
            add("government.hearing_participant_relation_responsibility",
                petitioner == 4 && both == petitioner && rejected == -4 && overruled == -2
                && GovernmentBusinessRules.ParticipantRelationDelta(true, true, true, false, false, false, true) == -2
                && GovernmentBusinessRules.ParticipantRelationDelta(false, false, true, false, false, false, true) == 2
                && GovernmentBusinessRules.ParticipantRelationDelta(true, true, false, false, false, false, false) == 0,
                "Petitioners and sponsors retain personal consequences without duplicate role rewards; the ruler receives less blame for an opposing binding vote and harmed parties react independently.",
                new { petitioner, both, rejected, overruled });
            AddGovernmentPrivateConversationSelfTests(add);
            AddGovernmentPublicDiscussionSelfTests(add);
            AddGovernmentPaymentOwnershipSelfTests(add);
        }

        private static void AddGovernmentPublicDiscussionSelfTests(Action<string, bool, string, object> add)
        {
            var injected = new Dictionary<string, object>
            {
                ["statements"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["speakerHeroId"] = "senator_a", ["text"] = "The villages need relief before winter." },
                    new Dictionary<string, object> { ["speakerHeroId"] = "invented_person", ["text"] = "I speak for a seat I do not hold." },
                    new Dictionary<string, object> { ["speakerHeroId"] = "senator_b", ["text"] = "My relationship with the ruler is +75." }
                },
                ["nativeActions"] = new object[] { new { command = "enact_policy" } },
                ["governmentCommitments"] = new object[] { new { accepted = true } },
                ["relationshipAssessments"] = new object[] { new { relation = 100 } },
                ["reputationChanges"] = new object[] { new { delta = 50 } }
            };
            var normalized = NormalizeGovernmentHearingResponse(injected, new[] { "senator_a", "senator_b" });
            var statements = ReadDictionaryList(normalized, "statements");
            bool effectsEmpty = new[] { "nativeActions", "governmentCommitments", "relationshipAssessments", "reputationChanges" }
                .All(key => normalized.TryGetValue(key, out object value) && value is System.Collections.ICollection items && items.Count == 0);
            add("government.public_hearing_real_speakers_and_no_effects",
                ReadBool(normalized, "ok", false) && statements.Count == 1
                && ReadString(statements[0], "speakerHeroId", "") == "senator_a" && effectsEmpty,
                "Production response normalization keeps a real public statement, removes fabricated speakers and live relationship leaks, and discards injected actions, commitments, relationship and reputation effects.", normalized);

            var many = new Dictionary<string, object> { ["statements"] = Enumerable.Range(0, 8)
                .Select(index => new Dictionary<string, object> { ["speakerHeroId"] = "senator_a", ["text"] = new string('a', 2500) }).ToList() };
            var bounded = NormalizeGovernmentHearingResponse(many, new[] { "senator_a" });
            var boundedStatements = ReadDictionaryList(bounded, "statements");
            var empty = NormalizeGovernmentHearingResponse(new Dictionary<string, object>(), new[] { "senator_a" });
            var noSpeakers = NormalizeGovernmentHearingResponse(injected, Array.Empty<string>());
            add("government.public_hearing_bounded_output_and_failure",
                boundedStatements.Count == 3 && boundedStatements.All(row => ReadString(row, "text", "").Length <= 1200)
                && !ReadBool(empty, "ok", true) && !ReadBool(noSpeakers, "ok", true),
                "The production hearing response is bounded to three short real-speaker statements; absent or unusable replies report failure rather than inventing successful discussion.",
                new { statementCount = boundedStatements.Count, maximumLength = boundedStatements.Max(row => ReadString(row, "text", "").Length), empty, noSpeakers });

            string[] hidden = { "My relationship is +75.", "Trust: 60", "His loyalty is 80 out of 100.",
                "+20 affinity with the ruler", "The support score is 91.", "Relations: 55", "75 relationship points" };
            string[] publicFacts = { "3 members voted for village relief.", "The treasury will pay 2500 denars.",
                "I support the petition because the village was raided.", "The ruler recommended peace; the assembly voted for war." };
            add("government.public_hearing_hidden_values_vs_public_facts",
                hidden.All(GovernmentDialogueLeaksHiddenValue) && publicFacts.All(text => !GovernmentDialogueLeaksHiddenValue(text)),
                "Hidden relationship, loyalty, trust and score figures are filtered while actual public votes, costs and stated positions remain usable.",
                new { hidden = hidden.Select(text => new { text, filtered = GovernmentDialogueLeaksHiddenValue(text) }).ToArray(), publicFacts });

            var missing = GovernmentHearingDiscussion(new Dictionary<string, object>
                { ["businessId"] = "case_a", ["playerMessage"] = "What do the members think?", ["speakers"] = new List<Dictionary<string, object>>() });
            add("government.public_hearing_missing_participants_no_provider",
                !ReadBool(missing, "ok", true) && ReadInt(missing, "providerCallCount", -1) == 0,
                "An empty participant set exits the actual server entrypoint before any provider request.", missing);
        }

        private static void AddGovernmentPrivateConversationSelfTests(Action<string, bool, string, object> add)
        {
            var eligibility = new List<object>();
            bool gatesCorrect = true;
            for (int mask = 0; mask < 64; mask++)
            {
                bool open = (mask & 1) != 0, voted = (mask & 2) != 0, ruler = (mask & 4) != 0;
                bool seated = (mask & 8) != 0, present = (mask & 16) != 0, duty = (mask & 32) != 0;
                bool actual = GovernmentPrivateConversationEligibility.CanDiscuss(open, voted, ruler, seated, present, duty);
                // Only bit pattern open+ruler+seated+present has every positive prerequisite and neither blocker.
                gatesCorrect &= actual == (mask == 29);
                eligibility.Add(new { mask, open, voted, ruler, seated, present, duty, actual });
            }
            add("government.private_chat_physical_and_authority_gates", gatesCorrect,
                "Production private-chat eligibility checks all sixty-four combinations: an unresolved unvoted matter, its current ruler, a seated co-located member and no blocking duty are all required. Absentee voting grants no remote persuasion.", eligibility);

            var positive = new List<object>();
            bool methodsCorrect = true;
            foreach (string method in new[] { "persuasion", "bribe", "deal" })
            {
                GovernmentPrivateReceiptProof proof = GovernmentReceiptFixture();
                proof.Method = method;
                proof.Gold = method == "bribe" ? 125 : 0;
                proof.ObligationId = method == "deal" ? "obligation_a" : string.Empty;
                if (method == "bribe")
                {
                    proof.PlayerText = proof.PlayerConsentQuote = "I offer 125 denars for your support of village relief.";
                    proof.MemberReply = proof.MemberConsentQuote = "I accept 125 denars and agree to support village relief.";
                }
                bool valid = GovernmentPrivateConversationEligibility.ValidateReceipt(proof, out string reason);
                methodsCorrect &= valid && reason.Length == 0;
                positive.Add(new { method, valid, reason });
            }
            add("government.private_chat_receipt_scoped_methods", methodsCorrect,
                "Each supported private agreement validates against an exact reserved exchange; bribery alone transfers a positive agreed amount and a deal must name an offered verifiable obligation.", positive);

            var invalidations = new Dictionary<string, Action<GovernmentPrivateReceiptProof>>
            {
                ["not_accepted"] = p => p.Accepted = false,
                ["forged_receipt"] = p => p.ReceiptId = "other_receipt",
                ["blank_receipt"] = p => p.ReceiptId = " ",
                ["missing_reservation"] = p => p.ExpectedReceiptId = null,
                ["other_session"] = p => p.SessionId = "other_session",
                ["blank_session"] = p => p.SessionId = "",
                ["other_member"] = p => p.MemberHeroId = "other_member",
                ["other_ruler"] = p => p.RulerHeroId = "other_ruler",
                ["self_consent"] = p => { p.MemberHeroId = "ruler_a"; p.ExpectedMemberHeroId = "ruler_a"; },
                ["other_matter"] = p => p.BusinessId = "business_b",
                ["missing_player_text"] = p => p.PlayerText = null,
                ["missing_member_reply"] = p => p.MemberReply = null,
                ["fabricated_player_quote"] = p => p.PlayerConsentQuote = "I offer one thousand denars",
                ["fabricated_member_quote"] = p => p.MemberConsentQuote = "I promise a guaranteed vote",
                ["empty_player_quote"] = p => p.PlayerConsentQuote = " ",
                ["short_member_quote"] = p => p.MemberConsentQuote = "I",
                ["quote_from_different_turn"] = p => p.MemberReply = "I have not answered that question.",
                ["unknown_option"] = p => p.OptionId = "option_other",
                ["missing_options"] = p => p.AllowedOptionIds = null,
                ["blank_option"] = p => p.OptionId = " ",
                ["unknown_method"] = p => p.Method = "force_vote",
                ["negative_payment"] = p => { p.Method = "bribe"; p.Gold = -1; },
                ["zero_bribe"] = p => p.Method = "bribe",
                ["unspoken_bribe_amount"] = p => { p.Method = "bribe"; p.Gold = 125; },
                ["paid_persuasion"] = p => p.Gold = 125,
                ["paid_deal"] = p => { p.Method = "deal"; p.Gold = 125; p.ObligationId = "obligation_a"; },
                ["invented_obligation"] = p => { p.Method = "deal"; p.ObligationId = "obligation_other"; },
                ["missing_obligation_catalog"] = p => { p.Method = "deal"; p.ObligationId = "obligation_a"; p.VerifiableObligationIds = null; },
                ["missing_deal_obligation"] = p => p.Method = "deal",
                ["hidden_persuasion_condition"] = p => p.ObligationId = "obligation_a"
            };
            var rejected = new List<object>();
            bool negativesCorrect = !GovernmentPrivateConversationEligibility.ValidateReceipt(null, out _);
            foreach (var test in invalidations)
            {
                GovernmentPrivateReceiptProof proof = GovernmentReceiptFixture();
                test.Value(proof);
                bool valid = GovernmentPrivateConversationEligibility.ValidateReceipt(proof, out string reason);
                negativesCorrect &= !valid && !string.IsNullOrWhiteSpace(reason);
                rejected.Add(new { caseId = test.Key, valid, reason });
            }
            add("government.private_chat_receipt_cross_scope_and_fabrication_rejected", negativesCorrect,
                "The production receipt boundary rejects mismatched people/session/matter/options, invented or absent exchange evidence, unsupported methods and invalid financial/obligation terms. It does not substitute quote matching for ordinary dialogue consent classification.", rejected);
            add("government.private_chat_bribe_amount_exact_evidence",
                GovernmentPrivateConversationEligibility.ContainsExactGold("I accept 125 denars.", 125)
                && GovernmentPrivateConversationEligibility.ContainsExactGold("We agree on 125,000 denars.", 125000)
                && !GovernmentPrivateConversationEligibility.ContainsExactGold("We agree on 1250 denars.", 125)
                && !GovernmentPrivateConversationEligibility.ContainsExactGold("We agree on 125.50 denars.", 125)
                && !GovernmentPrivateConversationEligibility.ContainsExactGold("We agree on 1,25 denars.", 125)
                && !GovernmentPrivateConversationEligibility.ContainsExactGold("We agree on -125 denars.", 125)
                && !GovernmentPrivateConversationEligibility.ContainsExactGold("I agree to help.", 125)
                && !GovernmentPrivateConversationEligibility.ContainsExactGold("I accept zero denars.", 0),
                "Exact numeric payment evidence cannot be invented from a different, fractional, malformed or negative amount; semantic acceptance remains the ordinary dialogue decision layer's responsibility.", null);
        }

        private static GovernmentPrivateReceiptProof GovernmentReceiptFixture() => new GovernmentPrivateReceiptProof
        {
            Accepted = true, ReceiptId = "receipt_a", ExpectedReceiptId = "receipt_a",
            SessionId = "session_a", ExpectedSessionId = "session_a",
            MemberHeroId = "member_a", ExpectedMemberHeroId = "member_a",
            RulerHeroId = "ruler_a", ExpectedRulerHeroId = "ruler_a",
            BusinessId = "business_a", ExpectedBusinessId = "business_a",
            PlayerText = "I ask you to support the village relief petition. Will you agree?",
            PlayerConsentQuote = "I ask you to support the village relief petition.",
            MemberReply = "I agree to support the village relief petition.",
            MemberConsentQuote = "I agree to support the village relief petition.",
            OptionId = "option_a", AllowedOptionIds = new[] { "option_a", "option_b" },
            Method = "persuasion", Gold = 0, ObligationId = string.Empty,
            VerifiableObligationIds = new[] { "obligation_a" }
        };

        private static bool ValidateGovernmentScrollPanels(string source, out string error)
        {
            error = string.Empty;
            try
            {
                var xml = new XmlDocument { XmlResolver = null };
                xml.LoadXml(source);
                XmlElement[] panels = xml.SelectNodes("//ScrollablePanel | //ReignGovernmentMemberScrollPanel").Cast<XmlElement>().ToArray();
                if (panels.Length < 3) { error = "Government must have bounded business, hearing and member scrolling."; return false; }
                foreach (XmlElement panel in panels)
                {
                    if (panel.Name == "ReignGovernmentMemberScrollPanel"
                        && (!int.TryParse(panel.GetAttribute("VisibleRows"), out int visibleRows) || visibleRows <= 0))
                    { error = "A snap-scrolling panel has no positive visible row count."; return false; }
                    string clipId = panel.GetAttribute("ClipRect");
                    string innerPath = panel.GetAttribute("InnerPanel");
                    string scrollPath = panel.GetAttribute("VerticalScrollbar");
                    XmlElement clip = panel.SelectNodes("Children/*").Cast<XmlElement>()
                        .SingleOrDefault(x => x.GetAttribute("Id") == clipId);
                    if (clip == null || clip.GetAttribute("ClipContents") != "true"
                        || !innerPath.StartsWith(clipId + "\\", StringComparison.Ordinal)
                        || panel.GetAttribute("HeightSizePolicy") == "CoverChildren")
                    { error = "A scroll panel has an unbounded or unresolved clip."; return false; }
                    string innerId = innerPath.Substring(clipId.Length + 1);
                    if (!clip.SelectNodes("Children/*").Cast<XmlElement>().Any(x =>
                        x.GetAttribute("Id") == innerId && x.GetAttribute("HeightSizePolicy") == "CoverChildren"))
                    { error = "A scroll panel inner content is outside its clip."; return false; }
                    if (!scrollPath.StartsWith("..\\", StringComparison.Ordinal))
                    { error = "A scroll panel scrollbar is not a sibling."; return false; }
                    XmlElement scrollbar = panel.ParentNode.ChildNodes.OfType<XmlElement>()
                        .SingleOrDefault(x => x.Name == "ScrollbarWidget"
                            && x.GetAttribute("Id") == scrollPath.Substring(3));
                    if (scrollbar == null || string.IsNullOrWhiteSpace(scrollbar.GetAttribute("Handle"))
                        || !scrollbar.SelectNodes("Children/*").Cast<XmlElement>().Any(x =>
                            x.GetAttribute("Id") == scrollbar.GetAttribute("Handle")))
                    { error = "A scroll panel scrollbar has no concrete child handle."; return false; }
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
