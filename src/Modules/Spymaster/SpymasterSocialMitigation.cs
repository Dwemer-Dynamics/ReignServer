using System;
using System.Collections.Generic;
using System.Linq;
using ReignShared.Spymaster;

namespace ReignBetaServer
{
    internal static partial class SpymasterService
    {
        private const string MitigationReceiptSchema = "reign-spymaster-mitigation-v1";

        private static string SocialMissionString(Dictionary<string, object> mission, string key)
            => ReadString(mission, key, ReadString(mission, char.ToLowerInvariant(key[0]) + key.Substring(1), ""));

        private static double SocialMissionNumber(Dictionary<string, object> mission, string key, double fallback)
            => ReadDouble(mission, key, ReadDouble(mission, char.ToLowerInvariant(key[0]) + key.Substring(1), fallback));

        private static Dictionary<string, object> MitigationTerms(Dictionary<string, object> payload,
            Dictionary<string, object> mission, string type)
        {
            if (type != "mitigate_own_rumor" && type != "mitigate_target_rumor"
                && type != "mitigate_own_reputation" && type != "mitigate_target_reputation")
                return null;

            string campaign = ReadString(payload, "campaignId", "");
            string timeline = ReadString(payload, "timelineId", "");
            string missionId = SocialMissionString(mission, "MissionId");
            string subject = SocialMissionString(mission, "TargetStringId");
            string tag = SocialMissionString(mission, "SocialItemId");
            double resolved = SocialMissionNumber(mission, "ResolvedDay", -1d);
            if (string.IsNullOrWhiteSpace(campaign) || string.IsNullOrWhiteSpace(timeline)
                || string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(subject)
                || string.IsNullOrWhiteSpace(tag) || !double.IsFinite(resolved) || resolved < 0)
                return null;

            string terms = CourtService.CourtHash(Json.Serialize(new object[]
            {
                MitigationReceiptSchema, campaign, timeline, missionId, type, subject, tag, resolved,
                SocialMissionString(mission, "SpymasterHeroStringId"), SocialMissionString(mission, "TargetType"),
                SocialMissionString(mission, "SponsorKingdomStringId"), SocialMissionNumber(mission, "StartedDay", 0d),
                SocialMissionNumber(mission, "DueDay", 0d), SocialMissionNumber(mission, "GoldCost", 0d),
                SocialMissionNumber(mission, "SuccessChance", 0d), SocialMissionNumber(mission, "OutcomeRoll", -1d)
            }));
            return new Dictionary<string, object>
            {
                ["campaign"] = campaign, ["timeline"] = timeline, ["mission"] = missionId,
                ["subject"] = subject, ["tag"] = tag, ["day"] = resolved, ["terms"] = terms
            };
        }

        private static Dictionary<string, object> MitigationError(string error)
            => new Dictionary<string, object> { ["ok"] = false, ["schema"] = MitigationReceiptSchema, ["error"] = error };

        private static Dictionary<string, object> ReadMitigationReceipt(ReignDbConnection connection,
            Dictionary<string, object> terms, ReignDbTransaction transaction = null)
        {
            var row = SpymasterStore.SelectMitigationReceipt(connection, terms, transaction).FirstOrDefault();
            if (row == null) return null;
            if (ReadString(row, "terms_hash", "") != (string)terms["terms"])
                return MitigationError("mitigation_terms_conflict");
            return TryParseJsonObject(ReadString(row, "response_json", ""))
                ?? throw new InvalidOperationException("The saved Spymaster mitigation receipt could not be read.");
        }

        private static Dictionary<string, object> SpymasterMitigationReceiptApi(Dictionary<string, object> payload)
        {
            var mission = ReadDictionary(payload, "mission");
            var terms = MitigationTerms(payload, mission, SocialMissionString(mission, "MissionType"));
            if (terms == null) return MitigationError("invalid_mitigation_terms");
            using var connection = ReignDatabase.OpenCampaignConnection((string)terms["campaign"]);
            return (SpymasterStore.MitigationReceiptTableExists(connection) ? ReadMitigationReceipt(connection, terms) : null)
                ?? MitigationError("mitigation_receipt_missing");
        }

