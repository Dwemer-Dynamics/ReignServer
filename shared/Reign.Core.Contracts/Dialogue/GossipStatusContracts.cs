using System;
using System.Collections.Generic;
using System.Globalization;

namespace Reign.Core.Contracts.Dialogue
{
    /// <summary>
    /// Typed wire contract for GET /gossip/status. The server's gossip dictionaries use a
    /// case-insensitive key comparer; ToDictionary keeps that comparer and the exact key order and
    /// value types, so adopting these types never changes bytes on the wire or how server code reads
    /// the result. Database rows and the communications block stay raw dictionaries.
    /// </summary>
    public static class GossipStatusProtocol
    {
        // GET; query campaignId (required), deliveryOnly ("true"), timelineId, worldDay.
        public const string StatusRoute = "/gossip/status";

        public const string DeliveryOnlyQueryValue = "true";
        public const string DeliveryReadinessSchema = "reign-gossip-delivery-readiness-v1";
        public const string CampaignRequiredError = "Campaign is required.";
        public const string TimelineNotActiveError = "Gossip timeline is not active.";

        public const string CampaignIdField = "campaignId";
        public const string DeliveryOnlyField = "deliveryOnly";
        public const string TimelineIdField = "timelineId";
        public const string WorldDayField = "worldDay";
        public const string OkField = "ok";
        public const string ErrorField = "error";
        public const string PolicyVersionField = "policyVersion";
        public const string MessagesField = "messages";
        public const string TopicsField = "topics";
        public const string SchemaField = "schema";
        public const string EnabledField = "enabled";
        public const string SchemaReadyField = "schemaReady";
        public const string ClockEpochField = "clockEpoch";
        public const string PendingCompositionField = "pendingComposition";
        public const string AwaitingNativeClockField = "awaitingNativeClock";
        public const string FailedCompositionField = "failedComposition";
        public const string ReadyNoncesField = "readyNonces";
        public const string CommunicationsField = "communications";
        public const string NextDueDayField = "nextDueDay";
        public const string DueInTransitField = "dueInTransit";

        internal static Dictionary<string, object> NewFields() =>
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>{"ok":false,"error":message} with the gossip comparer.</summary>
        public static Dictionary<string, object> Failure(string error)
        {
            Dictionary<string, object> result = NewFields();
            result[OkField] = false;
            result[ErrorField] = error;
            return result;
        }
    }

    /// <summary>GET /gossip/status query.</summary>
    public sealed class GossipStatusRequest
    {
        // Required; empty when absent. A blank value is rejected with CampaignRequiredError.
        public string CampaignId { get; set; } = string.Empty;
        // Only the exact value "true" selects the delivery readiness form.
        public bool DeliveryOnly { get; set; }
        // Readiness only: when present it must equal the active timeline.
        public bool HasTimelineId { get; set; }
        public string? TimelineId { get; set; }
        // Readiness only: an invariant-culture finite number overrides the stored gossip clock.
        public double? WorldDay { get; set; }

        public bool HasCampaign => !string.IsNullOrWhiteSpace(CampaignId);

