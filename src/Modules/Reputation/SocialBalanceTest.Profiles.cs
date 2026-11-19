using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class ReputationService
    {
        private static void RecordUnchasteProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> child = ReadDictionary(result, "child")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> femaleParent = ReadDictionary(result, "femaleParent")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> maleParent = ReadDictionary(result, "maleParent")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> femaleProducer = ReadDictionary(femaleParent, "producerEvidence")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> maleProducer = ReadDictionary(maleParent, "producerEvidence")
                ?? new Dictionary<string, object>();
            string femaleId = ReadString(femaleParent, "heroId", "");
            string maleId = ReadString(maleParent, "heroId", "");
            string childId = ReadString(child, "childId", "");
            string femaleSource = ReadString(femaleProducer, "sourceKey", "");
            string maleSource = ReadString(maleProducer, "sourceKey", "");

            List<Dictionary<string, object>> sourceEvents = new List<Dictionary<string, object>>();
            List<string> sourceEventIds = new List<string>();
            List<Dictionary<string, object>> sourceReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> occurrences = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> subjectTags = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> reputations = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(campaignId)
                && !string.IsNullOrWhiteSpace(femaleSource)
                && !string.IsNullOrWhiteSpace(maleSource))
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    sourceEvents = ReignDatabase.QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND correlation_id IN ($female,$male)
ORDER BY sequence DESC;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["female"] = femaleSource, ["male"] = maleSource
                    })
                        .GroupBy(row => ReadString(row, "correlation_id", ""),
                            StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First()).ToList();
                    sourceEventIds = sourceEvents
                        .Select(row => ReadString(row, "event_id", ""))
                        .Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
                    if (sourceEventIds.Count > 0)
                    {
                        DateTime receiptDeadline = DateTime.UtcNow.AddSeconds(30);
                        do
                        {
                            sourceReceipts = ReignDatabase.QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,
completed,attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id IN ($first,$second)
ORDER BY event_id;", new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["first"] = sourceEventIds[0],
                                ["second"] = sourceEventIds.Count > 1 ? sourceEventIds[1] : sourceEventIds[0]
                            });
                            if (sourceReceipts.Count == sourceEventIds.Count
                                && sourceReceipts.All(row => ReadInt(row, "completed", 0) != 0))
                                break;
                            if (DateTime.UtcNow >= receiptDeadline) break;
                            Thread.Sleep(100);
                        }
                        while (true);
                        occurrences = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='the_unchaste' AND source_event_id IN ($first,$second)
ORDER BY source_event_id;", new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["first"] = sourceEventIds[0],
                            ["second"] = sourceEventIds.Count > 1 ? sourceEventIds[1] : sourceEventIds[0]
                        });
                    }
                    List<string> occurrenceIds = occurrences
                        .Select(row => ReadString(row, "occurrence_id", ""))
                        .Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
                    if (occurrenceIds.Count > 0)
                    {
                        subjectTags = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE tag_id='the_unchaste' AND occurrence_id IN ($first,$second)
ORDER BY subject_id;", new Dictionary<string, object>
                        {
                            ["first"] = occurrenceIds[0],
                            ["second"] = occurrenceIds.Count > 1 ? occurrenceIds[1] : occurrenceIds[0]
                        });
                    }
                    reputations = ReignDatabase.QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND tag_id='the_unchaste' AND subject_id IN ($female,$male)
