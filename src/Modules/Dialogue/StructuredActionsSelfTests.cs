using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunStructuredActionSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass) => results.Add(TestDict("caseId", "structured_action_" + id, "passed", pass, "summary", id));
            ActionAgreement Gift(int quantity = 100)
            {
                var a = new ActionAgreement { PlayerId = "player", NpcId = "npc", ProposerId = "player", Origin = ActionOrigin.Conversation };
                a.Clauses.Add(new ActionClause { ActionId = "give.gold", ActorId = "player", RecipientId = "npc", DisplayText = "I wish to give you " + quantity + " denars", Arguments = new SortedDictionary<string, string> { ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture) } });
                return a;
            }
            void Agree(ActionAgreement a) { a.State = AgreementState.Offered; string hash = a.TermsHash(); AgreementRules.Accept(a, a.PlayerId, hash); AgreementRules.Accept(a, a.NpcId, hash); }
            var single = Gift();
            foreach (string guestAction in new[] { "accept_temporary_party_guest", "renew_temporary_party_guest" })
            {
                var guest = new ActionAgreement { PlayerId = "player", NpcId = "npc", ProposerId = "player", Origin = ActionOrigin.Conversation };
                guest.Clauses.Add(new ActionClause { ActionId = guestAction, ActorId = "npc", RecipientId = "player",
                    DisplayText = "I would like you to join my party temporarily", Arguments = new SortedDictionary<string, string> { ["terms.termKind"] = "open_ended" } });
                Check(guestAction + "_needs_no_separate_reason", AgreementRules.Validate(guest, ReignActionCatalog.Find, 1).Count == 0);
            }
            var composer = new ActionComposer();
            composer.SetText("Please consider this. It would help my people.");
            int insertion = "Please consider this.".Length;
            composer.Select(insertion, insertion, insertion); composer.Insert(Gift());
            Check("composer_inserts_at_caret", composer.Text == "Please consider this. I wish to give you 100 denars. It would help my people." && composer.HasValidAttachment);
            int firstStart = composer.ActionStart;
            composer.Replace(0, 0, "My friend, ");
            Check("composer_context_preserves_action", composer.HasValidAttachment && composer.ActionStart == firstStart + "My friend, ".Length);
            composer.Replace(composer.ActionStart + 10, 1, "");
            Check("composer_single_letter_delete_cancels_whole_action", composer.Draft == null && composer.Text == "My friend, Please consider this.  It would help my people.");
            composer.SetText("before after"); composer.Select(6, 6, 6); composer.Insert(Gift());
            composer.Replace(composer.ActionStart - 2, 5, "replacement ");
            Check("composer_cross_boundary_replacement_preserves_unselected_suffix", composer.Draft == null && composer.Text == "beforreplacement  after");
            composer.SetText(Gift().Sentence + " "); composer.Select(composer.Text.Length, composer.Text.Length, composer.Text.Length); composer.Insert(Gift());
            composer.Replace(composer.ActionStart, 1, "");
            Check("composer_identical_plain_sentence_is_not_removed", composer.Draft == null && composer.Text == Gift().Sentence + " ");
            composer.SetText("prefix suffix"); composer.Select(6, 6, 6); composer.Insert(Gift());
            composer.Replace(composer.ActionStart + 3, 0, "new words");
            Check("composer_typing_inside_cancels_action", composer.Draft == null && !composer.Text.Contains("denars"));
            composer.SetText("hello"); composer.Select(5, 5, 5); composer.Insert(Gift());
            composer.Replace(composer.ActionStart + composer.ActionLength, 0, " for your help");
            Check("composer_typing_after_retains_action", composer.HasValidAttachment && composer.Text.EndsWith(" for your help", StringComparison.Ordinal));
            int actionStart = composer.ActionStart; composer.Insert(Gift(200));
            Check("composer_revises_only_attached_sentence", composer.HasValidAttachment && composer.ActionStart == actionStart && composer.Text == "hello I wish to give you 200 denars. for your help");
            composer.SetText("I wish to give you 200 denars.");
            Check("composer_plain_text_never_creates_action", composer.Draft == null);
            Check("single_clause_is_complete_offer", AgreementRules.Validate(single, ReignActionCatalog.Find, 1).Count == 0);
            string original = single.TermsHash(); single.State = AgreementState.Offered;
            Check("accept_requires_exact_revision", !AgreementRules.Accept(single, "npc", "old-hash"));
            Check("player_cannot_accept_for_npc", !AgreementRules.Accept(single, "third-party", original));
            AgreementRules.Accept(single, "player", original);
            Check("one_signature_is_not_execution", single.State == AgreementState.Offered && !single.HasBothConsents);
            var invitedOffer = ActionNegotiation.CreateProposal(null, new ActionDecision { Kind = "propose", Clauses = new List<ActionProposedTerm>
            {
                new ActionProposedTerm { ActionId = "give.gold", ActorId = "npc", RecipientId = "player",
                    Arguments = new List<ActionArgument> { new ActionArgument { Key = "quantity", Value = "5" } } },
                new ActionProposedTerm { ActionId = "give.gold", ActorId = "player", RecipientId = "npc",
                    Arguments = new List<ActionArgument> { new ActionArgument { Key = "quantity", Value = "20" } } }
            } }, "player", "npc", "conversation", 1);
            invitedOffer.NpcAcceptedHash = invitedOffer.TermsHash();
            var invitedBackend = new AgreementTestBackend { Npc = 10 };
            var invitedJournal = new AgreementJournal { Agreement = invitedOffer };
            bool invitedBlocked = false;
            try { ReignAgreementEngine.Commit(invitedJournal, invitedBackend, 1); }
            catch (InvalidOperationException) { invitedBlocked = true; }
            Check("npc_invited_offer_cannot_reserve_or_execute_without_player", invitedBlocked
                && invitedOffer.PlayerAcceptedHash == "" && !invitedOffer.HasRequiredConsents
                && invitedBackend.Player == 100 && invitedBackend.Npc == 10 && invitedBackend.Escrow == 0 && invitedBackend.Applies == 0);
            Check("npc_invited_offer_player_reviews_exact_hash", !AgreementRules.Accept(invitedOffer, "player", "stale")
                && AgreementRules.Accept(invitedOffer, "player", invitedOffer.TermsHash()));
            ReignAgreementEngine.Commit(invitedJournal, invitedBackend, 1);
            Check("npc_invited_offer_executes_only_after_exact_player_consent", invitedBackend.Applies == 2
                && invitedBackend.Player == 85 && invitedBackend.Npc == 25 && invitedBackend.Escrow == 0);
            AgreementRules.Accept(single, "npc", original);
            Check("two_exact_signatures", single.State == AgreementState.Accepted && single.HasBothConsents);
            single.Clauses[0].Arguments["quantity"] = "200";
            Check("tampered_quantity_revokes_consent", !single.HasBothConsents);
            single.Revise(); Check("counteroffer_clears_approvals", single.PlayerAcceptedHash == "" && single.NpcAcceptedHash == "" && single.Revision == 2);
            var compound = Gift();
            compound.Clauses.Add(new ActionClause { ActionId = "give.troop", ActorId = "npc", RecipientId = "player", DisplayText = "and you will give me 12 archers", Arguments = new SortedDictionary<string, string> { ["assetId"] = "archer", ["quantity"] = "12" } });
            compound.Clauses.Add(new ActionClause { ActionId = "give.troop", ActorId = "npc", RecipientId = "player", DisplayText = "and 6 cavalry", Arguments = new SortedDictionary<string, string> { ["assetId"] = "cavalry", ["quantity"] = "6" } });
            Check("many_to_many_directions", AgreementRules.Validate(compound, ReignActionCatalog.Find, 0).Count == 0 && compound.Clauses.Count == 3);
            var move = Gift(); move.Clauses[0] = new ActionClause { ActionId = "go_to_settlement", ActorId = "player", RecipientId = "npc", Arguments = new SortedDictionary<string, string> { ["targetSettlementId"] = "town" } };
            Check("npc_cannot_move_player", AgreementRules.Validate(move, ReignActionCatalog.Find, 0).Any(x => x.Contains("movement")));
            foreach (string amount in new[] { "0", "-1", "2147483648", "NaN", "1.5", "" })
            {
                var bad = Gift(); bad.Clauses[0].Arguments["quantity"] = amount;
                Check("bad_quantity_" + (amount.Length == 0 ? "empty" : amount), AgreementRules.Validate(bad, ReignActionCatalog.Find, 0).Count > 0);
            }
            var unknown = Gift(); unknown.Clauses[0].ActionId = "unknown";
            Check("unknown_action_rejected", AgreementRules.Validate(unknown, ReignActionCatalog.Find, 0).Count > 0);
            var injection = Gift(); injection.Clauses[0].Arguments["forceExecution"] = "true";
            Check("unregistered_argument_rejected", AgreementRules.Validate(injection, ReignActionCatalog.Find, 0).Count > 0);
            var condition = Gift(); condition.Clauses[0].ConditionKind = "the model thinks the promise is satisfied";
            Check("free_prose_is_not_a_condition", AgreementRules.Validate(condition, ReignActionCatalog.Find, 0).Count > 0);
            Check("quote_range_enforced", AgreementRules.IsReasonableQuote(100, 100, 0.5, 2) && !AgreementRules.IsReasonableQuote(1000000, 100, 0.5, 2));
            Check("catalog_unique_ids", ReignActionCatalog.All.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() == ReignActionCatalog.All.Count);
            Check("catalog_includes_all_legacy_commands", ReadDictionaryList(ActionCatalog(), "commands").All(x => ReignActionCatalog.Find(ReadString(x, "command", "")) != null));
            Check("political_scopes_distinct", new[] { "government.join_party", "government.back_platform", "government.support_vote" }.All(id => ReignActionCatalog.Find(id)?.ConversationEligible == true));
            var cultureGift = Gift(); string h = cultureGift.TermsHash(); var priorCulture = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Check("hash_culture_independent", cultureGift.TermsHash() == h); }
            finally { CultureInfo.CurrentCulture = priorCulture; }

            var remote = Gift(40); Agree(remote); var backend = new AgreementTestBackend { TravelDays = 2 }; var journal = new AgreementJournal { Agreement = remote };
            ReignAgreementEngine.Commit(journal, backend, 1);
            Check("remote_money_reserved", backend.Player == 60 && backend.Npc == 0 && backend.Escrow == 40 && remote.State == AgreementState.InTransit);
            ReignAgreementEngine.Advance(journal, backend, 2);
            Check("no_payment_before_arrival", backend.Npc == 0 && backend.Applies == 0);
            ReignAgreementEngine.Advance(journal, backend, 3);
            Check("verified_arrival_payment", backend.Player == 60 && backend.Npc == 40 && backend.Escrow == 0 && remote.State == AgreementState.Completed);
            ReignAgreementEngine.Commit(journal, backend, 4); ReignAgreementEngine.Advance(journal, backend, 5);
            Check("replay_no_duplicate_money", backend.Applies == 1 && backend.Player + backend.Npc + backend.Escrow == 100);
            var restored = Json.Deserialize<AgreementJournal>(Json.Serialize(journal));
            ReignAgreementEngine.Advance(restored, backend, 6);
            Check("saved_receipt_no_duplicate", backend.Applies == 1 && restored.Agreement.TermsHash() == journal.CommittedHash);
            var cancel = Gift(30); Agree(cancel); backend = new AgreementTestBackend { TravelDays = 10 }; journal = new AgreementJournal { Agreement = cancel };
            ReignAgreementEngine.Commit(journal, backend, 0); ReignAgreementEngine.Cancel(journal, backend, 1);
            Check("cancel_returns_reserved_money", backend.Player == 100 && backend.Npc == 0 && backend.Escrow == 0 && cancel.State == AgreementState.Cancelled);
            var failure = Gift(25); Agree(failure); backend = new AgreementTestBackend { FailDelivery = true }; journal = new AgreementJournal { Agreement = failure };
            ReignAgreementEngine.Commit(journal, backend, 0);
            Check("delivery_failure_returns_assets", backend.Player == 100 && backend.Npc == 0 && backend.Escrow == 0 && failure.State == AgreementState.Impossible);

            var conditional = Gift(20);
            conditional.Clauses.Add(new ActionClause { ActionId = "give.gold", ActorId = "npc", RecipientId = "player", DueDay = 3,
                Arguments = new SortedDictionary<string, string> { ["quantity"] = "10" }, DisplayText = "and you will give me 10 denars on day 3" });
            Agree(conditional); backend = new AgreementTestBackend { Npc = 10 }; journal = new AgreementJournal { Agreement = conditional };
            ReignAgreementEngine.Commit(journal, backend, 0);
            Check("reciprocal_assets_wait_for_both_terms", backend.Player == 80 && backend.Npc == 0 && backend.Escrow == 30 && backend.Applies == 0);
            ReignAgreementEngine.Advance(journal, backend, 3);
            Check("reciprocal_assets_settle_together", backend.Player == 90 && backend.Npc == 20 && backend.Escrow == 0 && conditional.State == AgreementState.Completed);

            var installments = Gift(25); installments.Clauses[0].Installments = 2; installments.Clauses[0].IntervalDays = 2; Agree(installments);
            backend = new AgreementTestBackend(); journal = new AgreementJournal { Agreement = installments };
            ReignAgreementEngine.Commit(journal, backend, 0);
            Check("installment_reserves_total_pays_one", backend.Player == 50 && backend.Npc == 25 && backend.Escrow == 25);
            bool cancelledAfterPayment = false;
            try { ReignAgreementEngine.Cancel(journal, backend, 1); cancelledAfterPayment = true; } catch (InvalidOperationException) { }
            Check("cannot_cancel_after_first_installment", !cancelledAfterPayment);
            backend.FailDelivery = true; ReignAgreementEngine.Advance(journal, backend, 2);
            Check("failed_installment_returns_only_unpaid_balance", backend.Player == 75 && backend.Npc == 25 && backend.Escrow == 0 && installments.State == AgreementState.RecoveryRequired);

            var uncertain = Gift(30); Agree(uncertain); backend = new AgreementTestBackend { FailRollback = true }; journal = new AgreementJournal { Agreement = uncertain };
            ReignAgreementEngine.Commit(journal, backend, 0);
            Check("uncertain_native_rollback_never_duplicates_refund", backend.Player == 70 && backend.Npc == 30 && backend.Escrow == 0 && uncertain.State == AgreementState.RecoveryRequired && journal.RecoveryDetails.Length > 0);
            ReignAgreementEngine.Advance(journal, backend, 1);
            Check("uncertain_delivery_is_not_replayed", backend.Player + backend.Npc + backend.Escrow == 100 && backend.Applies == 1);
            var service = Gift(25);
            service.Clauses.Add(new ActionClause { ActionId = "go_to_settlement", ActorId = "npc", RecipientId = "player",
                Arguments = new SortedDictionary<string, string> { ["targetSettlementId"] = "town" } });
            Agree(service); backend = new AgreementTestBackend(); journal = new AgreementJournal { Agreement = service };
            ReignAgreementEngine.Commit(journal, backend, 0);
            Check("issuing_an_order_is_not_fulfillment", backend.ServicesStarted == 1 && backend.Npc == 0 && service.State == AgreementState.Waiting);
            ReignAgreementEngine.Advance(journal, backend, 1);
            Check("pending_service_is_not_reissued", backend.ServicesStarted == 1 && backend.Npc == 0);
            backend.ServiceComplete = true; ReignAgreementEngine.Advance(journal, backend, 2);
            Check("observed_service_releases_payment", service.State == AgreementState.Completed && backend.Npc == 25 && backend.ServicesStarted == 1);
            var interruptedService = Gift(25);
            interruptedService.Clauses.Add(new ActionClause { ActionId = "court.serve_office", ActorId = "npc", RecipientId = "player",
                Arguments = new SortedDictionary<string, string> { ["office"] = "Spymaster" } });
            Agree(interruptedService); backend = new AgreementTestBackend(); journal = new AgreementJournal { Agreement = interruptedService };
            ReignAgreementEngine.Commit(journal, backend, 0);
            backend.ServiceUncertain = true; ReignAgreementEngine.Advance(journal, backend, 1);
            Check("interrupted_remote_service_holds_payment_for_reconciliation", interruptedService.State == AgreementState.RecoveryRequired && backend.Npc == 0 && backend.Escrow == 25 && backend.ServicesStarted == 1);
            ReignAgreementEngine.Advance(journal, backend, 2);
            Check("interrupted_remote_service_is_not_reissued", backend.ServicesStarted == 1 && backend.Escrow == 25);

            var declaration = Gift();
            declaration.Clauses[0] = new ActionClause { ActionId = "start_ruling_clan_rebellion", ActorId = "player", RecipientId = "npc",
                Arguments = new SortedDictionary<string, string> { ["actorKingdomId"] = "realm", ["actorClanId"] = "clan" },
                DisplayText = "I declare rebellion against your rule of the realm, as leader of my clan" };
            declaration.State = AgreementState.Offered;
            AgreementRules.Accept(declaration, "player", declaration.TermsHash());
            backend = new AgreementTestBackend { ServiceComplete = true }; journal = new AgreementJournal { Agreement = declaration };
            ReignAgreementEngine.Commit(journal, backend, 0);
            Check("player_declaration_needs_no_ruler_veto", declaration.State == AgreementState.Completed && backend.ServicesStarted == 1
                && declaration.NpcAcceptedHash.Length == 0 && !declaration.HasBothConsents);
            restored = Json.Deserialize<AgreementJournal>(Json.Serialize(journal));
            ReignAgreementEngine.Commit(restored, backend, 1); ReignAgreementEngine.Advance(restored, backend, 2);
            Check("saved_declaration_is_not_reissued", backend.ServicesStarted == 1 && restored.Agreement.HasRequiredConsents);
            var unaccepted = Gift(); unaccepted.State = AgreementState.Accepted; unaccepted.PlayerAcceptedHash = unaccepted.TermsHash();
            bool ordinaryRejected = false;
            try { ReignAgreementEngine.Commit(new AgreementJournal { Agreement = unaccepted }, backend, 0); }
            catch (InvalidOperationException) { ordinaryRejected = true; }
            Check("declaration_exception_does_not_authorize_ordinary_gift", ordinaryRejected && backend.Applies == 0);

            var proposal = Gift(); proposal.ConversationId = "conversation"; proposal.State = AgreementState.Offered; proposal.PlayerAcceptedHash = proposal.TermsHash();
            var wire = new Dictionary<string, object> { ["structuredActionProtocolVersion"] = ActionNegotiation.ProtocolVersion,
                ["playerHeroStringId"] = "player", ["speakerHeroStringId"] = "npc",
                ["structuredActionContext"] = TestDict("playerId", "player", "npcId", "npc", "conversationId", "conversation", "proposal", proposal) };
            wire = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(wire));
            foreach (string kind in new[] { "accept", "reject" })
            {
                var answer = TestDict("reply", kind == "accept" ? "I accept your offer." : "I refuse your offer.",
                    "structuredActionDecision", new ActionDecision { Kind = kind, ProposalId = proposal.Id, Revision = proposal.Revision, TermsHash = proposal.TermsHash() });
                CaptureStructuredActionDecision(wire, Json.Deserialize<Dictionary<string, object>>(Json.Serialize(answer)));
                var envelope = ReadDictionary(wire, "validatedStructuredActionDecision");
                Check("hidden_" + kind + "_retains_exact_offer_identity", ReadBool(envelope, "valid", false) && ReadString(envelope, "kind", "") == kind
                    && ReadString(envelope, "termsHash", "") == proposal.TermsHash() && proposal.NpcAcceptedHash.Length == 0);
            }
            CaptureStructuredActionDecision(wire, TestDict("reply", "Yes, take it."));
            Check("spoken_yes_without_hidden_decision_grants_nothing", !ReadBool(ReadDictionary(wire, "validatedStructuredActionDecision"), "valid", true)
                && proposal.NpcAcceptedHash.Length == 0);
            foreach (string kind in new[] { "accept", "reject", "none" })
            {
                var answer = TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "Visible answer",
                    "structuredActionDecision", TestDict("kind", kind))));
                string before = ReadString(answer, "content", "");
                bool repairCalled = false;
                var result = EnforceConversationAgencyResponse(answer, TestDict(),
                    TestDict("conversationAgency", TestDict("peerId", "player")), wire,
                    "isolated", "isolated", "dialogue", "npc",
                    responder: _ => { repairCalled = true; return TestDict("ok", false); }, persist: false);
                Check("typed_" + kind + "_does_not_require_legacy_negotiation_metadata",
                    !repairCalled && ReadString(result, "content", "") == before);
            }
            results.AddRange(RunStructuredActionPromptScopeTests());
            return results;
        }

        private static List<Dictionary<string, object>> RunStructuredActionPromptScopeTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object evidence = null) => results.Add(TestDict(
                "caseId", "structured_action_scope_" + id, "passed", pass, "summary", id, "evidence", evidence));
            foreach (bool leader in new[] { false, true })
                foreach (bool owns in new[] { false, true })
                    foreach (bool fief in new[] { false, true })
                        Check("settlement_" + leader + "_" + owns + "_" + fief,
                            ActionPromptScope.CanGiveSettlement(leader, owns, fief) == (leader && owns && fief));

            var candidates = new List<ActionPromptAsset>();
            foreach (string actor in new[] { "player", "npc" })
                foreach (string kind in new[] { "item", "troop", "prisoner", "settlement", "workshop", "ship" })
                    for (int i = 0; i < 100; i++)
                        candidates.Add(new ActionPromptAsset { Name = "Asset " + i.ToString("D3"), Available = 5,
                            Clause = new ActionClause { ActorId = actor, RecipientId = actor == "player" ? "npc" : "player", ActionId = "give." + kind,
                                Arguments = new SortedDictionary<string, string> { ["assetId"] = kind + i, ["quantity"] = "1" } } });
            var sample = ActionPromptScope.Select(candidates, null);
            Check("ordinary_discovery_bounded_both_participants_all_kinds", sample.Count == 48
                && sample.Select(x => x.Clause.ActorId + "/" + x.Clause.ActionId).Distinct().Count() == 12);
            var mention = ActionPromptScope.Select(candidates, null, "I have news of prisoner99.");
            Check("named_asset_ranked_without_authorizing_terms", mention.Count == 48
                && mention.Count(x => x.Clause.Arguments["assetId"] == "prisoner99") == 2);

            var offer = new ActionAgreement { PlayerId = "player", NpcId = "npc", ConversationId = "conversation",
                Origin = ActionOrigin.Conversation, ProposerId = "player", State = AgreementState.Offered };
            foreach (var asset in candidates.Where(x => x.Clause.ActionId == "give.item" && x.Clause.ActorId == "player").Skip(36))
                offer.Clauses.Add(asset.Clause);
            offer.PlayerAcceptedHash = offer.TermsHash();
            var allTerms = ActionPromptScope.Select(candidates, offer);
            Check("all_64_explicit_assets_survive_discovery_limit", offer.Clauses.All(c => allTerms.Any(x => ActionPromptScope.SameAsset(c, x.Clause)))
                && allTerms.Count == 112 && offer.HasRequiredConsents == false);
            var originalHash = offer.TermsHash();
            var exact = new ActionClause { ActionId = "give.item", ActorId = "npc",
                Arguments = new SortedDictionary<string, string> { ["assetId"] = "sword", ["variantId"] = "fine", ["equipmentSet"] = "battle", ["equipmentSlot"] = "0" } };
            var wrong = new ActionClause { ActionId = "give.item", ActorId = "npc",
                Arguments = new SortedDictionary<string, string>(exact.Arguments) };
            wrong.Arguments["variantId"] = "rusty";
            Check("variants_not_conflated", !ActionPromptScope.SameAsset(exact, wrong));
            wrong.Arguments["variantId"] = "fine"; wrong.Arguments["equipmentSet"] = "civilian";
            Check("equipment_slots_not_conflated", !ActionPromptScope.SameAsset(exact, wrong));

            var rows = candidates.Select(asset => TestDict("actorId", asset.Clause.ActorId, "actionId", asset.Clause.ActionId,
                "assetId", asset.Clause.Arguments["assetId"], "name", asset.Name, "available", asset.Available,
                "arguments", asset.Clause.Arguments, "unitDenars", 123, "valueSource", "native_test_fixture")).ToList();
            var context = TestDict("playerId", "player", "npcId", "npc", "conversationId", "conversation",
                "nativeReceipt", TestDict("state", "Completed", "id", "receipt-exact"),
                "appraisals", TestDict("assets", rows, "scope", ActionPromptScope.Policy, "selectionComplete", true,
                    "playerGold", 1000, "npcGold", 2000, "npcExchangeMinimumRatio", 0.5, "npcExchangeMaximumRatio", 2.0));
            var payload = TestDict("structuredActionProtocolVersion", 1, "structuredActionContext", context,
                "playerHeroStringId", "player", "speakerHeroStringId", "npc");
            string before = Json.Serialize(payload);
            var scoped = StructuredActionPromptContext(payload);
            Check("server_rescopes_oversized_context_without_mutating_request", Json.Serialize(payload) == before
                && ReadDictionaryList(ReadDictionary(scoped, "appraisals"), "assets").Count == 48);
            Check("native_receipt_balances_and_price_bounds_retained", Json.Serialize(scoped["nativeReceipt"]) == Json.Serialize(context["nativeReceipt"])
                && ReadInt(ReadDictionary(scoped, "appraisals"), "playerGold", 0) == 1000
                && ReadInt(ReadDictionary(scoped, "appraisals"), "npcGold", 0) == 2000
                && ReadDouble(ReadDictionary(scoped, "appraisals"), "npcExchangeMinimumRatio", 0) == 0.5);
            foreach (int receiptState in new[] { 5, 7 })
            {
                var receiptWire = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(payload));
                var receiptContext = ReadDictionary(receiptWire, "structuredActionContext");
                receiptContext["nativeReceipt"] = TestDict("OperationId", "saved-npc-offer", "State", receiptState,
                    "TermsHash", "already-consented-exact-terms");
                receiptWire["structuredActionContext"] = receiptContext;
                string receiptPrompt = StructuredActionPrompt(receiptWire);
                CaptureStructuredActionDecision(receiptWire, Json.Deserialize<Dictionary<string, object>>(
                    Json.Serialize(TestDict("structuredActionDecision", new ActionDecision { Kind = "none" }))));
                Check("saved_offer_receipt_" + receiptState + "_acknowledged_without_new_consent",
                    receiptPrompt.Contains("NATIVE RECEIPT ACKNOWLEDGMENT:")
                    && ReadBool(ReadDictionary(receiptWire, "validatedStructuredActionDecision"), "valid", false)
                    && ReadString(ReadDictionary(receiptWire, "validatedStructuredActionDecision"), "kind", "") == "none"
                    && ReadDictionary(StructuredActionPromptContext(receiptWire), "proposal") == null);
                CaptureStructuredActionDecision(receiptWire, Json.Deserialize<Dictionary<string, object>>(
                    Json.Serialize(TestDict("structuredActionDecision",
                        new ActionDecision { Kind = "accept", ProposalId = "saved-npc-offer", Revision = 1, TermsHash = "already-consented-exact-terms" }))));
                Check("saved_offer_receipt_" + receiptState + "_cannot_grant_second_acceptance",
                    !ReadBool(ReadDictionary(receiptWire, "validatedStructuredActionDecision"), "valid", true)
                    && ReadString(ReadDictionary(receiptWire, "validatedStructuredActionDecision"), "error", "")
                        .Contains("The decision does not identify the exact offered revision."));
            }
            context["proposal"] = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(offer));
            context["termsHash"] = originalHash;
            scoped = StructuredActionPromptContext(payload);
            Check("entire_offer_identity_wording_schedule_retained", Json.Serialize(scoped["proposal"]) == Json.Serialize(context["proposal"])
                && ReadString(scoped, "termsHash", "") == originalHash && offer.TermsHash() == originalHash
                && ReadDictionaryList(ReadDictionary(scoped, "appraisals"), "assets").Count == 112);
            Check("pending_proposal_retains_its_decision_even_with_receipt",
                !StructuredActionPrompt(payload).Contains("NATIVE RECEIPT ACKNOWLEDGMENT:"));
            string beforeModelView = Json.Serialize(scoped);
            var modelView = StructuredActionModelFacts(scoped);
            var modelClauses = ReadDictionaryList(ReadDictionary(modelView, "proposal"), "Clauses");
            bool MatchesArgumentPairs(Dictionary<string, object> row, string key, IDictionary<string, string> expected)
            {
                var pairs = ReadDictionaryList(row, key);
                return pairs.Count == expected.Count
                    && pairs.Select(p => ReadString(p, "key", "")).Distinct(StringComparer.Ordinal).Count() == expected.Count
                    && pairs.All(p => p.Count == 2 && p.ContainsKey("key") && p.ContainsKey("value")
                        && expected.TryGetValue(ReadString(p, "key", ""), out var value)
                        && ReadString(p, "value", "") == value);
            }
            Check("model_view_keeps_all_64_terms_and_exact_argument_pairs", modelClauses.Count == 64
                && modelClauses.Zip(offer.Clauses, (row, clause) => MatchesArgumentPairs(row, "Arguments", clause.Arguments)).All(match => match));
            var modelAssets = ReadDictionaryList(ReadDictionary(modelView, "appraisals"), "assets");
            var nativeAssets = ReadDictionaryList(ReadDictionary(scoped, "appraisals"), "assets");
            Check("model_view_discovery_arguments_also_use_response_shape",
                modelAssets.Count == 112 && nativeAssets.Count == modelAssets.Count
                && modelAssets.Zip(nativeAssets, (row, native) => MatchesArgumentPairs(row, "arguments",
                    ReadDictionary(native, "arguments").ToDictionary(p => p.Key, p => Convert.ToString(p.Value,
                        System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal))).All(match => match));
            void RestoreArguments(Dictionary<string, object> row, string key)
            {
                row[key] = ReadDictionaryList(row, key).ToDictionary(p => ReadString(p, "key", ""), p => p["value"], StringComparer.Ordinal);
            }
            var restoredView = CloneDictionary(modelView);
            foreach (var clause in ReadDictionaryList(ReadDictionary(restoredView, "proposal"), "Clauses")) RestoreArguments(clause, "Arguments");
            foreach (var row in ReadDictionaryList(ReadDictionary(restoredView, "appraisals"), "assets")) RestoreArguments(row, "arguments");
            Check("model_argument_projection_lossless_for_identity_wording_schedules_and_facts",
                Json.Serialize(restoredView) == beforeModelView && Json.Serialize(scoped) == beforeModelView
                && Json.Deserialize<ActionAgreement>(Json.Serialize(ReadDictionary(restoredView, "proposal"))).TermsHash() == originalHash);
            Check("writer_renders_projected_facts_without_native_context_mutation",
                StructuredActionPrompt(payload).Contains(Json.Serialize(modelView))
                && Json.Serialize(ReadDictionary(payload, "structuredActionContext")) == Json.Serialize(context));

            var votesOffer = new ActionAgreement { Id = "four_votes_paid_offer", PlayerId = "player", NpcId = "npc",
                ConversationId = "conversation", ProposerId = "player", Origin = ActionOrigin.Conversation, State = AgreementState.Offered };
            for (int i = 0; i < 4; i++) votesOffer.Clauses.Add(new ActionClause { ActionId = "government.support_vote",
                ActorId = "npc", RecipientId = "player", Arguments = new SortedDictionary<string, string>
                    { ["businessId"] = "business_" + i, ["optionId"] = "accept" } });
            votesOffer.Clauses.Add(new ActionClause { ActionId = "give.gold", ActorId = "player", RecipientId = "npc",
                Arguments = new SortedDictionary<string, string> { ["quantity"] = "5" } });
            votesOffer.PlayerAcceptedHash = votesOffer.TermsHash();
            var votesWire = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(TestDict("structuredActionProtocolVersion", 1,
                "playerHeroStringId", "player", "speakerHeroStringId", "npc", "structuredActionContext",
                TestDict("playerId", "player", "npcId", "npc", "conversationId", "conversation", "proposal", votesOffer))));
            string beforeVotes = Json.Serialize(ReadDictionary(votesWire, "structuredActionContext"));
            var votesView = StructuredActionModelFacts(ReadDictionary(votesWire, "structuredActionContext"));
            var counterTerms = ReadDictionaryList(ReadDictionary(votesView, "proposal"), "Clauses").Take(4)
                .Select(c => TestDict("actionId", c["ActionId"], "actorId", c["ActorId"], "recipientId", c["RecipientId"],
                    "arguments", c["Arguments"], "dueDay", c["DueDay"], "durationDays", c["DurationDays"],
                    "installments", c["Installments"], "intervalDays", c["IntervalDays"],
                    "conditionKind", c["ConditionKind"], "conditionTargetId", c["ConditionTargetId"])).ToList();
            var counterAnswer = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(TestDict("structuredActionDecision",
                TestDict("kind", "counter", "proposalId", votesOffer.Id, "revision", votesOffer.Revision,
                    "termsHash", votesOffer.TermsHash(), "reason", "Four votes without payment.", "clauses", counterTerms))));
            CaptureStructuredActionDecision(votesWire, counterAnswer);
            var counterEnvelope = ReadDictionary(votesWire, "validatedStructuredActionDecision");
            var newVotes = ReadBool(counterEnvelope, "valid", false)
                ? Json.Deserialize<ActionAgreement>(Json.Serialize(counterEnvelope["proposal"])) : null;
            Check("four_vote_no_payment_counter_round_trips_response_shape", newVotes != null
                && newVotes.Clauses.Count == 4 && newVotes.Clauses.All(c => c.ActionId == "government.support_vote")
                && newVotes.Clauses.Select(c => c.Arguments["businessId"]).SequenceEqual(new[] { "business_0", "business_1", "business_2", "business_3" })
                && newVotes.Revision == votesOffer.Revision + 1);
            Check("counter_projection_grants_no_signature_or_execution", newVotes != null && !newVotes.HasRequiredConsents
                && newVotes.PlayerAcceptedHash == "" && newVotes.NpcAcceptedHash == ""
                && Json.Serialize(ReadDictionary(votesWire, "structuredActionContext")) == beforeVotes);
            var badShape = CloneDictionary(counterAnswer);
            ReadDictionaryList(ReadDictionary(badShape, "structuredActionDecision"), "clauses")[0]["arguments"]
                = TestDict("businessId", "business_0", "optionId", "accept");
            CaptureStructuredActionDecision(votesWire, badShape);
            Check("native_dictionary_counter_still_refused_not_repaired_into_consent",
                !ReadBool(ReadDictionary(votesWire, "validatedStructuredActionDecision"), "valid", true));
            foreach (string invalidKey in new[] { "businessId", "script" })
            {
                var invalidCounter = CloneDictionary(counterAnswer);
                var term = ReadDictionaryList(ReadDictionary(invalidCounter, "structuredActionDecision"), "clauses")[0];
                var pairs = ReadDictionaryList(term, "arguments"); pairs.Add(TestDict("key", invalidKey, "value", "unauthorized"));
                term["arguments"] = pairs;
                CaptureStructuredActionDecision(votesWire, invalidCounter);
                Check("counter_pair_" + invalidKey + "_duplicate_or_unregistered_key_refused",
                    !ReadBool(ReadDictionary(votesWire, "validatedStructuredActionDecision"), "valid", true));
            }
            foreach (string role in new[] { "player_only", "npc_only", "both" })
            {
                var roleOffer = new ActionAgreement { Id = "consent_role_" + role, Revision = 3,
                    PlayerId = "player", NpcId = "npc", ConversationId = "conversation",
                    Origin = ActionOrigin.Conversation, ProposerId = "player", State = AgreementState.Offered };
                foreach (string actor in role == "both" ? new[] { "player", "npc" }
                    : new[] { role == "player_only" ? "player" : "npc" })
                    roleOffer.Clauses.Add(new ActionClause { ActionId = "give.gold", ActorId = actor,
                        RecipientId = actor == "player" ? "npc" : "player",
                        Arguments = new SortedDictionary<string, string> { ["quantity"] = "5" } });
                string roleHash = roleOffer.TermsHash();
                roleOffer.PlayerAcceptedHash = roleHash;
                var roleWire = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(TestDict("structuredActionProtocolVersion", 1,
                    "playerHeroStringId", "player", "speakerHeroStringId", "npc",
                    "structuredActionContext", TestDict("playerId", "player", "npcId", "npc",
                        "conversationId", "conversation", "proposal", roleOffer, "termsHash", roleHash))));
                string roleBefore = Json.Serialize(roleWire);
                var roleDecision = new ActionDecision { Kind = "accept", ProposalId = roleOffer.Id,
                    Revision = roleOffer.Revision, TermsHash = roleHash };
                CaptureStructuredActionDecision(roleWire, Json.Deserialize<Dictionary<string, object>>(
                    Json.Serialize(TestDict("structuredActionDecision", roleDecision))));
                var acceptedRole = ReadDictionary(roleWire, "validatedStructuredActionDecision");
                string rolePrompt = StructuredActionPrompt(roleWire);
                Check("actor_role_" + role + "_allows_exact_package_consent",
                    ReadBool(acceptedRole, "valid", false) && ReadString(acceptedRole, "kind", "") == "accept"
                    && rolePrompt.Contains("Do not return none solely because you are not a clause's actor.")
                    && rolePrompt.Contains("it cannot supply the player's consent")
                    && rolePrompt.Contains("replace required government votes"));
                Check("actor_role_" + role + "_decision_does_not_sign_or_execute",
                    roleOffer.PlayerAcceptedHash == roleHash && roleOffer.NpcAcceptedHash == ""
                    && !roleOffer.HasRequiredConsents && roleOffer.State == AgreementState.Offered
                    && Json.Serialize(ReadDictionary(roleWire, "structuredActionContext"))
                        == Json.Serialize(ReadDictionary(Json.Deserialize<Dictionary<string, object>>(roleBefore), "structuredActionContext")));
                roleDecision.Revision++;
                CaptureStructuredActionDecision(roleWire, Json.Deserialize<Dictionary<string, object>>(
                    Json.Serialize(TestDict("structuredActionDecision", roleDecision))));
                Check("actor_role_" + role + "_stale_revision_still_refused",
                    !ReadBool(ReadDictionary(roleWire, "validatedStructuredActionDecision"), "valid", true));
                var unsignedRoleWire = Json.Deserialize<Dictionary<string, object>>(roleBefore);
                ReadDictionary(ReadDictionary(unsignedRoleWire, "structuredActionContext"), "proposal")["PlayerAcceptedHash"] = "";
                roleDecision.Revision = roleOffer.Revision;
                CaptureStructuredActionDecision(unsignedRoleWire, Json.Deserialize<Dictionary<string, object>>(
                    Json.Serialize(TestDict("structuredActionDecision", roleDecision))));
                Check("actor_role_" + role + "_cannot_supply_missing_player_consent",
                    !ReadBool(ReadDictionary(unsignedRoleWire, "validatedStructuredActionDecision"), "valid", true)
                    && ReadString(ReadDictionary(ReadDictionary(unsignedRoleWire, "structuredActionContext"), "proposal"), "PlayerAcceptedHash", "unexpected") == "");
            }
            context.Remove("proposal"); context.Remove("termsHash");
            ReadDictionary(context, "appraisals").Remove("scope");
            // Assign explicitly because readers may project JSON-backed dictionaries.
            var legacyAppraisals = CloneDictionary(ReadDictionary(context, "appraisals")); legacyAppraisals.Remove("scope");
            context["appraisals"] = legacyAppraisals;
            scoped = StructuredActionPromptContext(payload);
            Check("old_world_map_rows_never_imply_ownership", ReadDictionaryList(ReadDictionary(scoped, "appraisals"), "assets")
                .All(row => ReadString(row, "actionId", "") != "give.settlement")
                && ReadInt(ReadDictionary(scoped, "appraisals"), "omittedUnverifiedSettlementRows", 0) == 200);
            string compact = StructuredActionCatalogPrompt();
            using (var catalog = System.Text.Json.JsonDocument.Parse(compact.Substring(compact.IndexOf('{'))))
            {
                var definitions = ReignActionCatalog.All.Where(d => d.ConversationEligible).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
                var actionRows = catalog.RootElement.GetProperty("actions");
                var parameterRows = catalog.RootElement.GetProperty("parameters");
                bool equal = actionRows.GetArrayLength() == definitions.Length;
                for (int i = 0; equal && i < definitions.Length; i++)
                {
                    var row = actionRows[i]; var definition = definitions[i];
                    equal &= row[0].GetString() == definition.Id && row[1].GetString() == definition.Label
                        && row[2].GetInt32() == (definition.MovesActor ? 1 : 0) && row[3].GetArrayLength() == definition.Parameters.Count;
                    for (int j = 0; equal && j < definition.Parameters.Count; j++)
                    {
                        var parameter = definition.Parameters[j]; var cell = parameterRows[row[3][j].GetInt32()];
                        equal &= cell[0].GetString() == parameter.Key && cell[1].GetString() == parameter.Kind
                            && cell[2].GetInt32() == (parameter.Required ? 1 : 0)
                            && cell[3].EnumerateArray().Select(x => x.GetString()).SequenceEqual(parameter.Choices);
                    }
                }
                Check("every_action_and_parameter_choice_round_trips", equal, TestDict("actions", definitions.Length, "characters", compact.Length));
                string previousCatalog = Json.Serialize(definitions.Select(d => new object[] { d.Id, d.Label, d.MovesActor ? 1 : 0,
                    d.Parameters.Select(p => new object[] { p.Key, p.Kind, p.Required ? 1 : 0, p.Choices }).ToArray() }).ToArray());
                Check("shared_parameter_catalog_reduces_characters", compact.Length < previousCatalog.Length,
                    TestDict("actions", definitions.Length, "uniqueParameters", parameterRows.GetArrayLength(), "beforeCharacters", previousCatalog.Length, "afterCharacters", compact.Length));
            }
            string prompt = StructuredActionPrompt(payload);
            var inventedIdentityOffer = new ActionDecision { Kind = "propose", ProposalId = "prop_lucon_grain_20den_91093", Revision = 0,
                TermsHash = "grain1_gold20_v1", Clauses = new List<ActionProposedTerm> {
                    new ActionProposedTerm { ActionId = "give.gold", ActorId = "npc", RecipientId = "player",
                        Arguments = new List<ActionArgument> { new ActionArgument { Key = "quantity", Value = "5" } } } } };
            var inventedIdentityWire = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(payload));
            CaptureStructuredActionDecision(inventedIdentityWire, Json.Deserialize<Dictionary<string, object>>(
                Json.Serialize(TestDict("structuredActionDecision", inventedIdentityOffer))));
            Check("new_offer_prompt_explains_game_assigned_identity_and_rejects_invention",
                prompt.Contains("proposalId and termsHash must be empty strings and revision must be 0")
                && !ReadBool(ReadDictionary(inventedIdentityWire, "validatedStructuredActionDecision"), "valid", true)
                && ReadString(ReadDictionary(inventedIdentityWire, "validatedStructuredActionDecision"), "error", "")
                    .Contains("A new NPC proposal cannot claim an existing identity or consent."));
            Check("ordinary_action_context_reduction", prompt.Length < before.Length / 3,
                TestDict("unscopedContextCharacters", before.Length, "completeActionPromptCharacters", prompt.Length));

            string scene = "On the road. Arytha's husband is imprisoned; her last answer was no.";
            payload["sceneContext"] = scene;
            PreparePlayerActionPrompt(payload);
            Check("text_scene_memory_input_preserved_exactly", ReadString(payload, "sceneContext", "") == scene);
            PreparePlayerActionPrompt(payload);
            Check("reused_payload_never_accumulates_action_blocks", ReadString(payload, "sceneContext", "") == scene
                && ReadString(payload, "structuredActionPromptBlock", "").Split(new[] { "ACTION REQUEST AND NATIVE FACTS:" }, StringSplitOptions.None).Length == 2);
            string extendedScene = scene + "\nLater government and court context.";
            string writerScene = StructuredActionWriterScene(extendedScene, payload);
            Check("writer_gets_actions_after_retrieval_with_all_scene_facts", writerScene.StartsWith(extendedScene + "\n", StringComparison.Ordinal)
                && writerScene.Contains(StructuredActionTurnPrompt(payload))
                && !writerScene.Contains("REGISTERED INTERPERSONAL ACTIONS")
                && !writerScene.Contains("NPC-ORIGINATED OFFERS:") && ReadString(payload, "sceneContext", "") == scene);
            var sceneObject = TestDict("place", "On the road", "statement", "Arytha declined");
            payload["sceneContext"] = sceneObject;
            PreparePlayerActionPrompt(payload);
            Check("object_scene_keeps_all_facts_without_mechanics", ReadString(payload, "sceneContext", "") == Json.Serialize(sceneObject)
                && !ReadString(payload, "sceneContext", "").Contains("REGISTERED INTERPERSONAL ACTIONS"));
            Check("legacy_writer_scene_unchanged", StructuredActionWriterScene(scene, TestDict()) == scene);

            foreach (string route in new[] { "dialogue", "party_chat", "social_event", "castle_chat", "court", "correspondence" })
            {
                var envelope = CreatePromptEnvelope(route, "action_scope_test", "GLOBAL", "CHARACTER", writerScene);
                AttachPromptSectionDiagnostics(envelope, new Dictionary<string, string> { ["currentScene"] = writerScene }, new List<Dictionary<string, object>>());
                AttachStructuredActionDiagnostics(envelope, payload);
                Check(route + "_accounting_includes_scene_and_exact_messages", ReadInt(envelope.Diagnostics, "messageContentCharacters", 0) == envelope.TotalChars
                    && ReadInt(ReadDictionary(ReadDictionary(envelope.Diagnostics, "sectionSizes"), "currentScene"), "characters", 0) == writerScene.Length
                    && ReadInt(ReadDictionary(envelope.Diagnostics, "structuredActions"), "promptAssetRows", 0) == 40);
            }
            var npcOffer = new ActionDecision { Kind = "propose", Clauses = new List<ActionProposedTerm>
            {
                new ActionProposedTerm { ActionId = "give.prisoner", ActorId = "npc", RecipientId = "player",
                    Arguments = new List<ActionArgument> { new ActionArgument { Key = "assetId", Value = "prisoner99" }, new ActionArgument { Key = "quantity", Value = "1" } } },
                new ActionProposedTerm { ActionId = "give.gold", ActorId = "player", RecipientId = "npc",
                    Arguments = new List<ActionArgument> { new ActionArgument { Key = "quantity", Value = "123" } } }
            } };
            Check("npc_prisoner_for_money_proposal_still_supported", ActionNegotiation.ValidateDecision(null, npcOffer, "player", "npc", "conversation", 0).Count == 0
                && !ActionNegotiation.CreateProposal(null, npcOffer, "player", "npc", "conversation", 0).HasRequiredConsents);

            var empty = TestDict("ok", true, "content", "{}", "finishReason", "stop");
            int calls = 0;
            var request = TestDict("structuredActionProtocolVersion", 1, "requestType", "dialogue", "messages",
                new List<Dictionary<string, object>> { TestDict("role", "user", "content", scene) });
            request["structuredActionRepairContext"] = TestDict("playerId", "player", "npcId", "npc", "conversationId", "conversation");
            var failure = RetryMalformedStructuredResponse(CloneDictionary(empty), request, "structured_action_scope_tests", "empty-failure", "dialogue", "npc", "",
                retry => { calls++; return CloneDictionary(empty); });
            var attempts = ReadDictionaryList(ReadDictionary(failure, "formatRetry"), "attempts");
            Check("repeated_empty_object_has_one_regeneration_no_effects", calls == 1 && !ReadBool(failure, "ok", true)
                && attempts.Count == 1 && ReadString(attempts[0], "callKind", "") == "full_regeneration"
                && ReadInt(attempts[0], "requestCharacters", 0) > scene.Length);
            calls = 0;
            var recovered = RetryMalformedStructuredResponse(CloneDictionary(empty), request, "structured_action_scope_tests", "empty-success", "dialogue", "npc", "",
                retry => { calls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "I remember what you told me of her husband.",
                    "structuredActionDecision", TestDict("kind", "none", "proposalId", "", "revision", 0, "termsHash", "", "reason", "No new action is offered.", "clauses", new List<Dictionary<string, object>>())))); });
            Check("empty_object_can_recover_with_preserved_context", calls == 1 && ReadBool(recovered, "ok", false)
                && StructuredResponseIsComplete(ReadString(recovered, "content", ""), "dialogue")
                && StructuredActionResponseHasDecision(ReadString(recovered, "content", ""), request));

            var pending = new ActionAgreement { Id = "format-pending", PlayerId = "player", NpcId = "npc", ProposerId = "player", Origin = ActionOrigin.Conversation };
            pending.Clauses.Add(new ActionClause { ActionId = "give.gold", ActorId = "player", RecipientId = "npc",
                Arguments = new SortedDictionary<string, string> { ["quantity"] = "25" }, DisplayText = "I wish to give you 25 denars" });
            pending.ConversationId = "conversation";
            pending.State = AgreementState.Offered;
            pending.PlayerAcceptedHash = pending.TermsHash();
            var repairContext = TestDict("playerId", "player", "npcId", "npc", "conversationId", "conversation", "worldDay", 1d,
                "proposal", Json.Deserialize<Dictionary<string, object>>(Json.Serialize(pending)), "termsHash", pending.TermsHash());
            var metadataRequest = TestDict("structuredActionProtocolVersion", 1, "structuredActionRepairContext", repairContext,
                "playerText", "Please accept this gift.", "messages", new List<Dictionary<string, object>>
                { TestDict("role", "system", "content", new string('x', 100000) + "DO_NOT_RESEND_ARCHIVE") });
            Dictionary<string, object> Answer(string text) => CompleteConversationStructuredResponse(
                TestDict("reply", text, "privateFixture", TestDict("attribution", "unchanged exact evidence")), "dialogue", out _);
            Dictionary<string, object> Decision(string kind) => TestDict("kind", kind,
                "proposalId", kind == "accept" || kind == "reject" ? pending.Id : "", "revision", kind == "accept" || kind == "reject" ? pending.Revision : 0,
                "termsHash", kind == "accept" || kind == "reject" ? pending.TermsHash() : "", "reason", "Matches the existing visible answer.", "clauses", new List<Dictionary<string, object>>());
            foreach (string kind in new[] { "accept", "reject", "none" })
            {
                calls = 0;
                var original = Answer(kind == "accept" ? "I accept these exact terms." : kind == "reject" ? "I refuse this gift." : "Let me consider your request.");
                string originalJson = Json.Serialize(original), sparseMessages = "";
                var fixedAnswer = RetryMalformedStructuredResponse(TestDict("ok", true, "content", originalJson), metadataRequest,
                    "structured_action_scope_tests", "metadata-" + kind, "dialogue", "npc", "",
                    retry => { calls++; sparseMessages = Json.Serialize(ReadDictionaryList(retry, "messages"));
                        return TestDict("ok", true, "content", Json.Serialize(TestDict("structuredActionDecision", Decision(kind)))); });
                var parsedFixed = TryParseJsonObject(ReadString(fixedAnswer, "content", ""));
                var fixedDecision = ReadDictionary(parsedFixed, "structuredActionDecision");
                parsedFixed?.Remove("structuredActionDecision");
                Check("missing_" + kind + "_decision_repaired_once_without_rewriting_reply_or_private_metadata", calls == 1
                    && ReadBool(fixedAnswer, "ok", false) && ReadString(fixedDecision, "kind", "") == kind
                    && Json.Serialize(parsedFixed) == originalJson);
                Check("missing_" + kind + "_repair_omits_archive_and_is_scoped", sparseMessages.Length < 100000 / 2
                    && !sparseMessages.Contains("DO_NOT_RESEND_ARCHIVE") && sparseMessages.Contains(pending.Id)
                    && sparseMessages.Contains(pending.TermsHash()));
            }
            var repairReceiptContext = TestDict("playerId", "player", "npcId", "npc", "conversationId", "conversation",
                "nativeReceipt", TestDict("OperationId", "already-completed", "State", 7, "TermsHash", "native-exact"));
            var receiptRequest = CloneDictionary(metadataRequest); receiptRequest["structuredActionRepairContext"] = repairReceiptContext;
            calls = 0;
            var receiptFixed = RetryMalformedStructuredResponse(TestDict("ok", true, "content", Json.Serialize(Answer("The original sale is already complete."))),
                receiptRequest, "structured_action_scope_tests", "metadata-receipt", "dialogue", "npc", "",
                retry => { calls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("structuredActionDecision", Decision("none")))); });
            Check("completed_receipt_acknowledgment_gets_valid_none_without_new_consent", calls == 1 && ReadBool(receiptFixed, "ok", false)
                && ReadString(ReadDictionary(TryParseJsonObject(ReadString(receiptFixed, "content", "")), "structuredActionDecision"), "kind", "") == "none");
            calls = 0;
            var wrongHash = Decision("accept"); wrongHash["termsHash"] = "invented";
            var invalidRepair = RetryMalformedStructuredResponse(TestDict("ok", true, "content", Json.Serialize(Answer("I accept the gift."))),
                metadataRequest, "structured_action_scope_tests", "metadata-wrong-hash", "dialogue", "npc", "",
                retry => { calls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("structuredActionDecision", wrongHash))); });
            Check("metadata_repair_cannot_invent_consent_identity", calls == 1 && !ReadBool(invalidRepair, "ok", true)
                && ReadString(invalidRepair, "errorCode", "") == "structured_action_decision_incomplete");
            calls = 0;
            var unsignedPending = Json.Deserialize<ActionAgreement>(Json.Serialize(pending));
            unsignedPending.PlayerAcceptedHash = "";
            var unsignedRequest = CloneDictionary(metadataRequest);
            var unsignedContext = CloneDictionary(repairContext);
            unsignedContext["proposal"] = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(unsignedPending));
            unsignedRequest["structuredActionRepairContext"] = unsignedContext;
            var unsignedRepair = RetryMalformedStructuredResponse(TestDict("ok", true, "content", Json.Serialize(Answer("I accept the gift."))),
                unsignedRequest, "structured_action_scope_tests", "metadata-unsigned-offer", "dialogue", "npc", "",
                retry => { calls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("structuredActionDecision", Decision("accept")))); });
            Check("metadata_repair_cannot_authorize_an_unsigned_player_offer", calls == 1 && !ReadBool(unsignedRepair, "ok", true)
                && ReadString(unsignedRepair, "errorCode", "") == "structured_action_decision_incomplete");
            calls = 0;
            var malformedChoice = Answer("I accept the gift."); malformedChoice["structuredActionDecision"] = TestDict("kind", "accept");
            var malformedChoiceResult = RetryMalformedStructuredResponse(TestDict("ok", true, "content", Json.Serialize(malformedChoice)),
                metadataRequest, "structured_action_scope_tests", "metadata-malformed-choice", "dialogue", "npc", "",
                retry => { calls++; return CloneDictionary(empty); });
            Check("supplied_malformed_consent_is_not_rewritten", calls == 0 && !ReadBool(malformedChoiceResult, "ok", true));
            calls = 0;
            var missingAgain = RetryMalformedStructuredResponse(TestDict("ok", true, "content", Json.Serialize(Answer("I accept the gift."))),
                metadataRequest, "structured_action_scope_tests", "metadata-still-missing", "dialogue", "npc", "",
                retry => { calls++; return CloneDictionary(empty); });
            Check("failed_metadata_repair_is_bounded_and_never_defaults_to_accept_or_none", calls == 1 && !ReadBool(missingAgain, "ok", true)
                && ReadDictionary(TryParseJsonObject(ReadString(missingAgain, "content", "")), "structuredActionDecision") == null);
            calls = 0;
            var existingChoice = Answer("I accept these exact terms."); existingChoice["structuredActionDecision"] = Decision("accept");
            string existingJson = Json.Serialize(existingChoice);
            var existingResult = RetryMalformedStructuredResponse(TestDict("ok", true, "content", existingJson), metadataRequest,
                "structured_action_scope_tests", "metadata-existing", "dialogue", "npc", "", retry => { calls++; return CloneDictionary(empty); });
            Check("complete_existing_consent_is_byte_preserved_without_provider_call", calls == 0 && ReadBool(existingResult, "ok", false)
                && ReadString(existingResult, "content", "") == existingJson);
            Check("diagnostics_reject_generic_complete_reply_with_missing_hidden_decision",
                DiagnosticStructuredIssues(Json.Serialize(Answer("I accept the gift.")), "dialogue", metadataRequest).Any(x => x.Contains("structuredActionDecision")));
            // TestDict deliberately discards whitespace keys; retain the actual provider corruption here.
            var shifted = new Dictionary<string, object> { [""] = "reply", [" "] = "I accept these exact terms.",
                ["actionGate"] = TestDict("needed", false) };
            foreach (string broken in new[] { Json.Serialize(shifted), "{\"reply\":\"I accept these exact terms.\",\"unfinished\":" })
            {
                calls = 0;
                var recoveredShape = RetryMalformedStructuredResponse(TestDict("ok", true, "content", broken), metadataRequest,
                    "structured_action_scope_tests", "metadata-recovered-shape", "dialogue", "npc", "",
                    retry => { calls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("structuredActionDecision", Decision("accept")))); });
                Check("recovered_reply_shape_" + (broken == Json.Serialize(shifted) ? "shifted" : "truncated") + "_still_requires_exact_hidden_decision",
                    calls == 1 && ReadBool(recoveredShape, "ok", false)
                    && StructuredActionResponseHasDecision(ReadString(recoveredShape, "content", ""), metadataRequest)
                    && ReadString(TryParseJsonObject(ReadString(recoveredShape, "content", "")), "reply", "") == "I accept these exact terms.");
            }
            calls = 0;
            var regeneratedMissingDecision = RetryMalformedStructuredResponse(CloneDictionary(empty), metadataRequest,
                "structured_action_scope_tests", "metadata-after-empty-regeneration", "dialogue", "npc", "",
                retry => { calls++; return TestDict("ok", true, "content", calls == 1
                    ? Json.Serialize(Answer("I accept these exact terms."))
                    : Json.Serialize(TestDict("structuredActionDecision", Decision("accept")))); });
            Check("full_regeneration_missing_decision_uses_one_compact_repair_and_passes_required_shape", calls == 2
                && ReadBool(regeneratedMissingDecision, "ok", false)
                && ReadInt(ReadDictionary(regeneratedMissingDecision, "structuredActionMetadataRepair"), "providerCalls", 0) == 1
                && StructuredActionResponseHasDecision(ReadString(regeneratedMissingDecision, "content", ""), metadataRequest));
            calls = 0;
            var proposed = Decision("propose");
            proposed["clauses"] = new List<Dictionary<string, object>> { TestDict("actionId", "give.gold", "actorId", "npc", "recipientId", "player",
                "arguments", new List<Dictionary<string, object>> { TestDict("key", "quantity", "value", "3") },
                "dueDay", 0d, "durationDays", 0d, "installments", 1, "intervalDays", 0d, "conditionKind", "", "conditionTargetId", "") };
            var proposedResult = RetryMalformedStructuredResponse(TestDict("ok", true, "content", Json.Serialize(Answer("I offer you three denars, if you accept."))),
                receiptRequest, "structured_action_scope_tests", "metadata-new-npc-offer", "dialogue", "npc", "",
                retry => { calls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("structuredActionDecision", proposed))); });
            Check("metadata_repair_preserves_definite_new_npc_offer_instead_of_defaulting_none", calls == 1
                && ReadBool(proposedResult, "ok", false)
                && ReadString(ReadDictionary(TryParseJsonObject(ReadString(proposedResult, "content", "")), "structuredActionDecision"), "kind", "") == "propose");
            return results;
        }

        private sealed class AgreementTestBackend : IAgreementBackend
        {
            public int Player = 100, Npc, Escrow, Applies;
            public double TravelDays;
            public bool FailDelivery, FailRollback, ServiceComplete, ServiceUncertain;
            public int ServicesStarted;
            public IReadOnlyList<string> Preflight(ActionAgreement a) => AgreementRules.Validate(a, ReignActionCatalog.Find, 0);
            public string Capture(ActionAgreement a, ActionClause c) => c.ActionId.StartsWith("give.", StringComparison.Ordinal) ? (int.Parse(c.Arguments["quantity"]) * c.Installments).ToString(CultureInfo.InvariantCulture) : "0";
            public void Reserve(ActionAgreement a, ActionClause c, string saved) { int n = int.Parse(saved); if (n > (c.ActorId == a.PlayerId ? Player : Npc)) throw new InvalidOperationException("Insufficient balance."); if (c.ActorId == a.PlayerId) Player -= n; else Npc -= n; Escrow += n; }
            public void Return(ActionAgreement a, ActionClause c, string saved, int fulfilledInstallments) { if (!c.ActionId.StartsWith("give.", StringComparison.Ordinal)) return; int n = int.Parse(saved) - int.Parse(c.Arguments["quantity"]) * fulfilledInstallments; Escrow -= n; if (c.ActorId == a.PlayerId) Player += n; else Npc += n; }
            public void Apply(ActionAgreement a, ActionClause c, string saved, int installment) { if (!c.ActionId.StartsWith("give.", StringComparison.Ordinal)) { ServicesStarted++; return; } int n = int.Parse(c.Arguments["quantity"]); Escrow -= n; if (c.RecipientId == a.PlayerId) Player += n; else Npc += n; Applies++; }
            public void ApplyAssetsAtomically(ActionAgreement a, IReadOnlyList<AgreementStep> steps)
            {
                if (FailDelivery) throw new InvalidOperationException("Injected pre-delivery failure.");
                foreach (var s in steps) Apply(a, a.Clauses.Single(c => c.Id == s.ClauseId), s.Snapshot, s.Installment);
                if (FailRollback) throw new AgreementRecoveryException("Injected uncertain native rollback.", "retained before-state", new InvalidOperationException("Native callback failed."));
            }
            public bool Observe(ActionAgreement a, ActionClause c, string saved, int installment) => c.ActionId.StartsWith("give.", StringComparison.Ordinal) ? Applies > 0
                : ServiceUncertain ? throw new AgreementRecoveryException("Interrupted remote service.", "retained operation", null) : ServiceComplete;
            public bool ConditionSatisfied(ActionClause c, double now) => true;
            public double DeliveryDays(ActionAgreement a) => TravelDays;
            public void SaveJournal(AgreementJournal journal) { }
        }
    }
}
