using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class CourtService
    {
        private const string DirectCourtPrisonerReceiptSchema = "reign-direct-court-prisoner-v1";

        private static bool ClaimsDirectCourtPrisonerReceipt(Dictionary<string, object> receipt)
        {
            // A historical release used releasedHeroId, isPrisoner, ransomGold and optionally payment.
            // The remaining custody fields require a typed receipt even when its schema was stripped.
            return receipt != null && (ReadString(receipt, "schema", "").StartsWith("reign-direct-court-prisoner-", StringComparison.Ordinal)
                || receipt.ContainsKey("originalCaptorId") || receipt.ContainsKey("originalRosterCountBefore")
                || receipt.ContainsKey("originalRosterCountAfter") || receipt.ContainsKey("hasCaptor") || receipt.ContainsKey("ransom"));
        }

        private static string CourtReceiptIdentity(Dictionary<string, object> source, string key)
            => source != null && source.TryGetValue(key, out object value) && value is string id
                && !string.IsNullOrWhiteSpace(id) && id == id.Trim() ? id : "";

        private static long CourtReceiptInteger(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value)) return -1;
            return value switch { int integer => integer, long wide => wide, _ => -1 };
        }

        // Keep native execution evidence apart from LLM-authored plans and dialogue.
        // This existing timeline-scoped table participates in campaign Save Sync.
        private static void StoreCourtLifeResolutionContinuity(string campaign, string timeline, Dictionary<string, object> payload)
        {
            var facts = NormalizeCourtLifeResolutionFacts(payload);
            if (facts == null) return;
            string matter = ReadString(payload, "matterId", "");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                EnsureCourtLifeSceneStore(connection);
                foreach (string actor in ActionPipelineService.ObjectsFromValue(payload.TryGetValue("witnessHeroIds", out object witnesses) ? witnesses : null)
                    .OfType<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(4))
                {
                    string id = "court_resolution:" + CourtHash(timeline + "|" + matter + "|" + actor);
                    CourtStore.InsertOrIgnoreCourtLifeTurns2(connection, new Dictionary<string, object> { ["id"] = id, ["timeline"] = timeline, ["matter"] = matter,
                            ["actor"] = actor, ["facts"] = Json.Serialize(facts), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                }
            }
        }

        private static Dictionary<string, object> NormalizeCourtLifeResolutionFacts(Dictionary<string, object> payload)
        {
            if (!ReadBool(payload, "effectsCommitted", false) || string.IsNullOrWhiteSpace(ReadString(payload, "receiptId", ""))
                || string.IsNullOrWhiteSpace(ReadString(payload, "matterId", ""))) return null;
            string outcome = ReadString(payload, "outcome", "");
            if (!new[] { "favor_domestic", "favor_foreign", "compensate", "compromise", "accept", "refuse" }.Contains(outcome)) return null;
            var result = new Dictionary<string, object> { ["schema"] = "reign-court-resolution-knowledge-v1",
                ["matterId"] = ReadString(payload, "matterId", ""), ["receiptId"] = ReadString(payload, "receiptId", ""),
                ["title"] = DialogueService.LimitText(ReadString(payload, "title", "Court audience"), 200),
                ["outcome"] = outcome, ["completed"] = true, ["extortion"] = ReadBool(payload, "extortion", false),
                ["summary"] = DialogueService.LimitText(ReadString(payload, "summary", "The court decision completed."), 700) };
            var receipt = ReadDictionary(payload, "nativeReceipt");
            if (ClaimsDirectCourtPrisonerReceipt(receipt))
            {
                var foreign = ReadDictionary(payload, "foreign");
                var player = ReadDictionary(payload, "player");
                string matter = CourtReceiptIdentity(payload, "matterId");
                string foreignKingdom = CourtReceiptIdentity(foreign, "kingdomId"), playerKingdom = CourtReceiptIdentity(player, "kingdomId");
                string foreignLeader = CourtReceiptIdentity(foreign, "leaderHeroId"), playerLeader = CourtReceiptIdentity(player, "leaderHeroId");
                string released = CourtReceiptIdentity(receipt, "releasedHeroId"), captor = CourtReceiptIdentity(receipt, "originalCaptorId");
                long ransomGold = CourtReceiptInteger(receipt, "ransomGold");
                if (!receipt.TryGetValue("schema", out object schema) || schema is not string receiptSchema
                    || receiptSchema != DirectCourtPrisonerReceiptSchema
                    || CourtReceiptInteger(payload, "continuityVersion") != 1
                    || !payload.TryGetValue("effectsCommitted", out object committed) || committed is not true
                    || CourtReceiptIdentity(payload, "outcome") != "accept" || matter.Length == 0
                    || CourtReceiptIdentity(payload, "receiptId") != matter + "_resolution"
                    || CourtReceiptIdentity(payload, "campaignId").Length == 0 || CourtReceiptIdentity(payload, "timelineId").Length == 0
                    || foreignKingdom.Length == 0 || playerKingdom.Length == 0
                    || foreignKingdom.Equals(playerKingdom, StringComparison.OrdinalIgnoreCase)
                    || foreignLeader.Length == 0 || playerLeader.Length == 0 || foreignLeader == playerLeader
                    || !foreign.TryGetValue("isPlayerKingdom", out object foreignRole) || foreignRole is not false
                    || !player.TryGetValue("isPlayerKingdom", out object playerRole) || playerRole is not true
                    || released.Length == 0 || receipt.ContainsKey("type") || receipt.ContainsKey("gold")
                    || !((captor.StartsWith("mobile:", StringComparison.Ordinal) && captor.Length > "mobile:".Length)
                        || (captor.StartsWith("settlement:", StringComparison.Ordinal) && captor.Length > "settlement:".Length))
                    || CourtReceiptInteger(receipt, "originalRosterCountBefore") <= 0 || CourtReceiptInteger(receipt, "originalRosterCountAfter") != 0
                    || !receipt.TryGetValue("isPrisoner", out object prisonerValue) || prisonerValue is not false
                    || !receipt.TryGetValue("hasCaptor", out object captorValue) || captorValue is not false
                    || !receipt.TryGetValue("ransom", out object ransomValue) || ransomValue is not bool ransom
                    || ransomGold < 0 || ransomGold > 1000000) return null;
                var ransomPayment = ReadDictionary(receipt, "payment");
                if (ransom)
                {
                    if (CourtReceiptInteger(ransomPayment, "gold") != ransomGold
                        || CourtReceiptIdentity(ransomPayment, "payerId") != foreignLeader
                        || CourtReceiptIdentity(ransomPayment, "recipientId") != playerLeader
                        || CourtReceiptInteger(ransomPayment, "payerBefore") < 0 || CourtReceiptInteger(ransomPayment, "payerAfter") < 0
                        || CourtReceiptInteger(ransomPayment, "recipientBefore") < 0 || CourtReceiptInteger(ransomPayment, "recipientAfter") < 0)
                        return null;
                }
                else if (receipt.ContainsKey("payment") || ransomGold != 0) return null;
                result["release"] = new Dictionary<string, object> { ["heroId"] = released, ["confirmed"] = true };
            }
            if (ReadString(receipt, "type", "") == "DiplomacySignTradeAgreement")
            {
                long days = ReadLong(ReadDictionary(receipt, "terms"), "durationDays", -1);
                if (outcome != "accept" || ReadString(receipt, "status", "") != "completed"
                    || ReadString(receipt, "actionId", "") != ReadString(payload, "matterId", "") + "_native"
                    || days < 1 || days > 365) return null;
                result["agreement"] = new Dictionary<string, object> { ["kind"] = "trade_agreement",
                    ["completed"] = true, ["durationDays"] = days };
            }
            var payment = ReadDictionary(receipt, "payment") ?? receipt;
            if (payment != null && payment.ContainsKey("gold"))
            {
                long gold = ReadLong(payment, "gold", -1), payerBefore = ReadLong(payment, "payerBefore", -1),
                    payerAfter = ReadLong(payment, "payerAfter", -1), recipientBefore = ReadLong(payment, "recipientBefore", -1),
                    recipientAfter = ReadLong(payment, "recipientAfter", -1);
                string payer = ReadString(payment, "payerId", ""), recipient = ReadString(payment, "recipientId", "");
                if (gold < 0 || gold > 1000000 || payerBefore < 0 || payerAfter < 0 || recipientBefore < 0 || recipientAfter < 0
                    || payerBefore - payerAfter != gold || recipientAfter - recipientBefore != gold
                    || string.IsNullOrWhiteSpace(payer) || string.IsNullOrWhiteSpace(recipient) || payer == recipient) return null;
                var parties = ReadDictionaryList(payload, "paymentParties");
                result["payment"] = new Dictionary<string, object> { ["gold"] = gold, ["confirmed"] = true,
                    ["payerName"] = ReadString(parties.FirstOrDefault(x => ReadString(x, "heroId", "") == payer), "name", "the recorded payer"),
                    ["recipientName"] = ReadString(parties.FirstOrDefault(x => ReadString(x, "heroId", "") == recipient), "name", "the recorded recipient") };
            }
            else if (outcome == "compensate" || outcome == "compromise") return null;
            return result;
        }

        internal static string BuildCourtLifeResolutionContinuityPrompt(string campaign, string hero, Dictionary<string, object> payload)
        {
            string timeline = ReadString(payload, "timelineId", "");
            if (string.IsNullOrWhiteSpace(timeline) || string.IsNullOrWhiteSpace(hero)) return "";
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                EnsureCourtLifeSceneStore(connection);
                var rows = CourtStore.SelectCourtLifeTurns2(connection, new Dictionary<string, object> { ["timeline"] = timeline, ["actor"] = hero });
                if (rows.Count == 0) return "";
                return "\n\nAUTHORITATIVE COMPLETED COURT MATTERS YOU WITNESSED\n"
                    + "These native execution receipts supersede conflicting old dialogue, summaries, plans and crises about these exact matters. A confirmed payment is already received by the named recipient; do not demand coins in your own hands, proof of transfer or a second payment. Acceptance alone would not establish execution, but these records do. Update any obsolete plan/crisis accordingly while retaining unrelated concerns. Completion does not prove an original allegation or imply forgiveness. Only an explicit completed agreement record below establishes a treaty: acknowledge that agreement and its exact duration without asking for another authorization or proof of signing. An ordinary completed judgment does not establish a treaty. For an extortion demand, do not invent an injury whose proof remains outstanding. You can discuss feelings or the political consequences without reopening the completed demand. Do not recite internal IDs or balances.\n"
                    + "ACTION GATE FOR COMPLETED MATTERS: Acknowledging a recorded outcome, explaining its limits, or saying you will recount this already completed audience to your ruler is conversation only: actionGate.needed=false and commitment=roleplay_only when that is the whole reply. Routine reporting does not order a new payment, treaty, messenger dispatch, journey or other game action. Do not turn a question about what happened into a fresh commitment. This does not suppress a separate, explicit new actionable agreement or order in the same exchange; evaluate that new action under the normal action rules.\n"
                    + string.Join("\n", rows.Select(x => ReadString(x, "reply_text", "{}")));
            }
        }

        private static void AppendCourtLifeResolutionContinuityTests(List<Dictionary<string, object>> results, string campaign)
        {
            Action<string, bool> add = (id, passed) => results.Add(new Dictionary<string, object> {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "court_life_resolution", ["caseId"] = id, ["name"] = id, ["durationMs"] = 0 });
            var receipt = new Dictionary<string, object> { ["gold"] = 2000, ["payerId"] = "payer", ["recipientId"] = "king",
                ["payerBefore"] = 5000, ["payerAfter"] = 3000, ["recipientBefore"] = 1000, ["recipientAfter"] = 3000 };
            var payload = new Dictionary<string, object> { ["effectsCommitted"] = true, ["matterId"] = "demand", ["receiptId"] = "demand_resolution",
                ["outcome"] = "compensate", ["title"] = "A Price for Quiet Borders", ["extortion"] = true,
                ["nativeReceipt"] = receipt, ["witnessHeroIds"] = new object[] { "envoy", "envoy" } };
            var facts = NormalizeCourtLifeResolutionFacts(payload);
            add("native_payment_confirmation_omits_private_balances", ReadBool(ReadDictionary(facts, "payment"), "confirmed", false)
                && !Json.Serialize(facts).Contains("payerBefore"));
            payload["effectsCommitted"] = false;
            add("acceptance_without_execution_has_no_receipt", NormalizeCourtLifeResolutionFacts(payload) == null);
            payload["effectsCommitted"] = true; receipt["recipientAfter"] = 1000;
            add("failed_credit_is_not_confirmed_payment", NormalizeCourtLifeResolutionFacts(payload) == null);
            receipt["recipientAfter"] = 3000;
            StoreCourtLifeResolutionContinuity(campaign, "receipt_timeline", payload);
            StoreCourtLifeResolutionContinuity(campaign, "receipt_timeline", payload);
            string prompt = BuildCourtLifeResolutionContinuityPrompt(campaign, "envoy", new Dictionary<string, object> { ["timelineId"] = "receipt_timeline" });
            add("persisted_receipt_survives_fresh_connection_and_retry", prompt.Contains("already received")
                && prompt.Split(new[] { "demand_resolution" }, StringSplitOptions.None).Length == 2);
            add("receipt_is_private_to_witness_and_timeline", BuildCourtLifeResolutionContinuityPrompt(campaign, "bystander", new Dictionary<string, object> { ["timelineId"] = "receipt_timeline" }) == ""
                && BuildCourtLifeResolutionContinuityPrompt(campaign, "envoy", new Dictionary<string, object> { ["timelineId"] = "other_timeline" }) == "");
            add("missing_timeline_does_not_read_other_branch", BuildCourtLifeResolutionContinuityPrompt(campaign, "envoy", new Dictionary<string, object>()) == "");
            var tradeReceipt = new Dictionary<string, object> { ["actionId"] = "trade_native", ["status"] = "completed",
                ["type"] = "DiplomacySignTradeAgreement", ["terms"] = new Dictionary<string, object> { ["durationDays"] = 63 } };
            var trade = new Dictionary<string, object> { ["effectsCommitted"] = true, ["matterId"] = "trade",
                ["receiptId"] = "trade_resolution", ["outcome"] = "accept", ["nativeReceipt"] = tradeReceipt,
                ["witnessHeroIds"] = new object[] { "trade_envoy" } };
            var tradeFacts = NormalizeCourtLifeResolutionFacts(trade);
            add("completed_native_trade_retains_exact_duration", ReadLong(ReadDictionary(tradeFacts, "agreement"), "durationDays", -1) == 63);
            tradeReceipt["status"] = "executing";
            add("pending_government_does_not_establish_trade", NormalizeCourtLifeResolutionFacts(trade) == null);
            tradeReceipt["status"] = "completed"; tradeReceipt["actionId"] = "another_native";
            add("unrelated_native_trade_receipt_is_rejected", NormalizeCourtLifeResolutionFacts(trade) == null);
            tradeReceipt["actionId"] = "trade_native";
            StoreCourtLifeResolutionContinuity(campaign, "trade_timeline", trade);
            StoreCourtLifeResolutionContinuity(campaign, "trade_timeline", trade);
            string tradePrompt = BuildCourtLifeResolutionContinuityPrompt(campaign, "trade_envoy",
                new Dictionary<string, object> { ["timelineId"] = "trade_timeline" });
            add("native_trade_knowledge_is_persisted_once", tradePrompt.Contains("trade_agreement")
                && tradePrompt.Contains("63") && tradePrompt.Contains("without asking for another authorization")
                && tradePrompt.Split(new[] { "trade_resolution" }, StringSplitOptions.None).Length == 2);

            var ransomReceipt = new Dictionary<string, object> { ["schema"] = DirectCourtPrisonerReceiptSchema,
                ["releasedHeroId"] = "captive", ["originalCaptorId"] = "mobile:players_party",
                ["originalRosterCountBefore"] = 1, ["originalRosterCountAfter"] = 0,
                ["isPrisoner"] = false, ["hasCaptor"] = false, ["ransom"] = true, ["ransomGold"] = 2000, ["payment"] = receipt };
            const string ransomTimeline = "court_ransom_admission";
            var ransom = new Dictionary<string, object> { ["effectsCommitted"] = true, ["matterId"] = "ransom", ["receiptId"] = "ransom_resolution",
                ["campaignId"] = campaign, ["timelineId"] = ransomTimeline, ["worldDay"] = 1d, ["continuityVersion"] = 1,
                ["outcome"] = "accept", ["nativeReceipt"] = ransomReceipt,
                ["witnessHeroIds"] = new[] { "ransom_envoy" },
                ["foreign"] = TestDict("kingdomId", "ransom_foreign", "leaderHeroId", "payer", "isPlayerKingdom", false),
                ["player"] = TestDict("kingdomId", "ransom_player", "leaderHeroId", "king", "isPlayerKingdom", true) };
            var ransomFacts = NormalizeCourtLifeResolutionFacts(ransom);
            add("verified_court_ransom_preserves_both_effects", ReadBool(ReadDictionary(ransomFacts, "payment"), "confirmed", false)
                && ReadString(ReadDictionary(ransomFacts, "release"), "heroId", "") == "captive");
            ransomReceipt["originalRosterCountAfter"] = 1;
            add("court_ransom_flag_only_release_is_rejected", NormalizeCourtLifeResolutionFacts(ransom) == null);
            ransomReceipt["originalRosterCountAfter"] = 0;
            receipt["recipientAfter"] = 1000;
            add("court_ransom_partial_payment_is_rejected", NormalizeCourtLifeResolutionFacts(ransom) == null);
            receipt["recipientAfter"] = 3000;
            receipt["payerId"] = "different_payer";
            add("court_ransom_wrong_named_payer_is_rejected", NormalizeCourtLifeResolutionFacts(ransom) == null);
            receipt["payerId"] = "payer";
            AppendCourtPrisonerReceiptAdmissionTests(results, campaign, ransomTimeline, ransom, ransomReceipt, receipt);
            ransomReceipt["ransom"] = false;
            ransomReceipt["ransomGold"] = 0;
            ransomReceipt.Remove("payment");
            var releaseFacts = NormalizeCourtLifeResolutionFacts(ransom);
            add("verified_unpaid_court_release_omits_payment", ReadBool(ReadDictionary(releaseFacts, "release"), "confirmed", false)
                && ReadDictionary(releaseFacts, "payment") == null);
        }

        private static void AppendCourtPrisonerReceiptAdmissionTests(List<Dictionary<string, object>> results, string campaign,
            string timeline, Dictionary<string, object> payload, Dictionary<string, object> custody, Dictionary<string, object> payment)
        {
            void Add(string id, bool passed)
                => results.Add(TestDict("ok", true, "passed", passed, "suite", "court_life_resolution",
                    "caseId", id, "name", id, "durationMs", 0));
            string matter = ReadString(payload, "matterId", "");
            var scope = TestDict("campaign", campaign, "timeline", timeline);
            int Pressure() => DiplomacyService.ReadPoliticalPressureValue(campaign, timeline, "ransom_foreign", "ransom_player");
            int Pending()
            {
                using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                return CourtStore.SelectInternationalCourtQueue(connection, scope).Count;
            }
            string Witness() => BuildCourtLifeResolutionContinuityPrompt(campaign, "ransom_envoy", payload);
            StoreInternationalQueueItem(campaign, timeline, matter, "prisoner", 0, 1d, TestDict("matterId", matter));
            int before = Pressure();
            bool initialPending = Pending() == 1;
            bool RejectedWithoutWrites() => !ReadBool(InternationalCourtResolutionApi(payload), "ok", false)
                && initialPending && Pending() == 1 && Pressure() == before && Witness() == "";

            payload.Remove("continuityVersion");
            bool missingRejected = RejectedWithoutWrites();
            payload["continuityVersion"] = 0;
            Add("typed_court_ransom_missing_or_zero_version_cannot_resolve", RejectedWithoutWrites() && missingRejected);
            payload["continuityVersion"] = 2;
            bool unsupportedRejected = RejectedWithoutWrites();
            payload["continuityVersion"] = "1";
            Add("typed_court_ransom_requires_exact_integer_version_one", RejectedWithoutWrites() && unsupportedRejected);
            payload["continuityVersion"] = 1;
            custody.Remove("schema");
            custody["originalRosterCountAfter"] = 1;
            bool strippedRejected = RejectedWithoutWrites();
            payload.Remove("continuityVersion");
            Add("schema_stripped_court_ransom_cannot_resolve_as_payment", RejectedWithoutWrites() && strippedRejected);
            payload["continuityVersion"] = 1;
            custody["schema"] = DirectCourtPrisonerReceiptSchema;
            custody["originalRosterCountAfter"] = 0;
            payload["receiptId"] = "another_matter_resolution";
            Add("wrong_matter_typed_court_ransom_cannot_resolve", RejectedWithoutWrites());
            payload["receiptId"] = matter + "_resolution";

            var typed = InternationalCourtResolutionApi(payload);
            int afterTyped = Pressure();
            string typedWitness = Witness();
            Add("valid_typed_court_ransom_resolves_with_custody_and_payment", ReadBool(typed, "ok", false) && Pending() == 0
                && afterTyped == before + ReadInt(typed, "requestedDelta", 0) && afterTyped != before
                && typedWitness.Contains("\"release\"") && typedWitness.Contains("\"payment\""));

            const string legacyMatter = "historical_court_ransom";
            var legacy = new Dictionary<string, object>(payload)
            {
                ["matterId"] = legacyMatter, ["receiptId"] = legacyMatter + "_resolution",
                ["nativeReceipt"] = TestDict("releasedHeroId", "historical_captive", "isPrisoner", false, "ransomGold", 2000, "payment", payment)
            };
            StoreInternationalQueueItem(campaign, timeline, legacyMatter, "prisoner", 0, 1d, TestDict("matterId", legacyMatter));
            bool legacyPending = Pending() == 1;
            var historical = InternationalCourtResolutionApi(legacy);
            int afterHistorical = Pressure();
            Add("historical_untyped_court_ransom_remains_admitted", legacyPending && ReadBool(historical, "ok", false) && Pending() == 0
                && afterHistorical == afterTyped + ReadInt(historical, "requestedDelta", 0) && afterHistorical != afterTyped
                && Witness().Contains(legacyMatter + "_resolution"));
        }
    }
}