ORDER BY subject_id;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["female"] = femaleId, ["male"] = maleId
                    });
                }
            }

            bool publicParentage = ReadBool(child, "publiclyKnown", false)
                && ReadBool(child, "illegitimate", false)
                && ReadString(child, "motherId", "").Equals(femaleId, StringComparison.OrdinalIgnoreCase)
                && ReadString(child, "biologicalFatherId", "").Equals(maleId, StringComparison.OrdinalIgnoreCase)
                && ReadStringList(femaleProducer, "childIds").Contains(childId, StringComparer.OrdinalIgnoreCase)
                && ReadStringList(maleProducer, "childIds").Contains(childId, StringComparer.OrdinalIgnoreCase);
            bool ordinaryAdultParents = ReadBool(femaleParent, "isFemale", false)
                && !ReadBool(maleParent, "isFemale", true)
                && ReadBool(femaleParent, "isRegularLord", false)
                && ReadBool(maleParent, "isRegularLord", false)
                && !string.IsNullOrWhiteSpace(femaleId) && !string.IsNullOrWhiteSpace(maleId)
                && !femaleId.Equals(maleId, StringComparison.OrdinalIgnoreCase);
            bool productionProducer = ReadBool(result, "productionProducerUsed", false)
                && ReadBool(result, "forcedRareBranch", false)
                && ReadBool(result, "worldHistoryDrained", false)
                && ReadBool(femaleProducer, "forcedRareBranch", false)
                && ReadBool(maleProducer, "forcedRareBranch", false);
            bool exactSourceEvents = sourceEvents.Count == 2
                && sourceEvents.Any(row => ReadString(row, "correlation_id", "")
                    .Equals(femaleSource, StringComparison.OrdinalIgnoreCase))
                && sourceEvents.Any(row => ReadString(row, "correlation_id", "")
                    .Equals(maleSource, StringComparison.OrdinalIgnoreCase));
            bool exactOccurrences = exactSourceEvents && occurrences.Count == 2
                && occurrences.All(row => ReadString(row, "status", "")
                    .Equals("active", StringComparison.OrdinalIgnoreCase))
                && sourceEventIds.Count == 2
                && sourceEventIds.All(eventId => occurrences.Any(row =>
                    ReadString(row, "source_event_id", "")
                        .Equals(eventId, StringComparison.OrdinalIgnoreCase)));
            bool exactRumorValues = subjectTags.Any(row =>
                    ReadString(row, "subject_id", "").Equals(femaleId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "rumor_value", int.MinValue) == 0
                    && ReadInt(row, "reputation_value", int.MinValue) == -50)
                && subjectTags.Any(row =>
                    ReadString(row, "subject_id", "").Equals(maleId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "rumor_value", int.MinValue) == 0
                    && ReadInt(row, "reputation_value", int.MinValue) == -5);
            bool exactEstablishedValues = reputations.Count == 2
                && reputations.Any(row =>
                    ReadString(row, "subject_id", "").Equals(femaleId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "reputation_value", int.MinValue) == -50
                    && ReadString(row, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase))
                && reputations.Any(row =>
                    ReadString(row, "subject_id", "").Equals(maleId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "reputation_value", int.MinValue) == -5
                    && ReadString(row, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase));
            bool passed = publicParentage && ordinaryAdultParents && productionProducer
                && exactOccurrences && exactRumorValues && exactEstablishedValues;

            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["child"] = child, ["femaleParent"] = femaleParent, ["maleParent"] = maleParent,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["publicIllegitimateChildLinkedToBothParents"] = publicParentage,
                    ["ordinaryAdultMaleAndFemaleLords"] = ordinaryAdultParents,
                    ["productionCourtPersonalityProducerUsed"] = productionProducer,
                    ["exactForcedOccurrencesPersisted"] = exactOccurrences,
                    ["zeroRumorAndGenderSpecificTagValues"] = exactRumorValues,
                    ["femaleMinus50MaleMinus5Established"] = exactEstablishedValues
                },
                ["sourceEvents"] = sourceEvents,
                ["sourceReceipts"] = sourceReceipts,
                ["occurrences"] = occurrences,
                ["subjectTags"] = subjectTags,
                ["reputations"] = reputations
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["runId"] = runId,
                ["caseId"] = "court_unchaste_publicity_and_gender",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "unchaste", ["caseId"] = "court_unchaste_publicity_and_gender",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordNpcFavoringPresenceProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            string sourceKey = ReadString(result, "sourceKey", "");
            string rulerId = ReadString(result, "rulerId", "");
            string favoriteId = ReadString(result, "favoriteId", "");
            int rulerClanTier = ReadInt(result, "rulerClanTier", 0);
            Dictionary<string, object> sourceEvent = new Dictionary<string, object>();
            Dictionary<string, object> sourceReceipt = new Dictionary<string, object>();
            Dictionary<string, object> occurrence = new Dictionary<string, object>();
            List<Dictionary<string, object>> subjectTags = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> reputations = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(campaignId)
                && !string.IsNullOrWhiteSpace(timelineId)
                && !string.IsNullOrWhiteSpace(sourceKey))
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    sourceEvent = ReignDatabase.QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND correlation_id=$source ORDER BY sequence DESC LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["source"] = sourceKey
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    string eventId = ReadString(sourceEvent, "event_id", "");
                    if (!string.IsNullOrWhiteSpace(eventId))
                    {
                        DateTime receiptDeadline = DateTime.UtcNow.AddSeconds(30);
                        do
                        {
                            sourceReceipt = ReignDatabase.QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,
completed,attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id=$event LIMIT 1;",
                                new Dictionary<string, object>
                                {
                                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                                    ["event"] = eventId
                                }).FirstOrDefault() ?? new Dictionary<string, object>();
                            if (ReadInt(sourceReceipt, "completed", 0) != 0) break;
                            if (DateTime.UtcNow >= receiptDeadline) break;
                            Thread.Sleep(100);
                        }
                        while (true);
                        occurrence = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='ruler_favoring_presence' AND source_event_id=$event LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["event"] = eventId
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    }
                    string occurrenceId = ReadString(occurrence, "occurrence_id", "");
                    if (!string.IsNullOrWhiteSpace(occurrenceId))
                    {
                        subjectTags = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE occurrence_id=$occurrence AND tag_id LIKE 'ruler_favoring:%';",
                            new Dictionary<string, object> { ["occurrence"] = occurrenceId });
                    }
                    reputations = ReignDatabase.QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$ruler AND status='active' AND tag_id LIKE 'ruler_favoring:%';",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["ruler"] = rulerId
                        });
                }
            }

            double expectedChance = Math.Min(1d, 0.05d
                * Math.Max(1, Math.Min(6, rulerClanTier)) * 0.5d);
            Dictionary<string, object> eventPayload = TryParseJsonObject(
                ReadString(sourceEvent, "payload_json", "{}")) ?? new Dictionary<string, object>();
            bool productionStateMachine = ReadBool(result, "productionStateMachineUsed", false)
                && ReadInt(result, "pairProcessorRevision", 0) == 2
                && ReadInt(result, "pairProcessorCallCount", 0) == 3
                && ReadBool(result, "forcedRareBranch", false)
                && ReadBool(result, "worldHistoryDrained", false);
            bool resetAndThreshold = ReadInt(result, "separationResetCount", -1) == 1
                && ReadInt(result, "dayTenCount", -1) == 10
                && ReadBool(result, "dayTenProducedNoEvent", false)
                && ReadInt(result, "dayElevenCount", -1) == 11
                && ReadBool(result, "dayElevenQueuedExactEvent", false);
            bool exactEventAndReceipt = !string.IsNullOrWhiteSpace(ReadString(sourceEvent, "event_id", ""))
                && ReadString(sourceEvent, "correlation_id", "")
                    .Equals(sourceKey, StringComparison.OrdinalIgnoreCase)
                && ReadBool(eventPayload, "forcedRareBranch", false)
                && ReadString(eventPayload, "socialBalanceTestRunId", "")
                    .Equals(runId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(sourceReceipt, "completed", 0) != 0
                && ReadString(sourceReceipt, "archetype_id", "")
                    .Equals("ruler_favoring_presence", StringComparison.OrdinalIgnoreCase);
            bool exactOccurrence = ReadString(occurrence, "source_event_id", "")
                    .Equals(ReadString(sourceEvent, "event_id", ""), StringComparison.OrdinalIgnoreCase)
                && ReadString(occurrence, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(ReadDouble(occurrence, "exposure_chance", -1d) - expectedChance) < 0.0000001d;
            bool parameterizedEvidence = subjectTags.Any(row =>
                    ReadString(row, "subject_id", "").Equals(rulerId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "rumor_value", 0) == -5
                    && ReadInt(row, "reputation_value", 0) == -10
                    && ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}"))
                            ?? new Dictionary<string, object>(), "linkedHeroId", "")
                        .Equals(favoriteId, StringComparison.OrdinalIgnoreCase))
                && reputations.Any(row =>
                    ReadString(row, "subject_id", "").Equals(rulerId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "reputation_value", 0) == -10
                    && ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}"))
                            ?? new Dictionary<string, object>(), "linkedHeroId", "")
                        .Equals(favoriteId, StringComparison.OrdinalIgnoreCase));
            bool passed = productionStateMachine && resetAndThreshold && exactEventAndReceipt
                && exactOccurrence && parameterizedEvidence;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["rulerId"] = rulerId, ["favoriteId"] = favoriteId,
                ["sourceKey"] = sourceKey, ["rulerClanTier"] = rulerClanTier,
                ["expectedExposureChance"] = expectedChance,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["productionStateMachineUsed"] = productionStateMachine,
                    ["separationResetAndDayElevenThreshold"] = resetAndThreshold,
                    ["exactEventReceiptCompleted"] = exactEventAndReceipt,
                    ["fivePercentBaseClanTierScaling"] = exactOccurrence,
                    ["parameterizedFavoringPersisted"] = parameterizedEvidence
                },
                ["nativeResult"] = result, ["sourceEvent"] = sourceEvent,
                ["sourceReceipt"] = sourceReceipt, ["occurrence"] = occurrence,
                ["subjectTags"] = subjectTags, ["reputations"] = reputations
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["runId"] = runId,
                ["caseId"] = "npc_ruler_favoring_presence",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "npc_favoring_presence", ["caseId"] = "npc_ruler_favoring_presence",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordPlayerFavoringDialogueProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            string playerId = ReadString(enrollment, "mainHeroId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> primaryTarget = ReadDictionary(result, "primaryTarget")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> controlTarget = ReadDictionary(result, "controlTarget")
                ?? new Dictionary<string, object>();
            string primaryId = ReadFirstString(primaryTarget, "heroId", "id");
            string controlId = ReadFirstString(controlTarget, "heroId", "id");
            Dictionary<string, object> player = new Dictionary<string, object>();
            Dictionary<string, object> primaryCounter = new Dictionary<string, object>();
            Dictionary<string, object> controlCounter = new Dictionary<string, object>();
            List<Dictionary<string, object>> primaryReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> controlReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> primaryOccurrences = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> controlOccurrences = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                player = ReignDatabase.QuerySql(connection, @"SELECT hero_id,is_player,is_ruler,is_alive,is_adult,clan_tier
FROM identity_roster WHERE hero_id=$player LIMIT 1;",
                    new Dictionary<string, object> { ["player"] = playerId }).FirstOrDefault()
                    ?? new Dictionary<string, object>();
                primaryCounter = ReignDatabase.QuerySql(connection, @"SELECT ruler_id,favorite_id,exchange_count,last_exchange_id,last_world_day
FROM court_player_favoring_exchanges WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ruler"] = playerId, ["favorite"] = primaryId
                    }).FirstOrDefault() ?? new Dictionary<string, object>();
                controlCounter = ReignDatabase.QuerySql(connection, @"SELECT ruler_id,favorite_id,exchange_count,last_exchange_id,last_world_day
FROM court_player_favoring_exchanges WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ruler"] = playerId, ["favorite"] = controlId
                    }).FirstOrDefault() ?? new Dictionary<string, object>();
                primaryReceipts = ReignDatabase.QuerySql(connection, @"SELECT exchange_id,world_day,created_ts
FROM court_player_favoring_exchange_receipts WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite ORDER BY created_ts,exchange_id;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ruler"] = playerId, ["favorite"] = primaryId
                    });
                controlReceipts = ReignDatabase.QuerySql(connection, @"SELECT exchange_id,world_day,created_ts
FROM court_player_favoring_exchange_receipts WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite ORDER BY created_ts,exchange_id;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ruler"] = playerId, ["favorite"] = controlId
                    });
                primaryOccurrences = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,thread_key,source_event_id,status,
