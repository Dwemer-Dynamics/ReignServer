using System;
using System.Collections.Generic;
using System.Globalization;
using Reign.Core.Contracts.Platform;

namespace Reign.Core.Contracts.Diplomacy
{
    /// <summary>
    /// Typed wire contract for the political-pressure notice routes. Like the clan-conflict contract,
    /// these types map to and from the exact Dictionary&lt;string, object&gt; shapes the server writes
    /// (same keys, casing, key order, value types and omission rules), so adopting them never changes
    /// bytes on the wire.
    /// </summary>
    public static class PoliticalPressureNoticeProtocol
    {
        // GET; query string campaignId, timelineId, limit.
        public const string NoticesRoute = "/political-pressure/notices";
        // POST; JSON body PoliticalPressureAcknowledgeRequest.
        public const string AcknowledgeRoute = "/political-pressure/acknowledge";

        public const string DefaultCampaignId = "default";
        public const string DefaultTimelineId = "main";
        public const int DefaultNoticeLimit = 3;
        public const int MinNoticeLimit = 1;
        public const int MaxNoticeLimit = 10;
        // readinessPressure carries at most this many directed pairs; complete is false beyond it.
        public const int MaxReadinessPairs = 1024;
        public const int ReadinessPressureVersion = 1;
        // Server-side default for an absent or unparsable worldDay and receipt before/after values.
        public const double UnknownValue = -1d;
        public const string NotFoundError = "Political pressure incident was not found.";

        // Request and envelope fields (camelCase on the wire).
        public const string CampaignIdField = "campaignId";
        public const string TimelineIdField = "timelineId";
        public const string LimitField = "limit";
        public const string IncidentIdField = "incidentId";
        public const string WorldDayField = "worldDay";
        public const string EffectReceiptsField = "effectReceipts";
        public const string OkField = "ok";
        public const string ErrorField = "error";
        public const string CountField = "count";
        public const string NoticesField = "notices";
        public const string ReadinessPressureField = "readinessPressure";
        public const string VersionField = "version";
        public const string CompleteField = "complete";
        public const string PairsField = "pairs";

        // effectReceipts[] fields.
        public const string ReceiptEffectIdField = "effectId";
        public const string ReceiptOkField = "ok";
        public const string ReceiptBeforeField = "before";
        public const string ReceiptAfterField = "after";
        public const string ReceiptErrorField = "error";
    }

    /// <summary>
    /// Columns of one notice row as the client reads them. A notice on the wire is the complete
    /// political_pressure_incidents row (snake_case, database value types) plus news_description and
    /// nativeEffects; the server passes rows through unchanged.
    /// </summary>
    public static class PoliticalPressureNoticeFields
    {
        public const string IncidentId = "incident_id";
        public const string TimelineId = "timeline_id";
        public const string WorldDay = "world_day";
        public const string ArchetypeId = "archetype_id";
        public const string NoticeStatus = "notice_status";
        // Added by the server; not table columns.
        public const string NewsDescription = "news_description";
        public const string NativeEffects = "nativeEffects";

        // nativeEffects[] columns.
        public const string EffectId = "effect_id";
        public const string EffectType = "effect_type";
        public const string EffectTargetId = "target_id";
        public const string EffectAmount = "amount";
        public const string EffectStatus = "status";

        // readinessPressure.pairs[] columns (political_pressure_state).
        public const string ActorKingdomId = "actor_kingdom_id";
        public const string TargetKingdomId = "target_kingdom_id";
        public const string PressureValue = "pressure_value";
        public const string DominantChannel = "dominant_channel";
    }

    /// <summary>GET /political-pressure/notices query.</summary>
    public sealed class PoliticalPressureNoticesRequest
    {
        public string CampaignId { get; set; } = PoliticalPressureNoticeProtocol.DefaultCampaignId;
        public string TimelineId { get; set; } = PoliticalPressureNoticeProtocol.DefaultTimelineId;
        // Optional; a parsed value is clamped to [MinNoticeLimit, MaxNoticeLimit], otherwise DefaultNoticeLimit.
        public int Limit { get; set; } = PoliticalPressureNoticeProtocol.DefaultNoticeLimit;

