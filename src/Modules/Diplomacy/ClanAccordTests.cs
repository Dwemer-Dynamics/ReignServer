using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Reign.Core.Contracts.ClanAccords;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunClanAccordSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => rows.Add(new Dictionary<string, object>
            {
                ["id"] = "clan_accords." + id, ["suite"] = "clan_accords", ["passed"] = passed,
                ["summary"] = summary, ["evidence"] = new Dictionary<string, object> { ["layer"] = "deterministic-ledger" }
            });
            Func<string, string, ClanAccordType, ClanAccordCreateRequest> request = (action, partner, type) =>
                new ClanAccordCreateRequest
                {
                    ActionId = action, PlayerClanId = "player_clan", PlayerClanName = "Player Clan",
                    PartnerClanId = partner, PartnerClanName = "Partner " + partner, Type = type,
                    PlayerArrangerId = "player", PlayerArrangerName = "Player",
                    NpcArrangerId = "lesser_noble", NpcArrangerName = "Lesser Noble",
                    CampaignDay = 1, PlayerClanTier = 2, PlayerAccepted = true, NpcAccepted = true
                };

            var ledger = new ClanAccordLedger();
            var firstRequest = request("trade-a", "a", ClanAccordType.Trade);
            var first = ledger.Create(firstRequest);
            var replay = ledger.Create(firstRequest);
            var conflict = request("trade-a", "b", ClanAccordType.Trade);
            add("creation_identity_retry", first.Success && first.Changed && replay.Success && !replay.Changed
                && ledger.Records.Count == 1 && ledger.Create(conflict).Error == "action_id_conflict"
                && first.Record.NpcArrangerName == "Lesser Noble", "Explicit acceptance persists lesser-member attribution and retries cannot duplicate or retarget an accord.");

            var duplicate = ledger.Create(request("duplicate", "a", ClanAccordType.Trade));
            var second = ledger.Create(request("trade-b", "b", ClanAccordType.Trade));
            var full = ledger.Create(request("trade-c", "c", ClanAccordType.Trade));
            var otherType = ledger.Create(request("watch-a", "a", ClanAccordType.MutualWatch));
            add("player_capacity_per_type", duplicate.Error == "duplicate_accord" && second.Success
                && full.Error == "capacity_full" && otherType.Success && ledger.ActiveCount("player_clan", ClanAccordType.Trade) == 2,
                "Only the player has tier-based capacity, independently by type; each pair allows one of each type.");
            var higherTier = request("trade-c", "c", ClanAccordType.Trade);
            higherTier.PlayerClanTier = 3;
            add("tier_increase", ledger.Create(higherTier).Success && ledger.GetBonuses("player_clan").TradeIncome == 150,
                "A tier increase immediately enables another partner and additive income.");

            var refused = request("refused", "z", ClanAccordType.Artisan); refused.PlayerAccepted = false;
            var tentative = request("tentative", "z", ClanAccordType.Artisan); tentative.NpcAccepted = false;
            var hostile = request("hostile", "z", ClanAccordType.Artisan); hostile.IsHostile = true;
            var own = request("own", "player_clan", ClanAccordType.Artisan);
            var invalid = request("invalid", "z", (ClanAccordType)42);
            var zero = request("zero", "z", ClanAccordType.Artisan); zero.PlayerClanTier = 0;
            var badDate = request("date", "z", ClanAccordType.Artisan); badDate.CampaignDay = double.NaN;
            add("creation_gates", ledger.Create(refused).Error == "acceptance_required"
                && ledger.Create(tentative).Error == "acceptance_required" && ledger.Create(hostile).Error == "hostile_clans"
                && ledger.Create(own).Error == "invalid_clans" && ledger.Create(invalid).Error == "unsupported_type"
                && ledger.Create(zero).Error == "capacity_full" && ledger.Create(badDate).Error == "invalid_campaign_day",
                "Tentative, refused, hostile, own-clan, unsupported, no-capacity and invalid-time proposals have no effect.");

            var all = new ClanAccordLedger();
            foreach (ClanAccordType type in Enum.GetValues(typeof(ClanAccordType)))
                foreach (string partner in new[] { "a", "b" }) all.Create(request(type + partner, partner, type));
            var playerBonus = all.GetBonuses("player_clan");
            var npcBonus = all.GetBonuses("a");
            add("five_bonus_formulas", playerBonus.TradeIncome == 100 && Math.Abs(playerBonus.Security - .2) < 1e-9
                && Math.Abs(playerBonus.HearthGrowth - .4) < 1e-9 && Math.Abs(playerBonus.Prosperity - .2) < 1e-9
                && Math.Abs(playerBonus.GarrisonWageReduction - .04) < 1e-9 && npcBonus.TradeIncome == 50
                && Math.Abs(npcBonus.Security - .1) < 1e-9 && Math.Abs(npcBonus.HearthGrowth - .2) < 1e-9
                && Math.Abs(npcBonus.Prosperity - .1) < 1e-9 && Math.Abs(npcBonus.GarrisonWageReduction - .02) < 1e-9
                && all.GetBonuses("absent").TradeIncome == 0,
                "All five fixed daily benefits stack additively and apply equally to each participant.");

            int[] relations = { -100, 0, 18, 20, 75, int.MinValue };
            add("goodwill_ceiling", relations.Select(ClanAccordLedger.SeasonalGoodwill).SequenceEqual(new[] { 5, 5, 2, 0, 0, 5 }),
                "Goodwill grants up to five, reaches but never exceeds twenty, and never reduces high relation.");
            int award = all.ClaimSeasonalGoodwill("player_clan", "a", "member", 1084, 0, 18);
            int duplicateAward = all.ClaimSeasonalGoodwill("player_clan", "a", "member", 1084, 0, 0);
            int nextSeason = all.ClaimSeasonalGoodwill("player_clan", "a", "member", 1084, 1, 0);
            int otherMember = all.ClaimSeasonalGoodwill("player_clan", "a", "member2", 1084, 0, 0);
            add("seasonal_pair_person_receipts", award == 2 && duplicateAward == 0 && nextSeason == 5 && otherMember == 5
                && all.ClaimSeasonalGoodwill("player_clan", "missing", "member", 1084, 0, 0) == 0,
                "Five types still grant one award per partner/person/season; later seasons and distinct people remain eligible.");

            string accordId = first.Record.Id;
            var unconfirmed = ledger.Cancel(accordId, "cancel", "other_player", "Successor", 2, false);
            var cancel = ledger.Cancel(accordId, "cancel", "other_player", "Successor", 2, true);
            var cancelRetry = ledger.Cancel(accordId, "cancel", "other_player", "Successor", 2, true);
            add("cancel_penalty_and_history", !unconfirmed.Success && cancel.Success && cancel.RelationDelta == -10
                && cancelRetry.Success && !cancelRetry.Changed && cancelRetry.RelationDelta == 0
                && ledger.GetBonuses("a").TradeIncome == 0 && ledger.Records.Count == 4
                && cancel.Record.EndActorId == "other_player" && cancel.Record.NpcArrangerId == "lesser_noble"
                && cancel.Record.EndReason == ClanAccordEndReason.Cancelled,
                "Voluntary cancellation needs acceptance, emits minus ten once, releases effects, and preserves separate arranger and canceller identities.");

            var war = all.EndForPartner("a", ClanAccordEndReason.War, 2);
            var warRetry = all.EndForPartner("a", ClanAccordEndReason.War, 2);
            add("war_all_types", war.Count == 5 && war.All(x => !x.IsActive && x.EndReason == ClanAccordEndReason.War
                && x.EndActorId == "") && warRetry.Count == 0 && all.GetBonuses("a").TradeIncome == 0
                && all.GetBonuses("b").TradeIncome == 50, "War ends all partner types without voluntary blame and leaves peaceful partners untouched.");
            all.Create(request("recreated", "a", ClanAccordType.Trade));
            add("recreate_no_goodwill_farming", all.ClaimSeasonalGoodwill("player_clan", "a", "member", 1084, 0, 0) == 0,
                "Terminating and recreating does not erase season receipts.");
            var zeroLedger = new ClanAccordLedger();
            zeroLedger.Create(request("zero-award", "a", ClanAccordType.Trade));
            zeroLedger.ClaimSeasonalGoodwill("player_clan", "a", "member", 1084, 0, 50);
            add("zero_award_receipt", zeroLedger.ClaimSeasonalGoodwill("player_clan", "a", "member", 1084, 0, 0) == 0
                && zeroLedger.SeasonalReceipts.Count == 1, "A capped seasonal tick is recorded to prevent repeated claims after relation falls.");

            string snapshot = Json.Serialize(all);
            var restored = Json.Deserialize<ClanAccordLedger>(snapshot);
            add("save_roundtrip", restored.Records.Count == all.Records.Count && restored.SeasonalReceipts.Count == all.SeasonalReceipts.Count
                && restored.GetBonuses("player_clan").TradeIncome == 100
                && restored.ClaimSeasonalGoodwill("player_clan", "a", "member", 1084, 0, 0) == 0
                && restored.Records.Any(x => x.EndReason == ClanAccordEndReason.War && x.NpcArrangerName == "Lesser Noble"),
                "JSON roundtrip retains active bonuses, attributed history, and replay receipts without a JSON dependency in core.");

            var concurrent = new ClanAccordLedger();
            Parallel.For(0, 32, i => concurrent.Create(request("parallel-" + i, "partner-" + i, ClanAccordType.Trade)));
            add("concurrent_capacity", concurrent.Records.Count == 2 && concurrent.GetBonuses("player_clan").TradeIncome == 100,
                "Simultaneous accepted requests cannot overfill the player's capacity.");
            var eliminated = restored.EndForPartner("b", ClanAccordEndReason.ClanEliminated, 3);
            add("elimination", eliminated.Count == 5 && eliminated.All(x => x.EndReason == ClanAccordEndReason.ClanEliminated),
                "Eliminating a partner ends all its remaining accords and preserves history.");
            return rows;
        }
    }
}