exposure_chance,exposure_roll,participants_json,provenance_summary FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='ruler_favoring_dialogue'
AND thread_key=$thread;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["thread"] = playerId + "|ruler_favoring|" + primaryId
                    });
                controlOccurrences = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,thread_key,source_event_id,status,
exposure_chance,exposure_roll,participants_json,provenance_summary FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='ruler_favoring_dialogue'
AND thread_key=$thread;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["thread"] = playerId + "|ruler_favoring|" + controlId
                    });
            }

            Dictionary<string, object> primaryClose = ReadDictionary(result, "primaryCloseResult")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> primaryMemory = ReadDictionary(primaryClose, "memory")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> courtSocial = ReadDictionary(primaryMemory, "courtSocial")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> producerResults = ReadDictionaryList(courtSocial, "results");
            int tier = Math.Max(1, Math.Min(6, ReadInt(player, "clan_tier",
                ReadInt(result, "playerClanTier", 0))));
            double expectedChance = Math.Min(1d, 0.10d * tier * 0.5d);
            Dictionary<string, object> producerResult = producerResults.Count == 1
                ? producerResults[0] : new Dictionary<string, object>();
            bool producerExposed = ReadBool(producerResult, "exposed", false);
            bool dialogueUsed = ReadBool(result, "naturalLanguageOnly", false)
                && ReadBool(result, "configuredDialogueModelUsed", false)
                && ReadBool(result, "playerIsRuler", false)
                && ReadInt(player, "is_player", 0) != 0 && ReadInt(player, "is_ruler", 0) != 0;
            bool cleanTargetIsolation = PlayerFavoringTargetsHadCleanHistory(
                result, primaryId, controlId);
            bool primarySix = ReadInt(result, "primaryExchangeCount", 0) == 6
                && ReadDictionaryList(result, "primaryTurns").Count == 6
                && ReadInt(primaryCounter, "exchange_count", 0) == 6
                && primaryReceipts.Count == 6
                && primaryReceipts.Select(row => ReadString(row, "exchange_id", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6;
            bool independentControl = !string.IsNullOrWhiteSpace(primaryId)
                && !string.IsNullOrWhiteSpace(controlId)
                && !primaryId.Equals(controlId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(result, "controlExchangeCount", 0) == 1
                && ReadInt(controlCounter, "exchange_count", 0) == 1
                && controlReceipts.Count == 1 && controlOccurrences.Count == 0;
            bool chanceAndRoll = producerResults.Count == 1
                && Math.Abs(ReadDouble(producerResult, "chance", -1d) - expectedChance) < 0.0000001d
                && ReadDouble(producerResult, "roll", -1d) >= 0d
                && ReadDouble(producerResult, "roll", -1d) < 1d
                && (producerExposed
                    ? primaryOccurrences.Count == 1
                        && Math.Abs(ReadDouble(primaryOccurrences[0], "exposure_chance", -1d)
                            - expectedChance) < 0.0000001d
                    : primaryOccurrences.Count == 0);
            bool passed = dialogueUsed && cleanTargetIsolation
                && primarySix && independentControl && chanceAndRoll;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["playerId"] = playerId, ["primaryTargetId"] = primaryId,
                ["controlTargetId"] = controlId, ["playerClanTier"] = tier,
                ["expectedExposureChance"] = expectedChance,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["visibleProductionDialogueUsed"] = dialogueUsed,
                    ["cleanTargetIsolation"] = cleanTargetIsolation,
                    ["sixDistinctPrimaryExchanges"] = primarySix,
                    ["perTargetIndependentCounters"] = independentControl,
                    ["tenPercentBaseClanTierScaling"] = chanceAndRoll
                },
                ["nativeResult"] = result, ["playerRoster"] = player,
                ["primaryCounter"] = primaryCounter, ["controlCounter"] = controlCounter,
                ["primaryReceipts"] = primaryReceipts, ["controlReceipts"] = controlReceipts,
                ["producerResults"] = producerResults,
                ["primaryOccurrences"] = primaryOccurrences,
                ["controlOccurrences"] = controlOccurrences
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["runId"] = runId,
                ["caseId"] = "player_ruler_favoring_dialogue",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "player_favoring_dialogue",
                ["caseId"] = "player_ruler_favoring_dialogue",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static bool PlayerFavoringTargetsHadCleanHistory(
            Dictionary<string, object> result, string primaryId, string controlId)
        {
            List<Dictionary<string, object>> histories =
                ReadDictionaryList(result, "targetHistoryEvidence");
            bool primaryClean = histories.Any(row =>
                ReadString(row, "heroId", "").Equals(primaryId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(row, "persistedDialogueLineCount", -1) == 0
                && ReadBool(row, "cleanConversationalSlate", false));
            bool controlClean = histories.Any(row =>
                ReadString(row, "heroId", "").Equals(controlId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(row, "persistedDialogueLineCount", -1) == 0
                && ReadBool(row, "cleanConversationalSlate", false));
            return ReadBool(result, "cleanConversationalSlateVerified", false)
                && primaryClean && controlClean;
        }

        private static void RecordFavoringProjectionProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            string rulerId = ReadString(result, "rulerId", "");
            string favoriteOneId = ReadString(result, "favoriteOneId", "");
            string favoriteTwoId = ReadString(result, "favoriteTwoId", "");
            string nonFavoriteId = ReadString(result, "nonFavoriteObserverId", "");
            string foreignId = ReadString(result, "foreignObserverId", "");
            Dictionary<string, object> firstProducer = ReadDictionary(result,
                "firstProducerEvidence") ?? new Dictionary<string, object>();
            Dictionary<string, object> secondProducer = ReadDictionary(result,
                "secondProducerEvidence") ?? new Dictionary<string, object>();
            string firstSourceKey = ReadString(firstProducer, "sourceKey", "");
            string secondSourceKey = ReadString(secondProducer, "sourceKey", "");
            long firstSequence = ReadLong(firstProducer, "worldHistorySequence", 0);
            long secondSequence = ReadLong(secondProducer, "worldHistorySequence", 0);
            var currentSourceEvents = new List<Dictionary<string, object>>();
            var currentEventIds = new HashSet<string>(StringComparer.Ordinal);
            List<Dictionary<string, object>> sourceEvents = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> sourceReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> occurrences = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> subjectTags = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> reputations = new List<Dictionary<string, object>>();
            Dictionary<string, object> standing = new Dictionary<string, object>();
            Dictionary<string, object> favoriteOneView = new Dictionary<string, object>();
            Dictionary<string, object> favoriteTwoView = new Dictionary<string, object>();
            Dictionary<string, object> nonFavoriteView = new Dictionary<string, object>();
            Dictionary<string, object> foreignView = new Dictionary<string, object>();
            Dictionary<string, int> pairCountsBefore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> pairCountsAfter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int rulerCharm = 0;

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                sourceEvents = ReignDatabase.QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second)
ORDER BY sequence;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["first"] = firstSourceKey, ["second"] = secondSourceKey
                });
                currentSourceEvents = SocialBalanceCurrentFavorEvents(sourceEvents,
                    firstSourceKey, firstSequence, secondSourceKey, secondSequence);
                currentEventIds = new HashSet<string>(currentSourceEvents.Select(row => ReadString(row, "event_id", "")), StringComparer.Ordinal);
                DateTime receiptDeadline = DateTime.UtcNow.AddSeconds(30);
                do
                {
                    sourceReceipts = sourceEvents.Count == 0
                        ? new List<Dictionary<string, object>>()
                        : ReignDatabase.QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,completed,
attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_id IN (SELECT event_id FROM world_history_events WHERE campaign_id=$campaign
AND timeline_id=$timeline AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second))
ORDER BY event_id;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["first"] = firstSourceKey,
                                ["second"] = secondSourceKey
                            });
                    if (currentEventIds.Count == 2 && currentEventIds.All(id => sourceReceipts.Any(
                        row => ReadString(row, "event_id", "") == id && ReadInt(row, "completed", 0) != 0))) break;
                    if (DateTime.UtcNow >= receiptDeadline) break;
                    Thread.Sleep(100);
                }
                while (true);
                occurrences = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='ruler_favoring_presence'