        /// <summary>Exact server parsing: present keys are used verbatim; an unparsable limit falls back to the default.</summary>
        public static PoliticalPressureNoticesRequest FromQuery(IDictionary<string, string>? query)
        {
            var request = new PoliticalPressureNoticesRequest();
            if (query == null) return request;
            if (query.TryGetValue(PoliticalPressureNoticeProtocol.CampaignIdField, out string campaign)) request.CampaignId = campaign;
            if (query.TryGetValue(PoliticalPressureNoticeProtocol.TimelineIdField, out string timeline)) request.TimelineId = timeline;
            if (query.TryGetValue(PoliticalPressureNoticeProtocol.LimitField, out string rawLimit)
                && int.TryParse(rawLimit, out int parsedLimit))
                request.Limit = Math.Max(PoliticalPressureNoticeProtocol.MinNoticeLimit,
                    Math.Min(PoliticalPressureNoticeProtocol.MaxNoticeLimit, parsedLimit));
            return request;
        }

        /// <summary>Route plus query.</summary>
        public string ToRelativeUri()
        {
            return PoliticalPressureNoticeProtocol.NoticesRoute
                + "?" + PoliticalPressureNoticeProtocol.CampaignIdField + "=" + Uri.EscapeDataString(CampaignId ?? string.Empty)
                + "&" + PoliticalPressureNoticeProtocol.TimelineIdField + "=" + Uri.EscapeDataString(TimelineId ?? string.Empty)
                + "&" + PoliticalPressureNoticeProtocol.LimitField + "=" + Limit.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// readinessPressure block of the notices response:
    /// {"version":1,"campaignId":..,"timelineId":..,"complete":bool,"pairs":[row...]}.
    /// Pairs are passed through as the server read them.
    /// </summary>
    public sealed class PoliticalPressureReadiness
    {
        /// <param name="pairRows">Rows as queried; up to MaxReadinessPairs + 1 so completeness can be decided.</param>
        public PoliticalPressureReadiness(string campaignId, string timelineId, IEnumerable<Dictionary<string, object>>? pairRows)
        {
            List<Dictionary<string, object>> rows = WireReads.CopyRows(pairRows);
            CampaignId = campaignId;
            TimelineId = timelineId;
            Complete = rows.Count <= PoliticalPressureNoticeProtocol.MaxReadinessPairs;
            if (!Complete) rows = rows.GetRange(0, PoliticalPressureNoticeProtocol.MaxReadinessPairs);
            Pairs = rows;
        }

        public int Version => PoliticalPressureNoticeProtocol.ReadinessPressureVersion;
        public string CampaignId { get; }
        public string TimelineId { get; }
        public bool Complete { get; }
        public IReadOnlyList<Dictionary<string, object>> Pairs { get; }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                [PoliticalPressureNoticeProtocol.VersionField] = Version,
                [PoliticalPressureNoticeProtocol.CampaignIdField] = CampaignId,
                [PoliticalPressureNoticeProtocol.TimelineIdField] = TimelineId,
                [PoliticalPressureNoticeProtocol.CompleteField] = Complete,
                [PoliticalPressureNoticeProtocol.PairsField] = new List<Dictionary<string, object>>(Pairs)
            };
        }
    }

    /// <summary>
    /// GET /political-pressure/notices response:
    /// {"ok":true,"count":n,"notices":[row...],"readinessPressure":{...}}.
    /// </summary>
    public sealed class PoliticalPressureNoticesResponse
    {
        public PoliticalPressureNoticesResponse(IEnumerable<Dictionary<string, object>>? notices, PoliticalPressureReadiness readiness)
        {
            Notices = WireReads.CopyRows(notices);
            Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        }

