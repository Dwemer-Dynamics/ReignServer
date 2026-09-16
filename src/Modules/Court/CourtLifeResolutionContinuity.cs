using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Keep native execution evidence apart from LLM-authored plans and dialogue.
        // This existing timeline-scoped table participates in campaign Save Sync.
        private static void StoreCourtLifeResolutionContinuity(string campaign, string timeline, Dictionary<string, object> payload)
        {
            var facts = NormalizeCourtLifeResolutionFacts(payload);
            if (facts == null) return;
            string matter = ReadString(payload, "matterId", "");
            using (ReignDbConnection connection = OpenCampaignConnection(campaign))
            {
                EnsureCourtLifeSceneStore(connection);
                foreach (string actor in ObjectsFromValue(payload.TryGetValue("witnessHeroIds", out object witnesses) ? witnesses : null)
                    .OfType<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(4))
                {
                    string id = "court_resolution:" + CourtHash(timeline + "|" + matter + "|" + actor);
                    ExecuteSql(connection, "INSERT OR IGNORE INTO court_life_turns(turn_id,timeline_id,matter_id,actor_id,phase,player_text,reply_text,created_ts) VALUES($id,$timeline,$matter,$actor,'resolution','',$facts,$ts);",
                        new Dictionary<string, object> { ["id"] = id, ["timeline"] = timeline, ["matter"] = matter,
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
                ["title"] = LimitText(ReadString(payload, "title", "Court audience"), 200),
                ["outcome"] = outcome, ["completed"] = true, ["extortion"] = ReadBool(payload, "extortion", false),
                ["summary"] = LimitText(ReadString(payload, "summary", "The court decision completed."), 700) };
            var receipt = ReadDictionary(payload, "nativeReceipt");
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

        private static string BuildCourtLifeResolutionContinuityPrompt(string campaign, string hero, Dictionary<string, object> payload)
        {
            string timeline = ReadString(payload, "timelineId", "");
            if (string.IsNullOrWhiteSpace(timeline) || string.IsNullOrWhiteSpace(hero)) return "";
            using (ReignDbConnection connection = OpenCampaignConnection(campaign))
            {
                EnsureCourtLifeSceneStore(connection);
                var rows = QuerySql(connection, "SELECT reply_text FROM court_life_turns WHERE timeline_id=$timeline AND actor_id=$actor AND phase='resolution' ORDER BY created_ts DESC,turn_id DESC LIMIT 6;",
                    new Dictionary<string, object> { ["timeline"] = timeline, ["actor"] = hero });
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
        }
    }
}
