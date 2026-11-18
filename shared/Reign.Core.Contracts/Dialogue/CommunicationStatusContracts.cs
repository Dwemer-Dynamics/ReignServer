using System;
using System.Collections.Generic;
using Reign.Core.Contracts.Platform;

namespace Reign.Core.Contracts.Dialogue
{
    /// <summary>
    /// Typed wire contract for the communication ledger snapshot served by GET /communications/status
    /// (query string) and POST /communications/status (JSON body). ToDictionary reproduces the exact
    /// Dictionary&lt;string, object&gt; the server wrote before (keys, order, CLR value types), so adopting
    /// it never changes bytes on the wire. Message, history and admission rows are SELECT pass-through
    /// rows and stay raw dictionaries.
    /// </summary>
    public static class CommunicationStatusProtocol
    {
        public const string StatusRoute = "/communications/status";

        public const string DefaultTimelineId = "main";
        public const int DefaultOffset = 0;
        public const int DefaultLimit = 50;
        // The server clamps offset to [0, MaxOffset] and limit to [MinLimit, MaxLimit] in its queries.
        public const int MaxOffset = 1000000;
        public const int MinLimit = 1;
        public const int MaxLimit = 100;
        public const string CampaignRequiredError = "An exact campaign is required.";
        public const string TimelineNotActiveError = "The communication timeline is not active.";
        public const string Coverage = "Read-only ledger evidence; receipt presence does not certify native travel or provider semantics.";

        public const string CampaignIdField = "campaignId";
        public const string TimelineIdField = "timelineId";
        public const string HeroIdField = "heroId";
        public const string OffsetField = "offset";
        public const string LimitField = "limit";
        public const string OkField = "ok";
        public const string ErrorField = "error";
        public const string SchemaReadyField = "schemaReady";
        public const string GenerationField = "generation";
        public const string AdmissionsField = "admissions";
        public const string MessagesField = "messages";
        public const string HistoryField = "history";
        public const string MessageCountField = "messageCount";
        public const string HistoryCountField = "historyCount";
        public const string CoverageField = "coverage";

        /// <summary>{"ok":false,"error":message}.</summary>
        public static Dictionary<string, object> Failure(string error)
        {
            return new Dictionary<string, object> { [OkField] = false, [ErrorField] = error };
        }
    }

    /// <summary>/communications/status request (query values arrive as strings, body values as JSON types).</summary>
    public sealed class CommunicationStatusRequest
    {
        // Required; empty when absent.
        public string CampaignId { get; set; } = string.Empty;
        // Trimmed; a blank or absent value means DefaultTimelineId. Must equal the active timeline.
        public string TimelineId { get; set; } = CommunicationStatusProtocol.DefaultTimelineId;
        // Empty selects every hero.
        public string HeroId { get; set; } = string.Empty;
        // Unclamped as parsed; the server clamps when querying.
        public int Offset { get; set; } = CommunicationStatusProtocol.DefaultOffset;
        public int Limit { get; set; } = CommunicationStatusProtocol.DefaultLimit;

        public bool HasCampaign => !string.IsNullOrEmpty(CampaignId);

        public static CommunicationStatusRequest FromDictionary(IDictionary<string, object>? payload)
        {
            string timeline = WireReads.ReadString(payload, CommunicationStatusProtocol.TimelineIdField, string.Empty);
            return new CommunicationStatusRequest
            {
                CampaignId = WireReads.ReadString(payload, CommunicationStatusProtocol.CampaignIdField, string.Empty),
                TimelineId = string.IsNullOrWhiteSpace(timeline) ? CommunicationStatusProtocol.DefaultTimelineId : timeline.Trim(),
                HeroId = WireReads.ReadString(payload, CommunicationStatusProtocol.HeroIdField, string.Empty),
                Offset = WireReads.ReadInt(payload, CommunicationStatusProtocol.OffsetField, CommunicationStatusProtocol.DefaultOffset),
                Limit = WireReads.ReadInt(payload, CommunicationStatusProtocol.LimitField, CommunicationStatusProtocol.DefaultLimit)
            };
        }
    }

    /// <summary>
    /// Communication ledger snapshot. Before the schema exists:
    /// {"ok":true,"schemaReady":false,"messages":[],"history":[],"messageCount":0,"historyCount":0,"timelineId":..};
    /// afterwards {"ok":true,"generation":..,"timelineId":..,"admissions":[..],"messages":[..],"history":[..],
    /// "messageCount":long,"historyCount":long,"coverage":..}.
    /// </summary>
    public sealed class CommunicationStatusResponse
    {
        private CommunicationStatusResponse(bool schemaReady, string generation, string timelineId,
            IEnumerable<Dictionary<string, object>>? admissions, IEnumerable<Dictionary<string, object>>? messages,
            IEnumerable<Dictionary<string, object>>? history, long messageCount, long historyCount)
        {
            SchemaReady = schemaReady;
            Generation = generation;
            TimelineId = timelineId;
            Admissions = WireReads.CopyRows(admissions);
            Messages = WireReads.CopyRows(messages);
            History = WireReads.CopyRows(history);
            MessageCount = messageCount;
            HistoryCount = historyCount;
        }

        public bool Ok => true;
        public bool SchemaReady { get; }
        public string Generation { get; }
        public string TimelineId { get; }
        public IReadOnlyList<Dictionary<string, object>> Admissions { get; }
        public IReadOnlyList<Dictionary<string, object>> Messages { get; }
        public IReadOnlyList<Dictionary<string, object>> History { get; }
        public long MessageCount { get; }
        public long HistoryCount { get; }

        public static CommunicationStatusResponse NoSchema(string timelineId) =>
            new CommunicationStatusResponse(false, string.Empty, timelineId, null, null, null, 0, 0);

        public static CommunicationStatusResponse Snapshot(string generation, string timelineId,
            IEnumerable<Dictionary<string, object>>? admissions, IEnumerable<Dictionary<string, object>>? messages,
            IEnumerable<Dictionary<string, object>>? history, long messageCount, long historyCount) =>
            new CommunicationStatusResponse(true, generation, timelineId, admissions, messages, history, messageCount, historyCount);

        public Dictionary<string, object> ToDictionary()
        {
            if (!SchemaReady)
                return new Dictionary<string, object>
                {
                    [CommunicationStatusProtocol.OkField] = Ok,
                    [CommunicationStatusProtocol.SchemaReadyField] = false,
                    [CommunicationStatusProtocol.MessagesField] = new object[0],
                    [CommunicationStatusProtocol.HistoryField] = new object[0],
                    // The no-schema form has always carried int zeros.
                    [CommunicationStatusProtocol.MessageCountField] = 0,
                    [CommunicationStatusProtocol.HistoryCountField] = 0,
                    [CommunicationStatusProtocol.TimelineIdField] = TimelineId
                };
            return new Dictionary<string, object>
            {
                [CommunicationStatusProtocol.OkField] = Ok,
                [CommunicationStatusProtocol.GenerationField] = Generation,
                [CommunicationStatusProtocol.TimelineIdField] = TimelineId,
                [CommunicationStatusProtocol.AdmissionsField] = new List<Dictionary<string, object>>(Admissions),
                [CommunicationStatusProtocol.MessagesField] = new List<Dictionary<string, object>>(Messages),
                [CommunicationStatusProtocol.HistoryField] = new List<Dictionary<string, object>>(History),
                [CommunicationStatusProtocol.MessageCountField] = MessageCount,
                [CommunicationStatusProtocol.HistoryCountField] = HistoryCount,
                [CommunicationStatusProtocol.CoverageField] = CommunicationStatusProtocol.Coverage
            };
        }
    }
}