        public bool Ok => true;
        public int Count => Notices.Count;
        public IReadOnlyList<Dictionary<string, object>> Notices { get; }
        public PoliticalPressureReadiness Readiness { get; }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                [PoliticalPressureNoticeProtocol.OkField] = Ok,
                [PoliticalPressureNoticeProtocol.CountField] = Count,
                [PoliticalPressureNoticeProtocol.NoticesField] = new List<Dictionary<string, object>>(Notices),
                [PoliticalPressureNoticeProtocol.ReadinessPressureField] = Readiness.ToDictionary()
            };
        }
    }

    /// <summary>One effectReceipts[] entry of an acknowledgement.</summary>
    public sealed class PoliticalPressureEffectReceipt
    {
        public string EffectId { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public double Before { get; set; } = PoliticalPressureNoticeProtocol.UnknownValue;
        public double After { get; set; } = PoliticalPressureNoticeProtocol.UnknownValue;
        public string Error { get; set; } = string.Empty;

        public static PoliticalPressureEffectReceipt FromDictionary(IDictionary<string, object>? receipt)
        {
            return new PoliticalPressureEffectReceipt
            {
                EffectId = WireReads.ReadString(receipt, PoliticalPressureNoticeProtocol.ReceiptEffectIdField, string.Empty),
                Ok = WireReads.ReadBool(receipt, PoliticalPressureNoticeProtocol.ReceiptOkField, false),
                Before = WireReads.ReadDouble(receipt, PoliticalPressureNoticeProtocol.ReceiptBeforeField, PoliticalPressureNoticeProtocol.UnknownValue),
                After = WireReads.ReadDouble(receipt, PoliticalPressureNoticeProtocol.ReceiptAfterField, PoliticalPressureNoticeProtocol.UnknownValue),
                Error = WireReads.ReadString(receipt, PoliticalPressureNoticeProtocol.ReceiptErrorField, string.Empty)
            };
        }
    }

    /// <summary>POST /political-pressure/acknowledge body.</summary>
    public sealed class PoliticalPressureAcknowledgeRequest
    {
        public string CampaignId { get; set; } = PoliticalPressureNoticeProtocol.DefaultCampaignId;
        public string TimelineId { get; set; } = PoliticalPressureNoticeProtocol.DefaultTimelineId;
        public string IncidentId { get; set; } = string.Empty;
        // Receipt day recorded on effects and activity; UnknownValue when absent or unparsable.
        public double WorldDay { get; set; } = PoliticalPressureNoticeProtocol.UnknownValue;
        // Only dictionary entries of effectReceipts are read; other entries are ignored.
        public List<PoliticalPressureEffectReceipt> EffectReceipts { get; set; } = new List<PoliticalPressureEffectReceipt>();

        public static PoliticalPressureAcknowledgeRequest FromDictionary(IDictionary<string, object>? payload)
        {
            var request = new PoliticalPressureAcknowledgeRequest
            {
                CampaignId = WireReads.ReadString(payload, PoliticalPressureNoticeProtocol.CampaignIdField, PoliticalPressureNoticeProtocol.DefaultCampaignId),
                TimelineId = WireReads.ReadString(payload, PoliticalPressureNoticeProtocol.TimelineIdField, PoliticalPressureNoticeProtocol.DefaultTimelineId),
                IncidentId = WireReads.ReadString(payload, PoliticalPressureNoticeProtocol.IncidentIdField, string.Empty),
                WorldDay = WireReads.ReadDouble(payload, PoliticalPressureNoticeProtocol.WorldDayField, PoliticalPressureNoticeProtocol.UnknownValue)
            };
            foreach (Dictionary<string, object> receipt in WireReads.ReadDictionaryList(payload, PoliticalPressureNoticeProtocol.EffectReceiptsField))
                request.EffectReceipts.Add(PoliticalPressureEffectReceipt.FromDictionary(receipt));
            return request;
        }
    }

    /// <summary>
    /// POST /political-pressure/acknowledge response: {"ok":true,"incidentId":id} or
    /// {"ok":false,"error":message}. Each form carries only its own second field.
    /// </summary>
    public sealed class PoliticalPressureAcknowledgeResponse
    {
        private PoliticalPressureAcknowledgeResponse(bool ok, string incidentId, string error)
        {
            Ok = ok;
            IncidentId = incidentId;
            Error = error;
        }

        public bool Ok { get; }
        public string IncidentId { get; }
        public string Error { get; }

        public static PoliticalPressureAcknowledgeResponse Acknowledged(string incidentId) =>
            new PoliticalPressureAcknowledgeResponse(true, incidentId ?? string.Empty, string.Empty);

        public static PoliticalPressureAcknowledgeResponse NotFound() =>
            new PoliticalPressureAcknowledgeResponse(false, string.Empty, PoliticalPressureNoticeProtocol.NotFoundError);

        public Dictionary<string, object> ToDictionary()
        {
            if (Ok)
                return new Dictionary<string, object>
                {
                    [PoliticalPressureNoticeProtocol.OkField] = true,
                    [PoliticalPressureNoticeProtocol.IncidentIdField] = IncidentId
                };
            return new Dictionary<string, object>
            {
                [PoliticalPressureNoticeProtocol.OkField] = false,
                [PoliticalPressureNoticeProtocol.ErrorField] = Error
            };
        }
    }
}