        private static Dictionary<string, object> ApplySpymasterMitigation(Dictionary<string, object> payload,
            Dictionary<string, object> mission, string type)
        {
            var terms = MitigationTerms(payload, mission, type);
            if (terms == null) return MitigationError("invalid_mitigation_terms");
            string campaign = (string)terms["campaign"];
            string timeline = (string)terms["timeline"];
            string missionId = (string)terms["mission"];
            string subject = (string)terms["subject"];
            double day = (double)terms["day"];

            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            if (SpymasterStore.MitigationReceiptTableExists(connection))
            {
                var replay = ReadMitigationReceipt(connection, terms);
                if (replay != null) return replay;
            }
            else SpymasterSchema.CreateMitigationReceipts(connection);
            ReputationService.EnsureSocialReputationSchema(connection);
            RelationshipsService.EnsureWorldRelationshipSchema(connection);
            WorldSimulationService.EnsureWorldHistorySchema(connection);

            using var transaction = connection.BeginTransaction();
            // Serialize first admission and projection with other transactional campaign writers.
            AcquirePostgreSqlTransactionMutationLock(connection, transaction);
            var existing = ReadMitigationReceipt(connection, terms, transaction);
            if (existing != null) return existing;

            // Older clients did not send this transport fence. New clients bind
            // first admission to the world-history generation loaded at dispatch.
            if (payload.ContainsKey("restoreGeneration") && ReadString(payload, "restoreGeneration", "")
                != ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", ""))
                return MitigationError("mitigation_generation_changed");

            // A closed branch can still have active rumor rows and a previously valid court session.
            // Lock the registered timeline across first apply so a branch switch cannot overtake it.
            var timelines = SpymasterStore.LockMitigationTimelines(connection, terms, transaction);
            if (timelines.Count > 0 && (timelines.Count != 1 ||
                ReadString(timelines[0], "timeline_id", "") != timeline || ReadInt(timelines[0], "is_active", 0) != 1))
                return MitigationError("mitigation_inactive_timeline");

            bool rumor = type.EndsWith("_rumor", StringComparison.Ordinal);
            double roll = RelationshipsService.StableUnit(string.Join("|", campaign, timeline, missionId, "social_apply"));
            string outcome = ReignSpymasterCore.SocialMitigationOutcome(type, roll);
            terms["remove"] = outcome == "disproven" || outcome == "removed";
            terms["factor"] = ReignSpymasterCore.SocialMitigationFactor;
            terms["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var matched = rumor
                ? SpymasterStore.MitigateRumorTags(connection, terms, transaction)
                : SpymasterStore.MitigateReputationTag(connection, terms, transaction);
            int changed = matched.Count(row => ReadBool(row, "changed", false));
            string sourceKey = rumor ? "occurrence_id" : "source_occurrence_id";
            string[] occurrences = matched.Select(row => ReadString(row, sourceKey, ""))
                .Where(id => !string.IsNullOrEmpty(id)).ToArray();
            string[] changedOccurrences = matched.Where(row => ReadBool(row, "changed", false))
                .Select(row => ReadString(row, sourceKey, "")).Where(id => !string.IsNullOrEmpty(id)).ToArray();

            // These helpers enlist on this connection. The projection itself checks rumor expiry
            // at the resolution day. Lifecycle maintenance of unrelated rumors stays on its own path.
            if (changed > 0)
                ReputationService.RecomputePublicStandingForSubjects(connection, campaign, timeline, new[] { subject }, day);

            var result = new Dictionary<string, object>
            {
                ["ok"] = true, ["schema"] = MitigationReceiptSchema, ["campaignId"] = campaign,
                ["timelineId"] = timeline, ["missionId"] = missionId, ["missionType"] = type,
                ["subjectId"] = subject, ["tagId"] = terms["tag"], ["resolvedDay"] = day,
                ["disposition"] = matched.Count == 0 ? "no_match" : changed == 0 ? "no_change" : "applied",
                ["outcome"] = matched.Count > 0 ? outcome : "no_eligible_record", ["roll"] = roll,
                ["matchedCount"] = matched.Count, ["changedCount"] = changed,
                ["matchedOccurrenceIds"] = occurrences, ["changedOccurrenceIds"] = changedOccurrences,
                ["scopeHash"] = CourtService.CourtHash(Json.Serialize(new object[] { terms["terms"], matched }))
            };
            terms["response"] = Json.Serialize(result);
            SpymasterStore.InsertMitigationReceipt(connection, terms, transaction);
            transaction.Commit();
            return result;
        }
    }
}
