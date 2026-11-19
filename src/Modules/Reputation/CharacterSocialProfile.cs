using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class ReputationService
    {
        private static void EnsureCharacterSocialProfileSchema(ReignDbConnection connection)
        {
            ReputationSchema.CreateReputationActivations(connection);
            ReputationSchema.CreateIndexReputationActivationsSubject(connection);
            ReputationSchema.CreateReputationReasonJobs(connection);
            ReputationSchema.CreateIndexReputationReasonJobsStatus(connection);
            ReputationSchema.EnsureColumnCharacterReputationsReasonActivationId(connection);
            ReputationSchema.EnsureColumnCharacterReputationsReasonText(connection);
            ReputationSchema.EnsureColumnCharacterReputationsReasonStatus(connection);
            string recordedMigration = ReadString(ReputationStore.SelectSchemaMeta(connection)
                .FirstOrDefault(), "value", "");
            if (recordedMigration != "1")
            {
                NormalizeRecordedReputationReasons(connection);
                ReputationStore.UpsertSchemaMeta(connection);
            }
        }

        private static string RecordReputationActivation(ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId, string tagId, string sourceOccurrenceId, string sourceEventId, double worldDay,
            string description, string incidentDescription, Dictionary<string, object> evidence)
        {
            EnsureCharacterSocialProfileSchema(connection);
            string fallback = FirstNonEmpty(incidentDescription, description, "This reputation became established through recorded events.");
            string activationId = "reputation_activation_" + Guid.NewGuid().ToString("N");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ReputationStore.InsertReputationActivations(connection, new Dictionary<string, object>
                {
                    ["activation"] = activationId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId, ["tag"] = tagId, ["occurrence"] = sourceOccurrenceId ?? "",
                    ["event"] = sourceEventId ?? "", ["day"] = worldDay, ["description"] = description ?? "",
                    ["incident"] = fallback, ["evidence"] = Json.Serialize(evidence ?? new Dictionary<string, object>()),
                    ["fallback"] = fallback, ["ts"] = ts
                });
            ReputationStore.UpdateCharacterReputations(connection, new Dictionary<string, object>
                {
                    ["activation"] = activationId, ["fallback"] = fallback, ["ts"] = ts,
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId
                });
            return activationId;
        }

        private static string SocialOccurrenceIncident(ReignDbConnection connection, string occurrenceId, string fallback)
        {
            if (string.IsNullOrWhiteSpace(occurrenceId)) return fallback ?? "";
            Dictionary<string, object> row = ReputationStore.SelectRumorOccurrences(connection, new Dictionary<string, object> { ["id"] = occurrenceId }).FirstOrDefault();
            return FirstNonEmpty(ReadString(row, "provenance_summary", ""), fallback);
        }

        internal static void SchedulePendingReputationReasonJobs(string campaignId)
        {
            // Reputation reasons are authoritative producer evidence. Richer
            // phrasing belongs to the interaction prompt, never a background
            // LLM write that can delay or alter social state.
        }

        private static void NormalizeRecordedReputationReasons(ReignDbConnection connection)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ReputationStore.UpdateReputationActivations(connection, new Dictionary<string, object> { ["ts"] = ts });
            ReputationStore.UpdateCharacterReputations2(connection, new Dictionary<string, object> { ["ts"] = ts });
            ReputationStore.UpdateReputationReasonJobs(connection, new Dictionary<string, object> { ["ts"] = ts });
        }

        // Startup-only reconciliation with no dedicated retry. Program submits it immediately after
        // the scheduler starts, ahead of every other producer and memory fan-out, but admission is
        // still not guaranteed (busy, stopping or canceled before start skips it); the result
        // (queued, completed, yielded, failed or skipped_<reason>) is exposed through
        // /api/background/status as reputationReasonResumeOutcome.
        internal static string ReputationReasonResumeOutcome = "";

        internal static void ResumePendingReputationReasonJobs()
        {
            System.Threading.Volatile.Write(ref ReputationReasonResumeOutcome, "queued");
            Program.TryEnqueuePriorityBackgroundWork("reputation.reason.resume", "maintenance", () =>
            {
                string outcome = "failed";
                try { outcome = ReconcileRecordedReputationReasonsAtStartup(); }
                finally { System.Threading.Volatile.Write(ref ReputationReasonResumeOutcome, outcome); }
            }, reason => System.Threading.Volatile.Write(ref ReputationReasonResumeOutcome, "skipped_" + reason), out _);
        }

        private static string ReconcileRecordedReputationReasonsAtStartup()
        {
            string root = Program.CampaignsRoot();
            if (!Directory.Exists(root)) return "completed";
            foreach (string directory in Directory.GetDirectories(root))
            {
                if (Program.PriorityBackgroundWorkShouldYield()) return "yielded";
                string campaignId = Path.GetFileName(directory);
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    NormalizeRecordedReputationReasons(connection);
                }
            }
            return "completed";
        }

        internal static Dictionary<string, object> CharacterSocialStandingProjection(
            ReignDbConnection connection, string campaignId, string subjectId)
        {
            EnsureSocialReputationSchema(connection);
            string timelineId = ResolveCharacterSocialTimeline(connection);
            double worldDay = ResolveCharacterSocialWorldDay(connection, campaignId, timelineId);
            if (worldDay > 0d) ExpireSocialRumors(connection, campaignId, timelineId, worldDay);
            bool popularityChanged = RecomputeCourtPopularityReputations(connection, campaignId, timelineId,
                subjectId, worldDay, "character_profile|" + subjectId + "|"
                    + Math.Floor(worldDay).ToString(CultureInfo.InvariantCulture));
            if (popularityChanged)
            {
                ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                if (!campaignId.StartsWith("__", StringComparison.Ordinal))
                    SchedulePendingReputationReasonJobs(campaignId);
            }
            Dictionary<string, object> catalog = ReadActiveSocialCatalog(connection);
            Dictionary<string, Dictionary<string, object>> tags = ReadDictionaryList(catalog, "tags")
                .Where(tag => !string.IsNullOrWhiteSpace(ReadString(tag, "id", "")))
                .ToDictionary(tag => ReadString(tag, "id", ""), tag => tag, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> rumors = ReputationStore.SelectRumorSubjectTags(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["day"] = worldDay });
            foreach (Dictionary<string, object> row in rumors)
                EnrichSocialProfileRow(row, tags, false);
            List<Dictionary<string, object>> reputations = ReputationStore.SelectCharacterReputations(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId });
            foreach (Dictionary<string, object> row in reputations)
                EnrichSocialProfileRow(row, tags, true);
            Dictionary<string, object> publicStanding = ReadPublicStandingTrait(
                connection, campaignId, timelineId, subjectId, worldDay);
            return new Dictionary<string, object>
            {
                ["timelineId"] = timelineId, ["worldDay"] = worldDay,
                ["activeRumors"] = rumors, ["activeReputations"] = reputations,
                ["publicStanding"] = publicStanding,
                ["catalogChoices"] = BuildCharacterSocialCatalogChoices(catalog)
            };
        }

        private static void EnrichSocialProfileRow(Dictionary<string, object> row,
            Dictionary<string, Dictionary<string, object>> tags, bool reputation)
        {
            string tagId = ReadString(row, "tag_id", "");
            Dictionary<string, object> definition;
            if (!tags.TryGetValue(tagId, out definition))
                definition = TryParseJsonObject(ReadString(row, "snapshot_json", "{}")) ?? new Dictionary<string, object>();
            int value = reputation ? ReadInt(row, "reputation_value", 0) : ReadInt(row, "rumor_value", 0);
            row["label"] = ReadString(definition, "label", tagId);
            row["classification"] = value > 0 ? "positive" : value < 0 ? "negative" : "neutral";
            if (reputation) row["activationKey"] = ReadString(row, "reason_activation_id", "");
            row["incidentDescription"] = reputation
                ? FirstNonEmpty(ReadString(row, "reason_text", ""), ReadString(row, "description", ""))
                : FirstNonEmpty(ReadString(row, "provenance_summary", ""), ReadString(row, "description", ""));
            row.Remove("tag_id");
            row.Remove("rumor_value");
            row.Remove("reputation_value");
            row.Remove("snapshot_json");
            row.Remove("occurrence_snapshot_json");
            row.Remove("reason_activation_id");
            row.Remove("occurrence_id");
            row.Remove("source_event_id");
        }

        private static Dictionary<string, object> BuildCharacterSocialCatalogChoices(Dictionary<string, object> catalog)
        {
            List<Dictionary<string, object>> tags = ReadDictionaryList(catalog, "tags");
            Dictionary<string, Dictionary<string, object>> byId = tags
                .Where(tag => !string.IsNullOrWhiteSpace(ReadString(tag, "id", "")))
                .ToDictionary(tag => ReadString(tag, "id", ""), tag => tag, StringComparer.OrdinalIgnoreCase);
            List<object> rumorChoices = new List<object>();
            foreach (Dictionary<string, object> archetype in ReadDictionaryList(catalog, "archetypes")
                .Where(item => ReadBool(item, "enabled", true) && !ReadBool(item, "directReputation", false)))
            {
                foreach (KeyValuePair<string, object> role in Program.DictionaryOrDefault(archetype, "roleMappings", new Dictionary<string, object>()))
                {
                    List<Dictionary<string, object>> mapped = ValueTextList(role.Value)
                        .Where(byId.ContainsKey).Select(id => byId[id])
                        .Where(tag => ReadBool(tag, "enabled", true) && !ReadBool(tag, "derived", false)
                            && ReadBool(tag, "manualEligible", true)).ToList();
                    if (mapped.Count == 0) continue;
                    rumorChoices.Add(new Dictionary<string, object>
                    {
                        ["archetypeId"] = ReadString(archetype, "id", ""), ["role"] = role.Key,
                        ["label"] = string.Join(" / ", mapped.Select(tag => ReadString(tag, "label", ReadString(tag, "id", "")))),
                        ["description"] = ReadString(archetype, "description", ""),
                        ["allowPlayerSubject"] = ReadBool(archetype, "allowPlayerSubject", false)
                    });
                }
            }
            List<object> reputationChoices = tags
                .Where(tag => ReadBool(tag, "enabled", true) && !ReadBool(tag, "derived", false)
                    && ReadBool(tag, "manualEligible", true)
                    && ReadInt(tag, "reputationValue", 0) != 0)
                .OrderBy(tag => ReadString(tag, "label", ""))
                .Select(tag => (object)new Dictionary<string, object>
                {
                    ["tagId"] = ReadString(tag, "id", ""), ["label"] = ReadString(tag, "label", ""),
                    ["description"] = ReadString(tag, "description", ""),
                    ["classification"] = ReadInt(tag, "reputationValue", 0) > 0 ? "positive" : "negative",
                    ["allowPlayerSubject"] = ReadBool(tag, "allowPlayerSubject", true)
                }).ToList();
            return new Dictionary<string, object> { ["rumors"] = rumorChoices, ["reputations"] = reputationChoices };
        }

        private static string ResolveCharacterSocialTimeline(ReignDbConnection connection)
        {
            if (ReputationStore.SelectSqliteMaster(connection).Any())
            {
                string timeline = ReadString(ReputationStore.SelectWorldTestNativeHeartbeats(connection).FirstOrDefault(), "timeline_id", "");
                if (!string.IsNullOrWhiteSpace(timeline)) return timeline;
            }
            string rumorTimeline = ReadString(ReputationStore.SelectRumorOccurrences2(connection).FirstOrDefault(), "timeline_id", "");
            return FirstNonEmpty(rumorTimeline, "main");
        }

        private static double ResolveCharacterSocialWorldDay(ReignDbConnection connection, string campaignId, string timelineId)
        {
            double day = WorldSimulationService.LatestKnownWorldDay(connection, campaignId);
            if (ReputationStore.SelectSqliteMaster(connection).Any())
                day = Math.Max(day, ReadDouble(ReputationStore.SelectWorldTestNativeHeartbeats2(connection, new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault(), "day", 0d));
            return day;
        }

        internal static Dictionary<string, object> CharacterEditorSocialRumorAddApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = FirstNonEmpty(ReadString(payload, "campaignId", ""), Program.LatestCampaignId());
            string subjectId = ReadFirstString(payload, "heroStringId", "heroId", "subjectId");
            string archetypeId = ReadString(payload, "archetypeId", "");
            string role = ReadString(payload, "role", "");
            string incident = (ReadString(payload, "incidentDescription", "") ?? "").Trim();
            string idempotencyKey = ReadString(payload, "idempotencyKey", "");
            if (string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(archetypeId) || string.IsNullOrWhiteSpace(role))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Character, Rumor, and role are required." };
            if (incident.Length < 10 || incident.Length > 500)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Incident description must be between 10 and 500 characters." };
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "idempotencyKey is required." };
            Dictionary<string, object> result;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                string timelineId = FirstNonEmpty(ReadString(payload, "timelineId", ""), ResolveCharacterSocialTimeline(connection));
                double worldDay = ResolveCharacterSocialWorldDay(connection, campaignId, timelineId);
                if (worldDay <= 0d) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Current campaign day is unavailable. Load the campaign before adding social standing." };
                Dictionary<string, object> participant = CharacterSocialParticipant(connection, campaignId, subjectId, role);
                if (participant == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Only living adult characters can receive active Rumors." };
                result = RegisterSocialOccurrence(connection, campaignId, new Dictionary<string, object>
                {
                    ["timelineId"] = timelineId, ["archetypeId"] = archetypeId,
                    ["threadKey"] = subjectId + "|" + archetypeId,
                    ["sourceEventId"] = "control_center_rumor_" + DeterministicSocialId(idempotencyKey),
                    ["worldDay"] = worldDay, ["forceExposure"] = true,
                    ["provenanceSummary"] = incident, ["participants"] = new List<object> { participant }
                });
                result["socialStanding"] = CharacterSocialStandingProjection(connection, campaignId, subjectId);
            }
            if (!campaignId.StartsWith("__", StringComparison.Ordinal))
                SchedulePendingReputationReasonJobs(campaignId);
            return result;
        }

        internal static Dictionary<string, object> CharacterEditorSocialReputationAddApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = FirstNonEmpty(ReadString(payload, "campaignId", ""), Program.LatestCampaignId());
            string subjectId = ReadFirstString(payload, "heroStringId", "heroId", "subjectId");
            string tagId = ReadString(payload, "tagId", "");
            string incident = (ReadString(payload, "incidentDescription", "") ?? "").Trim();
            string idempotencyKey = ReadString(payload, "idempotencyKey", "");
            if (string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(tagId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Character and Reputation are required." };
            if (incident.Length < 10 || incident.Length > 500)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Incident description must be between 10 and 500 characters." };
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "idempotencyKey is required." };
            Dictionary<string, object> response;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                string timelineId = FirstNonEmpty(ReadString(payload, "timelineId", ""), ResolveCharacterSocialTimeline(connection));
                double worldDay = ResolveCharacterSocialWorldDay(connection, campaignId, timelineId);
                if (worldDay <= 0d) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Current campaign day is unavailable. Load the campaign before adding social standing." };
                Dictionary<string, object> participant = CharacterSocialParticipant(connection, campaignId, subjectId, "manual");
                if (participant == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Only living adult characters can receive active Reputations." };
                Dictionary<string, object> catalog = ReadActiveSocialCatalog(connection);
                Dictionary<string, object> tag = ReadDictionaryList(catalog, "tags").FirstOrDefault(item =>
                    string.Equals(ReadString(item, "id", ""), tagId, StringComparison.OrdinalIgnoreCase));
                if (tag == null || !ReadBool(tag, "enabled", true) || ReadBool(tag, "derived", false)
                    || !ReadBool(tag, "manualEligible", true))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "That Reputation is unavailable for manual addition." };
                string sourceEventId = "control_center_reputation_" + DeterministicSocialId(idempotencyKey);
                Dictionary<string, object> duplicateActivation = ReputationStore.SelectReputationActivations(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["event"] = sourceEventId }).FirstOrDefault();
                if (duplicateActivation != null)
                    return new Dictionary<string, object> { ["ok"] = true, ["duplicate"] = true, ["socialStanding"] = CharacterSocialStandingProjection(connection, campaignId, subjectId) };
                if (ReputationStore.SelectCharacterReputations2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId }).Any())
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "This character already has that active Reputation." };
                Dictionary<string, object> snapshot = SocialTagSnapshot(tag, participant);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int revision = ReadInt(catalog, "revision", BuiltInSocialCatalogRevision);
                ReputationStore.UpsertCharacterReputations(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId,
                        ["description"] = ReadString(snapshot, "description", ""), ["value"] = ReadInt(snapshot, "reputationValue", 0),
                        ["day"] = worldDay, ["revision"] = revision, ["snapshot"] = Json.Serialize(snapshot), ["ts"] = ts
                    });
                string activationId = RecordReputationActivation(connection, campaignId, timelineId, subjectId, tagId, "",
                    sourceEventId, worldDay, ReadString(snapshot, "description", ""), incident,
                    new Dictionary<string, object> { ["source"] = "manual_control_center", ["incidentDescription"] = incident });
                RecomputeDerivedSocialReputations(connection, campaignId, timelineId, subjectId, worldDay, sourceEventId);
                ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                response = new Dictionary<string, object>
                {
                    ["ok"] = true, ["activationId"] = activationId,
                    ["socialStanding"] = CharacterSocialStandingProjection(connection, campaignId, subjectId)
                };
            }
            if (!campaignId.StartsWith("__", StringComparison.Ordinal))
                SchedulePendingReputationReasonJobs(campaignId);
            return response;
        }

        internal static Dictionary<string, object> CharacterEditorSocialReasonRetryApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = FirstNonEmpty(ReadString(payload, "campaignId", ""), Program.LatestCampaignId());
            string activationId = ReadString(payload, "activationId", "");
            if (string.IsNullOrWhiteSpace(activationId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "activationId is required." };
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> activation = ReputationStore.SelectReputationActivations2(connection, new Dictionary<string, object> { ["activation"] = activationId }).FirstOrDefault();
                if (activation == null)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The recorded reputation event was not found." };
                string reason = FirstNonEmpty(ReadString(activation, "incident_description", ""),
                    ReadString(activation, "description", ""), "A recorded event established this reputation.");
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ReputationStore.UpdateReputationActivations2(connection, new Dictionary<string, object> { ["activation"] = activationId, ["reason"] = reason, ["ts"] = ts });
                ReputationStore.UpdateCharacterReputations3(connection, new Dictionary<string, object> { ["activation"] = activationId, ["reason"] = reason, ["ts"] = ts });
                ReputationStore.UpdateReputationReasonJobs2(connection, new Dictionary<string, object> { ["activation"] = activationId, ["ts"] = ts });
                return new Dictionary<string, object> { ["ok"] = true, ["recorded"] = true, ["reason"] = reason };
            }
        }

        private static Dictionary<string, object> CharacterSocialParticipant(
            ReignDbConnection connection, string campaignId, string subjectId, string role)
        {
            Dictionary<string, object> roster = ReputationStore.SelectIdentityRoster(connection, new Dictionary<string, object> { ["id"] = subjectId }).FirstOrDefault();
            Dictionary<string, object> profile = Program.ReadJsonObject(Program.CharacterFile(campaignId, subjectId, "profile.json"));
            bool alive = roster != null ? ReadInt(roster, "is_alive", 1) != 0 : ReadBool(profile, "isAlive", true);
            bool adult = roster != null ? ReadInt(roster, "is_adult", 1) != 0 : !ReadBool(profile, "isChild", false);
            if (!alive || !adult) return null;
            return new Dictionary<string, object>
            {
                ["subjectId"] = subjectId, ["role"] = role, ["isAlive"] = true, ["isAdult"] = true,
                ["isPlayer"] = roster != null ? ReadInt(roster, "is_player", 0) != 0 : ReadBool(profile, "isPlayer", false),
                ["clanTier"] = roster != null ? ReadInt(roster, "clan_tier", 0) : ReadInt(profile, "clanTier", 0),
                ["sex"] = roster != null ? ReadString(roster, "sex", "") : (ReadBool(profile, "isFemale", false) ? "female" : "male")
            };
        }
    }
}