AND source_event_id IN (SELECT event_id FROM world_history_events WHERE campaign_id=$campaign
AND timeline_id=$timeline AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second))
ORDER BY occurrence_id;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["first"] = firstSourceKey,
                        ["second"] = secondSourceKey
                    });
                subjectTags = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign
AND timeline_id=$timeline AND archetype_id='ruler_favoring_presence' AND source_event_id IN
(SELECT event_id FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second)))
AND tag_id LIKE 'ruler_favoring:%' ORDER BY occurrence_id,tag_id;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["first"] = firstSourceKey, ["second"] = secondSourceKey
                    });
                reputations = ReignDatabase.QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$ruler AND status='active' AND tag_id LIKE 'ruler_favoring:%'
AND source_occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign
AND timeline_id=$timeline AND archetype_id='ruler_favoring_presence' AND source_event_id IN
(SELECT event_id FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second)))
ORDER BY tag_id,source_occurrence_id;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = rulerId, ["first"] = firstSourceKey,
                    ["second"] = secondSourceKey
                });
                double worldDay = occurrences.Count == 0 ? 0d
                    : occurrences.Max(row => ReadDouble(row, "world_day", 0d));
                RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                    new[] { rulerId }, worldDay);
                standing = ReadPublicStandingTrait(connection, campaignId, timelineId,
                    rulerId, worldDay);
                rulerCharm = ReadInt(ReignDatabase.QuerySql(connection,
                    "SELECT current_charm FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = rulerId }).FirstOrDefault(),
                    "current_charm", 0);
                foreach (string observerId in new[] { favoriteOneId, favoriteTwoId, nonFavoriteId, foreignId })
                {
                    string pairKey = RelationshipsService.AmbientPairKey(observerId, rulerId);
                    pairCountsBefore[observerId] = ReadInt(ReignDatabase.QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault(), "count", 0);
                }
                favoriteOneView = RelationshipsService.ResolveObserverPublicStanding(connection, campaignId,
                    timelineId, favoriteOneId, rulerId);
                favoriteTwoView = RelationshipsService.ResolveObserverPublicStanding(connection, campaignId,
                    timelineId, favoriteTwoId, rulerId);
                nonFavoriteView = RelationshipsService.ResolveObserverPublicStanding(connection, campaignId,
                    timelineId, nonFavoriteId, rulerId);
                foreignView = RelationshipsService.ResolveObserverPublicStanding(connection, campaignId,
                    timelineId, foreignId, rulerId);
                foreach (string observerId in new[] { favoriteOneId, favoriteTwoId, nonFavoriteId, foreignId })
                {
                    string pairKey = RelationshipsService.AmbientPairKey(observerId, rulerId);
                    pairCountsAfter[observerId] = ReadInt(ReignDatabase.QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault(), "count", 0);
                }
            }

            List<Dictionary<string, object>> standingSources = ReadDictionaryList(standing, "sources");
            List<Dictionary<string, object>> favoringSources = standingSources
                .Where(IsRulerFavoringStandingSource).ToList();
            int expectedGlobal = RelationshipsService.RoundAwayFromZero(-10d * (1d - SocialCharmMitigation(rulerCharm)));
            int expectedNonFavorite = RelationshipsService.RoundAwayFromZero(-10d
                * (1d - SocialCharmMitigation(rulerCharm))
                * ReadDouble(nonFavoriteView, "jealousyFactor", 1d));
            HashSet<string> linkedFavorites = new HashSet<string>(reputations.Select(row =>
                ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}"))
                    ?? new Dictionary<string, object>(), "linkedHeroId", "")),
                StringComparer.OrdinalIgnoreCase);
            int currentOccurrenceCount = occurrences.Count(row => currentEventIds.Contains(ReadString(row, "source_event_id", "")));
            bool twoIndependentFavorites = SocialBalanceHasTwoFavorSources(currentSourceEvents, firstSourceKey, secondSourceKey)
                && currentEventIds.Count == 2 && currentEventIds.All(id => sourceReceipts.Any(
                    row => ReadString(row, "event_id", "") == id && ReadInt(row, "completed", 0) != 0))
                && currentOccurrenceCount == 2 && subjectTags.Count >= 2 && reputations.Count == 2
                && linkedFavorites.Contains(favoriteOneId) && linkedFavorites.Contains(favoriteTwoId);
            bool collapsedGlobal = SocialBalanceFavoringCostCollapsed(standing, rulerCharm);
            bool favoriteBonuses = ReadString(favoriteOneView, "scope", "") == "favorite"
                && ReadInt(favoriteOneView, "favoring", 0) == 15
                && ReadString(favoriteTwoView, "scope", "") == "favorite"
                && ReadInt(favoriteTwoView, "favoring", 0) == 15;
            bool scopedObservers = ReadString(nonFavoriteView, "scope", "") == "kingdom_non_favorite"
                && ReadInt(nonFavoriteView, "favoring", int.MinValue) == expectedNonFavorite
                && ReadString(foreignView, "scope", "") == "foreign"
                && ReadInt(foreignView, "favoring", int.MinValue) == 0;
            bool noPairCreation = pairCountsBefore.Count == 4
                && pairCountsBefore.All(pair => pairCountsAfter.TryGetValue(pair.Key, out int after)
                    && after == pair.Value);
            bool nativeProduction = ReadBool(result, "productionStateMachineUsed", false)
                && ReadBool(result, "worldHistoryDrained", false);
            bool passed = nativeProduction && twoIndependentFavorites && collapsedGlobal
                && favoriteBonuses && scopedObservers && noPairCreation;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["rulerId"] = rulerId, ["rulerCharm"] = rulerCharm,
                ["favoriteIds"] = new[] { favoriteOneId, favoriteTwoId },
                ["nonFavoriteObserverId"] = nonFavoriteId,
                ["foreignObserverId"] = foreignId,
                ["expectedGlobalFavoringContribution"] = expectedGlobal,
                ["preparationEventCount"] = sourceEvents.Count,
                ["currentPreparationEventIds"] = currentEventIds.ToList(),
                ["currentPreparationOccurrenceCount"] = currentOccurrenceCount,
                ["historicalOccurrenceCount"] = occurrences.Count - currentOccurrenceCount,
                ["uniqueSourceCount"] = sourceEvents.Select(row => ReadString(row, "correlation_id", "")).Distinct().Count(),
                ["actualGlobalFavoringContribution"] = favoringSources.Sum(source => ReadDouble(source, "effectiveContribution", 0d)),
                ["nonFavoringContribution"] = standingSources.Where(source => !IsRulerFavoringStandingSource(source))
                    .Sum(source => ReadDouble(source, "effectiveContribution", 0d)),
                ["expectedNonFavoriteFavoring"] = expectedNonFavorite,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["nativeProductionStateMachineUsed"] = nativeProduction,
                    ["twoIndependentParameterizedFavorites"] = twoIndependentFavorites,
                    ["globalFavoringCostCollapsed"] = collapsedGlobal,
                    ["eachNamedFavoriteReceivesEstablishedBonus"] = favoriteBonuses,
                    ["sameKingdomJealousyAndForeignSuppression"] = scopedObservers,
                    ["observerResolutionCreatesNoRelationshipPair"] = noPairCreation
                },
                ["nativeResult"] = result, ["sourceEvents"] = sourceEvents,
                ["sourceReceipts"] = sourceReceipts, ["occurrences"] = occurrences,
                ["subjectTags"] = subjectTags, ["reputations"] = reputations,
                ["publicStanding"] = standing,
                ["favoriteOneView"] = favoriteOneView,
                ["favoriteTwoView"] = favoriteTwoView,
                ["nonFavoriteView"] = nonFavoriteView,
                ["foreignView"] = foreignView,
                ["pairCountsBefore"] = pairCountsBefore,
                ["pairCountsAfter"] = pairCountsAfter
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["caseId"] = "favoring_collapsed_projection",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "favoring_projection",
                ["caseId"] = "favoring_collapsed_projection",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordFavoringJealousyCharmProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            string rulerId = "";
            string rulerKingdomId = "";
            int rulerCharm = 0;
            Dictionary<string, object> standing = new Dictionary<string, object>();
            List<Dictionary<string, object>> observerCandidates = new List<Dictionary<string, object>>();
            Dictionary<string, object> lowObserver = new Dictionary<string, object>();
            Dictionary<string, object> highObserver = new Dictionary<string, object>();
            Dictionary<string, object> lowView = new Dictionary<string, object>();
            Dictionary<string, object> highView = new Dictionary<string, object>();
            Dictionary<string, object> playerView = new Dictionary<string, object>();
            Dictionary<string, object> favoriteView = new Dictionary<string, object>();
            Dictionary<string, object> foreignView = new Dictionary<string, object>();
            Dictionary<string, int> pairCountsBefore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> pairCountsAfter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> favoriteIds = new List<string>();
            string playerId = "";
            string foreignId = "";

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> rulerRow = ReignDatabase.QuerySql(connection, @"SELECT subject_id,
COUNT(*) AS favoring_count FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
AND tag_id LIKE 'ruler_favoring:%' GROUP BY subject_id
ORDER BY favoring_count DESC,subject_id LIMIT 1;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
                rulerId = ReadString(rulerRow, "subject_id", "");
                Dictionary<string, object> roster = ReignDatabase.QuerySql(connection, @"SELECT hero_id,kingdom_id,
current_charm FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = rulerId }).FirstOrDefault()
                    ?? new Dictionary<string, object>();
                rulerKingdomId = ReadString(roster, "kingdom_id", "");
                rulerCharm = ReadInt(roster, "current_charm", 0);
                double worldDay = ReadDouble(ReignDatabase.QuerySql(connection, @"SELECT MAX(acquired_day) AS day
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND status='active' AND tag_id LIKE 'ruler_favoring:%';",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["subject"] = rulerId
                    }).FirstOrDefault(), "day", 0d);
                RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                    new[] { rulerId }, worldDay);
                standing = ReadPublicStandingTrait(connection, campaignId, timelineId,
                    rulerId, worldDay);
                favoriteIds = ReadDictionaryList(standing, "sources")
                    .Where(IsRulerFavoringStandingSource)
                    .Select(source => ReadString(source, "linkedHeroId", ""))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                observerCandidates = ReignDatabase.QuerySql(connection, @"SELECT hero_id,kingdom_id,is_player
FROM identity_roster WHERE is_alive=1 AND is_adult=1 AND kingdom_id=$kingdom
AND hero_id<>$subject ORDER BY hero_id;", new Dictionary<string, object>
                {
                    ["kingdom"] = rulerKingdomId, ["subject"] = rulerId
                }).Where(row => !favoriteIds.Contains(ReadString(row, "hero_id", ""),
                    StringComparer.OrdinalIgnoreCase)).Select(row =>
                {
                    string id = ReadString(row, "hero_id", "");
                    Dictionary<string, object> copy = new Dictionary<string, object>(row);
                    copy["jealousy"] = RelationshipsService.RelationshipTraitPercent(Program.ReadJsonObject(
                        Program.CharacterFile(campaignId, id, "traits.json")), "jealousy");
                    return copy;
                }).OrderBy(row => ReadDouble(row, "jealousy", 50d))
                    .ThenBy(row => ReadString(row, "hero_id", ""), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                lowObserver = observerCandidates.FirstOrDefault() ?? new Dictionary<string, object>();
                highObserver = observerCandidates.LastOrDefault() ?? new Dictionary<string, object>();
                playerId = ReadString(ReignDatabase.QuerySql(connection,
                    "SELECT hero_id FROM identity_roster WHERE is_player=1 LIMIT 1;",
                    new Dictionary<string, object>()).FirstOrDefault(), "hero_id", "");
                foreignId = ReadString(ReignDatabase.QuerySql(connection, @"SELECT hero_id FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND kingdom_id<>'' AND kingdom_id<>$kingdom
ORDER BY hero_id LIMIT 1;", new Dictionary<string, object>
                {
                    ["kingdom"] = rulerKingdomId
                }).FirstOrDefault(), "hero_id", "");
                string lowId = ReadString(lowObserver, "hero_id", "");
                string highId = ReadString(highObserver, "hero_id", "");
                string favoriteId = favoriteIds.FirstOrDefault() ?? "";
                foreach (string observerId in new[] { lowId, highId, playerId, favoriteId, foreignId }
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string pairKey = RelationshipsService.AmbientPairKey(observerId, rulerId);
                    pairCountsBefore[observerId] = ReadInt(ReignDatabase.QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault(), "count", 0);
                }
                lowView = RelationshipsService.ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    lowId, rulerId);
                highView = RelationshipsService.ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    highId, rulerId);
                playerView = RelationshipsService.ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    playerId, rulerId);
                favoriteView = RelationshipsService.ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    favoriteId, rulerId);
                foreignView = RelationshipsService.ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    foreignId, rulerId);
                foreach (string observerId in pairCountsBefore.Keys.ToList())
                {
                    string pairKey = RelationshipsService.AmbientPairKey(observerId, rulerId);
                    pairCountsAfter[observerId] = ReadInt(ReignDatabase.QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault(), "count", 0);
                }
            }

            double charmMitigation = SocialCharmMitigation(rulerCharm);
            Func<Dictionary<string, object>, int> expected = view => RelationshipsService.RoundAwayFromZero(-10d
                * (1d - charmMitigation) * ReadDouble(view, "jealousyFactor", 1d));
            bool productionStanding = !string.IsNullOrWhiteSpace(rulerId)
                && favoriteIds.Count >= 2
                && ReadDictionaryList(standing, "sources").Count(IsRulerFavoringStandingSource) >= 2;
            bool jealousyMath = observerCandidates.Count >= 2
                && ReadString(lowView, "scope", "") == "kingdom_non_favorite"
                && ReadString(highView, "scope", "") == "kingdom_non_favorite"
                && Math.Abs(ReadDouble(lowView, "jealousyFactor", -1d)
                    - (0.5d + ReadDouble(lowObserver, "jealousy", 50d) / 100d)) < 0.0000001d
                && Math.Abs(ReadDouble(highView, "jealousyFactor", -1d)
                    - (0.5d + ReadDouble(highObserver, "jealousy", 50d) / 100d)) < 0.0000001d
                && ReadInt(lowView, "favoring", int.MinValue) == expected(lowView)
                && ReadInt(highView, "favoring", int.MinValue) == expected(highView);
            bool factorBoundaries = Math.Abs((0.5d + Program.ClampDouble(0d, 0d, 100d) / 100d) - 0.5d) < 0.0000001d
                && Math.Abs((0.5d + Program.ClampDouble(100d, 0d, 100d) / 100d) - 1.5d) < 0.0000001d;
            bool charmMath = Math.Abs(ReadDouble(lowView, "charmMitigation", -1d)
                    - charmMitigation) < 0.0000001d
                && Math.Abs(ReadDouble(highView, "charmMitigation", -1d)
                    - charmMitigation) < 0.0000001d;
            bool neutralAndScoped = Math.Abs(ReadDouble(playerView, "jealousyFactor", -1d) - 1d) < 0.0000001d
                && ReadString(favoriteView, "scope", "") == "favorite"
                && ReadInt(favoriteView, "favoring", 0) == 15
                && ReadString(foreignView, "scope", "") == "foreign"
                && ReadInt(foreignView, "favoring", int.MinValue) == 0;
            bool noPairCreation = pairCountsBefore.Count >= 4
                && pairCountsBefore.All(pair => pairCountsAfter.TryGetValue(pair.Key, out int after)
                    && after == pair.Value);
            bool nativePrerequisites = ReadBool(result, "authoritativePassRequired", false)
                && ReadStringList(result, "blockers").Count == 0;
            bool passed = nativePrerequisites && productionStanding && jealousyMath
                && factorBoundaries && charmMath && neutralAndScoped && noPairCreation;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["rulerId"] = rulerId, ["rulerKingdomId"] = rulerKingdomId,
                ["rulerCharm"] = rulerCharm, ["charmMitigation"] = charmMitigation,
                ["favoriteIds"] = favoriteIds, ["playerObserverId"] = playerId,
                ["foreignObserverId"] = foreignId,
                ["lowObserver"] = lowObserver, ["highObserver"] = highObserver,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["nativePrerequisitesSatisfied"] = nativePrerequisites,
                    ["productionFavoringStandingAvailable"] = productionStanding,
                    ["nativeLowHighJealousyMathExact"] = jealousyMath,
                    ["jealousyFactorBoundariesHalfToOneAndHalf"] = factorBoundaries,
                    ["subjectCharmMitigationExact"] = charmMath,
                    ["playerNeutralFavoriteBonusForeignSuppression"] = neutralAndScoped,
                    ["observerResolutionCreatesNoRelationshipPair"] = noPairCreation
                },
                ["publicStanding"] = standing,
                ["lowObserverView"] = lowView, ["highObserverView"] = highView,
                ["playerView"] = playerView, ["favoriteView"] = favoriteView,
                ["foreignView"] = foreignView,
                ["pairCountsBefore"] = pairCountsBefore,
                ["pairCountsAfter"] = pairCountsAfter,
                ["nativeResult"] = result
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["caseId"] = "favoring_jealousy_charm_matrix",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "favoring_jealousy_charm",
                ["caseId"] = "favoring_jealousy_charm_matrix",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordPlayerParityProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> player = ReadDictionary(result, "player")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> child = ReadDictionary(result, "child")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> producer = ReadDictionary(player, "producerEvidence")
                ?? new Dictionary<string, object>();
            string playerId = ReadString(player, "heroId", "");
            string childId = ReadString(child, "childId", "");
            string sourceKey = ReadString(producer, "sourceKey", "");
            bool playerIsFemale = ReadBool(player, "isFemale", false);
            int expectedValue = playerIsFemale ? -50 : -5;

            Dictionary<string, object> sourceEvent = new Dictionary<string, object>();
            Dictionary<string, object> sourceReceipt = new Dictionary<string, object>();
            Dictionary<string, object> occurrence = new Dictionary<string, object>();
            Dictionary<string, object> subjectTag = new Dictionary<string, object>();
            Dictionary<string, object> reputation = new Dictionary<string, object>();
            Dictionary<string, object> roster = new Dictionary<string, object>();
            Dictionary<string, object> standing = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(campaignId)
                && !string.IsNullOrWhiteSpace(playerId)
                && !string.IsNullOrWhiteSpace(sourceKey))
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    sourceEvent = ReignDatabase.QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND correlation_id=$source ORDER BY sequence DESC LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["source"] = sourceKey
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    string eventId = ReadString(sourceEvent, "event_id", "");
                    if (!string.IsNullOrWhiteSpace(eventId))
                    {
                        DateTime receiptDeadline = DateTime.UtcNow.AddSeconds(30);
                        do
                        {
                            sourceReceipt = ReignDatabase.QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,
completed,attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id=$event LIMIT 1;",
                                new Dictionary<string, object>
                                {
                                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                                    ["event"] = eventId
                                }).FirstOrDefault() ?? new Dictionary<string, object>();
                            if (ReadInt(sourceReceipt, "completed", 0) != 0
                                || DateTime.UtcNow >= receiptDeadline) break;
                            Thread.Sleep(100);
                        }
                        while (true);
                        occurrence = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='the_unchaste' AND source_event_id=$event LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["event"] = eventId
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    }
                    string occurrenceId = ReadString(occurrence, "occurrence_id", "");
                    if (!string.IsNullOrWhiteSpace(occurrenceId))
                    {
                        subjectTag = ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE occurrence_id=$occurrence AND subject_id=$subject AND tag_id='the_unchaste' LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["occurrence"] = occurrenceId, ["subject"] = playerId
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                        reputation = ReignDatabase.QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id='the_unchaste' AND source_occurrence_id=$occurrence LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["subject"] = playerId, ["occurrence"] = occurrenceId
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    }
                    roster = ReignDatabase.QuerySql(connection, @"SELECT hero_id,is_player,is_alive,is_adult,sex,
kingdom_id,current_charm FROM identity_roster WHERE hero_id=$subject LIMIT 1;",
                        new Dictionary<string, object> { ["subject"] = playerId })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    double worldDay = ReadDouble(sourceEvent, "world_day", 0d);
                    RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                        new[] { playerId }, worldDay);
                    standing = ReadPublicStandingTrait(connection, campaignId, timelineId,
                        playerId, worldDay);
                }
            }

            Dictionary<string, object> currentStatus = SocialBalanceStatus(
                campaignId, timelineId, runId, "");
            List<Dictionary<string, object>> statusCases = ReadDictionaryList(currentStatus, "cases");
            Func<string, bool> compatiblePassed = caseId => statusCases.Any(row =>
                ReadString(row, "caseId", "").Equals(caseId, StringComparison.OrdinalIgnoreCase)
                && ReadString(row, "status", "").Equals("passed_cached", StringComparison.OrdinalIgnoreCase));
            bool prerequisiteCases = compatiblePassed("court_unchaste_publicity_and_gender")
                && compatiblePassed("court_flirt_validated_player_signal")
                && compatiblePassed("player_affair_intimacy_discovery");
            bool publicParentage = ReadBool(child, "publiclyKnown", false)
                && ReadBool(child, "illegitimate", false)
                && ReadStringList(producer, "childIds").Contains(childId, StringComparer.OrdinalIgnoreCase)
                && (ReadString(child, "motherId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    || ReadString(child, "biologicalFatherId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase));
            bool nativePlayer = ReadBool(player, "isPlayer", false)
                && ReadBool(player, "isRuler", false)
                && ReadInt(roster, "is_player", 0) == 1
                && ReadInt(roster, "is_alive", 0) == 1
                && ReadInt(roster, "is_adult", 0) == 1
                && ReadString(roster, "sex", "").Equals(
                    playerIsFemale ? "female" : "male", StringComparison.OrdinalIgnoreCase);
            bool productionProducer = ReadBool(result, "productionProducerUsed", false)
                && ReadBool(result, "forcedRareBranch", false)
                && ReadBool(result, "worldHistoryDrained", false)
                && ReadBool(producer, "forcedRareBranch", false);
            bool exactProductionRows = !string.IsNullOrWhiteSpace(ReadString(sourceEvent, "event_id", ""))
                && ReadString(sourceEvent, "correlation_id", "").Equals(sourceKey, StringComparison.OrdinalIgnoreCase)
                && ReadInt(sourceReceipt, "completed", 0) == 1
                && ReadString(sourceReceipt, "subject_id", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && ReadString(occurrence, "archetype_id", "").Equals("the_unchaste", StringComparison.OrdinalIgnoreCase)
                && ReadString(occurrence, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase);
            bool exactParityValues = ReadString(subjectTag, "subject_id", "")
                    .Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(subjectTag, "rumor_value", int.MinValue) == 0
                && ReadInt(subjectTag, "reputation_value", int.MinValue) == expectedValue
                && ReadString(reputation, "subject_id", "")
                    .Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(reputation, "reputation_value", int.MinValue) == expectedValue
                && ReadString(reputation, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase);
            bool passed = prerequisiteCases && publicParentage && nativePlayer
                && productionProducer && exactProductionRows && exactParityValues;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["player"] = player, ["child"] = child,
                ["expectedGenderParityValue"] = expectedValue,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["compatiblePlayerFlirtAffairAndNpcUnchasteEvidence"] = prerequisiteCases,
                    ["publicIllegitimateChildLinkedToPlayer"] = publicParentage,
                    ["nativeAdultRulerPlayerIndependentlyVerified"] = nativePlayer,
                    ["productionCourtPersonalityProducerUsed"] = productionProducer,
                    ["exactPlayerOccurrenceAndReceiptPersisted"] = exactProductionRows,
                    ["playerZeroRumorAndGenderSpecificEstablishedValue"] = exactParityValues
                },
                ["sourceEvent"] = sourceEvent, ["sourceReceipt"] = sourceReceipt,
                ["occurrence"] = occurrence, ["subjectTag"] = subjectTag,
                ["reputation"] = reputation, ["identityRoster"] = roster,
                ["publicStanding"] = standing,
                ["prerequisiteCaseIds"] = new[]
                {
                    "court_flirt_validated_player_signal",
                    "player_affair_intimacy_discovery",
                    "court_unchaste_publicity_and_gender"
                }
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["caseId"] = "court_unchaste_player_parity",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "player_parity", ["caseId"] = "court_unchaste_player_parity",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordSavePrepareProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> marker = ReadDictionary(result, "persistenceMarker")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> database = SocialBalancePersistenceDatabaseSnapshot(
                campaignId, timelineId);
            bool exactMarker = !string.IsNullOrWhiteSpace(ReadString(marker, "markerId", ""))
                && ReadString(marker, "runId", "").Equals(runId, StringComparison.OrdinalIgnoreCase)
                && ReadString(marker, "campaignId", "").Equals(campaignId, StringComparison.OrdinalIgnoreCase)
                && ReadString(marker, "timelineId", "").Equals(timelineId, StringComparison.OrdinalIgnoreCase);
            bool requiredEvidence = ReadBool(database, "hasRequiredEvidence", false);
            bool migrationSafe = ReadBool(database, "legacyFavoringMigrationSafe", false);
            Dictionary<string, object> prepared = new Dictionary<string, object>
            {
                ["schemaVersion"] = 1,
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["preparedGameInstanceId"] = ReadString(liveRun, "gameInstanceId", ""),
                ["marker"] = marker, ["database"] = database,
                ["recordedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["assertions"] = new Dictionary<string, object>
                {
                    ["exactSaveBackedMarkerStaged"] = exactMarker,
                    ["signalIntimacyFavoringEvidencePresent"] = requiredEvidence,
                    ["legacyFavoredByMigrationSafe"] = migrationSafe
                }
            };
            Program.WriteJsonObject(SocialBalancePersistencePath(runId), prepared);
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "save_prepare", ["staged"] = exactMarker && requiredEvidence && migrationSafe,
                ["requiresNativeSaveAndDifferentGameInstance"] = true,
                ["evidence"] = prepared
            };
        }

        private static void RecordSaveVerifyProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> clientVerification = ReadDictionary(result, "persistenceVerification")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> prepared = Program.ReadJsonObject(SocialBalancePersistencePath(runId));
            Dictionary<string, object> preparedMarker = ReadDictionary(prepared, "marker")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> reloadedMarker = ReadDictionary(clientVerification, "marker")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> preparedDatabase = ReadDictionary(prepared, "database")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> currentDatabase = SocialBalancePersistenceDatabaseSnapshot(
                campaignId, timelineId);
            string preparedInstance = ReadString(prepared, "preparedGameInstanceId", "");
            string currentInstance = ReadString(liveRun, "gameInstanceId", "");
            bool prepareExists = !string.IsNullOrWhiteSpace(ReadString(prepared, "runId", ""))
                && ReadString(prepared, "runId", "").Equals(runId, StringComparison.OrdinalIgnoreCase)
                && ReadString(prepared, "campaignId", "").Equals(campaignId, StringComparison.OrdinalIgnoreCase)
                && ReadString(prepared, "timelineId", "").Equals(timelineId, StringComparison.OrdinalIgnoreCase);
            bool markerMatches = !string.IsNullOrWhiteSpace(ReadString(preparedMarker, "markerId", ""))
                && ReadString(preparedMarker, "markerId", "").Equals(
                    ReadString(reloadedMarker, "markerId", ""), StringComparison.OrdinalIgnoreCase);
            bool differentInstance = !string.IsNullOrWhiteSpace(preparedInstance)
                && !string.IsNullOrWhiteSpace(currentInstance)
                && !preparedInstance.Equals(currentInstance, StringComparison.OrdinalIgnoreCase);
            bool clientVerified = ReadBool(clientVerification, "verified", false);
            bool databaseUnchanged = !string.IsNullOrWhiteSpace(ReadString(preparedDatabase, "overallHash", ""))
                && ReadString(preparedDatabase, "overallHash", "").Equals(
                    ReadString(currentDatabase, "overallHash", ""), StringComparison.OrdinalIgnoreCase);
            bool requiredEvidence = ReadBool(currentDatabase, "hasRequiredEvidence", false);
            bool migrationSafe = ReadBool(currentDatabase, "legacyFavoringMigrationSafe", false);
            bool passed = prepareExists && markerMatches && differentInstance && clientVerified
                && databaseUnchanged && requiredEvidence && migrationSafe;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["prepare"] = prepared,
                ["clientVerification"] = clientVerification,
                ["currentDatabase"] = currentDatabase,
                ["currentGameInstanceId"] = currentInstance,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["durablePrepareRecordExists"] = prepareExists,
                    ["sameSaveBackedMarkerReloaded"] = markerMatches,
                    ["differentGameInstanceProvesReload"] = differentInstance,
                    ["nativeHarnessAndCourtStateVerified"] = clientVerified,
                    ["signalIntimacyFavoringDatabaseUnchanged"] = databaseUnchanged,
                    ["requiredPersistenceCategoriesPresent"] = requiredEvidence,
                    ["legacyFavoredByRemainsDisabled"] = migrationSafe
                }
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["caseId"] = "core_save_reload_idempotency",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "save_verify", ["caseId"] = "core_save_reload_idempotency",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordCleanupMarkerProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            bool clientCleared = ReadBool(result, "persistenceMarkerCleared", false);
            string path = SocialBalancePersistencePath(runId);
            bool serverCleared = false;
            if (clientCleared)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    serverCleared = !File.Exists(path);
                }
                catch { serverCleared = false; }
            }
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "cleanup_marker", ["passed"] = clientCleared && serverCleared,
                ["evidence"] = new Dictionary<string, object>
                {
                    ["clientPersistenceMarkerCleared"] = clientCleared,
                    ["serverPrepareRecordCleared"] = serverCleared,
                    ["promptOverrideActive"] = ReadBool(result, "promptOverrideActive", true)
                }
            };
        }
    }
}
