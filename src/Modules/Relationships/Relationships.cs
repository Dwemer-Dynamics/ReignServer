using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        internal static readonly string[] RelationshipFacetKeys =
        {
            "trust", "respect", "affection", "loyalty", "fear", "resentment", "rivalry",
            "envy", "attraction", "jealousy", "dependence", "interest_alignment", "debt"
        };

        private static readonly HashSet<string> RelationshipMilestoneKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "deep_friendship", "confidant", "in_love", "devoted", "sworn_bond",
            "reconciled", "heartbroken", "estranged", "betrayed", "nemesis", "feud"
        };

        private static readonly HashSet<string> RelationshipPressureKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "humiliation", "suspected_betrayal", "succession_threat", "romantic_displacement",
            "status_threat", "exposure_blackmail", "divided_loyalty", "ambition_collision",
            "abandonment_fear", "unpaid_obligation", "strategic_seduction", "affair_exposure", "marital_discontent"
        };

        internal static void EnsureRelationshipAndCorrespondenceSchema(ReignDbConnection connection)
        {
            RelationshipsSchema.CreateRelationshipInteractionReceipts(connection);

            RelationshipsSchema.CreateCorrespondenceThreads(connection);
            RelationshipsSchema.CreateLetters(connection);
            RelationshipsSchema.CreateIndexLettersDelivery(connection);
            RelationshipsSchema.CreateIndexLettersRecipient(connection);
            // Defaults identify existing rows without changing their accepted ETA.
            RelationshipsSchema.EnsureColumnLettersTransitPolicyVersion(connection);
            RelationshipsSchema.EnsureColumnLettersBaseTransitDays(connection);
            DialogueService.EnsureCorrespondenceMemorySchema(connection);
            EnsureCorrespondenceDeliverySchema(connection);
            DialogueService.EnsureConversationMotiveSchema(connection);
            EnsureRelationshipDirectorSchema(connection);
        }

        internal static Dictionary<string, object> RelationshipEvaluateApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            lock (CampaignRelationshipWriteLock(campaignId))
                return RelationshipEvaluateApiLocked(payload);
        }

        private static Dictionary<string, object> RelationshipEvaluateApiLocked(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string eventId = FirstNonEmpty(ReadFirstString(payload, "eventId", "id"), "rel_evt_" + Guid.NewGuid().ToString("N"));
            string subjectId = ReadFirstString(payload, "subjectId", "subject_id", "npcId", "heroStringId");
            string targetId = ReadFirstString(payload, "targetId", "target_id", "playerId");
            if (string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(targetId) || subjectId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Distinct subjectId and targetId are required." };
            }

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                Dictionary<string, object> applied = RelationshipEvaluateCore(connection, payload, campaignId, eventId, subjectId, targetId);
                if (!ReadBool(applied, "idempotent", false))
                    CompleteRelationshipEvaluationReputation(connection, campaignId, ReadString(payload, "timelineId", "main"),
                        targetId, ReadDouble(applied, "worldDay", 0d), eventId, ReadInt(applied, "nativeRelationDelta", 0));
                return applied;
            }
        }

        // Connection-scoped directional evaluation. It may run inside a caller-owned
        // transaction; court-popularity recompute and job scheduling stay with the caller.
        private static Dictionary<string, object> RelationshipEvaluateCore(ReignDbConnection connection,
            Dictionary<string, object> payload, string campaignId, string eventId, string subjectId, string targetId)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double worldDay = ReadDouble(payload, "worldDay", ReadDouble(payload, "campaignDay", 0d));
            {
                Dictionary<string, object> priorEvaluation = RelationshipsStore.SelectRelationshipInteractionReceipts2(connection, new Dictionary<string, object> { ["event"] = eventId, ["subject"] = subjectId, ["target"] = targetId }).FirstOrDefault();
                if (priorEvaluation != null)
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["idempotent"] = true, ["eventId"] = eventId,
                        ["subjectId"] = subjectId, ["targetId"] = targetId,
                        ["nativeRelationDelta"] = ReadInt(priorEvaluation, "native_delta", 0),
                        ["relationshipModel"] = "native_relation_only",
                        ["facetsChanged"] = 0, ["mechanicalPassiveEvents"] = false, ["llmCalls"] = 0,
                        ["worldDay"] = ReadDouble(priorEvaluation, "world_day", 0d),
                        ["reason"] = ReadString(priorEvaluation, "reason", "")
                    };
                }
                int nativeDelta = DetermineSmallNativeRelationshipDelta(payload);
                string pairKey = AmbientPairKey(subjectId, targetId);
                Dictionary<string, object> chemistry = RelationshipsStore.SelectRelationshipPairChemistry8(connection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                if (chemistry != null && nativeDelta != 0)
                {
                    string timelineId = ReadString(payload, "timelineId", "main");
                    chemistry = ApplyAtomicDirectionalRelationshipDelta(
                        connection, campaignId, subjectId, targetId,
                        nativeDelta, worldDay, "direct_interaction",
                        timelineId) ?? chemistry;
                    RecordRelationshipPairProvenance(connection, campaignId, timelineId,
                        pairKey, "direct_interaction", false, worldDay);
                }
                else if (chemistry == null && nativeDelta != 0)
                {
                    ApplyAuthoritativeRelationshipDelta(connection, campaignId,
                        subjectId, targetId, nativeDelta, worldDay,
                        "direct_interaction",
                        ReadString(payload, "timelineId", "main"));
                    RecordRelationshipPairProvenance(connection, campaignId,
                        ReadString(payload, "timelineId", "main"), pairKey,
                        "direct_interaction", false, worldDay);
                }
                Dictionary<string, object> applied = new Dictionary<string, object>
                {
                    ["ok"] = true, ["subjectId"] = subjectId, ["targetId"] = targetId,
                    ["nativeRelationDelta"] = nativeDelta, ["relationshipModel"] = "native_relation_only",
                    ["facetsChanged"] = 0, ["mechanicalPassiveEvents"] = false,
                    ["llmCalls"] = 0, ["worldDay"] = worldDay,
                    ["reason"] = FirstNonEmpty(ReadFirstString(payload, "summary", "reason", "body"),
                        ReadFirstString(payload, "eventType", "type"))
                };
                applied["eventId"] = eventId;
                RelationshipsStore.InsertRelationshipInteractionReceipts(connection, new Dictionary<string, object>
                    {
                        ["event"] = eventId, ["subject"] = subjectId, ["target"] = targetId,
                        ["delta"] = nativeDelta, ["day"] = worldDay,
                        ["reason"] = ReadString(applied, "reason", ""),
                        ["source"] = ReadFirstString(payload, "source", "eventType", "type"), ["ts"] = ts
                    });
                return applied;
            }
        }

        private static void CompleteRelationshipEvaluationReputation(ReignDbConnection connection, string campaignId,
            string timelineId, string targetId, double worldDay, string eventId, int nativeDelta)
        {
            if (RecomputeRelationshipEvaluationReputation(connection, campaignId, timelineId,
                    targetId, worldDay, eventId, nativeDelta, schemaReady: false)
                && !campaignId.StartsWith("__", StringComparison.Ordinal))
                ReputationService.SchedulePendingReputationReasonJobs(campaignId);
        }

        // Derived court popularity and dependent public standing for one evaluated
        // target. Every write uses the supplied connection, so it joins a
        // caller-owned transaction. With schemaReady the caller has already run
        // EnsureSocialReputationSchema outside that transaction (its legacy
        // migration issues its own BEGIN). Reason scheduling stays with the caller.
        internal static bool RecomputeRelationshipEvaluationReputation(ReignDbConnection connection, string campaignId,
            string timelineId, string targetId, double worldDay, string eventId, int nativeDelta, bool schemaReady)
        {
            if (nativeDelta == 0) return false;
            if (!schemaReady) ReputationService.EnsureSocialReputationSchema(connection);
            bool popularityChanged = ReputationService.RecomputeCourtPopularityReputations(connection, campaignId,
                timelineId, targetId, worldDay, eventId + "|court_popularity");
            if (popularityChanged)
                ReputationService.ReconcileSocialRelationshipsForSubjects(connection, campaignId,
                    timelineId, new[] { targetId }, worldDay, schemaReady);
            return popularityChanged;
        }

        internal static Dictionary<string, object> RetiredRelationshipMechanicResult(string feature)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["retired"] = true, ["feature"] = feature,
                ["relationshipModel"] = "directional_mbti_native_relation",
                ["message"] = "This legacy relationship mechanic is retired. No state was changed."
            };
        }

        private static int DetermineSmallNativeRelationshipDelta(Dictionary<string, object> payload)
        {
            string source = ReadFirstString(payload, "source", "eventType", "type");
            if (source.Equals("ruler_docket", StringComparison.OrdinalIgnoreCase)
                && ReadBool(payload, "directionalOnly", false)
                && payload.ContainsKey("authoritativeDirectionalDelta"))
            {
                // Ruler-docket outcomes carry an already adjudicated rules-table
                // value. They update only the observer-to-ruler Reign affinity;
                // the Bannerlord client deliberately does not mirror this into
                // its mutual native relation. The ordinary conversational
                // override remains bounded to the smaller legacy range below.
                return Clamp(ReadInt(payload, "authoritativeDirectionalDelta", 0), -100, 100);
            }
            if (payload.ContainsKey("nativeRelationDeltaOverride"))
                return Clamp(ReadInt(payload, "nativeRelationDeltaOverride", 0), -4, 4);
            string signal = ReadFirstString(payload, "relationshipSignal", "polarity", "sentiment").ToLowerInvariant();
            string eventType = ReadFirstString(payload, "eventType", "type").ToLowerInvariant();
            string text = string.Join(" ", new[]
            {
                signal, eventType, ReadFirstString(payload, "summary", "reason", "body", "text")
            }).ToLowerInvariant();
            string[] strongPositive = { "saved my life", "saved their life", "married", "great favor", "deep gratitude", "loyal support" };
            string[] strongNegative = { "betray", "murder", "execut", "humiliat", "affair discovered", "grave insult" };
            string[] positive = { "thank", "kind", "help", "favor", "gift", "support", "praise", "agree", "warm", "friendly", "apolog" };
            string[] negative = { "insult", "threat", "refus", "hostile", "cruel", "deceiv", "lie", "angry", "mock", "condemn" };
            if (strongNegative.Any(text.Contains)) return -2;
            if (strongPositive.Any(text.Contains)) return 2;
            int score = positive.Count(text.Contains) - negative.Count(text.Contains);
            return score > 0 ? 1 : score < 0 ? -1 : 0;
        }

        private static Dictionary<string, object> EnsureRelationshipState(ReignDbConnection connection, string campaignId, string subjectId, string targetId, Dictionary<string, object> payload, long ts)
        {
            Dictionary<string, object> existing = RelationshipsStore.SelectRelationships3(connection, new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId }).FirstOrDefault();
            if (existing != null) return existing;

            Dictionary<string, object> subject = Program.ReadJsonObject(Program.CharacterFile(campaignId, subjectId, "profile.json"));
            Dictionary<string, object> target = Program.ReadJsonObject(Program.CharacterFile(campaignId, targetId, "profile.json"));
            Dictionary<string, object> suppliedSubject = ReadDictionary(payload, "subject") ?? new Dictionary<string, object>();
            Dictionary<string, object> suppliedTarget = ReadDictionary(payload, "target") ?? new Dictionary<string, object>();
            if(subject.Count==0)subject=new Dictionary<string, object>(suppliedSubject,StringComparer.OrdinalIgnoreCase);
            if(target.Count==0)target=new Dictionary<string, object>(suppliedTarget,StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> subjectTraitDocument = Program.ReadJsonObject(Program.CharacterFile(campaignId, subjectId, "traits.json"));
            if (subjectTraitDocument.Count == 0) subjectTraitDocument = ReadDictionary(suppliedSubject, "traits") ?? CharactersService.BuildTraitDocument(suppliedSubject);
            Dictionary<string, object> targetTraitDocument = Program.ReadJsonObject(Program.CharacterFile(campaignId, targetId, "traits.json"));
            if (targetTraitDocument.Count == 0) targetTraitDocument = ReadDictionary(suppliedTarget, "traits") ?? CharactersService.BuildTraitDocument(suppliedTarget);
            int nativeRelation = Clamp(ReadInt(payload, "nativeRelation", ReadInt(payload, "relation", 0)), -100, 100);
            bool spouse = ReadString(subject, "spouseId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase);
            bool family = spouse
                || ReadString(subject, "fatherId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase)
                || ReadString(subject, "motherId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase)
                || ReadString(target, "fatherId", "").Equals(subjectId, StringComparison.OrdinalIgnoreCase)
                || ReadString(target, "motherId", "").Equals(subjectId, StringComparison.OrdinalIgnoreCase);
            bool sameClan = !string.IsNullOrWhiteSpace(ReadString(subject, "clanId", "")) && ReadString(subject, "clanId", "").Equals(ReadString(target, "clanId", ""), StringComparison.OrdinalIgnoreCase);
            bool sameKingdom = !string.IsNullOrWhiteSpace(ReadString(subject, "kingdomId", "")) && ReadString(subject, "kingdomId", "").Equals(ReadString(target, "kingdomId", ""), StringComparison.OrdinalIgnoreCase);

            Dictionary<string, double> initial = BuildInitialRelationshipFacets(subjectId, targetId, subject, target,
                subjectTraitDocument, targetTraitDocument, nativeRelation, spouse, family, sameClan, sameKingdom);

            Dictionary<string, object> args = new Dictionary<string, object>
            {
                ["subject"] = subjectId, ["target"] = targetId, ["native"] = nativeRelation,
                ["confidence"] = Json.Serialize(RelationshipFacetKeys.ToDictionary(key => key, key => 0.30d)),
                ["evidence"] = Json.Serialize(new Dictionary<string, object> { ["initialization"] = new[] { spouse ? "spouse" : family ? "family" : sameClan ? "same_clan" : sameKingdom ? "same_kingdom" : "native_relation_and_personality" } }),
                ["ts"] = ts, ["payload"] = Json.Serialize(new Dictionary<string, object> { ["initializedFrom"] = "evidence_weighted_prior" })
            };
            foreach (string key in RelationshipFacetKeys) args[key] = ClampRelationship(initial[key]);
            RelationshipsStore.InsertRelationships(connection, args);
            return RelationshipsStore.SelectRelationships3(connection, new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId }).First();
        }

        private static Dictionary<string, double> BuildInitialRelationshipFacets(string subjectId, string targetId,
            Dictionary<string, object> subject, Dictionary<string, object> target,
            Dictionary<string, object> subjectTraits, Dictionary<string, object> targetTraits,
            int nativeRelation, bool spouse, bool family, bool sameClan, bool sameKingdom)
        {
            Dictionary<string, double> facets = RelationshipFacetKeys.ToDictionary(key => key, key => 0d, StringComparer.OrdinalIgnoreCase);
            double socialTrust = RelationshipTraitPercent(subjectTraits, "socialTrust");
            double authority = RelationshipTraitPercent(subjectTraits, "authorityRespect");
            double loyalty = RelationshipTraitPercent(subjectTraits, "loyalty");
            double fearfulness = RelationshipTraitPercent(subjectTraits, "fearfulness");
            double courage = RelationshipTraitPercent(subjectTraits, "courage");
            double vengefulness = RelationshipTraitPercent(subjectTraits, "vengefulness");
            double ambition = RelationshipTraitPercent(subjectTraits, "ambition");
            double pride = RelationshipTraitPercent(subjectTraits, "pride");
            double envy = RelationshipTraitPercent(subjectTraits, "envy");
            double jealousy = RelationshipTraitPercent(subjectTraits, "jealousy");
            double flirtatiousness = RelationshipTraitPercent(subjectTraits, "flirtatiousness");
            double sociability = RelationshipTraitPercent(subjectTraits, "sociability");
            double survival = RelationshipTraitPercent(subjectTraits, "survivalMotivation");
            double targetHonesty = RelationshipTraitPercent(targetTraits, "honesty");
            double targetCompassion = RelationshipTraitPercent(targetTraits, "compassion");
            double targetTact = RelationshipTraitPercent(targetTraits, "tact");
            double targetDiscipline = RelationshipTraitPercent(targetTraits, "discipline");
            double targetCourage = RelationshipTraitPercent(targetTraits, "courage");
            double targetAggression = RelationshipTraitPercent(targetTraits, "aggression");
            double targetAssertiveness = RelationshipTraitPercent(targetTraits, "assertiveness");
            double targetAmbition = RelationshipTraitPercent(targetTraits, "ambition");
            double targetPride = RelationshipTraitPercent(targetTraits, "pride");
            double targetSociability = RelationshipTraitPercent(targetTraits, "sociability");
            double targetPragmatism = RelationshipTraitPercent(targetTraits, "pragmatism");
            double subjectPragmatism = RelationshipTraitPercent(subjectTraits, "pragmatism");
            double subjectStatus = RelationshipStatusScore(subject);
            double targetStatus = RelationshipStatusScore(target);
            double relativeStatus = Program.ClampDouble((targetStatus - subjectStatus) / 50d, -1d, 1d);
            double targetAttractiveness = RelationshipVisibleAttractiveness(target, targetTraits);
            double subjectAttractiveness = RelationshipVisibleAttractiveness(subject, subjectTraits);
            double affinity = Program.ClampDouble(
                0.25d * (1d - Math.Abs(sociability - targetSociability) / 100d)
                + 0.20d * (1d - Math.Abs(subjectPragmatism - targetPragmatism) / 100d)
                + 0.25d * (targetCompassion / 100d) + 0.15d * (targetTact / 100d)
                + 0.15d * (targetHonesty / 100d), 0d, 1d) * 2d - 1d;
            string seed = "relationship_chemistry_v2|" + subjectId + "|" + targetId;
            double trustRoll = StableUnit(seed + "|trust") * 2d - 1d;
            double socialRoll = StableUnit(seed + "|social") * 2d - 1d;
            double competitionRoll = StableUnit(seed + "|competition") * 2d - 1d;
            double romanceRoll = StableUnit(seed + "|romance") * 2d - 1d;
            bool romanticSuitability = !family
                && ReadDouble(subject, "age", 0d) >= 18d && ReadDouble(target, "age", 0d) >= 18d
                && ReadBool(subject, "isFemale", false) != ReadBool(target, "isFemale", false);

            facets["trust"] = 0.35d * nativeRelation + 0.20d * (socialTrust - 50d) + 5d * affinity + 7d * trustRoll;
            facets["respect"] = 0.25d * nativeRelation + 0.16d * (authority - 50d) + 12d * relativeStatus
                + 0.05d * (targetDiscipline - 50d) + 0.04d * (targetCourage - 50d) + 5d * socialRoll;
            facets["affection"] = 0.40d * nativeRelation + (spouse ? 35d : family ? 20d : 0d) + 7d * affinity + 6d * socialRoll;
            facets["loyalty"] = 0.25d * nativeRelation + 0.20d * (loyalty - 50d) + (sameClan ? 20d : family ? 15d : sameKingdom ? 8d : 0d) + 4d * affinity;
            facets["fear"] = 0.20d * (fearfulness - 50d) - 0.12d * (courage - 50d) + 16d * relativeStatus
                + 0.10d * (targetAggression - 50d) + 0.08d * (targetAssertiveness - 50d) + 5d * competitionRoll;
            facets["resentment"] = Math.Max(0d, nativeRelation < 0 ? Math.Abs(nativeRelation) * 0.40d : 0d)
                + Math.Max(0d, 0.10d * (vengefulness - 50d) - 7d * affinity + 7d * relativeStatus + 4d * competitionRoll);
            facets["rivalry"] = Math.Max(0d, 0.12d * (ambition - 50d) + 0.10d * (pride - 50d)
                + 0.07d * (targetAmbition - 50d) + 0.05d * (targetPride - 50d)
                + (sameKingdom ? 7d : 2d) + 7d * (1d - Math.Abs(relativeStatus)) + 6d * competitionRoll);
            facets["envy"] = 0.18d * (envy - 50d) + Math.Max(0d, 18d * relativeStatus)
                + Math.Max(0d, 0.08d * (targetAttractiveness - subjectAttractiveness)) + 4d * competitionRoll;
            facets["attraction"] = romanticSuitability
                ? -6d + 0.22d * (targetAttractiveness - 50d) + 0.12d * (flirtatiousness - 50d)
                    + 0.06d * (sociability - 50d) + 15d * romanceRoll + 8d * relativeStatus
                : 0d;
            facets["jealousy"] = 0.12d * (jealousy - 50d) + Math.Max(0d, 8d * relativeStatus)
                + Math.Max(0d, 0.06d * (targetAttractiveness - subjectAttractiveness)) + 3d * competitionRoll;
            facets["dependence"] = (spouse ? 10d : 0d) + Math.Max(0d, relativeStatus * (4d + 0.08d * survival));
            facets["interest_alignment"] = (sameClan ? 25d : sameKingdom ? 12d : 0d) + 8d * affinity;
            facets["debt"] = 0d;
            foreach (string key in RelationshipFacetKeys) facets[key] = ClampRelationship(facets[key]);
            facets["resentment"] = Math.Max(0d, facets["resentment"]);
            facets["dependence"] = Math.Max(0d, facets["dependence"]);
            facets["debt"] = Math.Max(0d, facets["debt"]);
            return facets;
        }

        internal static double RelationshipTraitPercent(Dictionary<string, object> traitDocument, string key)
        {
            Dictionary<string, object> percentages = ReadDictionary(traitDocument, "traitPercentages")
                ?? ReadDictionary(ReadDictionary(traitDocument, "traits"), "traitPercentages");
            if (percentages != null && percentages.ContainsKey(key)) return Program.ClampDouble(ReadDouble(percentages, key, 50d), 0d, 100d);
            Dictionary<string, object> foundation = ReadDictionary(traitDocument, "foundationTraits")
                ?? ReadDictionary(ReadDictionary(traitDocument, "traits"), "foundationTraits")
                ?? traitDocument ?? new Dictionary<string, object>();
            return Program.ClampDouble(50d + 25d * ReadDouble(foundation, key, 0d), 0d, 100d);
        }

        private static double RelationshipVisibleAttractiveness(Dictionary<string, object> data, Dictionary<string, object> traits)
        {
            Dictionary<string, object> appearance = ReadDictionary(data, "appearance") ?? data ?? new Dictionary<string, object>();
            Dictionary<string, object> attractiveness = ReadDictionary(traits, "attractiveness") ?? new Dictionary<string, object>();
            return Program.ClampDouble(ReadDouble(appearance, "attractiveness",
                ReadDouble(data, "visibleAttractiveness", ReadDouble(attractiveness, "weight", ReadDouble(attractiveness, "build", 50d)))), 0d, 100d);
        }

        private static double RelationshipStatusScore(Dictionary<string, object> data)
        {
            data = data ?? new Dictionary<string, object>();
            double tier = Program.ClampDouble(ReadDouble(data, "clanTier", 0d), 0d, 6d);
            double fiefs = Math.Max(0d, ReadDouble(data, "clanFiefCount", ReadDouble(data, "fiefCount", 0d)));
            double renown = Math.Max(0d, ReadDouble(data, "clanRenown", ReadDouble(data, "renown", 0d)));
            double influence = Math.Max(0d, ReadDouble(data, "influence", 0d));
            return Program.ClampDouble(tier * 9d + Math.Min(20d, fiefs * 6d) + Math.Min(15d, renown / 80d)
                + Math.Min(10d, influence / 150d) + (ReadBool(data, "isRuler", false) ? 15d : 0d), 0d, 100d);
        }

        private static void EnforceMotiveSpecificRelationshipEvaluation(Dictionary<string, object> evaluation, Dictionary<string, object> payload)
        {
            Dictionary<string, object> motive = ReadDictionary(payload, "motiveDecision") ?? new Dictionary<string, object>();
            Dictionary<string, object> romance = ReadDictionary(motive, "romance") ?? motive;
            Dictionary<string, object> constraints = ReadDictionary(romance, "hardConstraints") ?? new Dictionary<string, object>();
            string presentation = ReadString(romance, "presentation", "");
            double genuine = ReadDouble(romance, "genuineInterest", 0d);
            double strategic = ReadDouble(romance, "strategicInterest", 0d);
            bool calculated = presentation.Equals("calculated", StringComparison.OrdinalIgnoreCase) && strategic >= genuine + 15d;
            bool coercive = ReadBool(constraints, "coercive", false);
            Dictionary<string, object> deltas = ReadDictionary(evaluation, "facetDeltas") ?? new Dictionary<string, object>();
            if (calculated || coercive)
            {
                double divertedAttraction = Math.Max(2d, ReadDouble(deltas, "attraction", 0d));
                deltas["attraction"] = 0;
                if (coercive) deltas["affection"] = Math.Min(0d, ReadDouble(deltas, "affection", 0d));
                if (calculated)
                {
                    deltas["interest_alignment"] = Math.Max(divertedAttraction, ReadDouble(deltas, "interest_alignment", 0d));
                    deltas["dependence"] = Math.Max(2d, Math.Max(divertedAttraction / 2d, ReadDouble(deltas, "dependence", 0d)));
                    deltas["debt"] = Math.Max(2d, Math.Max(divertedAttraction / 3d, ReadDouble(deltas, "debt", 0d)));
                }
                evaluation["facetDeltas"] = deltas;
                List<Dictionary<string, object>> milestones = ReadDictionaryList(evaluation, "milestoneUpdates")
                    .Where(x => !ReadString(x, "kind", "").Equals("in_love", StringComparison.OrdinalIgnoreCase)).ToList();
                evaluation["milestoneUpdates"] = milestones;
            }
        }

        private static bool IsRegisteredRelationshipStoryEvent(string eventType)
        {
            switch ((eventType ?? "").ToLowerInvariant())
            {
                case "practical_favor":
                case "shared_confidence":
                case "family_duty_cooperation":
                case "private_spousal_confidence":
                case "political_rivalry_argument":
                case "political_obstruction":
                case "marital_argument":
                case "marital_separation":
                case "mutual_romantic_flirtation":
                case "romantic_confidence":
                case "romantic_intimacy":
                case "secret_affair_intimacy":
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<string, object> DeterministicRelationshipEvaluation(string eventType, string summary, Dictionary<string, object> payload, Dictionary<string, object> state)
        {
            string text = ((eventType ?? "") + " " + (summary ?? "")).ToLowerInvariant();
            bool registeredStoryEvent = IsRegisteredRelationshipStoryEvent(eventType);
            bool betrayal = Program.ContainsAny(text, "betray", "lied", "deceiv", "broke oath", "exposed", "revealed my secret");
            bool humiliation = Program.ContainsAny(text, "mock", "humiliat", "publicly shamed", "disgrace");
            bool intimacy = Program.ContainsAny(text, "love", "kiss", "intimate", "embrace", "flirt");
            bool rejection = Program.ContainsAny(text, "reject", "spurn");
            bool reconciliation = Program.ContainsAny(text, "apolog", "forgive", "reconcile");
            string impact = Program.ContainsAny(text, "betray", "murder", "saved my child", "saved her child", "saved his child", "life debt") ? "transformative"
                : Program.ContainsAny(text, "love", "intimate", "kiss", "humiliat", "rescued", "confession", "oath", "threat") ? "major" : "ordinary";
            int unit = impact == "transformative" ? 35 : impact == "major" ? 16 : 4;
            Dictionary<string, object> deltas = RelationshipFacetKeys.ToDictionary(key => key, key => (object)0, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> motive = ReadDictionary(payload, "motiveDecision") ?? new Dictionary<string, object>();
            Dictionary<string, object> romance = ReadDictionary(motive, "romance") ?? motive;
            bool calculatedRomance = ReadString(romance, "presentation", "").Equals("calculated", StringComparison.OrdinalIgnoreCase)
                && ReadDouble(romance, "strategicInterest", 0d) >= ReadDouble(romance, "genuineInterest", 0d) + 15d;
            bool coerciveRomance = ReadBool(ReadDictionary(romance, "hardConstraints"), "coercive", false);
            switch((eventType??string.Empty).ToLowerInvariant())
            {
                case "practical_favor": deltas["trust"]=4;deltas["respect"]=2;deltas["affection"]=2;deltas["debt"]=3;break;
                case "shared_confidence": deltas["trust"]=5;deltas["affection"]=3;break;
                case "family_duty_cooperation": deltas["trust"]=3;deltas["affection"]=3;deltas["loyalty"]=4;break;
                case "private_spousal_confidence": deltas["trust"]=5;deltas["affection"]=5;deltas["loyalty"]=3;break;
                case "political_rivalry_argument": deltas["respect"]=-2;deltas["affection"]=-3;deltas["resentment"]=4;deltas["rivalry"]=5;break;
                case "political_obstruction": deltas["respect"]=-3;deltas["affection"]=-4;deltas["resentment"]=6;deltas["rivalry"]=7;break;
                case "marital_argument": deltas["trust"]=-4;deltas["affection"]=-5;deltas["loyalty"]=-2;deltas["resentment"]=6;break;
                case "marital_separation": deltas["trust"]=-8;deltas["affection"]=-8;deltas["loyalty"]=-6;deltas["resentment"]=8;break;
                case "mutual_romantic_flirtation": deltas["attraction"]=5;deltas["affection"]=3;break;
                case "romantic_confidence": deltas["trust"]=4;deltas["affection"]=4;deltas["attraction"]=3;break;
                case "romantic_intimacy": deltas["attraction"]=8;deltas["affection"]=7;deltas["trust"]=3;break;
                case "secret_affair_intimacy": deltas["attraction"]=10;deltas["affection"]=8;deltas["trust"]=3;break;
            }
            bool developedRomance = ReadDouble(payload, "preEventRomanceIntensity", 0d) >= 50d
                && !calculatedRomance && !coerciveRomance
                && (ReadString(romance, "presentation", "") == "sincere"
                    || (ReadString(romance, "presentation", "") == "mixed" && ReadDouble(romance, "genuineInterest", 0d) >= 25d));
            if (developedRomance && new[] { "mutual_romantic_flirtation", "romantic_confidence", "romantic_intimacy", "secret_affair_intimacy" }
                .Contains((eventType ?? "").ToLowerInvariant(), StringComparer.OrdinalIgnoreCase))
            {
                deltas["attraction"] = ReadDouble(deltas, "attraction", 0d) + 2d;
                deltas["affection"] = ReadDouble(deltas, "affection", 0d) + 2d;
            }
            if (!registeredStoryEvent)
            {
                if (Program.ContainsAny(text, "thank", "gift", "help", "saved", "rescue", "kindness")) { deltas["trust"] = unit; deltas["affection"] = unit; deltas["loyalty"] = Math.Max(2, unit / 2); }
                if (Program.ContainsAny(text, "confession", "secret", "trusted you")) { deltas["trust"] = unit; deltas["affection"] = Math.Max(2, unit / 2); }
                if (intimacy && calculatedRomance) { deltas["interest_alignment"] = unit; deltas["dependence"] = Math.Max(2, unit / 2); deltas["debt"] = Math.Max(2, unit / 3); }
                else if (intimacy && !coerciveRomance) { deltas["affection"] = unit; deltas["attraction"] = unit; deltas["loyalty"] = Math.Max(2, unit / 2); }
                if (rejection || humiliation) { deltas["affection"] = -unit; deltas["respect"] = -unit; deltas["resentment"] = unit; }
                if (betrayal) { deltas["trust"] = -unit; deltas["loyalty"] = -unit; deltas["resentment"] = unit; deltas["respect"] = -Math.Max(2, unit / 2); }
                if (Program.ContainsAny(text, "threat", "kill you", "execute")) { deltas["fear"] = unit; deltas["trust"] = -Math.Max(2, unit / 2); deltas["resentment"] = Math.Max(2, unit / 2); }
                if (reconciliation) { deltas["resentment"] = -unit; deltas["trust"] = Math.Max(2, unit / 2); }
            }

            ArrayList milestoneUpdates = new ArrayList();
            ArrayList pressureUpdates = new ArrayList();
            Dictionary<string, object> development = new Dictionary<string, object>();
            if (betrayal)
            {
                milestoneUpdates.Add(new Dictionary<string, object> { ["kind"] = "betrayed", ["operation"] = "activate", ["stabilityDelta"] = 35, ["strength"] = 80, ["reason"] = summary });
                milestoneUpdates.Add(new Dictionary<string, object> { ["kind"] = "in_love", ["operation"] = "erode", ["stabilityDelta"] = -40, ["reason"] = summary });
                milestoneUpdates.Add(new Dictionary<string, object> { ["kind"] = "devoted", ["operation"] = "erode", ["stabilityDelta"] = -45, ["reason"] = summary });
                milestoneUpdates.Add(new Dictionary<string, object> { ["kind"] = "sworn_bond", ["operation"] = "erode", ["stabilityDelta"] = -50, ["reason"] = summary });
                pressureUpdates.Add(new Dictionary<string, object> { ["kind"] = "suspected_betrayal", ["operation"] = "create", ["intensityDelta"] = 75, ["secrecy"] = 0.6d, ["summary"] = summary, ["triggered"] = true });
                development = new Dictionary<string, object> { ["kind"] = "betrayal_confrontation", ["polarity"] = "negative", ["deliveryMode"] = "in_person", ["requiresPhysical"] = true, ["priority"] = 0.95d, ["summary"] = summary };
            }
            else if (humiliation)
            {
                pressureUpdates.Add(new Dictionary<string, object> { ["kind"] = "humiliation", ["operation"] = "create", ["intensityDelta"] = 70, ["secrecy"] = 0.35d, ["summary"] = summary, ["triggered"] = true });
                development = new Dictionary<string, object> { ["kind"] = "humiliation_reckoning", ["polarity"] = "negative", ["deliveryMode"] = "letter", ["priority"] = 0.85d, ["summary"] = summary };
            }
            else if (intimacy && !rejection && !calculatedRomance && !coerciveRomance)
            {
                milestoneUpdates.Add(new Dictionary<string, object> { ["kind"] = "in_love", ["operation"] = "activate", ["stabilityDelta"] = 25, ["strength"] = 70, ["reason"] = summary });
                development = new Dictionary<string, object> { ["kind"] = "romantic_turning_point", ["polarity"] = "positive", ["deliveryMode"] = "in_person", ["requiresPhysical"] = true, ["priority"] = 0.9d, ["summary"] = summary };
            }
            else if (intimacy && calculatedRomance)
            {
                pressureUpdates.Add(new Dictionary<string, object> { ["kind"] = "strategic_seduction", ["operation"] = "create", ["intensityDelta"] = Math.Max(20, unit), ["secrecy"] = 0.85d, ["summary"] = summary, ["triggered"] = true });
                development = new Dictionary<string, object> { ["kind"] = "strategic_romantic_entanglement", ["polarity"] = "mixed", ["deliveryMode"] = "in_person", ["requiresPhysical"] = false, ["priority"] = 0.75d, ["summary"] = summary };
            }
            else if (rejection)
            {
                milestoneUpdates.Add(new Dictionary<string, object> { ["kind"] = "heartbroken", ["operation"] = "activate", ["stabilityDelta"] = 25, ["strength"] = 65, ["reason"] = summary });
                pressureUpdates.Add(new Dictionary<string, object> { ["kind"] = "romantic_displacement", ["operation"] = "create", ["intensityDelta"] = 60, ["secrecy"] = 0.8d, ["summary"] = summary, ["triggered"] = false });
                development = new Dictionary<string, object> { ["kind"] = "romantic_rejection", ["polarity"] = "negative", ["deliveryMode"] = "letter", ["priority"] = 0.8d, ["summary"] = summary };
            }
            else if (reconciliation)
            {
                milestoneUpdates.Add(new Dictionary<string, object> { ["kind"] = "reconciled", ["operation"] = "activate", ["stabilityDelta"] = 15, ["strength"] = 55, ["reason"] = summary });
                pressureUpdates.Add(new Dictionary<string, object> { ["kind"] = "suspected_betrayal", ["operation"] = "reduce", ["intensityDelta"] = -25, ["summary"] = summary });
                pressureUpdates.Add(new Dictionary<string, object> { ["kind"] = "humiliation", ["operation"] = "reduce", ["intensityDelta"] = -20, ["summary"] = summary });
                pressureUpdates.Add(new Dictionary<string, object> { ["kind"] = "romantic_displacement", ["operation"] = "reduce", ["intensityDelta"] = -20, ["summary"] = summary });
            }

            int nativeDelta = impact == "ordinary" ? Math.Sign(deltas.Values.Sum(ValueAsDouble)) : Clamp((int)Math.Round(deltas.Values.Sum(ValueAsDouble) / 20d), -25, 15);
            if (betrayal && impact == "transformative") nativeDelta = -20;
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["impactLevel"] = impact,
                ["reasoning"] = registeredStoryEvent ? "Shared deterministic storyline mechanics applied the registered event outcome." : "Deterministic relationship fallback classified explicit event language.",
                ["facetDeltas"] = deltas,
                ["facetReasons"] = new Dictionary<string, object>(),
                ["nativeRelationDelta"] = nativeDelta,
                ["milestoneUpdates"] = milestoneUpdates,
                ["pressureUpdates"] = pressureUpdates,
                ["development"] = development
            };
            EnforceMotiveSpecificRelationshipEvaluation(result, payload);
            return result;
        }

        private static List<Dictionary<string, object>> ApplyPressureUpdates(ReignDbConnection connection, string subjectId, string targetId, string eventId, double day, long ts, Dictionary<string, object> evaluation)
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> item in ReadDictionaryList(evaluation, "pressureUpdates").Take(6))
            {
                string kind = ReadString(item, "kind", "");
                string operation = ReadString(item, "operation", "reinforce").ToLowerInvariant();
                if (!RelationshipPressureKinds.Contains(kind)) continue;
                Dictionary<string, object> existing = RelationshipsStore.SelectRelationshipPressures2(connection, new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId, ["kind"] = kind }).FirstOrDefault();
                double delta = Program.ClampDouble(ReadDouble(item, "intensityDelta", operation == "create" ? 25d : operation == "resolve" ? -100d : 10d), -100d, 100d);
                if (existing == null && operation != "resolve")
                {
                    string id = "rpr_" + Guid.NewGuid().ToString("N");
                    double intensity = Program.ClampDouble(Math.Max(0d, delta), 0d, 100d);
                    RelationshipsStore.InsertRelationshipPressures2(connection, new Dictionary<string, object> { ["id"] = id, ["subject"] = subjectId, ["target"] = targetId, ["kind"] = kind, ["intensity"] = intensity, ["secrecy"] = Program.ClampDouble(ReadDouble(item, "secrecy", 0.5d), 0d, 1d), ["summary"] = ReadString(item, "summary", ""), ["entities"] = Json.Serialize(ReadStringList(item, "relatedEntityIds")), ["trigger"] = Json.Serialize(new Dictionary<string, object> { ["triggered"] = ReadBool(item, "triggered", false) }), ["evidence"] = Json.Serialize(new[] { eventId }), ["day"] = day, ["ts"] = ts, ["payload"] = Json.Serialize(item) });
                    results.Add(new Dictionary<string, object> { ["kind"] = kind, ["operation"] = "created", ["intensity"] = intensity, ["pressureId"] = id });
                }
                else if (existing != null)
                {
                    double intensity = Program.ClampDouble(ReadDouble(existing, "intensity", 0d) + delta, 0d, 100d);
                    string status = operation == "resolve" || intensity <= 0d ? "resolved" : "active";
                    RelationshipsStore.UpdateRelationshipPressures(connection, new Dictionary<string, object> { ["status"] = status, ["intensity"] = intensity, ["summary"] = FirstNonEmpty(ReadString(item, "summary", ""), ReadString(existing, "summary", "")), ["day"] = day, ["ts"] = ts, ["payload"] = Json.Serialize(item), ["id"] = ReadString(existing, "pressure_id", "") });
                    results.Add(new Dictionary<string, object> { ["kind"] = kind, ["operation"] = status == "resolved" ? "resolved" : operation, ["intensity"] = intensity, ["pressureId"] = ReadString(existing, "pressure_id", "") });
                }
            }
            return results;
        }

        internal static Dictionary<string, object> RelationshipContextApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string subjectId = ReadFirstString(payload, "subjectId", "npcId", "heroStringId");
            string targetId = ReadFirstString(payload, "targetId", "playerId");
            double day = ReadDouble(payload, "worldDay", 0d);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                ReputationService.EnsureSocialReputationSchema(connection);
                string timelineId = ReadString(payload, "timelineId", "main");
                double standingDay = day > 0d
                    ? day
                    : WorldSimulationService.LatestKnownWorldDay(connection, campaignId);
                string pairKey = AmbientPairKey(subjectId, targetId);
                Dictionary<string, object> chemistry = RelationshipsStore.SelectRelationshipPairChemistry8(connection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                bool subjectIsA = chemistry != null && ReadString(chemistry, "hero_a_id", "")
                    .Equals(subjectId, StringComparison.OrdinalIgnoreCase);
                Dictionary<string, object> effectiveAttitude = ResolveEffectiveAttitude(
                    connection, campaignId, timelineId, subjectId, targetId,
                    ReadString(payload, "context", "relationship_context"));
                int nativeRelation = ReadInt(payload, "nativeRelation",
                    chemistry == null ? 0 : ReadInt(chemistry, "projected_native_relation", 0));
                int directionalAffinity = ReadInt(effectiveAttitude,
                    "effectiveAttitude", chemistry == null ? nativeRelation : 0);
                string observerMbti = chemistry == null ? ""
                    : ReadString(chemistry, subjectIsA ? "mbti_a" : "mbti_b", "");
                string targetMbti = chemistry == null ? ""
                    : ReadString(chemistry, subjectIsA ? "mbti_b" : "mbti_a", "");
                int compatibility = chemistry == null ? 0
                    : ReadInt(chemistry, subjectIsA ? "chance_a_to_b" : "chance_b_to_a", 0);
                Dictionary<string, object> lifecycle = RelationshipLifecycleView(connection, subjectId, targetId);
                List<Dictionary<string, object>> incidents = RelationshipsStore.SelectRelationshipIncidents(connection, new Dictionary<string, object> { ["pair"] = pairKey });
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["subjectId"] = subjectId, ["targetId"] = targetId,
                    ["nativeRelation"] = nativeRelation, ["directionalAffinity"] = directionalAffinity,
                    ["personalAffinity"] = ReadInt(effectiveAttitude, "personalAffinity", 0),
                    ["targetPublicStandingValue"] = ReadInt(effectiveAttitude, "targetPublicStanding", 0),
                    ["effectiveAttitude"] = directionalAffinity,
                    ["personalBand"] = ReadString(effectiveAttitude, "personalBand", "neutral"),
                    ["effectiveBand"] = ReadString(effectiveAttitude, "effectiveBand", "neutral"),
                    ["pairProvenance"] = effectiveAttitude.TryGetValue("provenance", out object provenance)
                        ? provenance : new List<string>(),
                    ["publicStandingRevision"] = ReadInt(effectiveAttitude, "publicStandingRevision", 1),
                    ["perceptionTag"] = RelationshipTagForObserver(connection, campaignId, subjectId, targetId, nativeRelation),
                    ["observerMbti"] = observerMbti, ["targetMbti"] = targetMbti,
                    ["compatibilityChance"] = compatibility, ["lifecycle"] = lifecycle,
                    ["subjectPublicStanding"] = ReputationService.ReadPublicStandingTrait(connection,
                        campaignId, timelineId, subjectId, standingDay),
                    ["targetPublicStanding"] = ReputationService.ReadPublicStandingTrait(connection,
                        campaignId, timelineId, targetId, standingDay),
                    ["incidents"] = incidents, ["relationshipModel"] = "mbti_directional_native_relation",
                    ["facets"] = new Dictionary<string, object>(),
                    ["milestones"] = new List<Dictionary<string, object>>(),
                    ["pressures"] = new List<Dictionary<string, object>>(),
                    ["romanceThread"] = new Dictionary<string, object>(),
                    ["worldDay"] = day
                };
            }
        }

        internal static List<Dictionary<string, object>> EvaluateCompletedActionRelationships(Dictionary<string, object> payload)
        {
            List<Dictionary<string, object>> changes = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> directions = CompletedActionRelationshipDirections(payload);
            if (directions.Count == 0) return changes;
            List<int> nativeDeltas = new List<int>();
            foreach (Dictionary<string, object> direction in directions)
            {
                Dictionary<string, object> evaluation = RelationshipEvaluateApi(direction);
                int delta = ReadInt(evaluation, "nativeRelationDelta", 0);
                if (delta != 0 && !ReadBool(evaluation, "idempotent", false)) nativeDeltas.Add(delta);
            }
            int sharedDelta = CompletedActionSharedDelta(nativeDeltas);
            if (sharedDelta != 0)
            {
                changes.Add(new Dictionary<string, object> { ["subjectId"] = ReadString(directions[0], "subjectId", ""), ["targetId"] = ReadString(directions[0], "targetId", ""), ["delta"] = sharedDelta, ["eventId"] = "action_relationship_" + ReadString(directions[0], "actionId", "") });
            }
            return changes;
        }

        // Actor-toward-target first, then the reverse. Empty when the report has no
        // relationship effect. A null timeline keeps the legacy "main" default.
        internal static List<Dictionary<string, object>> CompletedActionRelationshipDirections(Dictionary<string, object> payload, string timelineId = null)
        {
            List<Dictionary<string, object>> directions = new List<Dictionary<string, object>>();
            if (Program.IsPracticeDuelActionReport(payload)) return directions;
            string status = ReadString(payload, "status", "");
            string actorId = ReadFirstString(payload, "actorHeroStringId", "actorHeroId");
            string targetId = ReadFirstString(payload, "targetHeroStringId", "targetHeroId");
            if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(targetId) || actorId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
            {
                return directions;
            }

            string campaignId = ReadString(payload, "campaignId", "default");
            string actionId = ReadFirstString(payload, "serverActionId", "actionId", "id");
            string actionType = ReadFirstString(payload, "type", "command");
            if (IsTemporaryPartyGuestLifecycleAction(Program.CanonicalCommand(actionType)))
            {
                return directions;
            }
            string summary = FirstNonEmpty(ReadFirstString(payload, "message"), ReadFirstString(ReadDictionary(payload, "result"), "message", "debugMessage"), actionType + " completed");
            Dictionary<string, object> actor = ReadDictionary(payload, "actorHero") ?? new Dictionary<string, object>();
            Dictionary<string, object> target = ReadDictionary(payload, "targetHero") ?? new Dictionary<string, object>();
            foreach (Tuple<string, string, Dictionary<string, object>, Dictionary<string, object>> pair in new[]
            {
                Tuple.Create(actorId, targetId, actor, target),
                Tuple.Create(targetId, actorId, target, actor)
            })
            {
                Dictionary<string, object> direction = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["eventId"] = "action_relationship_" + actionId + "_" + pair.Item1 + "_toward_" + pair.Item2,
                    ["eventType"] = "completed_action_" + actionType,
                    ["subjectId"] = pair.Item1,
                    ["targetId"] = pair.Item2,
                    ["subject"] = pair.Item3,
                    ["target"] = pair.Item4,
                    ["nativeRelation"] = ReadInt(pair.Item3, "relationToPlayer", 0),
                    ["summary"] = summary,
                    ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                    ["source"] = "completed_game_action",
                    ["actionId"] = actionId
                };
                if (timelineId != null) direction["timelineId"] = timelineId;
                directions.Add(direction);
            }
            return directions;
        }

        internal static int CompletedActionSharedDelta(List<int> nativeDeltas) => nativeDeltas.Count == 0 ? 0
            : Clamp((int)Math.Round(nativeDeltas.Average(), MidpointRounding.AwayFromZero), -25, 15);

        // Opt-in terminal reports only: both directional evaluations and their
        // idempotency rows join the caller's transaction. The caller has verified
        // that no directional row predates the protocol, so none is idempotent here.
        internal static int EvaluateCompletedActionRelationshipsInTransaction(ReignDbConnection connection,
            List<Dictionary<string, object>> directions, List<Dictionary<string, object>> evaluations)
        {
            List<int> nativeDeltas = new List<int>();
            foreach (Dictionary<string, object> direction in directions)
            {
                Dictionary<string, object> evaluation = RelationshipEvaluateCore(connection, direction,
                    ReadString(direction, "campaignId", "default"), ReadString(direction, "eventId", ""),
                    ReadString(direction, "subjectId", ""), ReadString(direction, "targetId", ""));
                if (ReadBool(evaluation, "idempotent", false))
                    throw new InvalidOperationException("A protocol terminal report found a pre-existing directional relationship receipt.");
                evaluations.Add(evaluation);
                int delta = ReadInt(evaluation, "nativeRelationDelta", 0);
                if (delta != 0) nativeDeltas.Add(delta);
            }
            return CompletedActionSharedDelta(nativeDeltas);
        }

        internal static bool IsTemporaryPartyGuestLifecycleAction(string actionType)
        {
            switch ((actionType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "accept_temporary_party_guest":
                case "renew_temporary_party_guest":
                case "end_temporary_party_guest":
                case "acknowledge_own_faction_combat_risk":
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<string, object> RelationshipDevelopmentPollApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            double day = ReadDouble(payload, "worldDay", 0d);
            HashSet<string> present = new HashSet<string>(ReadStringList(payload, "presentHeroIds"), StringComparer.OrdinalIgnoreCase);
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                List<Dictionary<string, object>> candidates = RelationshipsStore.SelectRelationshipDevelopments2(connection, new Dictionary<string, object> { ["day"] = day });
                Dictionary<string, object> selected = candidates.FirstOrDefault(row =>
                {
                    string mode = ReadString(row, "delivery_mode", "in_person");
                    string subject = ReadString(row, "subject_id", "");
                    string target = ReadString(row, "target_id", "");
                    if (mode == "in_person") return present.Contains(subject) && present.Contains(target);
                    if (mode == "letter") return subject.Equals(playerId, StringComparison.OrdinalIgnoreCase) || target.Equals(playerId, StringComparison.OrdinalIgnoreCase);
                    return mode == "offscreen" && !subject.Equals(playerId, StringComparison.OrdinalIgnoreCase) && !target.Equals(playerId, StringComparison.OrdinalIgnoreCase);
                });
                if (selected == null) return new Dictionary<string, object> { ["ok"] = true, ["development"] = new Dictionary<string, object>() };
                RelationshipsStore.UpdateRelationshipDevelopments(connection, new Dictionary<string, object> { ["day"] = day, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = ReadString(selected, "development_id", "") });
                selected["status"] = "claimed";
                return new Dictionary<string, object> { ["ok"] = true, ["development"] = selected };
            }
        }

        private static Dictionary<string, object> RelationshipDevelopmentResolveApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string id = ReadFirstString(payload, "developmentId", "development_id");
            string status = ReadString(payload, "status", "resolved").ToLowerInvariant();
            if (status != "resolved" && status != "deferred" && status != "cancelled") status = "resolved";
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                RelationshipsStore.UpdateRelationshipDevelopments2(connection, new Dictionary<string, object> { ["status"] = status, ["day"] = ReadDouble(payload, "worldDay", 0d), ["payload"] = Json.Serialize(payload), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = id });
            }
            return new Dictionary<string, object> { ["ok"] = true, ["developmentId"] = id, ["status"] = status };
        }

        internal static Dictionary<string, object> CorrespondenceSendApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string senderId = ReadFirstString(payload, "senderId", "sender_id");
            string recipientId = ReadFirstString(payload, "recipientId", "recipient_id");
            string body = ReadString(payload, "body", "");
            if (string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(recipientId) || senderId.Equals(recipientId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(body))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Distinct sender, recipient, and nonempty body are required." };
            double dispatchDay = ReadDouble(payload, "dispatchDay", ReadDouble(payload, "worldDay", 0d));
            string source = ReadString(payload, "source", "player");
            bool urgentCampaignReport = source.Equals("campaign_command_urgent_report", StringComparison.OrdinalIgnoreCase);
            bool campaignReport = urgentCampaignReport
                || source.Equals("campaign_command_report", StringComparison.OrdinalIgnoreCase);
            double deliveryDay = campaignReport
                ? dispatchDay
                : Math.Max(dispatchDay + 0.25d, ReadDouble(payload, "deliveryDay", dispatchDay + 3d));
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                string reason = ReadString(payload, "reason", "");
                if ((source.Equals("court_economic_transfer", StringComparison.OrdinalIgnoreCase)
                        || campaignReport || IsDeterministicMarriageLetter(source))
                    && !string.IsNullOrWhiteSpace(reason))
                {
                    Dictionary<string, object> existing = RelationshipsStore.SelectLetters8(connection, new Dictionary<string, object> { ["sender"] = senderId, ["recipient"] = recipientId, ["source"] = source, ["reason"] = reason })
                        .FirstOrDefault(row => !IsDeterministicMarriageLetter(source)
                            || CorrespondenceDispatchOnTimeline(connection, row, ReadString(payload, "timelineId", "main")));
                    if (existing != null)
                    {
                        return new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["deduplicated"] = true,
                            ["letterId"] = ReadString(existing, "letter_id", ""),
                            ["threadId"] = ReadString(existing, "thread_id", ""),
                            ["status"] = ReadString(existing, "status", "in_transit"),
                            ["dispatchDay"] = ReadDouble(existing, "dispatch_day", dispatchDay),
                            ["deliveryDay"] = ReadDouble(existing, "delivery_day", deliveryDay),
                            ["queuedActions"] = new List<Dictionary<string, object>>()
                        };
                    }
                }
                Dictionary<string, object> inserted = InsertLetter(connection, senderId, ReadString(payload, "senderName", senderId), recipientId, ReadString(payload, "recipientName", recipientId), body, ReadString(payload, "source", "player"), ReadString(payload, "reason", ""), ReadString(payload, "parentLetterId", ""), ReadString(payload, "originId", ""), ReadString(payload, "destinationId", ""), dispatchDay, deliveryDay, payload);
                if (campaignReport)
                {
                    string letterId = ReadString(inserted, "letterId", "");
                    RelationshipsStore.UpdateLetters2(connection, new Dictionary<string, object>
                        {
                            ["day"] = dispatchDay,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["id"] = letterId
                        });
                    inserted["status"] = "delivered";
                    inserted["deliveryDay"] = dispatchDay;
                    inserted["deliveredDay"] = dispatchDay;
                    Dictionary<string, object> delivered = RelationshipsStore.SelectLetters9(connection, new Dictionary<string, object> { ["id"] = letterId }).FirstOrDefault();
                    if (delivered != null) AppendDeliveredLetterHistory(campaignId, recipientId, delivered, dispatchDay);
                }
                List<Dictionary<string, object>> queuedActions = !DialogueService.UsesStructuredActions(payload) && source.Equals("player", StringComparison.OrdinalIgnoreCase)
                    ? QueueImmediateRebellionLetterAction(campaignId, payload, senderId, recipientId, body)
                    : new List<Dictionary<string, object>>();
                if (!DialogueService.UsesStructuredActions(payload) && source.Equals("player", StringComparison.OrdinalIgnoreCase))
                    queuedActions.AddRange(QueueImmediateCampaignOrderGuidanceAction(
                        connection, campaignId, payload, senderId, recipientId, body));
                inserted["ok"] = true;
                inserted["queuedActions"] = queuedActions;
                return inserted;
            }
        }

        private static List<Dictionary<string, object>> QueueImmediateRebellionLetterAction(
            string campaignId,
            Dictionary<string, object> payload,
            string senderId,
            string recipientId,
            string body)
        {
            string command = Program.LooksLikeExplicitPlayerRebellionDeclaration(body)
                ? "start_ruling_clan_rebellion"
                : Program.LooksLikeExplicitPlayerRebellionSurrender(body) ? "surrender_rebellion" : string.Empty;
            if (string.IsNullOrWhiteSpace(command)) return new List<Dictionary<string, object>>();
            Dictionary<string, object> sender = ReadDictionary(payload, "sender") ?? new Dictionary<string, object>();
            Dictionary<string, object> recipient = ReadDictionary(payload, "recipient") ?? new Dictionary<string, object>();
            Dictionary<string, object> candidate = Program.BuildRebellionActionCandidate(
                command,
                senderId,
                FirstNonEmpty(ReadFirstString(payload, "playerKingdomId", "actorKingdomId"), ReadString(sender, "kingdomId", "")),
                FirstNonEmpty(ReadFirstString(payload, "playerClanId", "actorClanId"), ReadString(sender, "clanId", "")),
                recipientId,
                FirstNonEmpty(ReadFirstString(payload, "speakerKingdomId", "targetKingdomId"), ReadString(recipient, "kingdomId", "")),
                FirstNonEmpty(ReadFirstString(payload, "speakerClanId", "targetClanId"), ReadString(recipient, "clanId", "")),
                command == "start_ruling_clan_rebellion"
                    ? "The player dispatched an explicit unilateral challenge and declaration of rebellion to the current ruler."
                    : "The player dispatched an explicit surrender of their named rebellion leadership.");
            List<Dictionary<string, object>> queued = Program.EvaluateAndQueueActionCandidates(
                campaignId, payload, body, new List<Dictionary<string, object>> { candidate },
                out List<string> errors, out List<Dictionary<string, object>> rejected);
            if (queued.Count == 0 && errors.Count > 0)
                Program.LogOperational("correspondence.rebellion_action_failed",
                    new Dictionary<string, object> { ["campaignId"] = campaignId, ["errors"] = errors, ["rejected"] = rejected });
            return queued;
        }

        private static List<Dictionary<string, object>> QueueImmediateCampaignOrderGuidanceAction(
            ReignDbConnection connection,
            string campaignId,
            Dictionary<string, object> payload,
            string playerId,
            string commanderId,
            string body)
        {
            string response = NormalizeCampaignOrderGuidance(body);
            if (string.IsNullOrWhiteSpace(response)) return new List<Dictionary<string, object>>();
            Dictionary<string, object> report = RelationshipsStore.SelectLetters10(connection, new Dictionary<string, object>
                {
                    ["commander"] = commanderId,
                    ["player"] = playerId
                }).FirstOrDefault();
            if (report == null) return new List<Dictionary<string, object>>();
            Dictionary<string, object> reportPayload = TryParseJsonObject(
                ReadString(report, "payload_json", "{}")) ?? new Dictionary<string, object>();
            string orderId = ReadFirstString(reportPayload, "orderId", "order_id");
            if (string.IsNullOrWhiteSpace(orderId)) return new List<Dictionary<string, object>>();

            Dictionary<string, object> candidate = new Dictionary<string, object>
            {
                ["command"] = "respond_to_order_report",
                ["source"] = "campaign_command_correspondence_guidance",
                ["requiresAcceptance"] = false,
                ["actorHeroId"] = commanderId ?? string.Empty,
                ["TargetHero"] = playerId ?? string.Empty,
                ["reason"] = "The sovereign sent explicit written guidance in response to the commander's urgent report.",
                ["terms"] = new Dictionary<string, object>
                {
                    ["orderId"] = orderId,
                    ["response"] = response,
                    ["reportId"] = ReadFirstString(reportPayload, "reportId", "report_id")
                }
            };
            List<Dictionary<string, object>> queued = Program.EvaluateAndQueueActionCandidates(
                campaignId, payload, body, new List<Dictionary<string, object>> { candidate },
                out List<string> errors, out List<Dictionary<string, object>> rejected);
            if (queued.Count == 0 && errors.Count > 0)
                Program.LogOperational("correspondence.campaign_guidance_action_failed",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["orderId"] = orderId,
                        ["response"] = response,
                        ["errors"] = errors,
                        ["rejected"] = rejected
                    });
            return queued;
        }

        private static string NormalizeCampaignOrderGuidance(string text)
        {
            string q = Program.NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(q)
                || Program.ContainsAnyPhrase(q, new[] { "should i", "could i", "would i", "maybe", "perhaps" }))
                return string.Empty;
            if (Program.ContainsAnyPhrase(q, new[] { "cancel the order", "cancel this order", "abort the order" })) return "cancel";
            if (Program.ContainsAnyPhrase(q, new[] { "withdraw", "retreat", "fall back" })) return "withdraw";
            if (Program.ContainsAnyPhrase(q, new[] { "hold position", "hold your position", "wait there", "stay put" })) return "hold";
            if (Program.ContainsAnyPhrase(q, new[] { "adapt", "change the plan", "use your judgment", "use your judgement" })) return "adapt";
            if (Program.ContainsAnyPhrase(q, new[] { "continue", "proceed", "carry on", "execute the order" })) return "continue";
            return string.Empty;
        }

        internal static Dictionary<string, object> InsertLetter(ReignDbConnection connection, string senderId, string senderName, string recipientId, string recipientName, string body, string source, string reason, string parentLetterId, string originId, string destinationId, double dispatchDay, double deliveryDay, Dictionary<string, object> payload, double? travelTimeMultiplier = null)
        {
            // Accept the letter and its author's continuity changes together. A
            // failed projection must not leave a sent letter that a retry duplicates.
            using (var transaction = connection.BeginTransaction())
            {
            if (source == "npc_reply" && !string.IsNullOrWhiteSpace(parentLetterId))
            {
                var existing = RelationshipsStore.SelectLetters11(connection, new Dictionary<string, object> { ["parent"] = parentLetterId, ["sender"] = senderId, ["recipient"] = recipientId }).FirstOrDefault();
                if (existing != null)
                {
                    transaction.Commit();
                    return new Dictionary<string, object> { ["letterId"] = ReadString(existing, "letter_id", ""), ["threadId"] = ReadString(existing, "thread_id", ""),
                        ["status"] = ReadString(existing, "status", ""), ["replayed"] = true,
                        ["dispatchDay"] = ReadDouble(existing, "dispatch_day", 0d), ["deliveryDay"] = ReadDouble(existing, "delivery_day", 0d),
                        ["transitPolicyVersion"] = ReadInt(existing, "transit_policy_version", 0) };
                }
            }
            string a = string.Compare(senderId, recipientId, StringComparison.OrdinalIgnoreCase) <= 0 ? senderId : recipientId;
            string b = a == senderId ? recipientId : senderId;
            Dictionary<string, object> thread = RelationshipsStore.SelectCorrespondenceThreads(connection, new Dictionary<string, object> { ["a"] = a, ["b"] = b }).FirstOrDefault();
            string threadId = thread == null ? "thread_" + Guid.NewGuid().ToString("N") : ReadString(thread, "thread_id", "");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (thread == null)
                RelationshipsStore.InsertCorrespondenceThreads(connection, new Dictionary<string, object> { ["id"] = threadId, ["a"] = a, ["b"] = b, ["day"] = dispatchDay, ["ts"] = ts });
            string letterId = "letter_" + Guid.NewGuid().ToString("N");
            if (double.IsNaN(dispatchDay) || double.IsInfinity(dispatchDay)) throw new InvalidOperationException("Letter dispatch day must be finite.");
            bool immediate = string.Equals(source, "campaign_command_urgent_report", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "campaign_command_report", StringComparison.OrdinalIgnoreCase);
            double baseTransit = immediate ? 0d : CorrespondenceTransitPolicy.NormalizeBaseDays(deliveryDay - dispatchDay);
            deliveryDay = dispatchDay + CorrespondenceTransitPolicy.NewTransitDays(baseTransit, immediate,
                travelTimeMultiplier ?? Program.CorrespondenceOption(Program.LoadSettings(), "correspondenceTravelTimeMultiplier"));
            RelationshipsStore.InsertLetters(connection, new Dictionary<string, object> { ["id"] = letterId, ["thread"] = threadId, ["sender"] = senderId, ["senderName"] = senderName, ["recipient"] = recipientId, ["recipientName"] = recipientName, ["body"] = body, ["source"] = source, ["reason"] = reason, ["parent"] = parentLetterId, ["origin"] = originId, ["destination"] = destinationId, ["dispatch"] = dispatchDay, ["delivery"] = deliveryDay, ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()), ["ts"] = ts,
                    ["policy"] = CorrespondenceTransitPolicy.Version, ["baseTransit"] = baseTransit });
            RelationshipsStore.UpdateCorrespondenceThreads(connection, new Dictionary<string, object> { ["letter"] = letterId, ["day"] = dispatchDay, ["ts"] = ts, ["thread"] = threadId });
            RelationshipsStore.UpdateLetters3(connection, new Dictionary<string, object> { ["timeline"] = WorldSimulationService.ActiveWorldHistoryTimeline(connection), ["id"] = letterId });
            if ((source == "npc_reply" || source == "npc_initiated" || source == "npc_introduction" || source == "relationship_development") && ReadDictionaryList(payload, "continuityWrites").Count > 0)
            {
                // Accepted composition belongs to the author. Recipient knowledge
                // still requires the existing delivered-letter path.
                var continuityPayload = new Dictionary<string, object>(payload) { ["sceneTurnId"] = letterId,
                    ["worldDay"] = dispatchDay, ["timelineId"] = WorldSimulationService.ActiveWorldHistoryTimeline(connection) };
                DialogueService.StoreAcceptedConversationContinuity(connection, continuityPayload, senderId, recipientId,
                    letterId, body, ReadDictionaryList(payload, "continuityWrites"), ts);
            }
            var agencyContext = ReadDictionary(ReadDictionary(payload, "motiveDecision"), "conversationAgency");
            if (agencyContext != null && ReadDictionaryList(payload, "proposalDecisions").Count > 0
                && (source == "npc_reply" || source == "npc_initiated" || source == "npc_introduction" || source == "relationship_development"))
            {
                var agencyReceipt = DialogueService.CommitConversationAgency(connection, payload, agencyContext,
                    FirstNonEmpty(ReadString(payload, "agencyTurnId", ""), letterId), false);
                if (!ReadBool(agencyReceipt, "ok", false)) throw new InvalidOperationException("The letter's negotiation became stale before dispatch.");
                payload["conversationAgencyReceipt"] = agencyReceipt;
                RelationshipsStore.UpdateLetters4(connection, new Dictionary<string, object> { ["id"] = letterId, ["payload"] = Json.Serialize(payload) });
            }
            transaction.Commit();
            return new Dictionary<string, object> { ["letterId"] = letterId, ["threadId"] = threadId, ["status"] = "in_transit", ["dispatchDay"] = dispatchDay, ["deliveryDay"] = deliveryDay,
                ["transitPolicyVersion"] = CorrespondenceTransitPolicy.Version, ["baseTransitDays"] = baseTransit,
                ["transitDays"] = deliveryDay - dispatchDay };
            }
        }

        internal static Dictionary<string, object> CorrespondenceThreadsApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId");
            HashSet<string> contacts = new HashSet<string>(ReadStringList(payload, "contactIds"), StringComparer.OrdinalIgnoreCase);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                // Contact priority must cover the complete visible history, even
                // when an old thread falls outside the bounded body snapshot.
                var parameters = TestDict("player", playerId);
                string where = "(sender_id=$player OR recipient_id=$player) AND (sender_id=$player OR status<>'in_transit')";
                if (contacts.Count > 0)
                {
                    var placeholders = contacts.Select((id, i) => { parameters["contact" + i] = id; return "$contact" + i; }).ToList();
                    string allowed = string.Join(",", placeholders);
                    where += " AND (sender_id IN (" + allowed + ") OR recipient_id IN (" + allowed
                        + ") OR source IN ('campaign_command_urgent_report','campaign_command_report','npc_introduction',"
                        + "'reign_marriage_offer','reign_marriage_answer','reign_marriage_meeting'))";
                }
                var states = RelationshipsStore.SelectLetters12(connection, where,parameters)
                    .Select(row => TestDict("contactId", ReadString(row, "contact_id", ""), "hasCorrespondence", true,
                        "unreadCount", ReadInt(row, "unread_count", 0), "status", ReadString(row, "status", ""))).ToList();
                string correspondent = ReadString(payload, "correspondentId", "");
                if (correspondent.Length > 0)
                {
                    parameters["correspondent"] = correspondent;
                    where += " AND (sender_id=$correspondent OR recipient_id=$correspondent)";
                }
                // The mailbox never needs generation prompts, profiles or action
                // diagnostics stored in payload_json. Keep those in the ledger.
                const string displayColumns = "letter_id,thread_id,sender_id,sender_name,recipient_id,recipient_name,body,status,source,reason,dispatch_day,delivery_day,delivered_day,read_day,created_ts";
                var letters = ReadBool(payload, "summaryOnly", false) ? new List<Dictionary<string, object>>()
                    : RelationshipsStore.SelectLetters13(connection, displayColumns, where,parameters);
                return TestDict("ok", true, "letters", letters, "contactStates", states,
                    "unreadCount", states.Sum(row => ReadInt(row, "unreadCount", 0)));
            }
        }

        internal static Dictionary<string, object> CorrespondenceTickApi(Dictionary<string, object> payload)
        {
            payload ??= new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId");
            double day = ReadDouble(payload, "worldDay", double.NaN);
            if (playerId.Length == 0 || !double.IsFinite(day) || day < 0)
                return TestDict("ok", false, "error", "A player and finite native world day are required.");
            string timeline;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaignId)) timeline = WorldSimulationService.ActiveWorldHistoryTimeline(connection);
            string requestedTimeline = ReadString(payload, "timelineId", timeline);
            if (requestedTimeline != timeline) return TestDict("ok", false, "error", "Correspondence timeline changed; refresh the campaign state.");
            string phase = ReadString(payload, "phase", "all");
            if (phase != "all" && phase != "delivery" && phase != "replies")
                return TestDict("ok", false, "error", "Unknown correspondence phase.");
            if (phase != "replies") DeliverDueCorrespondence(campaignId, playerId, day, timeline);
            if (phase != "delivery") ProcessPendingCorrespondence(campaignId, playerId, day, timeline);
            var result = ReadCorrespondenceDeliveryResults(campaignId, playerId, timeline, ReadStringList(payload, "deliveryReceiptAcks"));
            // Legacy clients do not support receipt acknowledgments. Preserve
            // their original one-shot response behavior until the client updates.
            if (!payload.ContainsKey("phase"))
                ReadCorrespondenceDeliveryResults(campaignId, playerId, timeline, ReadStringList(result, "deliveryReceiptIds"));
            return result;
        }

        private static Dictionary<string, object> AcceptDeliveredCorrespondence(string campaignId, Dictionary<string, object> payload,
            Dictionary<string, object> letter, Dictionary<string, object> reply)
        {
            string letterId = ReadString(letter, "letter_id", ""), senderId = ReadString(letter, "sender_id", ""),
                recipientId = ReadString(letter, "recipient_id", ""), playerId = ReadString(payload, "playerId", "");
            double day = ReadDouble(payload, "worldDay", 0d);
            var queuedReplies = new List<Dictionary<string, object>>();
            var nativeRelationChanges = new List<Dictionary<string, object>>();
            var queuedActions = new List<Dictionary<string, object>>();
            var structuredActions = new List<Dictionary<string, object>>();
            using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> storedPayload = TryParseJsonObject(ReadString(letter, "payload_json", "{}")) ?? new Dictionary<string, object>();
                if (recipientId == playerId && DialogueService.UsesStructuredActions(storedPayload))
                    structuredActions.Add(TestDict("letterId", letterId, "request", storedPayload,
                        "response", TestDict("ok", true, "structuredActionDecision", ReadDictionary(storedPayload, "validatedStructuredActionDecision") ?? new Dictionary<string, object>())));
                Dictionary<string, object> recipientProfile = ReadDictionary(storedPayload, "recipient") ?? new Dictionary<string, object>();
                Dictionary<string, object> relationshipPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["eventId"] = "mail_delivery_" + letterId, ["eventType"] = "delivered_letter",
                    ["subjectId"] = recipientId, ["targetId"] = senderId, ["summary"] = ReadString(letter, "body", ""), ["worldDay"] = day,
                    ["source"] = "correspondence", ["letterId"] = letterId, ["timelineId"] = ReadString(payload, "timelineId", "main"),
                    ["nativeRelation"] = ReadInt(recipientProfile, "relationToPlayer", 0),
                    ["subject"] = recipientProfile,
                    ["target"] = ReadDictionary(storedPayload, "sender") ?? new Dictionary<string, object>()
                };
                Dictionary<string, object> storedMotive = ReadDictionary(storedPayload, "motiveDecision");
                relationshipPayload["motiveDecision"] = storedMotive ?? DialogueService.BuildConversationDecisionContext(
                    campaignId, "correspondence", recipientId, recipientProfile, Program.LoadCharacterStack(campaignId, recipientId), new Dictionary<string, object>(),
                    ReadString(letter, "body", ""), "Private written correspondence.",
                    new Dictionary<string, object> { ["playerHeroStringId"] = senderId, ["recipientId"] = senderId, ["worldDay"] = day, ["mode"] = "correspondence", ["sceneOpportunity"] = new Dictionary<string, object> { ["private"] = true, ["exposure"] = 0.08d, ["witnessIds"] = new List<string>() } },
                    new Dictionary<string, object> { ["identityState"] = "known" });
                bool agencyTurn = ReadDictionaryList(reply, "proposalDecisions").Count > 0;
                Dictionary<string, object> relationshipResult = agencyTurn ? new Dictionary<string, object>() : RelationshipEvaluateApi(relationshipPayload);
                int nativeDelta = ReadInt(relationshipResult, "nativeRelationDelta", 0);
                if (nativeDelta != 0)
                {
                    nativeRelationChanges.Add(new Dictionary<string, object>
                    {
                        ["subjectId"] = recipientId,
                        ["targetId"] = senderId,
                        ["delta"] = nativeDelta,
                        ["eventId"] = "mail_delivery_" + letterId
                    });
                }
                if (senderId.Equals(playerId, StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadBool(reply, "shouldReply", false) && !string.IsNullOrWhiteSpace(ReadString(reply, "body", "")))
                    {
                        double transit = CorrespondenceTransitPolicy.ReplyBaseDays(ReadInt(letter, "transit_policy_version", 0),
                            ReadDouble(letter, "base_transit_days", 0d), ReadDouble(letter, "delivery_day", day) - ReadDouble(letter, "dispatch_day", day));
                        var dispatched = InsertLetter(connection, recipientId, ReadString(letter, "recipient_name", recipientId), senderId, ReadString(letter, "sender_name", senderId), ReadString(reply, "body", ""), "npc_reply", ReadString(reply, "reason", "reply"), letterId, ReadString(letter, "destination_id", ""), ReadString(letter, "origin_id", ""), day, day + transit, reply);
                        queuedReplies.Add(dispatched);
                        var agencyReceipt = ReadDictionary(reply, "conversationAgencyReceipt");
                        if (agencyReceipt != null && !DialogueService.ConversationRepairFailed(reply))
                        {
                            var assessments = new List<Dictionary<string, object>>();
                            var reactionPayload = new Dictionary<string, object> { ["conversationAgencyReceipt"] = agencyReceipt, ["playerText"] = ReadString(letter, "body", "") };
                            DialogueService.ApplyAgencyRelationshipAssessment(assessments, reactionPayload, recipientId, senderId);
                            if (assessments.Count > 0)
                            {
                                var reaction = ConversationRelationshipEvaluateApi(new Dictionary<string, object> {
                                    ["campaignId"] = campaignId, ["timelineId"] = ReadString(reply, "timelineId", "main"),
                                    ["exchangeId"] = ReadString(agencyReceipt, "receiptId", "mail_delivery_" + letterId),
                                    ["mode"] = "correspondence", ["worldDay"] = day, ["playerHeroStringId"] = senderId,
                                    ["assessments"] = assessments, ["participants"] = new[] { recipientId, senderId },
                                    ["relationshipPairs"] = new List<Dictionary<string, object>> { new Dictionary<string, object> {
                                        ["subjectId"] = recipientId, ["targetId"] = senderId, ["nativeRelation"] = ReadInt(recipientProfile, "relationToPlayer", 0) } } });
                                nativeRelationChanges.AddRange(ReadDictionaryList(reaction, "nativeChanges"));
                            }
                        }
                        var actionPayload = new Dictionary<string, object>(storedPayload);
                        if (agencyReceipt != null) actionPayload["conversationAgencyReceipt"] = agencyReceipt;
                        if (!DialogueService.UsesStructuredActions(storedPayload) && !DialogueService.ConversationRepairFailed(reply))
                        {
                            queuedActions.AddRange(QueueAcceptedRebellionLetterReply(campaignId, actionPayload, letter, reply, senderId, recipientId));
                            if (agencyReceipt == null || ReadDictionaryList(agencyReceipt, "updates").Any(r => ReadString(r, "topic", "") == "service" && DialogueService.AgencyAccepted(ReadString(r, "disposition", ""))))
                                queuedActions.AddRange(QueueAcceptedCampaignOrderLetterReply(campaignId, actionPayload, letter, reply, senderId, recipientId));
                        }
                    }
                }
            }
            foreach (var change in nativeRelationChanges)
                if (ReadString(change, "eventId", "").Length == 0)
                    change["eventId"] = "mail_delivery_" + letterId + "_" + ReadString(change, "subjectId", "") + "_" + ReadString(change, "targetId", "");
            return TestDict("ok", true, "queuedReplies", queuedReplies, "nativeRelationChanges", nativeRelationChanges, "queuedActions", queuedActions,
                "structuredActionDeliveries", structuredActions);
        }

        private static List<Dictionary<string, object>> QueueAcceptedRebellionLetterReply(
            string campaignId,
            Dictionary<string, object> storedPayload,
            Dictionary<string, object> receivedLetter,
            Dictionary<string, object> npcReply,
            string playerId,
            string npcId)
        {
            List<Dictionary<string, object>> candidates = BuildRebellionLetterReplyCandidates(
                storedPayload, receivedLetter, npcReply, playerId, npcId);
            candidates = candidates.Where(candidate => DialogueService.BindConversationAgencyAuthority(storedPayload,
                ReadString(candidate, "command", ""), ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>(),
                new Dictionary<string, object>(), new List<string>(), candidate)).ToList();
            if (candidates.Count == 0) return candidates;
            string request = ReadString(receivedLetter, "body", "");
            string replyBody = ReadString(npcReply, "body", "");
            return Program.EvaluateAndQueueActionCandidates(campaignId, storedPayload,
                request + "\n" + replyBody, candidates,
                out List<string> errors, out List<Dictionary<string, object>> rejected);
        }

        internal static List<Dictionary<string, object>> BuildRebellionLetterReplyCandidates(
            Dictionary<string, object> storedPayload,
            Dictionary<string, object> receivedLetter,
            Dictionary<string, object> npcReply,
            string playerId,
            string npcId)
        {
            storedPayload = storedPayload ?? new Dictionary<string, object>();
            receivedLetter = receivedLetter ?? new Dictionary<string, object>();
            npcReply = npcReply ?? new Dictionary<string, object>();
            string request = ReadString(receivedLetter, "body", "");
            string replyBody = ReadString(npcReply, "body", "");
            Dictionary<string, object> recipient = ReadDictionary(storedPayload, "recipient")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> actionGate = ReadDictionary(npcReply, "actionGate")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> preparationPayload = new Dictionary<string, object>(
                storedPayload, StringComparer.OrdinalIgnoreCase)
            {
                ["interactionMode"] = "correspondence",
                ["playerHeroStringId"] = playerId ?? string.Empty,
                ["speakerHeroStringId"] = npcId ?? string.Empty
            };
            List<Dictionary<string, object>> preparation =
                RebellionService.BuildRebellionPreparationDialogueCandidates(preparationPayload, recipient,
                    request, replyBody, actionGate);
            if (preparation.Count > 0) return preparation;

            string decision = ReadString(npcReply, "rebellionDecision", "none").ToLowerInvariant();
            string command = string.Empty;
            bool npcIsActor = false;
            if (Program.LooksLikeRebellionRecruitmentRequest(request)
                && (decision == "accept_recruitment" || (decision == "none" && Program.IsUnambiguousActionAcceptance(replyBody))))
            {
                command = "recruit_lord_to_rebellion";
            }
            else if (Program.LooksLikePlayerJoinRebellionRequest(request)
                && (decision == "accept_player_join" || (decision == "none" && Program.IsUnambiguousActionAcceptance(replyBody))))
            {
                command = "join_rebellion";
            }
            else if (Program.LooksLikeRebellionSurrenderDemand(request)
                && (decision == "surrender" || Program.LooksLikeExplicitNpcRebellionSurrender(replyBody)))
            {
                command = "surrender_rebellion";
                npcIsActor = true;
            }
            if (string.IsNullOrWhiteSpace(command)) return new List<Dictionary<string, object>>();

            Dictionary<string, object> player = ReadDictionary(storedPayload, "sender") ?? new Dictionary<string, object>();
            Dictionary<string, object> candidate = npcIsActor
                ? Program.BuildRebellionActionCandidate(command, npcId, ReadString(recipient, "kingdomId", ""), ReadString(recipient, "clanId", ""),
                    playerId, ReadString(player, "kingdomId", ""), ReadString(player, "clanId", ""),
                    "The NPC's delivered written reply explicitly surrendered their named rebellion leadership.")
                : Program.BuildRebellionActionCandidate(command, playerId, ReadString(player, "kingdomId", ""), ReadString(player, "clanId", ""),
                    npcId, ReadString(recipient, "kingdomId", ""), ReadString(recipient, "clanId", ""),
                    command == "recruit_lord_to_rebellion"
                        ? "The NPC's delivered written reply explicitly accepted recruitment into the player's active rebellion."
                        : "The NPC rebel leader's delivered written reply explicitly accepted the player clan into the rebellion.");
            return new List<Dictionary<string, object>> { candidate };
        }

        internal static Dictionary<string, object> CorrespondenceReadApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string letterId = ReadFirstString(payload, "letterId", "letter_id");
            var letterIds = ReadStringList(payload, "letterIds").Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(1000).ToList();
            double day = ReadDouble(payload, "worldDay", 0d);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                if (letterIds.Count == 0)
                    RelationshipsStore.UpdateLetters5(connection, new Dictionary<string, object> { ["day"] = day, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = letterId });
                else
                {
                    string playerId = ReadString(payload, "playerId", "");
                    if (playerId.Length == 0) return TestDict("ok", false, "error", "A recipient is required for a batch read acknowledgment.");
                    var parameters = TestDict("day", day, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "player", playerId);
                    var ids = letterIds.Select((id, i) => { parameters["id" + i] = id; return "$id" + i; });
                    RelationshipsStore.UpdateLetters6(connection, string.Join(",", ids),parameters);
                }
            }
            return new Dictionary<string, object> { ["ok"] = true, ["letterId"] = letterId, ["status"] = "read" };
        }

        private static Dictionary<string, object> GenerateNpcLetter(string campaignId, string senderId, string recipientId, Dictionary<string, object> receivedLetter, double day, bool autonomousInitiation = false, bool firstIntroduction = false)
        {
            var settings = Program.LoadSettings();
            Dictionary<string, object> profile = Program.ReadJsonObject(Program.CharacterFile(campaignId, senderId, "profile.json"));
            profile = MergeCorrespondenceRecipientProfile(profile, receivedLetter, senderId);
            Dictionary<string, object> characteristics = Program.LoadCharacterStack(campaignId, senderId);
            Dictionary<string, object> relationship = RelationshipContextApi(new Dictionary<string, object> { ["campaignId"] = campaignId, ["subjectId"] = senderId, ["targetId"] = recipientId });
            string senderName = ReadString(profile, "name", senderId);
            string recipientName = ReadString(receivedLetter, "sender_name", recipientId);
            string letterBody = ReadString(receivedLetter, "body", "");
            var receivedPayload = TryParseJsonObject(ReadString(receivedLetter, "payload_json", "{}"));
            string timeline;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaignId)) timeline = WorldSimulationService.ActiveWorldHistoryTimeline(connection);
            Dictionary<string, object> memoryContextPayload = new Dictionary<string, object>(profile, StringComparer.OrdinalIgnoreCase)
            {
                ["worldDay"] = day, ["playerHeroStringId"] = recipientId, ["interactionMode"] = "correspondence",
                ["timelineId"] = timeline, ["deferContinuityAllocation"] = true,
            };
            if (DialogueService.UsesStructuredActions(receivedPayload))
            {
                memoryContextPayload["structuredActionProtocolVersion"] = Reign.Core.Contracts.Actions.ActionNegotiation.ProtocolVersion;
                memoryContextPayload["structuredActionContext"] = ReadDictionary(receivedPayload, "structuredActionContext");
                memoryContextPayload["speakerHeroStringId"] = senderId;
            }
            var letterMemoryPacket = Program.BuildNpcMemoryPacket(campaignId, senderId, recipientId, "", letterBody, 1200,
                memoryContextPayload, settings);
            memoryContextPayload["memoryPrecisionEvidence"] = ReadDictionary(letterMemoryPacket, "precisionEvidence");
            string memoryContext = ReadString(letterMemoryPacket, "memoryPacket", "");
            DialogueService.PromptEnvelope promptEnvelope = DialogueService.BuildCorrespondencePromptEnvelope(campaignId, senderId, senderName, recipientId, recipientName, day, letterBody, profile, characteristics, relationship, memoryContext, memoryContextPayload, autonomousInitiation, firstIntroduction);
            Dictionary<string, object> motiveDecision = ReadDictionary(promptEnvelope.Diagnostics, "motiveDecision") ?? new Dictionary<string, object>();
            Dictionary<string, object> llmRequest = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["heroStringId"] = senderId,
                ["requestType"] = "correspondence", ["maxTokens"] = 2400, ["temperature"] = 0.65d,
                ["messages"] = promptEnvelope.Messages,
                ["promptEnvelope"] = promptEnvelope.Diagnostics,
                ["promptCacheEligible"] = true,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };
            string correlationId = Program.EnsureCorrelationId(llmRequest);
            if (DialogueService.UsesStructuredActions(memoryContextPayload)) llmRequest["structuredActionProtocolVersion"] = Reign.Core.Contracts.Actions.ActionNegotiation.ProtocolVersion;
            memoryContextPayload["turnId"] = "mail_" + ReadString(receivedLetter, "letter_id", correlationId);
            memoryContextPayload["playerText"] = letterBody;
            Dictionary<string, object> llm = DialogueService.GenerateSinglePassConversation(llmRequest, memoryContextPayload, "correspondence");
            Dictionary<string, object> parsed = DialogueService.PrepareCorrespondenceDelivery(llm);
            if (parsed != null)
            {
                if (DialogueService.UsesStructuredActions(memoryContextPayload))
                {
                    DialogueService.CaptureStructuredActionDecision(memoryContextPayload, parsed);
                    parsed["structuredActionProtocolVersion"] = Reign.Core.Contracts.Actions.ActionNegotiation.ProtocolVersion;
                    parsed["structuredActionContext"] = ReadDictionary(memoryContextPayload, "structuredActionContext");
                    parsed["validatedStructuredActionDecision"] = ReadDictionary(memoryContextPayload, "validatedStructuredActionDecision");
                }
                string body = CharactersService.RemoveSpokenNumericSkillLevels(ReadFirstString(parsed, "body", "response", "letter"), out bool skillNumericRepairApplied);
                parsed["body"] = body;
                parsed["timelineId"] = DialogueService.ContinuityTimeline(memoryContextPayload);
                parsed["worldDay"] = day;
                parsed["skillNumericRepairApplied"] = skillNumericRepairApplied;
                parsed["shouldReply"] = ReadBool(parsed, "shouldReply", true);
                parsed["motiveDecision"] = motiveDecision;
                parsed["agencyTurnId"] = ReadString(memoryContextPayload, "turnId", correlationId);
                parsed["acceptedResponseHash"] = ReadString(memoryContextPayload, "acceptedResponseHash", "");
                parsed["acceptedReplyHash"] = ReadString(memoryContextPayload, "acceptedReplyHash", "");
                return parsed;
            }
            return new Dictionary<string, object> { ["shouldReply"] = false, ["body"] = "", ["error"] = FirstNonEmpty(ReadString(llm, "error", ""), "No valid letter draft was returned.") };
        }

        internal static Dictionary<string, object> MergeCorrespondenceRecipientProfile(
            Dictionary<string, object> persistedProfile,
            Dictionary<string, object> receivedLetter,
            string expectedHeroId)
        {
            Dictionary<string, object> merged = new Dictionary<string, object>(
                persistedProfile ?? new Dictionary<string, object>(),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> storedPayload = TryParseJsonObject(
                ReadString(receivedLetter, "payload_json", "{}"))
                ?? new Dictionary<string, object>();
            Dictionary<string, object> currentRecipient =
                ReadDictionary(storedPayload, "recipient") ?? new Dictionary<string, object>();
            string currentHeroId = ReadFirstString(currentRecipient,
                "heroStringId", "heroId", "id");
            if (string.IsNullOrWhiteSpace(currentHeroId)
                || !currentHeroId.Equals(expectedHeroId ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                return merged;

            foreach (KeyValuePair<string, object> pair in currentRecipient)
                merged[pair.Key] = pair.Value;
            return merged;
        }

        private static void AppendDeliveredLetterHistory(string campaignId, string playerId, Dictionary<string, object> letter, double day)
        {
            string senderId = ReadString(letter, "sender_id", "");
            string recipientId = ReadString(letter, "recipient_id", "");
            string npcId = senderId.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? recipientId : senderId;
            if (string.IsNullOrWhiteSpace(npcId)) return;
            Dictionary<string, object> line = Program.DialogueLine(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), senderId.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? "player" : "npc", ReadString(letter, "sender_name", senderId), ReadString(letter, "body", ""));
            line["channel"] = "correspondence";
            line["letterId"] = ReadString(letter, "letter_id", "");
            line["threadId"] = ReadString(letter, "thread_id", "");
            line["senderId"] = senderId;
            line["recipientId"] = recipientId;
            line["worldDay"] = day;
            line["direction"] = senderId.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? "sent" : "received";
            Program.AppendJsonLineToPath(Program.CharacterFile(campaignId, npcId, "history", "dialogue.jsonl"), line);
            DialogueService.StoreCorrespondenceTurn(campaignId, npcId, playerId, letter, day);
            using (var pregnancyConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                AcceptPregnancyDisclosures(pregnancyConnection, ReadString(letter, "timeline_id", "main"), senderId,
                    ReadString(letter, "body", ""), TryParseJsonObject(ReadString(letter, "payload_json", "{}")),
                    new[] { recipientId }, "delivered_letter:" + ReadString(letter, "letter_id", ""), day);
        }

        private static double ClampRelationship(double value) => Program.ClampDouble(value, -100d, 100d);
        private static double ValueAsDouble(object value)
        {
            if (value == null) return 0d;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0d;
        }
    }
}
