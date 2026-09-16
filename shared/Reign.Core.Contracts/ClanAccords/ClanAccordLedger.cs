using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.ClanAccords
{
    public enum ClanAccordType { Trade, MutualWatch, Agricultural, Artisan, Garrison }
    public enum ClanAccordEndReason { None, Cancelled, War, ClanEliminated }

    // Public setters are the persistence boundary. Hosts must not modify stored identities after creation.
    public sealed class ClanAccordRecord
    {
        public string Id { get; set; } = "";
        public string ActionId { get; set; } = "";
        public ClanAccordType Type { get; set; }
        public string PlayerClanId { get; set; } = "";
        public string PlayerClanName { get; set; } = "";
        public string PartnerClanId { get; set; } = "";
        public string PartnerClanName { get; set; } = "";
        public string PlayerArrangerId { get; set; } = "";
        public string PlayerArrangerName { get; set; } = "";
        public string NpcArrangerId { get; set; } = "";
        public string NpcArrangerName { get; set; } = "";
        public double StartDay { get; set; }
        public bool IsActive { get; set; } = true;
        public double? EndDay { get; set; }
        public ClanAccordEndReason EndReason { get; set; }
        public string EndActionId { get; set; } = "";
        public string EndActorId { get; set; } = "";
        public string EndActorName { get; set; } = "";
    }

    public sealed class ClanAccordCreateRequest
    {
        public string ActionId { get; set; } = "";
        public ClanAccordType Type { get; set; }
        public string PlayerClanId { get; set; } = "";
        public string PlayerClanName { get; set; } = "";
        public string PartnerClanId { get; set; } = "";
        public string PartnerClanName { get; set; } = "";
        public string PlayerArrangerId { get; set; } = "";
        public string PlayerArrangerName { get; set; } = "";
        public string NpcArrangerId { get; set; } = "";
        public string NpcArrangerName { get; set; } = "";
        public double CampaignDay { get; set; }
        public int PlayerClanTier { get; set; }
        public bool PlayerAccepted { get; set; }
        public bool NpcAccepted { get; set; }
        public bool IsHostile { get; set; }
    }

    public sealed class ClanAccordResult
    {
        public bool Success { get; set; }
        public bool Changed { get; set; }
        public string Error { get; set; } = "";
        public ClanAccordRecord? Record { get; set; }
        // Per eligible person. Only a newly committed voluntary cancellation emits -10.
        public int RelationDelta { get; set; }
    }

    public sealed class ClanAccordBonuses
    {
        public int TradeIncome { get; set; }
        public double Security { get; set; }
        public double HearthGrowth { get; set; }
        public double Prosperity { get; set; }
        public double GarrisonWageReduction { get; set; }
    }

    public sealed class ClanAccordSeasonalReceipt
    {
        public string PlayerClanId { get; set; } = "";
        public string PartnerClanId { get; set; } = "";
        public string PersonId { get; set; } = "";
        public int Year { get; set; }
        public int Season { get; set; }
        public int RelationDelta { get; set; }
    }

    /// <summary>
    /// Campaign-local deterministic ledger. The host owns authorization, real-world eligibility,
    /// atomic persistence with side effects, current membership, and native relation synchronization.
    /// Serialize only while the host's campaign mutation lock is held. Never edit collections directly.
    /// </summary>
    public sealed class ClanAccordLedger
    {
        private readonly object _sync = new object();
        public List<ClanAccordRecord> Records { get; set; } = new List<ClanAccordRecord>();
        public List<ClanAccordSeasonalReceipt> SeasonalReceipts { get; set; } = new List<ClanAccordSeasonalReceipt>();

        public static bool IsSupportedType(ClanAccordType type) => type >= ClanAccordType.Trade && type <= ClanAccordType.Garrison;
        public static int SeasonalGoodwill(int currentRelation) => currentRelation >= 20 ? 0 : (int)Math.Min(5L, 20L - currentRelation);

        public int ActiveCount(string playerClanId, ClanAccordType type)
        {
            lock (_sync) return Records.Count(x => x.IsActive && x.PlayerClanId == playerClanId && x.Type == type);
        }

        public ClanAccordResult Create(ClanAccordCreateRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(request.ActionId)) return Fail("missing_action_id");
                var prior = Records.FirstOrDefault(x => x.ActionId == request.ActionId);
                if (prior != null)
                {
                    if (prior.Type != request.Type || prior.PlayerClanId != request.PlayerClanId
                        || prior.PartnerClanId != request.PartnerClanId || prior.PlayerArrangerId != request.PlayerArrangerId
                        || prior.NpcArrangerId != request.NpcArrangerId) return Fail("action_id_conflict");
                    return new ClanAccordResult { Success = true, Record = prior };
                }
                if (Records.Any(x => x.EndActionId == request.ActionId)) return Fail("action_id_conflict");
                if (!IsSupportedType(request.Type)) return Fail("unsupported_type");
                if (!ValidDay(request.CampaignDay)) return Fail("invalid_campaign_day");
                if (string.IsNullOrWhiteSpace(request.PlayerClanId) || string.IsNullOrWhiteSpace(request.PartnerClanId)
                    || request.PlayerClanId == request.PartnerClanId) return Fail("invalid_clans");
                if (string.IsNullOrWhiteSpace(request.PlayerArrangerId) || string.IsNullOrWhiteSpace(request.NpcArrangerId)
                    || request.PlayerArrangerId == request.NpcArrangerId) return Fail("invalid_arrangers");
                if (!request.PlayerAccepted || !request.NpcAccepted) return Fail("acceptance_required");
                if (request.IsHostile) return Fail("hostile_clans");
                if (request.PlayerClanTier < 0) return Fail("invalid_tier");
                if (Records.Any(x => x.IsActive && x.PlayerClanId == request.PlayerClanId
                    && x.PartnerClanId == request.PartnerClanId && x.Type == request.Type)) return Fail("duplicate_accord");
                if (ActiveCount(request.PlayerClanId, request.Type) >= request.PlayerClanTier) return Fail("capacity_full");
                var record = new ClanAccordRecord
                {
                    Id = "clan-accord:" + request.ActionId, ActionId = request.ActionId, Type = request.Type,
                    PlayerClanId = request.PlayerClanId, PlayerClanName = request.PlayerClanName,
                    PartnerClanId = request.PartnerClanId, PartnerClanName = request.PartnerClanName,
                    PlayerArrangerId = request.PlayerArrangerId, PlayerArrangerName = request.PlayerArrangerName,
                    NpcArrangerId = request.NpcArrangerId, NpcArrangerName = request.NpcArrangerName,
                    StartDay = request.CampaignDay
                };
                Records.Add(record);
                return new ClanAccordResult { Success = true, Changed = true, Record = record };
            }
        }

        public ClanAccordResult Cancel(string accordId, string actionId, string actorId, string actorName,
            double campaignDay, bool explicitlyAccepted)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(actionId)) return Fail("missing_action_id");
                var previous = Records.FirstOrDefault(x => x.EndActionId == actionId);
                if (previous != null)
                    return previous.Id == accordId && previous.EndActorId == actorId
                        ? new ClanAccordResult { Success = true, Record = previous } : Fail("action_id_conflict");
                if (Records.Any(x => x.ActionId == actionId)) return Fail("action_id_conflict");
                if (!explicitlyAccepted) return Fail("acceptance_required");
                if (string.IsNullOrWhiteSpace(actorId)) return Fail("missing_actor");
                var record = Records.FirstOrDefault(x => x.Id == accordId);
                if (record == null) return Fail("accord_not_found");
                if (!record.IsActive) return Fail("accord_already_ended");
                if (!ValidDay(campaignDay) || campaignDay < record.StartDay) return Fail("invalid_campaign_day");
                End(record, ClanAccordEndReason.Cancelled, campaignDay);
                record.EndActionId = actionId;
                record.EndActorId = actorId;
                record.EndActorName = actorName;
                return new ClanAccordResult { Success = true, Changed = true, Record = record, RelationDelta = -10 };
            }
        }

        public List<ClanAccordRecord> EndForPartner(string partnerClanId, ClanAccordEndReason reason, double campaignDay)
        {
            if (reason != ClanAccordEndReason.War && reason != ClanAccordEndReason.ClanEliminated)
                throw new ArgumentOutOfRangeException(nameof(reason));
            if (!ValidDay(campaignDay)) throw new ArgumentOutOfRangeException(nameof(campaignDay));
            lock (_sync)
            {
                var ended = Records.Where(x => x.IsActive && x.PartnerClanId == partnerClanId).ToList();
                if (ended.Any(x => campaignDay < x.StartDay)) throw new ArgumentOutOfRangeException(nameof(campaignDay));
                foreach (var record in ended) End(record, reason, campaignDay);
                return ended;
            }
        }

        public ClanAccordBonuses GetBonuses(string clanId)
        {
            lock (_sync)
            {
                var active = Records.Where(x => x.IsActive && (x.PlayerClanId == clanId || x.PartnerClanId == clanId)).ToList();
                return new ClanAccordBonuses
                {
                    TradeIncome = active.Count(x => x.Type == ClanAccordType.Trade) * 50,
                    Security = active.Count(x => x.Type == ClanAccordType.MutualWatch) * 0.1,
                    HearthGrowth = active.Count(x => x.Type == ClanAccordType.Agricultural) * 0.2,
                    Prosperity = active.Count(x => x.Type == ClanAccordType.Artisan) * 0.1,
                    GarrisonWageReduction = active.Count(x => x.Type == ClanAccordType.Garrison) * 0.02
                };
            }
        }

        public int ClaimSeasonalGoodwill(string playerClanId, string partnerClanId, string personId,
            int year, int season, int currentRelation)
        {
            if (year < 0 || season < 0 || season > 3) throw new ArgumentOutOfRangeException(nameof(season));
            if (string.IsNullOrWhiteSpace(personId)) throw new ArgumentException("Person identity is required.", nameof(personId));
            lock (_sync)
            {
                if (!Records.Any(x => x.IsActive && x.PlayerClanId == playerClanId && x.PartnerClanId == partnerClanId)) return 0;
                if (SeasonalReceipts.Any(x => x.PlayerClanId == playerClanId && x.PartnerClanId == partnerClanId
                    && x.PersonId == personId && x.Year == year && x.Season == season)) return 0;
                int delta = SeasonalGoodwill(currentRelation);
                SeasonalReceipts.Add(new ClanAccordSeasonalReceipt { PlayerClanId = playerClanId,
                    PartnerClanId = partnerClanId, PersonId = personId, Year = year, Season = season, RelationDelta = delta });
                return delta;
            }
        }

        private static bool ValidDay(double day) => !double.IsNaN(day) && !double.IsInfinity(day) && day >= 0;
        private static ClanAccordResult Fail(string error) => new ClanAccordResult { Error = error };
        private static void End(ClanAccordRecord record, ClanAccordEndReason reason, double day)
        {
            record.IsActive = false;
            record.EndReason = reason;
            record.EndDay = day;
        }
    }
}
