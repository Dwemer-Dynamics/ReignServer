using System;
using System.Collections.Generic;
using System.Globalization;
using Reign.Core.Contracts.Platform;

namespace Reign.Core.Contracts.Diplomacy
{
    /// <summary>
    /// Typed wire contract for the diplomatic announcement routes. The types reproduce the exact
    /// Dictionary&lt;string, object&gt; shapes the server writes, so adopting them never changes bytes
    /// on the wire. Event objects stay raw dictionaries: they are queue entries (minus private
    /// diagnostics) or the public projection, and their key sets vary per event.
    /// </summary>
    public static class DiplomaticEventProtocol
    {
        // GET; query campaignId, limit, publicHistory ("true"); campaignId may also come from a JSON body.
        public const string NextRoute = "/diplomacy/events/next";
        // POST; JSON body DiplomaticEventAcknowledgeRequest.
        public const string AcknowledgeRoute = "/diplomacy/events/acknowledge";

        public const string DefaultCampaignId = "default";
        public const int DefaultLimit = 10;
        public const int MinLimit = 1;
        public const int MaxPopupLimit = 20;
        public const int MaxPublicHistoryLimit = 500;
        public const string PublicHistoryQueryValue = "true";
        public const string PublicProjection = "reign-public-diplomacy-v1";
        public const string NotFoundError = "Diplomatic event was not found.";

        public const string CampaignIdField = "campaignId";
        public const string LimitField = "limit";
        public const string PublicHistoryField = "publicHistory";
        public const string EventIdField = "eventId";
        public const string OkField = "ok";
        public const string ErrorField = "error";
        public const string CountField = "count";
        public const string EventsField = "events";
        public const string ProjectionField = "projection";
    }

    /// <summary>Event fields clients read (camelCase, as stored in the director queue).</summary>
    public static class DiplomaticEventFields
    {
        public const string EventId = "eventId";
        public const string ActionId = "actionId";
        public const string Title = "title";
        public const string Outcome = "outcome";
        public const string Summary = "summary";
        public const string TermsText = "termsText";
        public const string WorldDay = "worldDay";
        public const string Accepted = "accepted";
        public const string Acknowledged = "acknowledged";
        public const string ActorHeroId = "actorHeroId";
        public const string ActorName = "actorName";
        public const string ActorKingdomId = "actorKingdomId";
        public const string ActorKingdomName = "actorKingdomName";
        public const string ActorPublicReason = "actorPublicReason";
        public const string TargetHeroId = "targetHeroId";
        public const string TargetName = "targetName";
        public const string TargetKingdomId = "targetKingdomId";
        public const string TargetKingdomName = "targetKingdomName";
        public const string TargetPublicReason = "targetPublicReason";
        // Set on public-history projections only.
        public const string PublicAnnouncement = "publicAnnouncement";
    }

    /// <summary>GET /diplomacy/events/next request (query, with a body campaignId fallback).</summary>
    public sealed class DiplomaticEventsNextRequest
    {
        public string CampaignId { get; set; } = DiplomaticEventProtocol.DefaultCampaignId;
        // Only the exact query value "true" selects the public history projection.
        public bool PublicHistory { get; set; }
        // Clamped to [MinLimit, MaxPopupLimit], or [MinLimit, MaxPublicHistoryLimit] for public history.
        public int Limit { get; set; } = DiplomaticEventProtocol.DefaultLimit;

        /// <summary>Exact server parsing: a query campaignId wins over the body; an unparsable limit falls back to the default.</summary>
        public static DiplomaticEventsNextRequest FromQuery(IDictionary<string, string>? query, IDictionary<string, object>? body)
        {
            var request = new DiplomaticEventsNextRequest();
            query = query ?? new Dictionary<string, string>();
            request.CampaignId = query.TryGetValue(DiplomaticEventProtocol.CampaignIdField, out string campaign)
                ? campaign
                : WireReads.ReadString(body, DiplomaticEventProtocol.CampaignIdField, DiplomaticEventProtocol.DefaultCampaignId);
            request.PublicHistory = query.TryGetValue(DiplomaticEventProtocol.PublicHistoryField, out string historyMode)
                && historyMode == DiplomaticEventProtocol.PublicHistoryQueryValue;
            int limit = DiplomaticEventProtocol.DefaultLimit;
            if (query.TryGetValue(DiplomaticEventProtocol.LimitField, out string rawLimit) && int.TryParse(rawLimit, out int parsedLimit))
                limit = parsedLimit;
            int max = request.PublicHistory ? DiplomaticEventProtocol.MaxPublicHistoryLimit : DiplomaticEventProtocol.MaxPopupLimit;
            request.Limit = Math.Max(DiplomaticEventProtocol.MinLimit, Math.Min(max, limit));
            return request;
        }

