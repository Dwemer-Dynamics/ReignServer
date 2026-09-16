using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureInternationalCourtLifeSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS international_court_queue (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,matter_id TEXT NOT NULL,kind TEXT NOT NULL,
severity_rank INTEGER NOT NULL,created_day REAL NOT NULL,payload_json TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
PRIMARY KEY(campaign_id,timeline_id,matter_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS international_court_referrals (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,matter_id TEXT NOT NULL,referral_id TEXT NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,referral_id));");
        }

        private static void QueueInternationalPoliticalIncident(string campaignId, string timelineId, double day,
            string incidentId, Dictionary<string, object> world, Dictionary<string, object> origin,
            Dictionary<string, object> target, List<string> originLords, List<string> targetLords,
            int severityRank, string title, string premise, string channel, string originStance, string targetStance, string polarity)
        {
            bool playerOrigin = ReadBool(origin, "isPlayerKingdom", false);
            Dictionary<string, object> foreign = playerOrigin ? target : origin;
            Dictionary<string, object> player = playerOrigin ? origin : target;
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                ["matterId"] = incidentId, ["kind"] = "incident", ["title"] = title, ["summary"] = premise,
                ["severityRank"] = Math.Max(0, Math.Min(3, severityRank - 1)), ["createdDay"] = day,
                ["foreignKingdomId"] = ReadString(foreign, "kingdomId", ""),
                ["foreignRulerId"] = ReadString(foreign, "leaderHeroId", ""),
                ["playerKingdomId"] = ReadString(player, "kingdomId", ""),
                ["domesticLordIds"] = playerOrigin ? originLords : targetLords,
                ["foreignLordIds"] = playerOrigin ? targetLords : originLords,
                ["foreignRuling"] = playerOrigin ? targetStance : originStance,
                ["pressureAlreadyApplied"] = true, ["channel"] = channel,
                ["constructive"] = polarity != "hostile",
                ["foreign"] = foreign, ["player"] = player,
                ["playerDecision"] = "awaiting_player"
            };
            StoreInternationalQueueItem(campaignId, timelineId, incidentId, "incident",
                Math.Max(0, Math.Min(3, severityRank - 1)), day, data);
        }

        private static void StoreInternationalQueueItem(string campaignId, string timelineId, string matterId,
            string kind, int severity, double day, Dictionary<string, object> data)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                ExecuteSql(connection, @"INSERT INTO international_court_queue
(campaign_id,timeline_id,matter_id,kind,severity_rank,created_day,payload_json,status)
VALUES($campaign,$timeline,$matter,$kind,$severity,$day,$payload,'pending')
ON CONFLICT(campaign_id,timeline_id,matter_id) DO NOTHING;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["matter"] = matterId,
                    ["kind"] = kind, ["severity"] = severity, ["day"] = day, ["payload"] = Json.Serialize(data)
                });
            }
        }

        private static Dictionary<string, object> InternationalCourtPendingApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            Dictionary<string, object> profiles = new Dictionary<string, object>();
            Dictionary<string, object> power = new Dictionary<string, object>();
            foreach (Dictionary<string, object> kingdom in ReadDictionaryList(ReadDictionary(payload, "worldSnapshot"), "kingdoms"))
                profiles[ReadString(kingdom, "kingdomId", "")] = LoadDirectorTraits(ctx.CampaignId, kingdom);
            foreach (Dictionary<string, object> assessment in BuildNationalPowerAssessments(ReadDictionary(payload, "worldSnapshot")
                ?? new Dictionary<string, object>(), ReadDirectorState(ctx.CampaignId)).OfType<Dictionary<string, object>>())
                power[ReadString(assessment, "kingdomId", "")] = ReadDouble(assessment, "score", 0d);
            using (ReignDbConnection connection = OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                EnsureForeignAmbassadorSchema(connection);
                List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT payload_json FROM international_court_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='pending' ORDER BY created_day,matter_id LIMIT 100;",
                    new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId });
                List<Dictionary<string, object>> replies = QuerySql(connection, @"SELECT l.matter_id,r.* FROM international_court_referrals l
JOIN ambassador_referrals r ON l.referral_id=r.referral_id AND l.campaign_id=r.campaign_id AND l.timeline_id=r.timeline_id
WHERE l.campaign_id=$campaign AND l.timeline_id=$timeline ORDER BY r.due_day,r.referral_id;",
                    new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId });
                foreach (Dictionary<string, object> reply in replies.Where(x => ReadString(x, "status", "") == "countered"))
                {
                    Dictionary<string, object> counter = ParseAmbassadorJsonObject(ReadString(reply, "counter_terms_json", "{}"));
                    Dictionary<string, object> world = ReadDictionary(payload, "worldSnapshot") ?? new Dictionary<string, object>();
                    Dictionary<string, object> actor = FindDirectorKingdom(world, ReadFirstString(counter, "actorKingdomId", "actorKingdomStringId"));
                    Dictionary<string, object> normalized = NormalizeActionCommand(counter, ctx.CampaignId, out List<string> errors,
                        BuildDirectorNormalizationPayload(world, actor ?? new Dictionary<string, object>()), "Exact sovereign counteroffer awaiting the player's acceptance.");
                    reply["normalizedAction"] = normalized;
                    reply["normalizationErrors"] = errors;
                }
                return new Dictionary<string, object> { ["ok"] = true, ["rulerProfiles"] = profiles, ["nationalPower"] = power, ["replies"] = replies,
                    ["matters"] = rows.Select(x => (object)(TryParseJsonObject(ReadString(x, "payload_json", "{}")) ?? new Dictionary<string, object>())).ToList() };
            }
        }

        private static bool IsInternationalCourtReferral(string campaignId, string timelineId, string referralId)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                return QuerySql(connection, @"SELECT matter_id FROM international_court_referrals
WHERE campaign_id=$campaign AND timeline_id=$timeline AND referral_id=$referral;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["referral"] = referralId }).Count > 0;
            }
        }

        private static Dictionary<string, object> InternationalCourtReferralApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string matterId = ReadString(payload, "matterId", "");
            string postingId = ReadString(payload, "postingId", "");
            Dictionary<string, object> candidate = ReadDictionary(payload, "candidate");
            if (string.IsNullOrWhiteSpace(matterId) || candidate == null || string.IsNullOrWhiteSpace(postingId))
                return CourtError("The case, posting, and exact proposed terms are required.");
            string command = ReadString(candidate, "command", "");
            if (!DirectorSupportedCommands.Contains(command)) return CourtError("The requested diplomatic action is unsupported.");
            using (ReignDbConnection connection = OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                Dictionary<string, object> posting = QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting AND status IN ('resident','sheltered');",
                    new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId, ["posting"] = postingId }).FirstOrDefault();
                string host = ReadString(posting, "host_kingdom_id", "");
                string foreign = ReadString(posting, "origin_kingdom_id", "");
                if (posting == null || !host.Equals(ReadString(payload, "playerKingdomStringId", ""), StringComparison.OrdinalIgnoreCase))
                    return CourtError("The represented ambassador is no longer available at this court.");
                string actor = ReadFirstString(candidate, "actorKingdomId", "actorKingdomStringId");
                string target = ReadFirstString(candidate, "targetKingdomId", "targetKingdomStringId");
                if (!((actor == host && target == foreign) || (actor == foreign && target == host)))
                    return CourtError("The referred terms must involve exactly the host and represented kingdoms.");
            }
            string hash = CourtHash(CourtCanonicalJson(candidate));
            Dictionary<string, object> referral = CreateAmbassadorReferral(ctx.CampaignId, ctx.TimelineId, postingId,
                CanonicalAmbassadorActionId(command), candidate, hash, ctx.WorldDay);
            using (ReignDbConnection connection = OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                ExecuteSql(connection, @"INSERT INTO international_court_referrals(campaign_id,timeline_id,matter_id,referral_id)
VALUES($campaign,$timeline,$matter,$referral) ON CONFLICT(campaign_id,timeline_id,referral_id) DO NOTHING;",
                    new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId,
                        ["matter"] = matterId, ["referral"] = ReadString(referral, "referralId", "") });
            }
            referral["ok"] = true;
            return referral;
        }

        private static Dictionary<string, object> InternationalCourtResolutionApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string matterId = ReadString(payload, "matterId", "");
            string receiptId = ReadString(payload, "receiptId", "");
            string outcome = ReadString(payload, "outcome", "");
            if (string.IsNullOrWhiteSpace(matterId) || string.IsNullOrWhiteSpace(receiptId)
                || !ReadBool(payload, "effectsCommitted", false)) return CourtError("A committed court decision receipt is required.");
            Dictionary<string, object> foreign = ReadDictionary(payload, "foreign");
            Dictionary<string, object> player = ReadDictionary(payload, "player");
            if (foreign == null || player == null || ReadBool(foreign, "isPlayerKingdom", false)
                || !ReadBool(player, "isPlayerKingdom", false)) return CourtError("Pressure must run from a foreign kingdom toward the player kingdom.");
            if (!new[] { "favor_domestic", "favor_foreign", "compensate", "compromise", "accept", "refuse" }.Contains(outcome))
                return CourtError("The supplied outcome is not a completed international decision.");
            if (ReadInt(payload, "continuityVersion", 0) > 0 && NormalizeCourtLifeResolutionFacts(payload) == null)
                return CourtError("The native completion evidence is incomplete or inconsistent.");
            int severity = Math.Max(0, Math.Min(3, ReadInt(payload, "severityRank", 0)));
            bool favorable = outcome == "favor_foreign" || outcome == "compensate" || outcome == "compromise" || outcome == "accept";
            int delta = favorable ? -ReignInternationalDocketRules.PressureMagnitude((ReignNobleMatterSeverity)severity)
                : ReignInternationalDocketRules.PressureMagnitude((ReignNobleMatterSeverity)severity);
            bool incidentAlreadyApplied = false;
            using (ReignDbConnection connection = OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                Dictionary<string, object> queued = QuerySql(connection, @"SELECT payload_json FROM international_court_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline AND matter_id=$matter;", new Dictionary<string, object>
                    { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId, ["matter"] = matterId }).FirstOrDefault();
                incidentAlreadyApplied = ReadBool(TryParseJsonObject(ReadString(queued, "payload_json", "{}")), "pressureAlreadyApplied", false);
            }
            int after = incidentAlreadyApplied ? ReadPoliticalPressureValue(ctx.CampaignId, ctx.TimelineId,
                ReadString(foreign, "kingdomId", ""), ReadString(player, "kingdomId", ""))
                : ApplyPoliticalPressureDelta(ctx.CampaignId, ctx.TimelineId, ctx.WorldDay, foreign, player,
                    delta, ReadString(payload, "channel", "treaty_strain"), "court-resolution:" + matterId);
            using (ReignDbConnection connection = OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                ExecuteSql(connection, @"UPDATE international_court_queue SET status='resolved'
WHERE campaign_id=$campaign AND timeline_id=$timeline AND matter_id=$matter;",
                    new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId, ["matter"] = matterId });
            }
            StoreCourtLifeResolutionContinuity(ctx.CampaignId, ctx.TimelineId, payload);
            return new Dictionary<string, object> { ["ok"] = true, ["matterId"] = matterId,
                ["receiptId"] = receiptId, ["pressureAfter"] = after, ["requestedDelta"] = incidentAlreadyApplied ? 0 : delta };
        }
    }
}
