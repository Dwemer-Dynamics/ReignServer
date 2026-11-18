using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace Reign.Core.Contracts.Relationships
{
    /// <summary>
    /// Typed wire contract for the clan-conflict notice routes. The server keeps its own
    /// JSON serializer: these types map to and from the exact Dictionary&lt;string, object&gt;
    /// shapes it already writes (same keys, casing, key order and omission rules), so
    /// adopting them never changes bytes on the wire.
    /// </summary>
    public static class ClanConflictNoticeProtocol
    {
        // GET; query string campaignId, timelineId, limit.
        public const string NoticesRoute = "/clan-conflicts/notices";
        // POST; JSON body ClanConflictAcknowledgeRequest.
        public const string AcknowledgeRoute = "/clan-conflicts/acknowledge";

        public const string DefaultCampaignId = "default";
        public const string DefaultTimelineId = "main";
        public const int DefaultNoticeLimit = 5;
        public const int MinNoticeLimit = 1;
        public const int MaxNoticeLimit = 20;
        public const string NotFoundError = "Clan conflict incident was not found.";

        // Request and envelope fields (camelCase on the wire).
        public const string CampaignIdField = "campaignId";
        public const string TimelineIdField = "timelineId";
        public const string LimitField = "limit";
        public const string IncidentIdField = "incidentId";
        public const string WorldDayField = "worldDay";
        public const string OkField = "ok";
        public const string ErrorField = "error";
        public const string CountField = "count";
        public const string NoticesField = "notices";

        /// <summary>Same rule as the server's ReadString: a missing or null value falls back.</summary>
        internal static string ReadString(IDictionary<string, object>? source, string key, string fallback)
        {
            if (source == null || !source.TryGetValue(key, out object? value) || value == null) return fallback;
            return Convert.ToString(value) ?? fallback;
        }

        internal static double? ReadOptionalDouble(IDictionary<string, object>? source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object? value) || value == null) return null;
            return double.TryParse(Convert.ToString(value), out double parsed) ? parsed : (double?)null;
        }

        internal static bool ReadBool(IDictionary<string, object>? source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object? value) || value == null) return false;
            if (value is bool flag) return flag;
            return bool.TryParse(Convert.ToString(value), out bool parsed) && parsed;
        }
    }

    /// <summary>
    /// Columns of one notice row as the client reads them. A notice on the wire is the
    /// complete clan_conflict_incidents row (snake_case column names, database value types)
    /// plus news_description; the server passes rows through unchanged.
    /// </summary>
    public static class ClanConflictNoticeFields
    {
        public const string IncidentId = "incident_id";
        public const string TimelineId = "timeline_id";
        public const string WorldDay = "world_day";                       // REAL
        public const string KingdomId = "kingdom_id";
        public const string KingdomName = "kingdom_name";
        public const string RulerId = "ruler_id";
        public const string RulerName = "ruler_name";
        public const string ClanAName = "clan_a_name";
        public const string ClanBName = "clan_b_name";
        public const string LeaderAId = "leader_a_id";
        public const string LeaderBId = "leader_b_id";
        public const string InvolvedANamesJson = "involved_a_names_json"; // JSON array text
        public const string InvolvedBNamesJson = "involved_b_names_json"; // JSON array text
        public const string ArchetypeId = "archetype_id";
        public const string Headline = "headline";
        public const string Narrative = "narrative";
        public const string WinningSide = "winning_side";                 // "a" or "b"
        public const string CharmSkill = "charm_skill";                   // INTEGER
        public const string CharmChanceBasisPoints = "charm_chance_bp";   // INTEGER
        public const string CharmRoll = "charm_roll";                     // INTEGER
        public const string MediationSucceeded = "mediation_succeeded";   // INTEGER 0/1
        public const string NoticeStatus = "notice_status";
        // Added by the server from the archetype catalog; not a table column.
        public const string NewsDescription = "news_description";
    }

    /// <summary>GET /clan-conflicts/notices query.</summary>
    public sealed class ClanConflictNoticesRequest
    {
        public string CampaignId { get; set; } = ClanConflictNoticeProtocol.DefaultCampaignId;
        public string TimelineId { get; set; } = ClanConflictNoticeProtocol.DefaultTimelineId;
        // Optional on the wire; clamped to [MinNoticeLimit, MaxNoticeLimit], default DefaultNoticeLimit.
        public int Limit { get; set; } = ClanConflictNoticeProtocol.DefaultNoticeLimit;

        /// <summary>Exact server parsing: present keys are used verbatim, an unparsable limit falls back to the default.</summary>
        public static ClanConflictNoticesRequest FromQuery(IDictionary<string, string>? query)
        {
            var request = new ClanConflictNoticesRequest();
            if (query == null) return request;
            if (query.TryGetValue(ClanConflictNoticeProtocol.CampaignIdField, out string campaign)) request.CampaignId = campaign;
            if (query.TryGetValue(ClanConflictNoticeProtocol.TimelineIdField, out string timeline)) request.TimelineId = timeline;
            int limit = ClanConflictNoticeProtocol.DefaultNoticeLimit;
            if (query.TryGetValue(ClanConflictNoticeProtocol.LimitField, out string rawLimit)
                && int.TryParse(rawLimit, out int parsedLimit))
                limit = parsedLimit;
            request.Limit = Math.Max(ClanConflictNoticeProtocol.MinNoticeLimit, Math.Min(ClanConflictNoticeProtocol.MaxNoticeLimit, limit));
            return request;
        }

        /// <summary>Route plus query in the order the client sends it.</summary>
        public string ToRelativeUri()
        {
            return ClanConflictNoticeProtocol.NoticesRoute
                + "?" + ClanConflictNoticeProtocol.CampaignIdField + "=" + Uri.EscapeDataString(CampaignId ?? string.Empty)
                + "&" + ClanConflictNoticeProtocol.TimelineIdField + "=" + Uri.EscapeDataString(TimelineId ?? string.Empty)
                + "&" + ClanConflictNoticeProtocol.LimitField + "=" + Limit.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// GET /clan-conflicts/notices response: {"ok":true,"count":n,"notices":[row...]}.
    /// Rows are passed through as the server read them, so their keys and value types are untouched.
    /// </summary>
    public sealed class ClanConflictNoticesResponse
    {
        public ClanConflictNoticesResponse(IEnumerable<Dictionary<string, object>>? notices)
        {
            Notices = new List<Dictionary<string, object>>(notices ?? Array.Empty<Dictionary<string, object>>());
        }

        public bool Ok => true;
        public int Count => Notices.Count;
        public IReadOnlyList<Dictionary<string, object>> Notices { get; }

        public Dictionary<string, object> ToDictionary()
        {
            var notices = new List<object>(Notices.Count);
            foreach (Dictionary<string, object> notice in Notices) notices.Add(notice);
            return new Dictionary<string, object>
            {
                [ClanConflictNoticeProtocol.OkField] = Ok,
                [ClanConflictNoticeProtocol.CountField] = Count,
                [ClanConflictNoticeProtocol.NoticesField] = notices
            };
        }

        /// <summary>Reads a deserialized response; null when it is not ok or has no notices array.</summary>
        public static ClanConflictNoticesResponse? FromDictionary(IDictionary<string, object>? source)
        {
            if (!ClanConflictNoticeProtocol.ReadBool(source, ClanConflictNoticeProtocol.OkField)) return null;
            if (!source!.TryGetValue(ClanConflictNoticeProtocol.NoticesField, out object? value)
                || !(value is IEnumerable items) || value is string) return null;
            var notices = new List<Dictionary<string, object>>();
            foreach (object? item in items)
            {
                if (item is Dictionary<string, object> row) notices.Add(row);
                else if (item is IDictionary<string, object> other) notices.Add(new Dictionary<string, object>(other));
            }
            return new ClanConflictNoticesResponse(notices);
        }
    }

    /// <summary>POST /clan-conflicts/acknowledge body.</summary>
    public sealed class ClanConflictAcknowledgeRequest
    {
        public string CampaignId { get; set; } = ClanConflictNoticeProtocol.DefaultCampaignId;
        public string TimelineId { get; set; } = ClanConflictNoticeProtocol.DefaultTimelineId;
        // Required for a successful acknowledgement; empty never matches an incident.
        public string IncidentId { get; set; } = string.Empty;
        // Optional; the client sends the game day as a float and the server does not use it. A client
        // adopting this type should keep sending a float: widening to double changes the serialized digits.
        public double? WorldDay { get; set; }

        public static ClanConflictAcknowledgeRequest FromDictionary(IDictionary<string, object>? payload)
        {
            return new ClanConflictAcknowledgeRequest
            {
                CampaignId = ClanConflictNoticeProtocol.ReadString(payload, ClanConflictNoticeProtocol.CampaignIdField, ClanConflictNoticeProtocol.DefaultCampaignId),
                TimelineId = ClanConflictNoticeProtocol.ReadString(payload, ClanConflictNoticeProtocol.TimelineIdField, ClanConflictNoticeProtocol.DefaultTimelineId),
                IncidentId = ClanConflictNoticeProtocol.ReadString(payload, ClanConflictNoticeProtocol.IncidentIdField, string.Empty),
                WorldDay = ClanConflictNoticeProtocol.ReadOptionalDouble(payload, ClanConflictNoticeProtocol.WorldDayField)
            };
        }

        /// <summary>Body in the client's key order; worldDay is omitted when unknown.</summary>
        public Dictionary<string, object> ToDictionary()
        {
            var body = new Dictionary<string, object>
            {
                [ClanConflictNoticeProtocol.CampaignIdField] = CampaignId ?? ClanConflictNoticeProtocol.DefaultCampaignId,
                [ClanConflictNoticeProtocol.TimelineIdField] = TimelineId ?? ClanConflictNoticeProtocol.DefaultTimelineId,
                [ClanConflictNoticeProtocol.IncidentIdField] = IncidentId ?? string.Empty
            };
            if (WorldDay.HasValue) body[ClanConflictNoticeProtocol.WorldDayField] = WorldDay.Value;
            return body;
        }
    }

    /// <summary>
    /// POST /clan-conflicts/acknowledge response: {"ok":true,"incidentId":id} or
    /// {"ok":false,"error":message}. Each form carries only its own second field.
    /// </summary>
    public sealed class ClanConflictAcknowledgeResponse
    {
        private ClanConflictAcknowledgeResponse(bool ok, string incidentId, string error)
        {
            Ok = ok;
            IncidentId = incidentId;
            Error = error;
        }

        public bool Ok { get; }
        public string IncidentId { get; }
        public string Error { get; }

        public static ClanConflictAcknowledgeResponse Acknowledged(string incidentId) =>
            new ClanConflictAcknowledgeResponse(true, incidentId ?? string.Empty, string.Empty);

        public static ClanConflictAcknowledgeResponse NotFound() =>
            new ClanConflictAcknowledgeResponse(false, string.Empty, ClanConflictNoticeProtocol.NotFoundError);

        public Dictionary<string, object> ToDictionary()
        {
            if (Ok)
                return new Dictionary<string, object>
                {
                    [ClanConflictNoticeProtocol.OkField] = true,
                    [ClanConflictNoticeProtocol.IncidentIdField] = IncidentId
                };
            return new Dictionary<string, object>
            {
                [ClanConflictNoticeProtocol.OkField] = false,
                [ClanConflictNoticeProtocol.ErrorField] = Error
            };
        }

        public static ClanConflictAcknowledgeResponse FromDictionary(IDictionary<string, object>? source)
        {
            bool ok = ClanConflictNoticeProtocol.ReadBool(source, ClanConflictNoticeProtocol.OkField);
            return new ClanConflictAcknowledgeResponse(ok,
                ok ? ClanConflictNoticeProtocol.ReadString(source, ClanConflictNoticeProtocol.IncidentIdField, string.Empty) : string.Empty,
                ok ? string.Empty : ClanConflictNoticeProtocol.ReadString(source, ClanConflictNoticeProtocol.ErrorField, string.Empty));
        }
    }
}