        /// <summary>Route plus query; publicHistory is sent only when selected.</summary>
        public string ToRelativeUri()
        {
            string uri = DiplomaticEventProtocol.NextRoute
                + "?" + DiplomaticEventProtocol.CampaignIdField + "=" + Uri.EscapeDataString(CampaignId ?? string.Empty)
                + "&" + DiplomaticEventProtocol.LimitField + "=" + Limit.ToString(CultureInfo.InvariantCulture);
            if (PublicHistory) uri += "&" + DiplomaticEventProtocol.PublicHistoryField + "=" + DiplomaticEventProtocol.PublicHistoryQueryValue;
            return uri;
        }
    }

    /// <summary>
    /// GET /diplomacy/events/next response. Popup form: {"ok":true,"campaignId":..,"count":n,"events":[..]}.
    /// Public history form: {"ok":true,"campaignId":..,"publicHistory":true,"projection":..,"count":n,"events":[..]}.
    /// </summary>
    public sealed class DiplomaticEventsNextResponse
    {
        private DiplomaticEventsNextResponse(string campaignId, bool publicHistory, IEnumerable<Dictionary<string, object>>? events)
        {
            CampaignId = campaignId;
            PublicHistory = publicHistory;
            Events = WireReads.CopyRows(events);
        }

        public bool Ok => true;
        public string CampaignId { get; }
        public bool PublicHistory { get; }
        public int Count => Events.Count;
        public IReadOnlyList<Dictionary<string, object>> Events { get; }

        public static DiplomaticEventsNextResponse Popups(string campaignId, IEnumerable<Dictionary<string, object>>? events) =>
            new DiplomaticEventsNextResponse(campaignId, false, events);

        public static DiplomaticEventsNextResponse PublicHistoryProjection(string campaignId, IEnumerable<Dictionary<string, object>>? events) =>
            new DiplomaticEventsNextResponse(campaignId, true, events);

        public Dictionary<string, object> ToDictionary()
        {
            var result = new Dictionary<string, object>
            {
                [DiplomaticEventProtocol.OkField] = Ok,
                [DiplomaticEventProtocol.CampaignIdField] = CampaignId
            };
            if (PublicHistory)
            {
                result[DiplomaticEventProtocol.PublicHistoryField] = true;
                result[DiplomaticEventProtocol.ProjectionField] = DiplomaticEventProtocol.PublicProjection;
            }
            result[DiplomaticEventProtocol.CountField] = Count;
            result[DiplomaticEventProtocol.EventsField] = new List<Dictionary<string, object>>(Events);
            return result;
        }
    }

    /// <summary>POST /diplomacy/events/acknowledge body.</summary>
    public sealed class DiplomaticEventAcknowledgeRequest
    {
        public string CampaignId { get; set; } = DiplomaticEventProtocol.DefaultCampaignId;
        // Matched case-insensitively against queue entries.
        public string EventId { get; set; } = string.Empty;

        public static DiplomaticEventAcknowledgeRequest FromDictionary(IDictionary<string, object>? payload)
        {
            return new DiplomaticEventAcknowledgeRequest
            {
                CampaignId = WireReads.ReadString(payload, DiplomaticEventProtocol.CampaignIdField, DiplomaticEventProtocol.DefaultCampaignId),
                EventId = WireReads.ReadString(payload, DiplomaticEventProtocol.EventIdField, string.Empty)
            };
        }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                [DiplomaticEventProtocol.CampaignIdField] = CampaignId ?? DiplomaticEventProtocol.DefaultCampaignId,
                [DiplomaticEventProtocol.EventIdField] = EventId ?? string.Empty
            };
        }
    }

    /// <summary>
    /// POST /diplomacy/events/acknowledge response: always {"ok":found,"eventId":id,"error":""|message}.
    /// </summary>
    public sealed class DiplomaticEventAcknowledgeResponse
    {
        public DiplomaticEventAcknowledgeResponse(bool found, string eventId)
        {
            Ok = found;
            EventId = eventId ?? string.Empty;
        }

        public bool Ok { get; }
        public string EventId { get; }
        public string Error => Ok ? string.Empty : DiplomaticEventProtocol.NotFoundError;

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                [DiplomaticEventProtocol.OkField] = Ok,
                [DiplomaticEventProtocol.EventIdField] = EventId,
                [DiplomaticEventProtocol.ErrorField] = Error
            };
        }
    }
}
