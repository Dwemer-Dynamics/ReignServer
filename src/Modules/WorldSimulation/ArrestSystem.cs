using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
#if !REIGN_EXCLUDE_VERIFICATION
        // POST /arrests/test/contracts (registered in Program.WorldSimulationRoutes.cs).
        internal static Dictionary<string, object> ArrestContractReportApi()
        {
            List<Dictionary<string, object>> results = RunArrestSystemSelfTests();
            return new Dictionary<string, object>
            {
                ["ok"] = results.All(item => ReadBool(item, "passed", false)),
                ["schema"] = "reign-arrest-contract-report-v2",
                ["caseCount"] = results.Count,
                ["languageCaseCount"] = results.Count(item =>
                    ReadString(item, "name", "").StartsWith("arrest_language_",
                        StringComparison.OrdinalIgnoreCase)),
                ["results"] = results
            };
        }
#endif

        internal static Dictionary<string, object> ArrestEvidenceEvaluateApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            bool versioned = payload.ContainsKey("schema");
            string campaignId;
            string timelineId;
            string chargeCategory;
            string accusedId;
            if (versioned)
            {
                if (ArrestEvidenceExactText(payload, "schema") != "reign-arrest-evidence-request-v2"
                    || (campaignId = ArrestEvidenceExactText(payload, "campaignId")) == null
                    || (timelineId = ArrestEvidenceExactText(payload, "timelineId")) == null
                    || (chargeCategory = ArrestEvidenceExactText(payload, "chargeCategory")) == null
                    || (accusedId = ArrestEvidenceExactText(payload, "accusedHeroId")) == null)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["error"] = "An exact campaign, timeline, charge and accused are required."
                    };
            }
            else
            {
                campaignId = ReadString(payload, "campaignId", "");
                timelineId = ReadString(payload, "timelineId", "");
                chargeCategory = ReadString(payload, "chargeCategory", "");
                accusedId = ReadString(payload, "accusedHeroId", "").Trim();
            }
            if (accusedId.Length == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["error"] = "accusedHeroId is required."
                };
            List<string> evidenceIds = new List<string>();
            // A documented exposure establishes espionage. World History participates in
            // other game systems, but its generic roles and flags do not establish a charge.
            if (chargeCategory == "espionage" && !string.IsNullOrWhiteSpace(campaignId)
                && !string.IsNullOrWhiteSpace(timelineId))
            {
                using var connection = ReignDatabase.OpenCampaignConnection(campaignId);
                if (TableExists(connection, "foreign_agent_assignments"))
                {
                    evidenceIds = WorldSimulationStore.SelectExposedForeignAgentArrestEvidence(connection,
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["accused"] = accusedId
                        }).Select(row => ArrestExposedAgentEvidenceId(row, accusedId))
                        .Where(id => id != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            if (versioned)
                evidenceIds.RemoveAll(id => id.Length > 8192 || id != id.Trim() || id.Contains(",") || id.Any(char.IsControl));
            var result = new Dictionary<string, object>
            {
                ["ok"] = true, ["accusedHeroId"] = accusedId,
                ["causeEstablished"] = evidenceIds.Count > 0,
                ["evidenceStatus"] = evidenceIds.Count > 0 ? "established" : "unsupported",
                ["evidenceIds"] = evidenceIds,
                ["hiddenAgentInformationRevealed"] = false
            };
            if (versioned)
            {
                result["schema"] = "reign-arrest-evidence-response-v2";
                result["campaignId"] = campaignId;
                result["timelineId"] = timelineId;
                result["chargeCategory"] = chargeCategory;
                result["evidence"] = evidenceIds.Select(id => new Dictionary<string, object>
                {
                    ["evidenceId"] = id,
                    ["kind"] = "foreign_agent_exposure",
                    ["subjectRole"] = "agent",
                    ["subjectHeroId"] = accusedId
                }).ToList();
            }
            return result;
        }

        private static string ArrestEvidenceExactText(Dictionary<string, object> payload, string name) =>
            payload.TryGetValue(name, out object value) && value is string text && !string.IsNullOrWhiteSpace(text)
            && text.Length <= 8192 && text == text.Trim() && !text.Any(char.IsControl) ? text : null;

        internal static Dictionary<string, object> ArrestCaseApplyApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string operation = ReadString(payload, "operation", "apply")
                .Trim().ToLowerInvariant();
            string caseId = ReadString(payload, "caseId", "").Trim();
            string accusedId = ReadString(payload, "accusedHeroId", "").Trim();
            string playerId = ReadString(payload, "playerHeroId", "").Trim();
            string tagId = ReadString(payload, "reputationTagId", "").Trim();
            const string generationField = "sharedActivityGeneration";
            var generationFields = payload.Keys.Where(key =>
                string.Equals(key, generationField, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            bool requireOwner = generationFields.Length != 0;
            if (requireOwner && (generationFields.Length != 1 || generationFields[0] != generationField
                || payload[generationField] is not string
                || !ExactArrestEffectField(payload, "campaignId", campaignId)
                || !ExactArrestEffectField(payload, "timelineId", timelineId)
                || !ExactArrestEffectField(payload, "operation", operation)
                || !ExactArrestEffectField(payload, "caseId", caseId)
                || !ExactArrestEffectField(payload, "accusedHeroId", accusedId)
                || !ExactArrestEffectField(payload, "playerHeroId", playerId)
                || !ExactArrestEffectField(payload, "reputationTagId", tagId)))
                return ArrestEffectOwnerFailure("ownership_mismatch", "The arrest effect requires its exact campaign, timeline, case, and restore owner.");

            bool requestReceipt = payload.Keys.Any(IsArrestEffectReceiptField);
            if (requestReceipt && !requireOwner)
                return ArrestEffectOwnerFailure("ownership_mismatch", "A versioned arrest effect requires a current restore owner.");
            bool inspect = requestReceipt && operation == "inspect";
            ArrestEffectIntent intent = null;
            if (inspect)
            {
                if (!ExactArrestIntentValue(payload, "arrestEffectVersion", out int version) || version != 2
                    || payload.Keys.Any(key => !string.Equals(key, "arrestEffectVersion", StringComparison.Ordinal) && IsArrestEffectReceiptField(key))
                    || !TryReadArrestEffectMaterial(payload, out _, out _))
                    return ArrestEffectOwnerFailure("invalid_effect_intent", "An arrest inspection requires exact v2 material with no proposed write intent.");
            }
            else if (requestReceipt && (operation != "apply" || !TryReadArrestEffectIntent(payload, out intent)))
                return ArrestEffectOwnerFailure("invalid_effect_intent", "A versioned arrest apply requires an exact intent, revision and material.");

            if (caseId.Length == 0 || accusedId.Length == 0 || playerId.Length == 0
                || tagId.Length == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "caseId, accusedHeroId, playerHeroId, and reputationTagId are required."
                };
            if (!inspect && operation != "apply" && operation != "rescind")
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["error"] = "operation must be apply or rescind."
                };

            double worldDay = intent?.WorldDay ?? ReadDouble(payload, "worldDay", 0d);
            int desiredDelta = Clamp(ReadInt(payload, "relationshipDelta", 0), -100, 100);
            int reputationValue = Clamp(ReadInt(payload, "reputationValue", 0), -100, 100);
            string description = DialogueService.LimitText(ReadString(payload, "description", ""), 1000);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (EnterWorldHistoryTimelineScope(campaignId))
            using (ReignDbConnection connection = requireOwner
                ? ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaignId, trackCampaignMutations: true)
                : ReignDatabase.OpenCampaignConnection(campaignId))
            {
                if (connection == null)
                    return ArrestEffectOwnerFailure("ownership_mismatch", "The arrest effect campaign is not registered.");

                if (!requireOwner)
                {
                    ReputationService.EnsureSocialReputationSchema(connection);
                    RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                    RelationshipsService.EnsureRelationshipDirectorSchema(connection);
                    WorldSimulationStore.EnsureArrestCaseEffects(connection);
                }
                using var typedTransaction = requireOwner ? connection.BeginTransaction() : null;
                if (requireOwner)
                    AcquirePostgreSqlTransactionMutationLock(connection, typedTransaction);
                else
                    WorldSimulationStore.Begin(connection);

                void CommitEffect()
                {
                    if (requireOwner) typedTransaction.Commit();
                    else WorldSimulationStore.Commit(connection);
                }

                void RollbackEffect()
                {
                    if (requireOwner) typedTransaction.Rollback();
                    else WorldSimulationStore.Rollback(connection);
                }

                try
                {
                    if (requireOwner)
                    {
                        // Queries enlist in the physical transaction opened above.
                        if (!NoticeAcknowledgementOwnerMatches(connection, typedTransaction, payload, campaignId, timelineId))
                        {
                            RollbackEffect();
                            return ArrestEffectOwnerFailure("ownership_mismatch", "The arrest effect owner is no longer current.");
                        }
                        if (!TableExists(connection, "arrest_case_effect_state")
                            || !TableExists(connection, "character_reputations")
                            || !TableExists(connection, "relationship_pair_chemistry"))
                        {
                            RollbackEffect();
                            return ArrestEffectOwnerFailure("arrest_effect_schema_unavailable", "The campaign arrest effect storage is not initialized.");
                        }
                    }
                    else
                    {
                        string activeTimeline = TableExists(connection, "world_history_timelines")
                            ? ActiveWorldHistoryTimeline(connection) : "main";
                        if (!string.Equals(activeTimeline, timelineId, StringComparison.Ordinal))
                            throw new InvalidOperationException("The arrest effect does not belong to the active timeline.");
                    }

                    bool receiptSchema = WorldSimulationStore.ArrestEffectReceiptSchemaReady(connection);
                    if (requestReceipt && !receiptSchema)
                    {
                        RollbackEffect();
                        return ArrestEffectOwnerFailure("arrest_effect_schema_unavailable", "The campaign must open normally before using versioned arrest effects.");
                    }

                    Dictionary<string, object> key = new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["case"] = caseId,
                        ["subject"] = accusedId, ["player"] = playerId, ["tag"] = tagId
                    };
                    var states = WorldSimulationStore.SelectArrestCaseEffects(connection, key, receiptSchema);
                    var effectState = states.FirstOrDefault();
                    Dictionary<string, object> existing =
                        WorldSimulationStore.SelectCharacterReputations(connection, key).FirstOrDefault();
                    Dictionary<string, object> priorSnapshot = TryParseJsonObject(
                        ReadString(existing, "snapshot_json", "{}"));
                    if (states.Any(state => ReadString(state, "case_id", "") != caseId
                            || ReadString(state, "accused_id", "") != accusedId
                            || ReadString(state, "player_id", "") != playerId
                            || ReadString(state, "tag_id", "") != tagId)
                        || existing != null && (ReadString(existing, "archetype_id", "") != "arrest_accusation"
                            || ReadString(priorSnapshot, "arrestCaseId", "") != caseId
                            || ReadString(priorSnapshot, "accusedHeroId", "") != accusedId
                            || ReadString(priorSnapshot, "accuserHeroId", "") != playerId)
                        || states.Count == 0 && WorldSimulationStore.SelectOtherArrestCaseReputations(connection, key).Any(row =>
                            ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "")), "arrestCaseId", "") == caseId))
                    {
                        RollbackEffect();
                        return new Dictionary<string, object>
                        {
                            ["ok"] = false, ["caseId"] = caseId,
                            ["error"] = "The arrest case and reputation identities do not match."
                        };
                    }

                    bool recordedRescission = ReadBool(effectState, "rescinded", false);
                    bool legacyRescission = ReadString(existing, "status", "") == "rescinded_by_accuser";
                    bool terminal = recordedRescission || legacyRescission;
                    bool rescind = operation == "rescind";
                    long revision = effectState == null || !receiptSchema ? 0 : ReadLong(effectState, "material_revision", -1);
                    if (revision < 0)
                        throw new InvalidOperationException("The arrest effect material revision is invalid.");
                    if (!inspect && intent == null && terminal && !rescind)
                    {
                        RollbackEffect();
                        return new Dictionary<string, object>
                        {
                            ["ok"] = false, ["caseId"] = caseId, ["reputationTagId"] = tagId,
                            ["error"] = "The arrest accusation has already been rescinded."
                        };
                    }
                    // Older rows retained the nominal policy cell, even when the affinity clamp
                    // applied a different amount. Never treat that cell as a measured effect.
                    bool measured = ReadString(priorSnapshot, "arrestRelationshipMeasurement", "") == "directional-affinity-v1";
                    int requested = measured ? ReadInt(priorSnapshot, "arrestRequestedRelationshipDelta", int.MinValue) : 0;
                    int applied = measured ? ReadInt(priorSnapshot, "arrestAppliedRelationshipDelta", int.MinValue) : 0;
                    if (requested < -100 || requested > 100 || applied < -100 || applied > 100)
                        throw new InvalidOperationException("The measured arrest relationship effect is invalid.");
                    bool legacyUnverified = existing != null && !measured;
                    if ((recordedRescission && existing != null && !legacyRescission)
                        || (!recordedRescission && states.Count != 0 && existing == null))
                    {
                        RollbackEffect();
                        return new Dictionary<string, object>
                        {
                            ["ok"] = false, ["caseId"] = caseId,
                            ["error"] = "The recorded arrest case has an inconsistent reputation state."
                        };
                    }
                    if (inspect)
                    {
                        RollbackEffect();
                        bool matching = measured && requested == desiredDelta
                            && ArrestEffectMaterialMatches(payload, existing, priorSnapshot, description, reputationValue);
                        string status = terminal ? "terminal_rescinded" : effectState == null && existing == null ? "ready"
                            : matching ? "settled" : "material_mismatch";
                        return ArrestEffectInspection(payload, revision, status, status == "settled" ? applied : (int?)null);
                    }
                    if (intent != null && terminal)
                    {
                        RollbackEffect();
                        return ArrestEffectIntentStatus(intent, caseId, tagId, revision, "terminal_rescinded");
                    }
                    if (intent != null && ReadString(effectState, "effect_intent_id", "") == intent.Id)
                    {
                        RollbackEffect();
                        if (revision - 1 != intent.ExpectedRevision
                            || ReadString(effectState, "effect_intent_material", "") != intent.Material)
                            return ArrestEffectIntentStatus(intent, caseId, tagId, revision, "intent_conflict");
                        var receipt = TryParseJsonObject(ReadString(effectState, "effect_receipt_json", ""));
                        int receiptDelta = ReadInt(receipt, "appliedRelationshipDelta", int.MinValue);
                        if (!measured || requested != desiredDelta || receiptDelta != applied
                            || !ArrestEffectMaterialMatches(payload, existing, priorSnapshot, description, reputationValue)
                            || !ReadBool(receipt, "ok", false) || ReadString(receipt, "operation", "") != "apply"
                            || ReadString(receipt, "caseId", "") != caseId || ReadString(receipt, "reputationTagId", "") != tagId
                            || ReadString(receipt, "status", "") != "committed" || ReadInt(receipt, "arrestEffectVersion", 0) != 2
                            || ReadString(receipt, "effectIntentId", "") != intent.Id
                            || ReadLong(receipt, "materialRevision", -1) != revision)
                            return ArrestEffectIntentStatus(intent, caseId, tagId, revision, "receipt_unavailable");
                        return receipt;
                    }
                    if (intent != null && revision != intent.ExpectedRevision)
                    {
                        RollbackEffect();
                        return ArrestEffectIntentStatus(intent, caseId, tagId, revision, "stale_material");
                    }
                    if (recordedRescission)
                    {
                        CommitEffect();
                        return ArrestEffectAcknowledgement(operation, caseId, tagId,
                            legacyUnverified ? null : applied, true);
                    }
                    if (legacyUnverified && !rescind)
                        throw new InvalidOperationException("The prior arrest relationship effect predates exact affinity measurement.");

                    bool sameRequest = !rescind && measured && requested == desiredDelta;
                    int correction = legacyUnverified || terminal || sameRequest ? 0
                        : ArrestRelationshipCorrection(operation, desiredDelta, applied);
                    if (correction != 0)
                    {
                        string pairKey = RelationshipsService.AmbientPairKey(accusedId, playerId);
                        string heroA = pairKey.StartsWith(accusedId + "|",
                            StringComparison.OrdinalIgnoreCase) ? accusedId : playerId;
                        string heroB = string.Equals(heroA, accusedId,
                            StringComparison.OrdinalIgnoreCase) ? playerId : accusedId;
                        RelationshipsService.EnsurePoliticalRelationshipPair(connection, campaignId,
                            timelineId, pairKey, heroA, heroB, worldDay);
                        Dictionary<string, object> lockedPair = WorldSimulationStore.LockArrestRelationshipPair(connection,
                            new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                        string affinityColumn = heroA == accusedId ? "affinity_a_to_b" : "affinity_b_to_a";
                        int before = ArrestDirectionalAffinity(lockedPair, heroA, heroB, affinityColumn);
                        string reason = rescind ? "arrest_rescinded:" : "arrest_case:";
                        Dictionary<string, object> pair =
                            RelationshipsService.ApplyAtomicDirectionalRelationshipDelta(connection,
                                campaignId, accusedId, playerId, correction, worldDay, reason + caseId, timelineId);
                        int after = ArrestDirectionalAffinity(pair, heroA, heroB, affinityColumn);
                        int movement = after - before;
                        if (movement != Clamp(before + correction, -100, 100) - before)
                            throw new InvalidOperationException("The arrest relationship change could not be measured exactly.");
                        applied += movement;
                    }

                    if (!rescind)
                    {
                        Dictionary<string, object> snapshot = new Dictionary<string, object>
                        {
                            ["label"] = "Arrest accusation",
                            ["description"] = description,
                            ["arrestCaseId"] = caseId,
                            ["accuserHeroId"] = playerId,
                            ["accusedHeroId"] = accusedId,
                            ["chargeCategory"] = ReadString(payload, "chargeCategory", "other"),
                            ["severity"] = ReadString(payload, "severity", "serious"),
                            ["causeEstablished"] = ReadBool(payload, "causeEstablished", false),
                            ["evidenceStatus"] = ReadString(payload, "evidenceStatus", "unsupported"),
                            ["evidenceIds"] = ReadString(payload, "evidenceIds", ""),
                            ["arrestRelationshipMeasurement"] = "directional-affinity-v1",
                            ["arrestRequestedRelationshipDelta"] = desiredDelta,
                            ["arrestAppliedRelationshipDelta"] = applied,
                            ["playerOriginated"] = true
                        };
                        WorldSimulationStore.UpsertCharacterReputations(connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["subject"] = accusedId, ["tag"] = tagId,
                                ["archetype"] = "arrest_accusation",
                                ["description"] = description, ["value"] = reputationValue,
                                ["day"] = worldDay, ["snapshot"] = Json.Serialize(snapshot),
                                ["ts"] = ts
                            });
                    }
                    else if (existing != null && !legacyRescission)
                    {
                        if (measured)
                        {
                            priorSnapshot["arrestRequestedRelationshipDelta"] = 0;
                            priorSnapshot["arrestAppliedRelationshipDelta"] = applied;
                        }
                        WorldSimulationStore.UpdateCharacterReputations(connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["subject"] = accusedId, ["tag"] = tagId,
                                ["snapshot"] = Json.Serialize(priorSnapshot), ["ts"] = ts
                            });
                    }
                    var response = ArrestEffectAcknowledgement(operation, caseId, tagId,
                        legacyUnverified ? null : applied, intent == null && (rescind ? terminal : sameRequest || correction == 0));
                    if (receiptSchema)
                    {
                        long nextRevision = checked(revision + 1);
                        if (intent != null)
                        {
                            response["arrestEffectVersion"] = 2;
                            response["effectIntentId"] = intent.Id;
                            response["materialRevision"] = nextRevision;
                            response["status"] = "committed";
                        }
                        key["rescinded"] = rescind;
                        key["revision"] = nextRevision;
                        key["intent"] = intent?.Id ?? "";
                        key["material"] = intent?.Material ?? "";
                        key["receipt"] = intent == null ? "{}" : Json.Serialize(response);
                        WorldSimulationStore.UpsertVersionedArrestCaseEffect(connection, key);
                    }
                    else if (states.Count == 0 || rescind)
                    {
                        key["rescinded"] = rescind;
                        WorldSimulationStore.UpsertArrestCaseEffect(connection, key);
                    }
                    // Do not commit a replayable result if the checkpoint sidecar could not be made dirty.
                    if (intent != null) PersistenceService.MarkSaveSyncActiveStateDirty(campaignId);
                    CommitEffect();
                    RelationshipsService.InvalidateRelationshipPairStateCache(campaignId);
                    return response;
                }
                catch (Exception ex)
                {
                    try { RollbackEffect(); } catch { }
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["caseId"] = caseId, ["error"] = ex.Message
                    };
                }
            }
        }

        private sealed record ArrestEffectIntent(string Id, long ExpectedRevision, string Material, double WorldDay);

        private static bool IsArrestEffectReceiptField(string key) =>
            string.Equals(key, "arrestEffectVersion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "effectIntentId", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "expectedMaterialRevision", StringComparison.OrdinalIgnoreCase);

        private static bool ExactArrestIntentValue<T>(Dictionary<string, object> input, string key, out T value)
        {
            value = default;
            if (input.Keys.Count(field => string.Equals(field, key, StringComparison.OrdinalIgnoreCase)) != 1
                || !input.Keys.Contains(key, StringComparer.Ordinal) || input[key] is not T exact)
                return false;
            value = exact;
            return true;
        }

        private static bool TryReadArrestEffectIntent(Dictionary<string, object> input, out ArrestEffectIntent intent)
        {
            intent = null;
            if (!ExactArrestIntentValue(input, "arrestEffectVersion", out int version) || version != 2
                || !ExactArrestIntentValue(input, "expectedMaterialRevision", out object revision) || !(revision is int or long)
                || !ExactArrestIntentValue(input, "effectIntentId", out string id)
                || !TryReadArrestEffectMaterial(input, out double day, out string material))
                return false;
            long expected = Convert.ToInt64(revision, CultureInfo.InvariantCulture);
            if (expected < 0 || expected == long.MaxValue)
                return false;
            // Revisions form part of the intent identity so a superseded nonce cannot
            // be reapplied by resubmitting it with a refreshed revision.
            string prefix = expected.ToString(CultureInfo.InvariantCulture) + "_";
            if (!id.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(id.Substring(prefix.Length), "N", out Guid nonce) || nonce == Guid.Empty
                || id != prefix + nonce.ToString("N"))
                return false;
            intent = new ArrestEffectIntent(id, expected, material, day);
            return true;
        }

        private static bool TryReadArrestEffectMaterial(Dictionary<string, object> input, out double day, out string material)
        {
            day = 0;
            material = null;
            if (!ExactArrestIntentValue(input, "relationshipDelta", out int delta) || delta < -100 || delta > 100
                || !ExactArrestIntentValue(input, "reputationValue", out int value) || value < -100 || value > 100
                || !ExactArrestIntentValue(input, "worldDay", out object rawDay) || !(rawDay is int or long or float or double or decimal)
                || !ExactArrestIntentValue(input, "description", out string description)
                || description != DialogueService.LimitText(description, 1000)
                || !ExactArrestIntentValue(input, "chargeCategory", out string category) || category.Length > 128
                || !ExactArrestIntentValue(input, "severity", out string severity) || severity.Length > 128
                || !ExactArrestIntentValue(input, "causeEstablished", out bool established)
                || !ExactArrestIntentValue(input, "evidenceStatus", out string evidenceStatus) || evidenceStatus.Length > 128
                || !ExactArrestIntentValue(input, "evidenceIds", out string evidenceIds) || evidenceIds.Length > 8192)
                return false;
            day = Convert.ToDouble(rawDay, CultureInfo.InvariantCulture);
            if (!double.IsFinite(day) || day < 0)
                return false;
            material = Json.Serialize(new object[] { delta, value, day, description, category, severity,
                established, evidenceStatus, evidenceIds });
            return true;
        }

        private static bool ArrestEffectMaterialMatches(Dictionary<string, object> payload,
            Dictionary<string, object> reputation, Dictionary<string, object> snapshot, string description, int value) =>
            ReadString(reputation, "status", "") == "active"
            && ReadInt(reputation, "reputation_value", int.MinValue) == value
            && ReadString(reputation, "description", "") == description
            && ReadString(snapshot, "chargeCategory", "") == ReadString(payload, "chargeCategory", "")
            && ReadString(snapshot, "severity", "") == ReadString(payload, "severity", "")
            && ReadBool(snapshot, "causeEstablished", false) == ReadBool(payload, "causeEstablished", false)
            && ReadString(snapshot, "evidenceStatus", "") == ReadString(payload, "evidenceStatus", "")
            && ReadString(snapshot, "evidenceIds", "") == ReadString(payload, "evidenceIds", "");

        private static Dictionary<string, object> ArrestEffectInspection(Dictionary<string, object> request,
            long revision, string status, int? delta)
        {
            var response = TestDict("ok", status == "ready" || status == "settled", "operation", "inspect",
                "arrestEffectVersion", 2, "status", status, "materialRevision", revision,
                "appliedRelationshipDelta", delta, "relationshipEffectResolved", status == "settled");
            foreach (string field in new[] { "campaignId", "timelineId", "sharedActivityGeneration", "caseId",
                "accusedHeroId", "playerHeroId", "reputationTagId" })
                response[field] = request[field];
            return response;
        }

        private static Dictionary<string, object> ArrestEffectIntentStatus(ArrestEffectIntent intent,
            string caseId, string tagId, long revision, string status) =>
            TestDict("ok", false, "operation", "apply", "caseId", caseId, "reputationTagId", tagId,
                "arrestEffectVersion", 2, "effectIntentId", intent.Id, "materialRevision", revision, "status", status);

        private static bool ExactArrestEffectField(Dictionary<string, object> payload, string key, string expected)
        {
            return !string.IsNullOrWhiteSpace(expected) && payload.TryGetValue(key, out object value)
                && value is string supplied && string.Equals(supplied, expected, StringComparison.Ordinal);
        }

        private static Dictionary<string, object> ArrestEffectOwnerFailure(string status, string error)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["status"] = status, ["error"] = error };
        }

        private static int ArrestDirectionalAffinity(Dictionary<string, object> pair, string heroA, string heroB,
            string affinityColumn)
        {
            int affinity = ReadInt(pair, affinityColumn, int.MinValue);
            if (ReadString(pair, "hero_a_id", "") != heroA || ReadString(pair, "hero_b_id", "") != heroB
                || affinity < -100 || affinity > 100)
                throw new InvalidOperationException("The arrest relationship pair has no matching directional affinity.");
            return affinity;
        }

        private static Dictionary<string, object> ArrestEffectAcknowledgement(string operation, string caseId,
            string tagId, int? appliedDelta, bool idempotent)
        {
            bool unresolved = operation == "rescind" && appliedDelta != 0;
            return new Dictionary<string, object>
            {
                ["ok"] = !unresolved, ["operation"] = operation,
                ["caseId"] = caseId, ["reputationTagId"] = tagId,
                ["appliedRelationshipDelta"] = appliedDelta, ["idempotent"] = idempotent,
                ["relationshipEffectResolved"] = !unresolved,
                ["error"] = !unresolved ? null : appliedDelta.HasValue
                    ? "The accusation was rescinded, but the affinity limit prevented full relationship compensation."
                    : "The accusation was rescinded, but its historical relationship effect cannot be measured."
            };
        }

        internal static List<Dictionary<string, object>> BuildArrestDialogueCandidates(
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            string visibleReply)
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Dictionary<string, object> context = ReadDictionary(payload, "arrestContext")
                ?? new Dictionary<string, object>();
            string normalized = ActionPipelineService.NormalizeLookup(playerText);
            if (string.IsNullOrWhiteSpace(normalized) || ArrestTextIsNonDirective(normalized))
                return results;
            string playerId = ReadFirstString(payload, "playerHeroStringId", "playerId");
            string speakerId = FirstNonEmpty(
                ReadFirstString(payload, "heroStringId", "speakerHeroStringId"),
                ReadString(hero, "heroStringId", ""));
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(speakerId))
                return results;
            if (ArrestNamesAbsentThirdParty(payload, hero, normalized, speakerId))
                return results;

            string caseId = ReadString(context, "activeCaseId", "");
            string phase = ReadString(context, "phase", "").ToLowerInvariant();
            bool available = ReadBool(context, "available", false);
            bool held = ReadBool(context, "heldByPlayer", false);

            if ((held || !string.IsNullOrWhiteSpace(caseId)) && LooksLikeRescission(normalized))
            {
                results.Add(ArrestCandidate("rescind_arrest_accusation", playerId,
                    speakerId, caseId, "The player rescinded the accusation and cleared the accused's name."));
                return results;
            }
            if (held && LooksLikeRelease(normalized))
            {
                results.Add(ArrestCandidate("release_arrested_character", playerId,
                    speakerId, caseId, "The player ordered the accused released while retaining the record."));
                return results;
            }
            if (phase == "refused" && LooksLikePlayerPartyAttack(normalized))
            {
                results.Add(ArrestCandidate("player_attack_party", playerId,
                    speakerId, caseId, "The player ordered their party to attack after arrest was refused."));
                return results;
            }
            if (phase == "refused" && LooksLikeDuel(normalized)
                && ArrestReplyAccepts(visibleReply))
            {
                Dictionary<string, object> duel = new Dictionary<string, object>
                {
                    ["command"] = "duel_player", ["actorHeroId"] = speakerId,
                    ["TargetHero"] = playerId,
                    ["reason"] = "A nonlethal duel will decide the refused arrest.",
                    ["source"] = "arrest_dialogue", ["requiresAcceptance"] = false,
                    ["terms"] = new Dictionary<string, object>
                    {
                        ["duelMode"] = "training", ["lethal"] = false,
                        ["duelPurpose"] = "arrest_capture", ["caseId"] = caseId
                    }
                };
                results.Add(duel);
                return results;
            }
            if ((phase == "awaitingconfirmation" || phase == "awaiting_confirmation"
                || phase == "refused") && LooksLikeArrestConfirmation(normalized))
            {
                results.Add(ArrestCandidate("confirm_arrest", playerId, speakerId,
                    caseId, "The player confirmed the prepared arrest."));
                return results;
            }
            if (!available || !LooksLikeArrestOrder(normalized)) return results;

            string accusation = ExtractArrestReason(playerText);
            if (string.IsNullOrWhiteSpace(accusation)) return results;
            string severity = ClassifyArrestSeverity(accusation);
            string category = ClassifyArrestCharge(accusation);
            bool partyEncounter = string.Equals(ReadString(context, "contextKind", ""),
                "party_encounter", StringComparison.OrdinalIgnoreCase);
            Dictionary<string, object> prepare = ArrestCandidate("prepare_arrest",
                playerId, speakerId, string.Empty, accusation);
            prepare["reason"] = accusation;
            prepare["terms"] = new Dictionary<string, object>
            {
                ["accusation"] = accusation,
                ["severity"] = severity,
                ["chargeCategory"] = category,
                ["surrenderAccepted"] = !partyEncounter || ArrestReplyAccepts(visibleReply),
                ["reactionSnapshot"] = DialogueService.LimitText(visibleReply ?? string.Empty, 1000)
            };
            results.Add(prepare);
            return results;
        }

        private static Dictionary<string, object> ArrestCandidate(string command,
            string playerId, string speakerId, string caseId, string reason)
        {
            Dictionary<string, object> terms = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(caseId)) terms["caseId"] = caseId;
            return new Dictionary<string, object>
            {
                ["command"] = command,
                ["actorHeroId"] = playerId,
                ["TargetHero"] = speakerId,
                ["reason"] = reason ?? "Conversation arrest action.",
                ["source"] = "arrest_dialogue",
                ["requiresAcceptance"] = false,
                ["terms"] = terms
            };
        }

        internal static string BuildArrestDialoguePromptBlock(
            Dictionary<string, object> turnPayload)
        {
            Dictionary<string, object> context = ReadDictionary(turnPayload, "arrestContext");
            if (context == null || !ReadBool(context, "available", false)
                && string.IsNullOrWhiteSpace(ReadString(context, "activeCaseId", "")))
                return string.Empty;
            return @"CONVERSATION ARREST STATE (private live-game authority)
" + Json.Serialize(context) + @"
- Names and gendered or neutral pronouns in an arrest order resolve only to the current NPC speaker. Do not accept an order to arrest an absent third party.
- If the player orders this NPC arrested but gives no reason or charge, ask what the charge is. Do not act as if an arrest case exists yet.
- On a first reasoned arrest order, show only this NPC's immediate spoken/visible reaction as guards move into position. Do not narrate the player, guards completing custody, or a dungeon transfer.
- Custody begins only after a later explicit player command to take the NPC away. Treat the supplied phase as authoritative and never skip that second turn.
- During a field-party encounter the NPC may surrender or refuse according to personality, strength, relationship, and circumstances. Refusal may be followed by a nonlethal duel or native party attack.
- Never reveal hidden spy status or secret evidence. Only causeEstablished/evidenceStatus supplied here may be treated as disclosed proof.
- A release preserves the accusation; rescinding/clearing the accusation also releases the NPC when this case is the custody basis.";
        }

        private static bool ArrestTextIsNonDirective(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "do not arrest", "dont arrest", "do not take him", "do not take her",
                "not arrest", "if i arrest", "if we arrest", "would arrest",
                "would you arrest", "could arrest", "should i arrest", "should i put",
                "should we put", "gave no order", "give no order", "no arrest order",
                "talk about arrest", "rumor of arrest");
        }

        private static bool LooksLikeArrestOrder(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "arrest", "guards take", "guard take", "take him to the dungeon",
                "take her to the dungeon", "take them to the dungeon",
                "take him away", "take her away", "take them away", "take you away",
                "take him to prison", "take her to prison", "take them to prison",
                "seize him", "seize her", "seize them", "seize this lord", "seize this lady",
                "place him in custody",
                "place her in custody", "place them in custody", "have him detained",
                "have her detained", "have them detained", "lock him up", "lock her up",
                "lock them up", "take you into custody", "taking you into custody", "detain him",
                "detain her", "detain them", "placed in custody")
                || (normalized.Contains("lock ") && normalized.Contains(" up"))
                || (ActionPipelineService.ContainsAnyNormalized(normalized, "dungeon", "prison", "custody", "detained")
                    && ActionPipelineService.ContainsAnyNormalized(normalized, "guards", "guard", "take", "send", "lock", "put", "place"));
        }

        private static bool ArrestNamesAbsentThirdParty(
            Dictionary<string, object> payload,
            Dictionary<string, object> speaker,
            string normalizedText,
            string speakerId)
        {
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex")
                ?? new Dictionary<string, object>();
            string playerId = ReadFirstString(payload, "playerHeroStringId", "playerId");
            List<Dictionary<string, object>> heroes = ReadDictionaryList(index, "heroes");
            foreach (Dictionary<string, object> hero in heroes)
            {
                string id = ReadFirstString(hero, "heroStringId", "heroId", "id");
                if (string.IsNullOrWhiteSpace(id)
                    || string.Equals(id, speakerId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(id, playerId, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string name in new[] { ReadString(hero, "name", ""), id })
                {
                    string candidate = ActionPipelineService.NormalizeLookup(name);
                    if (candidate.Length >= 3 && ActionPipelineService.NormalizedTextContainsPhrase(normalizedText, candidate))
                        return true;
                }
            }
            return false;
        }

        private static bool LooksLikeArrestConfirmation(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "take him away", "take her away", "take them away", "take you away",
                "guards take him", "guards take her", "guards take them", "guards take you",
                "to the dungeon", "to prison", "do it guards", "proceed with the arrest",
                "carry out the arrest", "the arrest stands", "take the prisoner away");
        }

        private static bool LooksLikeRescission(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "rescind the accusation", "withdraw the accusation", "clear his name",
                "clear her name", "clear their name", "clear your name", "charges are dropped",
                "drop the charges", "pardon and clear", "accusation was false", "exonerate");
        }

        private static bool LooksLikeRelease(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "release him", "release her", "release them", "release you",
                "free him", "free her", "free them", "free you", "let him out",
                "let her out", "let them out", "leave the dungeon");
        }

        private static bool LooksLikeDuel(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "nonlethal duel", "non lethal duel", "duel for your surrender",
                "duel for the arrest", "fight me for your freedom", "settle this by duel");
        }

        private static bool LooksLikePlayerPartyAttack(string normalized)
        {
            return ActionPipelineService.ContainsAnyNormalized(normalized,
                "order my men to attack", "order our men to attack", "my men attack",
                "our men attack", "troops attack", "party attack", "attack their party",
                "attack his party", "attack her party", "men seize them by force");
        }

        private static bool ArrestReplyAccepts(string reply)
        {
            string normalized = ActionPipelineService.NormalizeLookup(reply);
            if (ActionPipelineService.ContainsAnyNormalized(normalized, "refuse", "will not", "wont", "never surrender",
                "fight you", "resist", "defy", "not surrender")) return false;
            return ActionPipelineService.ContainsAnyNormalized(normalized, "surrender", "submit", "yield", "i accept",
                "very well", "i will go", "take me", "i comply", "agreed", "duel", "accept your challenge");
        }

        private static string ExtractArrestReason(string playerText)
        {
            string text = (playerText ?? string.Empty).Trim();
            if (text.Length == 0) return string.Empty;
            string lower = text.ToLowerInvariant();
            foreach (string marker in new[]
            {
                " because ", " for the crime of ", " for crimes of ", " for ",
                " on the charge of ", " on charges of ", " accused of ", " accusation of "
            })
            {
                int index = lower.LastIndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                {
                    string reason = text.Substring(index + marker.Length).Trim(' ', '.', ',', ';', ':', '!', '?');
                    if (reason.Length >= 3) return DialogueService.LimitText(reason, 500);
                }
            }
            foreach (string charge in new[]
            {
                "treason", "espionage", "spying", "spy", "murder", "assassination",
                "sabotage", "rebellion", "corruption", "fraud", "theft", "assault",
                "conspiracy", "conspiring", "stole", "disorder", "trespass", "insult"
            })
            {
                int index = lower.IndexOf(charge, StringComparison.Ordinal);
                if (index >= 0)
                    return DialogueService.LimitText(text.Substring(index).Trim(' ', '.', ',', ';', ':', '!', '?'), 500);
            }
            return string.Empty;
        }

        private static string ClassifyArrestSeverity(string accusation)
        {
            string value = ActionPipelineService.NormalizeLookup(accusation);
            if (IsCapitalArrestAccusation(value))
                return "capital";
            if (ActionPipelineService.ContainsAnyNormalized(value, "murder", "assassination", "espionage", "spying",
                "foreign spy", "sabotage", "rebellion")) return "grave";
            if (ActionPipelineService.ContainsAnyNormalized(value, "theft", "fraud", "corruption", "assault",
                "stole", "conspiracy", "conspiring", "bribery", "kidnapping")) return "serious";
            return "minor";
        }

        private static bool IsCapitalArrestAccusation(string normalizedAccusation) =>
            ActionPipelineService.ContainsAnyNormalized(normalizedAccusation,
                "treason", "regicide", "betray the realm", "wartime betrayal");

        private static string ClassifyArrestCharge(string accusation)
        {
            string value = ActionPipelineService.NormalizeLookup(accusation);
            if (IsCapitalArrestAccusation(value)) return "treason";
            if (ActionPipelineService.ContainsAnyNormalized(value, "espionage", "spying", "foreign spy", "spy")) return "espionage";
            if (ActionPipelineService.ContainsAnyNormalized(value, "murder", "assassination")) return "murder";
            if (ActionPipelineService.ContainsAnyNormalized(value, "sabotage")) return "sabotage";
            if (ActionPipelineService.ContainsAnyNormalized(value, "rebellion")) return "rebellion";
            if (ActionPipelineService.ContainsAnyNormalized(value, "theft", "stole", "fraud", "corruption", "bribery")) return "property_corruption";
            if (ActionPipelineService.ContainsAnyNormalized(value, "assault", "kidnapping")) return "violence";
            if (ActionPipelineService.ContainsAnyNormalized(value, "conspiracy", "conspiring")) return "conspiracy";
            return "other";
        }

        private static string ArrestExposedAgentEvidenceId(Dictionary<string, object> assignment, string accusedId)
        {
            if (ReadString(assignment, "agent_hero_id", "") != accusedId
                || ReadString(assignment, "status", "") != "exposed")
                return null;
            return "foreign_agent:" + accusedId + ":" + ReadString(assignment, "sponsor_kingdom_id", "unknown");
        }

        private static int ArrestRelationshipCorrection(string operation,
            int desiredDelta, int priorDelta)
        {
            return string.Equals(operation, "rescind", StringComparison.OrdinalIgnoreCase)
                ? -priorDelta : desiredDelta - priorDelta;
        }

        private static List<Dictionary<string, object>> RunArrestSystemSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (name, passed, detail) => results.Add(
                new Dictionary<string, object>
                {
                    ["name"] = "arrest_" + name, ["passed"] = passed, ["detail"] = detail
                });
            Dictionary<string, object> context = new Dictionary<string, object>
            {
                ["available"] = true, ["contextKind"] = "player_settlement",
                ["activeCaseId"] = "", ["phase"] = "", ["heldByPlayer"] = false
            };
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["playerHeroStringId"] = "player", ["heroStringId"] = "npc",
                ["arrestContext"] = context
            };
            Dictionary<string, object> hero = new Dictionary<string, object>
            {
                ["heroStringId"] = "npc", ["name"] = "Raganvad"
            };
            payload["actionResolutionIndex"] = new Dictionary<string, object>
            {
                ["heroes"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "npc", ["name"] = "Raganvad"
                    },
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "third_party", ["name"] = "Caladog"
                    }
                }
            };
            Action<string, string, string, string, bool> language =
                (id, phase, playerText, expectedCommand, heldByPlayer) =>
                {
                    context["activeCaseId"] = string.IsNullOrWhiteSpace(phase)
                        ? string.Empty : "case_1";
                    context["phase"] = phase ?? string.Empty;
                    context["heldByPlayer"] = heldByPlayer;
                    string visibleReply = id == "AR-LANG-027"
                        ? "Very well, I accept your challenge."
                        : id == "AR-LANG-028"
                            ? "I refuse and will not accept your challenge."
                            : "You will regret this.";
                    List<Dictionary<string, object>> candidates =
                        BuildArrestDialogueCandidates(payload, hero, playerText, visibleReply);
                    string actual = ReadString(candidates.FirstOrDefault(), "command", "");
                    bool passed = string.IsNullOrWhiteSpace(expectedCommand)
                        ? candidates.Count == 0
                        : candidates.Count == 1
                            && string.Equals(actual, expectedCommand,
                                StringComparison.OrdinalIgnoreCase);
                    add("language_" + id, passed, playerText + " expected="
                        + expectedCommand + " actual=" + actual);
                };

            language("AR-LANG-001", "", "Guards, arrest her for treason.", "prepare_arrest", false);
            language("AR-LANG-002", "", "Captain, please place him in custody for corruption.", "prepare_arrest", false);
            language("AR-LANG-003", "", "Have the guards take him to the dungeon because he is a foreign spy.", "prepare_arrest", false);
            language("AR-LANG-004", "", "Put her in prison on charges of sabotage.", "prepare_arrest", false);
            language("AR-LANG-005", "", "Take them away for conspiring against the crown.", "prepare_arrest", false);
            language("AR-LANG-006", "", "Seize this lord for rebellion against the realm.", "prepare_arrest", false);
            language("AR-LANG-007", "", "Lock Raganvad up for fraud against the treasury.", "prepare_arrest", false);
            language("AR-LANG-008", "", "I am taking you into custody for murder.", "prepare_arrest", false);
            language("AR-LANG-009", "", "I want the guard to detain her on the charge of bribery.", "prepare_arrest", false);
            language("AR-LANG-010", "", "Lock him up; he stole from the guild.", "prepare_arrest", false);
            language("AR-LANG-011", "awaiting_confirmation", "Guards, take her away.", "confirm_arrest", false);
            language("AR-LANG-012", "awaiting_confirmation", "Take him to the dungeon now.", "confirm_arrest", false);
            language("AR-LANG-013", "awaiting_confirmation", "Proceed with the arrest and put them in prison.", "confirm_arrest", false);
            language("AR-LANG-014", "awaiting_confirmation", "The arrest stands; carry it out.", "confirm_arrest", false);
            language("AR-LANG-015", "awaiting_confirmation", "Do it, guards.", "confirm_arrest", false);
            language("AR-LANG-016", "", "Do not arrest her for treason.", "", false);
            language("AR-LANG-017", "", "Don't take him to prison.", "", false);
            language("AR-LANG-018", "", "If I arrest him, what happens?", "", false);
            language("AR-LANG-019", "", "Would you arrest someone for treason?", "", false);
            language("AR-LANG-020", "", "Should I put her in the dungeon?", "", false);
            language("AR-LANG-021", "", "He said, 'arrest her for spying,' but I gave no order.", "", false);
            language("AR-LANG-022", "", "Guards, arrest her.", "", false);
            language("AR-LANG-023", "", "Guards, arrest Caladog for treason.", "", false);
            language("AR-LANG-024", "awaiting_confirmation", "No, cancel that arrest order.", "", false);
            language("AR-LANG-025", "", "Not her—arrest Caladog for treason instead.", "", false);
            language("AR-LANG-026", "refused", "I order my men to attack his party and take him alive.", "player_attack_party", false);
            language("AR-LANG-027", "refused", "Settle this by a nonlethal duel for your surrender.", "duel_player", false);
            language("AR-LANG-028", "refused", "Settle this by a nonlethal duel for your surrender.", "", false);
            language("AR-LANG-029", "captured", "Release her from the dungeon.", "release_arrested_character", true);
            language("AR-LANG-030", "captured", "Let him out, but leave the accusation on record.", "release_arrested_character", true);
            language("AR-LANG-031", "captured", "Free them from custody.", "release_arrested_character", true);
            language("AR-LANG-032", "captured", "I rescind the accusation and clear her name.", "rescind_arrest_accusation", true);
            language("AR-LANG-033", "captured", "Drop the charges and release him.", "rescind_arrest_accusation", true);
            language("AR-LANG-034", "captured", "The accusation was false; exonerate them.", "rescind_arrest_accusation", true);
            language("AR-LANG-035", "", "Have them detained for assault.", "prepare_arrest", false);
            language("AR-LANG-036", "", "The lady should be placed in custody for conspiracy; see it done.", "prepare_arrest", false);

            // Keep these term-pair regressions separate from the fixed certification utterance count.
            context["activeCaseId"] = string.Empty;
            context["phase"] = string.Empty;
            context["heldByPlayer"] = false;
            foreach (var (id, accusation, category, severity) in new[]
            {
                ("treason", "treason", "treason", "capital"),
                ("regicide", "regicide", "treason", "capital"),
                ("betray_the_realm", "a plan to betray the realm", "treason", "capital"),
                ("wartime_betrayal", "wartime betrayal", "treason", "capital"),
                ("capital_and_murder", "wartime betrayal and murder", "treason", "capital"),
                ("capital_and_espionage", "espionage and a plan to betray the realm", "treason", "capital"),
                ("ordinary_murder", "murder", "murder", "grave"),
                ("ordinary_espionage", "espionage", "espionage", "grave")
            })
            {
                List<Dictionary<string, object>> candidates = BuildArrestDialogueCandidates(
                    payload, hero, "Guards, arrest her for " + accusation + ".", "You will regret this.");
                var candidate = candidates.FirstOrDefault();
                var terms = ReadDictionary(candidate, "terms");
                add("charge_terms_" + id, candidates.Count == 1
                    && ReadString(candidate, "command", "") == "prepare_arrest"
                    && ReadString(candidate, "TargetHero", "") == "npc"
                    && ReadString(terms, "accusation", "") == accusation
                    && ReadString(terms, "chargeCategory", "") == category
                    && ReadString(terms, "severity", "") == severity,
                    "Arrest proposal binds the exact accusation, target, category and severity.");
            }

            add("severity_table", ClassifyArrestSeverity("treason") == "capital"
                && ClassifyArrestSeverity("foreign espionage") == "grave"
                && ClassifyArrestSeverity("corruption") == "serious"
                && ClassifyArrestSeverity("trespass") == "minor",
                "All four charge tiers are deterministic.");
            add("relationship_first_apply", ArrestRelationshipCorrection("apply", -30, 0) == -30,
                "The first policy application applies the complete selected cell.");
            add("relationship_retry_idempotent", ArrestRelationshipCorrection("apply", -30, -30) == 0,
                "Repeating one case does not duplicate its relationship effect.");
            add("relationship_reclassification", ArrestRelationshipCorrection("apply", 0, -30) == 30,
                "Later evidence changes only the difference between policy cells.");
            add("relationship_rescission", ArrestRelationshipCorrection("rescind", 0, -30) == 30,
                "Rescission removes the arrest case's prior relationship effect.");
            var exposure = new Dictionary<string, object>
            {
                ["agent_hero_id"] = "npc", ["sponsor_kingdom_id"] = "enemy", ["status"] = "exposed"
            };
            add("evidence_exact_exposed_agent_accepted",
                ArrestExposedAgentEvidenceId(exposure, "npc") == "foreign_agent:npc:enemy",
                "A retained exposure for the exact agent supplies a public espionage evidence ID.");
            add("evidence_wrong_actor_or_unexposed_rejected",
                ArrestExposedAgentEvidenceId(exposure, "victim") == null
                && ArrestExposedAgentEvidenceId(exposure, "NPC") == null
                && ArrestExposedAgentEvidenceId(new Dictionary<string, object>(exposure) { ["status"] = "active" }, "npc") == null,
                "Another hero, a case alias or an active hidden assignment cannot establish cause.");
            var unsupportedEvidence = new Dictionary<string, object>
            {
                ["campaignId"] = "fixture", ["timelineId"] = "main", ["accusedHeroId"] = "npc",
                ["chargeCategory"] = "murder", ["officialFinding"] = true, ["witnessedActorHeroId"] = "npc"
            };
            add("evidence_wrong_or_legacy_charge_unsupported",
                !ReadBool(ArrestEvidenceEvaluateApi(unsupportedEvidence), "causeEstablished", true)
                && ReadString(ArrestEvidenceEvaluateApi(new Dictionary<string, object>(unsupportedEvidence)
                    { ["chargeCategory"] = "" }), "evidenceStatus", "") == "unsupported",
                "Generic history flags cannot supply a charge. Missing categories retain the unsupported result.");
            var treasonEvidence = ArrestEvidenceEvaluateApi(new Dictionary<string, object>(unsupportedEvidence)
            {
                ["schema"] = "reign-arrest-evidence-request-v2", ["chargeCategory"] = "treason"
            });
            add("evidence_treason_has_no_authoritative_producer",
                ReadBool(treasonEvidence, "ok", false)
                && ReadString(treasonEvidence, "schema", "") == "reign-arrest-evidence-response-v2"
                && ReadString(treasonEvidence, "campaignId", "") == "fixture"
                && ReadString(treasonEvidence, "timelineId", "") == "main"
                && ReadString(treasonEvidence, "accusedHeroId", "") == "npc"
                && ReadString(treasonEvidence, "chargeCategory", "") == "treason"
                && !ReadBool(treasonEvidence, "causeEstablished", true)
                && ReadString(treasonEvidence, "evidenceStatus", "") == "unsupported"
                && treasonEvidence["evidenceIds"] is List<string> ids && ids.Count == 0
                && ReadDictionaryList(treasonEvidence, "evidence").Count == 0,
                "A generic finding or named actor supplies no public treason evidence.");
            add("evidence_espionage_requires_explicit_scope",
                ReadString(ArrestEvidenceEvaluateApi(new Dictionary<string, object>(unsupportedEvidence)
                    { ["chargeCategory"] = "espionage", ["timelineId"] = "" }), "evidenceStatus", "") == "unsupported",
                "An espionage request without its timeline cannot read a fallback campaign scope.");
            return results;
        }
    }
}
