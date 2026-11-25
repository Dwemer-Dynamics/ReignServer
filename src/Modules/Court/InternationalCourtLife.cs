using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class CourtService
    {
        internal static void EnsureInternationalCourtLifeSchema(ReignDbConnection connection)
        {
            CourtSchema.CreateInternationalCourtQueue(connection);
            CourtSchema.CreateInternationalCourtReferrals(connection);
        }

        internal static void QueueInternationalPoliticalIncident(string campaignId, string timelineId, double day,
            string incidentId, Dictionary<string, object> world, Dictionary<string, object> origin,
            Dictionary<string, object> target, List<string> originLords, List<string> targetLords,
            int severityRank, string title, string premise, string channel, string originStance, string targetStance, string polarity)
        {
            string payload = BuildInternationalPoliticalIncidentPayload(incidentId, day, origin, target, originLords,
                targetLords, severityRank, title, premise, channel, originStance, targetStance, polarity);
            StoreInternationalQueueItem(campaignId, timelineId, incidentId, "incident",
                PoliticalIncidentCourtSeverity(severityRank), day, payload);
        }

        internal static string BuildInternationalPoliticalIncidentPayload(string incidentId, double day,
            Dictionary<string, object> origin, Dictionary<string, object> target, List<string> originLords, List<string> targetLords,
            int severityRank, string title, string premise, string channel, string originStance, string targetStance, string polarity)
        {
            bool playerOrigin = ReadBool(origin, "isPlayerKingdom", false);
            Dictionary<string, object> foreign = playerOrigin ? target : origin;
            Dictionary<string, object> player = playerOrigin ? origin : target;
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                ["matterId"] = incidentId, ["kind"] = "incident", ["title"] = title, ["summary"] = premise,
                ["severityRank"] = PoliticalIncidentCourtSeverity(severityRank), ["createdDay"] = day,
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
            return Json.Serialize(data);
        }

        private static int PoliticalIncidentCourtSeverity(int severityRank) => Math.Max(0, Math.Min(3, severityRank - 1));

        internal static void StoreExactInternationalPoliticalIncident(ReignDbConnection connection, ReignDbTransaction transaction,
            string campaignId, string timelineId, string incidentId, int severityRank, double day, string payloadJson)
        {
            if (connection == null || transaction == null || !ReferenceEquals(transaction.Connection, connection)
                || ReignPostgreSqlStorage.CampaignIdForConnection(connection) != campaignId)
                throw new InvalidOperationException("The political incident must use its campaign's active transaction.");

            var parameters = InternationalCourtQueueParameters(campaignId, timelineId, incidentId, "incident",
                PoliticalIncidentCourtSeverity(severityRank), day, payloadJson);
            CourtStore.InsertOrIgnoreInternationalCourtQueue(connection, parameters, transaction);
            // A retry must prove the immutable material before treating an existing row as its own.
            // The mutable status is left intact, including when the original item was resolved.
            if (!CourtStore.HasExactInternationalCourtQueueMaterial(connection, parameters, transaction))
                throw new InvalidOperationException("The international court queue has conflicting political incident material.");
        }

        internal static void StoreInternationalQueueItem(string campaignId, string timelineId, string matterId,
            string kind, int severity, double day, Dictionary<string, object> data)
            => StoreInternationalQueueItem(campaignId, timelineId, matterId, kind, severity, day, Json.Serialize(data));

        private static void StoreInternationalQueueItem(string campaignId, string timelineId, string matterId,
            string kind, int severity, double day, string payloadJson)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                CourtStore.InsertOrIgnoreInternationalCourtQueue(connection,
                    InternationalCourtQueueParameters(campaignId, timelineId, matterId, kind, severity, day, payloadJson));
            }
        }

        private static Dictionary<string, object> InternationalCourtQueueParameters(string campaignId, string timelineId,
            string matterId, string kind, int severity, double day, string payloadJson) => new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId, ["matter"] = matterId,
                ["kind"] = kind, ["severity"] = severity, ["day"] = day, ["payload"] = payloadJson
            };

        internal static Dictionary<string, object> InternationalCourtPendingApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            Dictionary<string, object> profiles = new Dictionary<string, object>();
            Dictionary<string, object> power = new Dictionary<string, object>();
            foreach (Dictionary<string, object> kingdom in ReadDictionaryList(ReadDictionary(payload, "worldSnapshot"), "kingdoms"))
                profiles[ReadString(kingdom, "kingdomId", "")] = DiplomacyService.LoadDirectorTraits(ctx.CampaignId, kingdom);
            foreach (Dictionary<string, object> assessment in DiplomacyService.BuildNationalPowerAssessments(ReadDictionary(payload, "worldSnapshot")
                ?? new Dictionary<string, object>(), DiplomacyService.ReadDirectorState(ctx.CampaignId)).OfType<Dictionary<string, object>>())
                power[ReadString(assessment, "kingdomId", "")] = ReadDouble(assessment, "score", 0d);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                EnsureForeignAmbassadorSchema(connection);
                List<Dictionary<string, object>> rows = CourtStore.SelectInternationalCourtQueue(connection, new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId });
                List<Dictionary<string, object>> replies = CourtStore.SelectInternationalCourtReferrals(connection, new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId });
                foreach (Dictionary<string, object> reply in replies.Where(x => ReadString(x, "status", "") == "countered"))
                {
                    Dictionary<string, object> counter = ParseAmbassadorJsonObject(ReadString(reply, "counter_terms_json", "{}"));
                    Dictionary<string, object> world = ReadDictionary(payload, "worldSnapshot") ?? new Dictionary<string, object>();
                    Dictionary<string, object> actor = DiplomacyService.FindDirectorKingdom(world, ReadFirstString(counter, "actorKingdomId", "actorKingdomStringId"));
                    Dictionary<string, object> normalized = ActionPipelineService.NormalizeActionCommand(counter, ctx.CampaignId, out List<string> errors,
                        DiplomacyService.BuildDirectorNormalizationPayload(world, actor ?? new Dictionary<string, object>()), "Exact sovereign counteroffer awaiting the player's acceptance.");
                    reply["normalizedAction"] = normalized;
                    reply["normalizationErrors"] = errors;
                }
                return new Dictionary<string, object> { ["ok"] = true, ["rulerProfiles"] = profiles, ["nationalPower"] = power, ["replies"] = replies,
                    ["matters"] = rows.Select(x => (object)(TryParseJsonObject(ReadString(x, "payload_json", "{}")) ?? new Dictionary<string, object>())).ToList() };
            }
        }

        private static bool IsInternationalCourtReferral(string campaignId, string timelineId, string referralId)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                return CourtStore.SelectInternationalCourtReferrals2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["referral"] = referralId }).Count > 0;
            }
        }

        internal static Dictionary<string, object> InternationalCourtReferralApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string matterId = ReadString(payload, "matterId", "");
            string postingId = ReadString(payload, "postingId", "");
            Dictionary<string, object> candidate = ReadDictionary(payload, "candidate");
            if (string.IsNullOrWhiteSpace(matterId) || candidate == null || string.IsNullOrWhiteSpace(postingId))
                return CourtError("The case, posting, and exact proposed terms are required.");
            string command = ReadString(candidate, "command", "");
            if (!DiplomacyService.DirectorSupportedCommands.Contains(command)) return CourtError("The requested diplomatic action is unsupported.");
            string actionId = CanonicalAmbassadorActionId(command);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                Dictionary<string, object> posting = CourtStore.SelectForeignAmbassadorPostings10(connection, new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId, ["posting"] = postingId }).FirstOrDefault();
                string host = ReadString(posting, "host_kingdom_id", "");
                string foreign = ReadString(posting, "origin_kingdom_id", "");
                if (posting == null || !host.Equals(ReadString(payload, "playerKingdomStringId", ""), StringComparison.OrdinalIgnoreCase))
                    return CourtError("The represented ambassador is no longer available at this court.");
                string actor = ReadFirstString(candidate, "actorKingdomId", "actorKingdomStringId");
                string target = ReadFirstString(candidate, "targetKingdomId", "targetKingdomStringId");
                if (!((actor == host && target == foreign) || (actor == foreign && target == host)))
                    return CourtError("The referred terms must involve exactly the host and represented kingdoms.");

                EnsureInternationalCourtLifeSchema(connection);
                Dictionary<string, object> unavailable = FindUnavailableInternationalCourtReferral(
                    connection, ctx.CampaignId, ctx.TimelineId, postingId, actionId, candidate);
                if (unavailable != null) return UnavailableInternationalCourtReferralResult(unavailable);
            }
            string hash = CourtHash(CourtCanonicalJson(candidate));
            Dictionary<string, object> referral = CreateAmbassadorReferral(ctx.CampaignId, ctx.TimelineId, postingId,
                actionId, candidate, hash, ctx.WorldDay);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                CourtStore.InsertOrIgnoreInternationalCourtReferrals(connection, new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId,
                        ["matter"] = matterId, ["referral"] = ReadString(referral, "referralId", "") });
            }
            referral["ok"] = true;
            return referral;
        }

        private static string InternationalCourtReferralMaterial(Dictionary<string, object> candidate)
        {
            // D51 suppresses repeats on the executable proposal. The frozen original
            // remains intact for typed replies, including its Court and director context.
            var material = new Dictionary<string, object>(candidate);
            foreach (string key in new[]
            {
                "courtMatterId", "courtOptionId", "courtRulerId",
                "correlationId", "publicReason", "reason", "decisionFactors", "confidence",
                "initiativeDiagnostics", "intentFamily", "requiredPolarity",
                "relationshipOpportunityId", "relationshipThreshold",
                "politicalPressureId", "politicalPressureValue", "politicalPressureChannel",
                "actorEffectiveAttitude", "targetEffectiveAttitude"
            })
                material.Remove(key);
            return CourtCanonicalJson(material);
        }

        private static Dictionary<string, object> FindUnavailableInternationalCourtReferral(ReignDbConnection connection,
            string campaignId, string timelineId, string postingId, string actionId, Dictionary<string, object> candidate)
        {
            string material = InternationalCourtReferralMaterial(candidate);
            var scope = TestDict("campaign", campaignId, "timeline", timelineId, "posting", postingId, "action", actionId);
            return CourtStore.SelectUnavailableInternationalCourtReferrals(connection, scope).FirstOrDefault(row =>
                ReadBool(ParseAmbassadorJsonObject(ReadString(row, "authoritative_receipt_json", "{}")), "technicalFailure", false)
                && InternationalCourtReferralMaterial(ParseAmbassadorJsonObject(ReadString(row, "exact_terms_json", "{}"))) == material);
        }

        private static Dictionary<string, object> UnavailableInternationalCourtReferralResult(Dictionary<string, object> row)
            => TestDict("ok", true, "referralId", ReadString(row, "referral_id", ""), "termsHash", ReadString(row, "terms_hash", ""),
                "dueDay", ReadDouble(row, "due_day", 0d), "status", "model_output_unusable", "outcome", "format_failure", "technicalFailure", true);

        internal static Dictionary<string, object> InternationalCourtResolutionApi(Dictionary<string, object> payload)
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
            if ((ClaimsDirectCourtPrisonerReceipt(ReadDictionary(payload, "nativeReceipt")) || ReadInt(payload, "continuityVersion", 0) > 0)
                && NormalizeCourtLifeResolutionFacts(payload) == null)
                return CourtError("The native completion evidence is incomplete or inconsistent.");
            int severity = Math.Max(0, Math.Min(3, ReadInt(payload, "severityRank", 0)));
            bool favorable = outcome == "favor_foreign" || outcome == "compensate" || outcome == "compromise" || outcome == "accept";
            int delta = favorable ? -ReignInternationalDocketRules.PressureMagnitude((ReignNobleMatterSeverity)severity)
                : ReignInternationalDocketRules.PressureMagnitude((ReignNobleMatterSeverity)severity);
            bool incidentAlreadyApplied = false;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                Dictionary<string, object> queued = CourtStore.SelectInternationalCourtQueue2(connection, new Dictionary<string, object>
                    { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId, ["matter"] = matterId }).FirstOrDefault();
                incidentAlreadyApplied = ReadBool(TryParseJsonObject(ReadString(queued, "payload_json", "{}")), "pressureAlreadyApplied", false);
            }
            int after = incidentAlreadyApplied ? DiplomacyService.ReadPoliticalPressureValue(ctx.CampaignId, ctx.TimelineId,
                ReadString(foreign, "kingdomId", ""), ReadString(player, "kingdomId", ""))
                : DiplomacyService.ApplyPoliticalPressureDelta(ctx.CampaignId, ctx.TimelineId, ctx.WorldDay, foreign, player,
                    delta, ReadString(payload, "channel", "treaty_strain"), "court-resolution:" + matterId);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureInternationalCourtLifeSchema(connection);
                CourtStore.UpdateInternationalCourtQueue(connection, new Dictionary<string, object> { ["campaign"] = ctx.CampaignId, ["timeline"] = ctx.TimelineId, ["matter"] = matterId });
            }
            StoreCourtLifeResolutionContinuity(ctx.CampaignId, ctx.TimelineId, payload);
            return new Dictionary<string, object> { ["ok"] = true, ["matterId"] = matterId,
                ["receiptId"] = receiptId, ["pressureAfter"] = after, ["requestedDelta"] = incidentAlreadyApplied ? 0 : delta };
        }
    }
}