        public static GossipStatusRequest FromQuery(IDictionary<string, string>? query)
        {
            var request = new GossipStatusRequest();
            if (query == null) return request;
            if (query.TryGetValue(GossipStatusProtocol.CampaignIdField, out string campaign)) request.CampaignId = campaign;
            request.DeliveryOnly = query.TryGetValue(GossipStatusProtocol.DeliveryOnlyField, out string compact)
                && compact == GossipStatusProtocol.DeliveryOnlyQueryValue;
            if (query.TryGetValue(GossipStatusProtocol.TimelineIdField, out string timeline))
            {
                request.HasTimelineId = true;
                request.TimelineId = timeline;
            }
            if (query.TryGetValue(GossipStatusProtocol.WorldDayField, out string supplied)
                && double.TryParse(supplied, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
                request.WorldDay = parsed;
            return request;
        }
    }

    /// <summary>
    /// Diagnostic GET /gossip/status response (deliveryOnly not set). Before the gossip schema exists:
    /// {"ok":true,"messages":[],"topics":[]}; afterwards
    /// {"ok":true,"policyVersion":..,"messages":[row...],"topics":[projection...]}.
    /// </summary>
    public sealed class GossipStatusResponse
    {
        private GossipStatusResponse(string? policyVersion, IEnumerable<Dictionary<string, object>>? messages,
            IEnumerable<Dictionary<string, object>>? topics)
        {
            PolicyVersion = policyVersion;
            Messages = messages == null ? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>>(messages);
            Topics = topics == null ? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>>(topics);
        }

        public bool Ok => true;
        // Null for the no-schema form, which omits policyVersion.
        public string? PolicyVersion { get; }
        public IReadOnlyList<Dictionary<string, object>> Messages { get; }
        public IReadOnlyList<Dictionary<string, object>> Topics { get; }

        public static GossipStatusResponse NoSchema() => new GossipStatusResponse(null, null, null);

        public static GossipStatusResponse Snapshot(string policyVersion, IEnumerable<Dictionary<string, object>>? messages,
            IEnumerable<Dictionary<string, object>>? topics) =>
            new GossipStatusResponse(policyVersion ?? string.Empty, messages, topics);

        public Dictionary<string, object> ToDictionary()
        {
            Dictionary<string, object> result = GossipStatusProtocol.NewFields();
            result[GossipStatusProtocol.OkField] = Ok;
            if (PolicyVersion == null)
            {
                result[GossipStatusProtocol.MessagesField] = new List<object>();
                result[GossipStatusProtocol.TopicsField] = new List<object>();
                return result;
            }
            result[GossipStatusProtocol.PolicyVersionField] = PolicyVersion;
            result[GossipStatusProtocol.MessagesField] = new List<Dictionary<string, object>>(Messages);
            result[GossipStatusProtocol.TopicsField] = new List<Dictionary<string, object>>(Topics);
            return result;
        }
    }

    /// <summary>
    /// Delivery-readiness GET /gossip/status?deliveryOnly=true response. Read only on the server.
    /// Counts are long; nextDueDay is the database value or null.
    /// </summary>
    public sealed class GossipDeliveryReadinessResponse
    {
        public bool Ok => true;
        public string Schema => GossipStatusProtocol.DeliveryReadinessSchema;
        public string CampaignId { get; set; } = string.Empty;
        public string TimelineId { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public bool SchemaReady { get; set; }
        public string ClockEpoch { get; set; } = string.Empty;
        public long PendingComposition { get; set; }
        public long AwaitingNativeClock { get; set; }
        public long FailedComposition { get; set; }
        public List<string> ReadyNonces { get; set; } = new List<string>();
        // {"active":bool,"pending":long,"due":long,"failed":long} as built by the communication readiness query.
        public Dictionary<string, object> Communications { get; set; } = new Dictionary<string, object>();
        // Database value of MIN(due_day), or null when there is none.
        public object? NextDueDay { get; set; }
        public long DueInTransit { get; set; }

        public Dictionary<string, object> ToDictionary()
        {
            Dictionary<string, object> result = GossipStatusProtocol.NewFields();
            result[GossipStatusProtocol.OkField] = Ok;
            result[GossipStatusProtocol.SchemaField] = Schema;
            result[GossipStatusProtocol.CampaignIdField] = CampaignId;
            result[GossipStatusProtocol.TimelineIdField] = TimelineId;
            result[GossipStatusProtocol.EnabledField] = Enabled;
            result[GossipStatusProtocol.SchemaReadyField] = SchemaReady;
            result[GossipStatusProtocol.ClockEpochField] = ClockEpoch;
            result[GossipStatusProtocol.PendingCompositionField] = PendingComposition;
            result[GossipStatusProtocol.AwaitingNativeClockField] = AwaitingNativeClock;
            result[GossipStatusProtocol.FailedCompositionField] = FailedComposition;
            result[GossipStatusProtocol.ReadyNoncesField] = ReadyNonces;
            result[GossipStatusProtocol.CommunicationsField] = Communications;
            result[GossipStatusProtocol.NextDueDayField] = NextDueDay!;
            result[GossipStatusProtocol.DueInTransitField] = DueInTransit;
            return result;
        }
    }
}
