using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] RelationshipFacetKeys =
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

        private static void EnsureRelationshipAndCorrespondenceSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_interaction_receipts (
    event_id TEXT NOT NULL,
    subject_id TEXT NOT NULL,
    target_id TEXT NOT NULL,
    native_delta INTEGER NOT NULL DEFAULT 0,
    world_day REAL NOT NULL DEFAULT 0,
    reason TEXT NOT NULL DEFAULT '',
    source TEXT NOT NULL DEFAULT '',
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(event_id,subject_id,target_id)
);");

            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS correspondence_threads (
    thread_id TEXT PRIMARY KEY,
    participant_a TEXT NOT NULL,
    participant_b TEXT NOT NULL,
    last_letter_id TEXT NOT NULL DEFAULT '',
    last_activity_day REAL NOT NULL DEFAULT 0,
    created_ts INTEGER NOT NULL,
    updated_ts INTEGER NOT NULL,
    UNIQUE(participant_a,participant_b)
);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS letters (
    letter_id TEXT PRIMARY KEY,
    thread_id TEXT NOT NULL,
    sender_id TEXT NOT NULL,
    sender_name TEXT NOT NULL DEFAULT '',
    recipient_id TEXT NOT NULL,
    recipient_name TEXT NOT NULL DEFAULT '',
    body TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'in_transit',
    source TEXT NOT NULL DEFAULT 'player',
    reason TEXT NOT NULL DEFAULT '',
    parent_letter_id TEXT NOT NULL DEFAULT '',
    origin_id TEXT NOT NULL DEFAULT '',
    destination_id TEXT NOT NULL DEFAULT '',
    dispatch_day REAL NOT NULL DEFAULT 0,
    delivery_day REAL NOT NULL DEFAULT 0,
    delivered_day REAL NOT NULL DEFAULT 0,
    read_day REAL NOT NULL DEFAULT 0,
    relationship_evaluated INTEGER NOT NULL DEFAULT 0,
    history_written INTEGER NOT NULL DEFAULT 0,
    payload_json TEXT NOT NULL DEFAULT '{}',
    created_ts INTEGER NOT NULL,
    updated_ts INTEGER NOT NULL
);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_letters_delivery ON letters(status,delivery_day);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_letters_recipient ON letters(recipient_id,status,delivery_day DESC);");
            EnsureConversationMotiveSchema(connection);
            EnsureRelationshipDirectorSchema(connection);
        }

        private static Dictionary<string, object> RelationshipEvaluateApi(Dictionary<string, object> payload)
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

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double worldDay = ReadDouble(payload, "worldDay", ReadDouble(payload, "campaignDay", 0d));
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                Dictionary<string, object> priorEvaluation = QuerySql(connection,
                    "SELECT * FROM relationship_interaction_receipts WHERE event_id=$event AND subject_id=$subject AND target_id=$target LIMIT 1;",
                    new Dictionary<string, object> { ["event"] = eventId, ["subject"] = subjectId, ["target"] = targetId }).FirstOrDefault();
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
                Dictionary<string, object> chemistry = QuerySql(connection,
                    "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
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
                ExecuteSql(connection, @"INSERT INTO relationship_interaction_receipts(
event_id,subject_id,target_id,native_delta,world_day,reason,source,created_ts)
VALUES($event,$subject,$target,$delta,$day,$reason,$source,$ts);",
                    new Dictionary<string, object>
                    {
                        ["event"] = eventId, ["subject"] = subjectId, ["target"] = targetId,
                        ["delta"] = nativeDelta, ["day"] = worldDay,
                        ["reason"] = ReadString(applied, "reason", ""),
                        ["source"] = ReadFirstString(payload, "source", "eventType", "type"), ["ts"] = ts
                    });
                if (nativeDelta != 0)
                {
                    EnsureSocialReputationSchema(connection);
                    bool popularityChanged = RecomputeCourtPopularityReputations(connection, campaignId,
                        ReadString(payload, "timelineId", "main"), targetId, worldDay,
                        eventId + "|court_popularity");
                    if (popularityChanged)
                    {
                        ReconcileSocialRelationshipsForSubjects(connection, campaignId,
                            ReadString(payload, "timelineId", "main"), new[] { targetId }, worldDay);
                        if (!campaignId.StartsWith("__", StringComparison.Ordinal))
                            SchedulePendingReputationReasonJobs(campaignId);
                    }
                }
                return applied;
            }
        }

        private static Dictionary<string, object> RetiredRelationshipMechanicResult(string feature)
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
            Dictionary<string, object> existing = QuerySql(connection,
                "SELECT * FROM relationships WHERE subject_id=$subject AND target_id=$target LIMIT 1;",
                new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId }).FirstOrDefault();
            if (existing != null) return existing;

            Dictionary<string, object> subject = ReadJsonObject(CharacterFile(campaignId, subjectId, "profile.json"));
            Dictionary<string, object> target = ReadJsonObject(CharacterFile(campaignId, targetId, "profile.json"));
            Dictionary<string, object> suppliedSubject = ReadDictionary(payload, "subject") ?? new Dictionary<string, object>();
            Dictionary<string, object> suppliedTarget = ReadDictionary(payload, "target") ?? new Dictionary<string, object>();
            if(subject.Count==0)subject=new Dictionary<string, object>(suppliedSubject,StringComparer.OrdinalIgnoreCase);
            if(target.Count==0)target=new Dictionary<string, object>(suppliedTarget,StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> subjectTraitDocument = ReadJsonObject(CharacterFile(campaignId, subjectId, "traits.json"));
            if (subjectTraitDocument.Count == 0) subjectTraitDocument = ReadDictionary(suppliedSubject, "traits") ?? BuildTraitDocument(suppliedSubject);
            Dictionary<string, object> targetTraitDocument = ReadJsonObject(CharacterFile(campaignId, targetId, "traits.json"));
            if (targetTraitDocument.Count == 0) targetTraitDocument = ReadDictionary(suppliedTarget, "traits") ?? BuildTraitDocument(suppliedTarget);
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
            ExecuteSql(connection, @"INSERT INTO relationships(subject_id,target_id,trust,fear,respect,resentment,loyalty,debt,affection,rivalry,envy,attraction,jealousy,dependence,interest_alignment,native_relation,confidence_json,evidence_json,initialized_ts,last_eval_ts,last_updated_from_json,updated_ts,payload_json)
VALUES($subject,$target,$trust,$fear,$respect,$resentment,$loyalty,$debt,$affection,$rivalry,$envy,$attraction,$jealousy,$dependence,$interest_alignment,$native,$confidence,$evidence,$ts,0,'[]',$ts,$payload);", args);
            return QuerySql(connection, "SELECT * FROM relationships WHERE subject_id=$subject AND target_id=$target LIMIT 1;",
                new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId }).First();
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
            double relativeStatus = ClampDouble((targetStatus - subjectStatus) / 50d, -1d, 1d);
            double targetAttractiveness = RelationshipVisibleAttractiveness(target, targetTraits);
            double subjectAttractiveness = RelationshipVisibleAttractiveness(subject, subjectTraits);
            double affinity = ClampDouble(
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

        private static double RelationshipTraitPercent(Dictionary<string, object> traitDocument, string key)
        {
            Dictionary<string, object> percentages = ReadDictionary(traitDocument, "traitPercentages")
                ?? ReadDictionary(ReadDictionary(traitDocument, "traits"), "traitPercentages");
            if (percentages != null && percentages.ContainsKey(key)) return ClampDouble(ReadDouble(percentages, key, 50d), 0d, 100d);
            Dictionary<string, object> foundation = ReadDictionary(traitDocument, "foundationTraits")
                ?? ReadDictionary(ReadDictionary(traitDocument, "traits"), "foundationTraits")
                ?? traitDocument ?? new Dictionary<string, object>();
            return ClampDouble(50d + 25d * ReadDouble(foundation, key, 0d), 0d, 100d);
        }

        private static double RelationshipVisibleAttractiveness(Dictionary<string, object> data, Dictionary<string, object> traits)
        {
            Dictionary<string, object> appearance = ReadDictionary(data, "appearance") ?? data ?? new Dictionary<string, object>();
            Dictionary<string, object> attractiveness = ReadDictionary(traits, "attractiveness") ?? new Dictionary<string, object>();
            return ClampDouble(ReadDouble(appearance, "attractiveness",
                ReadDouble(data, "visibleAttractiveness", ReadDouble(attractiveness, "weight", ReadDouble(attractiveness, "build", 50d)))), 0d, 100d);
        }

        private static double RelationshipStatusScore(Dictionary<string, object> data)
        {
            data = data ?? new Dictionary<string, object>();
            double tier = ClampDouble(ReadDouble(data, "clanTier", 0d), 0d, 6d);
            double fiefs = Math.Max(0d, ReadDouble(data, "clanFiefCount", ReadDouble(data, "fiefCount", 0d)));
            double renown = Math.Max(0d, ReadDouble(data, "clanRenown", ReadDouble(data, "renown", 0d)));
            double influence = Math.Max(0d, ReadDouble(data, "influence", 0d));
            return ClampDouble(tier * 9d + Math.Min(20d, fiefs * 6d) + Math.Min(15d, renown / 80d)
                + Math.Min(10d, influence / 150d) + (ReadBool(data, "isRuler", false) ? 15d : 0d), 0d, 100d);
        }

        private static Dictionary<string, object> EvaluateRelationshipEvent(string campaignId, string subjectId, string targetId, string eventId, Dictionary<string, object> payload, Dictionary<string, object> state)
        {
            string summary = LimitText(ReadFirstString(payload, "summary", "text", "description", "playerText"), 2400);
            string eventType = ReadString(payload, "eventType", "relationship_event");
            Dictionary<string, object> memory = BuildNpcMemoryPacket(campaignId, subjectId, targetId, ReadString(payload, "locationId", ""), summary, 1400);
            Dictionary<string, object> subjectTraitDocument = ReadJsonObject(CharacterFile(campaignId, subjectId, "traits.json"));
            Dictionary<string, object> subjectTraits = ReadDictionary(subjectTraitDocument, "foundationTraits") ?? new Dictionary<string, object>();
            Dictionary<string, object> subjectProfile = ReadJsonObject(CharacterFile(campaignId, subjectId, "profile.json"));
            string prompt = BuildRelationshipEvaluationPrompt(subjectId, targetId, eventId, eventType, summary, state, subjectTraits, memory, payload)
                + "\n\nSubject defining capabilities:\n" + BuildCoreSkillAwarenessPrompt(subjectProfile,
                    LoadPassiveCharacterStack(campaignId, subjectId, subjectProfile))
                + "\nCapability evidence is background and self-knowledge only. Do not change relationship facets because of skill alone; require conduct in the supplied event.";
            Dictionary<string, object> llm = ChatWithLlm(new Dictionary<string, object>
            {
                ["requestType"] = "relationship",
                ["maxTokens"] = 1400,
                ["temperature"] = 0.15d,
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = "You are Reign's private directional relationship evaluator. Return strict JSON only. Judge only the subject's relationship toward the target from supplied evidence. Do not roleplay or write dialogue." },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = prompt }
                },
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            });
            Dictionary<string, object> parsed = ReadBool(llm, "ok", false) ? TryParseJsonObject(ReadString(llm, "content", "")) : null;
            if (parsed != null)
            {
                EnforceMotiveSpecificRelationshipEvaluation(parsed, payload);
                parsed["evaluator"] = "relationship_model";
                return parsed;
            }
            Dictionary<string, object> fallback = DeterministicRelationshipEvaluation(eventType, summary, payload, state);
            fallback["evaluator"] = "deterministic_fallback";
            fallback["evaluatorError"] = ReadString(llm, "error", "");
            return fallback;
        }

        private static string BuildRelationshipEvaluationPrompt(string subjectId, string targetId, string eventId, string eventType, string summary, Dictionary<string, object> state, Dictionary<string, object> traits, Dictionary<string, object> memory, Dictionary<string, object> payload)
        {
            return "Evaluate one event as a coherent relationship outcome.\n"
                + "Subject: " + subjectId + "\nTarget: " + targetId + "\nEvent: " + eventId + " / " + eventType + "\n"
                + "Event summary:\n" + summary + "\n\nCurrent facets (-100..100):\n" + Json.Serialize(RelationshipFacetSnapshot(state))
                + "\n\nSubject personality (-2..2):\n" + Json.Serialize(traits)
                + "\n\nRelevant memory:\n" + ReadString(memory, "memoryPacket", "")
                + "\n\nEvent metadata:\n" + Json.Serialize(payload)
                + "\n\nMotive rule: calculated romantic reciprocity may raise interest_alignment, dependence, or debt, but must not invent attraction or love. Genuine reciprocity may raise attraction and affection. Coercive placation is not consent."
                + "\n\nReturn {impactLevel:'ordinary|major|transformative', reasoning:'...', facetDeltas:{trust:0,...}, facetReasons:{trust:'...'}, nativeRelationDelta:0, milestoneUpdates:[{kind:'in_love',operation:'activate|reinforce|erode|remove',stabilityDelta:0,strength:0,reason:'...'}], pressureUpdates:[{kind:'humiliation',operation:'create|reinforce|reduce|resolve',intensityDelta:0,secrecy:0.5,summary:'...',triggered:false}], development:{kind:'',polarity:'positive|negative|mixed',deliveryMode:'in_person|letter|offscreen',requiresPhysical:false,priority:0.5,summary:''}}. Change only facets supported by this event. Ordinary max 10, major max 30, transformative max 60. At most eight nonzero facets.";
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
            bool betrayal = ContainsAny(text, "betray", "lied", "deceiv", "broke oath", "exposed", "revealed my secret");
            bool humiliation = ContainsAny(text, "mock", "humiliat", "publicly shamed", "disgrace");
            bool intimacy = ContainsAny(text, "love", "kiss", "intimate", "embrace", "flirt");
            bool rejection = ContainsAny(text, "reject", "spurn");
            bool reconciliation = ContainsAny(text, "apolog", "forgive", "reconcile");
            string impact = ContainsAny(text, "betray", "murder", "saved my child", "saved her child", "saved his child", "life debt") ? "transformative"
                : ContainsAny(text, "love", "intimate", "kiss", "humiliat", "rescued", "confession", "oath", "threat") ? "major" : "ordinary";
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
                if (ContainsAny(text, "thank", "gift", "help", "saved", "rescue", "kindness")) { deltas["trust"] = unit; deltas["affection"] = unit; deltas["loyalty"] = Math.Max(2, unit / 2); }
                if (ContainsAny(text, "confession", "secret", "trusted you")) { deltas["trust"] = unit; deltas["affection"] = Math.Max(2, unit / 2); }
                if (intimacy && calculatedRomance) { deltas["interest_alignment"] = unit; deltas["dependence"] = Math.Max(2, unit / 2); deltas["debt"] = Math.Max(2, unit / 3); }
                else if (intimacy && !coerciveRomance) { deltas["affection"] = unit; deltas["attraction"] = unit; deltas["loyalty"] = Math.Max(2, unit / 2); }
                if (rejection || humiliation) { deltas["affection"] = -unit; deltas["respect"] = -unit; deltas["resentment"] = unit; }
                if (betrayal) { deltas["trust"] = -unit; deltas["loyalty"] = -unit; deltas["resentment"] = unit; deltas["respect"] = -Math.Max(2, unit / 2); }
                if (ContainsAny(text, "threat", "kill you", "execute")) { deltas["fear"] = unit; deltas["trust"] = -Math.Max(2, unit / 2); deltas["resentment"] = Math.Max(2, unit / 2); }
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

        private static Dictionary<string, object> ApplyRelationshipEvaluation(ReignDbConnection connection, string campaignId, string eventId, string subjectId, string targetId, double worldDay, long ts, Dictionary<string, object> state, Dictionary<string, object> evaluation)
        {
            string impact = ReadString(evaluation, "impactLevel", "ordinary").ToLowerInvariant();
            int maxDelta = impact == "transformative" ? 60 : impact == "major" ? 30 : 10;
            Dictionary<string, object> rawDeltas = ReadDictionary(evaluation, "facetDeltas") ?? new Dictionary<string, object>();
            Dictionary<string, object> reasons = ReadDictionary(evaluation, "facetReasons") ?? new Dictionary<string, object>();
            List<string> changed = rawDeltas.Keys.Where(key => RelationshipFacetKeys.Contains(key, StringComparer.OrdinalIgnoreCase) && Math.Abs(ReadDouble(rawDeltas, key, 0d)) > 0.001d).Take(8).ToList();
            Dictionary<string, object> args = new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId, ["ts"] = ts, ["event"] = Json.Serialize(new[] { eventId }) };
            Dictionary<string, object> updated = new Dictionary<string, object>();
            foreach (string key in RelationshipFacetKeys)
            {
                double oldValue = ReadDouble(state, key, 0d);
                double delta = changed.Contains(key, StringComparer.OrdinalIgnoreCase) ? ClampDouble(ReadDouble(rawDeltas, key, 0d), -maxDelta, maxDelta) : 0d;
                double newValue = ClampRelationship(oldValue + delta);
                args[key] = newValue;
                updated[key] = newValue;
                if (Math.Abs(delta) > 0.001d)
                {
                    ExecuteSql(connection, @"INSERT INTO relationship_changes(change_id,evaluation_id,event_id,subject_id,target_id,facet,old_value,delta,new_value,impact_level,reason,confidence,evidence_json,world_day,ts)
VALUES($id,'',$event,$subject,$target,$facet,$old,$delta,$new,$impact,$reason,$confidence,$evidence,$day,$ts);",
                        new Dictionary<string, object>
                        {
                            ["id"] = "rchg_" + Guid.NewGuid().ToString("N"), ["event"] = eventId, ["subject"] = subjectId, ["target"] = targetId,
                            ["facet"] = key, ["old"] = oldValue, ["delta"] = delta, ["new"] = newValue, ["impact"] = impact,
                            ["reason"] = ReadString(reasons, key, ReadString(evaluation, "reasoning", "")), ["confidence"] = ClampDouble(ReadDouble(evaluation, "confidence", 0.65d), 0d, 1d),
                            ["evidence"] = Json.Serialize(new[] { eventId }), ["day"] = worldDay, ["ts"] = ts
                        });
                }
            }
            bool socialEventOverride = ReadBool(evaluation, "nativeRelationOverride", false);
            int nativeDelta = socialEventOverride
                ? Clamp(ReadInt(evaluation, "nativeRelationDelta", 0), -4, 4)
                : Clamp(ReadInt(evaluation, "nativeRelationDelta", 0), impact == "ordinary" ? -2 : -25, impact == "ordinary" ? 2 : 15);
            int native = Clamp(ReadInt(state, "native_relation", 0) + nativeDelta, -100, 100);
            args["native"] = native;
            ExecuteSql(connection, @"UPDATE relationships SET trust=$trust,fear=$fear,respect=$respect,resentment=$resentment,loyalty=$loyalty,debt=$debt,affection=$affection,rivalry=$rivalry,envy=$envy,attraction=$attraction,jealousy=$jealousy,dependence=$dependence,interest_alignment=$interest_alignment,native_relation=$native,last_eval_ts=$ts,last_updated_from_json=$event,updated_ts=$ts WHERE subject_id=$subject AND target_id=$target;", args);

            List<Dictionary<string, object>> milestoneResults = ApplyMilestoneUpdates(connection, subjectId, targetId, eventId, worldDay, ts, evaluation, updated);
            List<Dictionary<string, object>> pressureResults = ApplyPressureUpdates(connection, subjectId, targetId, eventId, worldDay, ts, evaluation);
            Dictionary<string, object> development = ApplyRelationshipDevelopment(connection, subjectId, targetId, eventId, worldDay, ts, evaluation, updated, milestoneResults, pressureResults);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["subjectId"] = subjectId, ["targetId"] = targetId, ["impactLevel"] = impact,
                ["facetValues"] = updated, ["nativeRelationDelta"] = nativeDelta, ["proposedNativeRelation"] = native,
                ["milestones"] = milestoneResults, ["pressures"] = pressureResults, ["development"] = development,
                ["evaluator"] = ReadString(evaluation, "evaluator", "")
            };
        }

        private static List<Dictionary<string, object>> ApplyMilestoneUpdates(ReignDbConnection connection, string subjectId, string targetId, string eventId, double day, long ts, Dictionary<string, object> evaluation, Dictionary<string, object> facets)
        {
            List<Dictionary<string, object>> updates = ReadDictionaryList(evaluation, "milestoneUpdates");
            string summary = ReadString(evaluation, "reasoning", "");
            if (ReadDouble(facets, "affection", 0d) >= 65d && ReadDouble(facets, "attraction", 0d) >= 35d && ContainsAny(summary, "intimate", "love", "vulnerab", "kiss"))
                updates.Add(new Dictionary<string, object> { ["kind"] = "in_love", ["operation"] = "activate", ["stabilityDelta"] = 25, ["strength"] = 70, ["reason"] = summary });
            if (ReadDouble(facets, "trust", 0d) >= 70d && ReadDouble(facets, "affection", 0d) >= 30d && ContainsAny(summary, "confession", "secret", "confid"))
                updates.Add(new Dictionary<string, object> { ["kind"] = "confidant", ["operation"] = "activate", ["stabilityDelta"] = 20, ["strength"] = 65, ["reason"] = summary });
            if (ReadDouble(facets, "resentment", 0d) >= 70d && ReadDouble(facets, "rivalry", 0d) >= 55d)
                updates.Add(new Dictionary<string, object> { ["kind"] = "nemesis", ["operation"] = "activate", ["stabilityDelta"] = 20, ["strength"] = 70, ["reason"] = summary });

            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> item in updates.Take(6))
            {
                string kind = ReadString(item, "kind", "");
                string operation = ReadString(item, "operation", "reinforce").ToLowerInvariant();
                if (!RelationshipMilestoneKinds.Contains(kind)) continue;
                Dictionary<string, object> existing = QuerySql(connection, "SELECT * FROM relationship_milestones WHERE subject_id=$subject AND target_id=$target AND kind=$kind AND status='active' ORDER BY updated_ts DESC LIMIT 1;",
                    new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId, ["kind"] = kind }).FirstOrDefault();
                if (existing == null && (operation == "activate" || operation == "reinforce"))
                {
                    string id = "rms_" + Guid.NewGuid().ToString("N");
                    double stability = ClampDouble(ReadDouble(item, "stability", 50d) + ReadDouble(item, "stabilityDelta", 0d), 0d, 100d);
                    ExecuteSql(connection, @"INSERT INTO relationship_milestones(milestone_id,subject_id,target_id,kind,status,stability,strength,reason,created_event_id,last_event_id,evidence_json,created_day,last_meaningful_day,updated_ts,payload_json)
VALUES($id,$subject,$target,$kind,'active',$stability,$strength,$reason,$event,$event,$evidence,$day,$day,$ts,$payload);",
                        new Dictionary<string, object> { ["id"] = id, ["subject"] = subjectId, ["target"] = targetId, ["kind"] = kind, ["stability"] = stability, ["strength"] = ClampDouble(ReadDouble(item, "strength", 50d), 0d, 100d), ["reason"] = ReadString(item, "reason", ""), ["event"] = eventId, ["evidence"] = Json.Serialize(new[] { eventId }), ["day"] = day, ["ts"] = ts, ["payload"] = Json.Serialize(item) });
                    results.Add(new Dictionary<string, object> { ["kind"] = kind, ["operation"] = "activated", ["stability"] = stability, ["milestoneId"] = id });
                }
                else if (existing != null)
                {
                    double delta = ClampDouble(ReadDouble(item, "stabilityDelta", operation == "remove" ? -100d : operation == "erode" ? -15d : 10d), -100d, 100d);
                    double stability = ClampDouble(ReadDouble(existing, "stability", 50d) + delta, 0d, 100d);
                    string status = operation == "remove" || stability <= 0d ? "inactive" : "active";
                    ExecuteSql(connection, "UPDATE relationship_milestones SET status=$status,stability=$stability,last_event_id=$event,last_meaningful_day=$day,updated_ts=$ts,payload_json=$payload WHERE milestone_id=$id;",
                        new Dictionary<string, object> { ["status"] = status, ["stability"] = stability, ["event"] = eventId, ["day"] = day, ["ts"] = ts, ["payload"] = Json.Serialize(item), ["id"] = ReadString(existing, "milestone_id", "") });
                    results.Add(new Dictionary<string, object> { ["kind"] = kind, ["operation"] = status == "inactive" ? "removed" : operation, ["stability"] = stability, ["milestoneId"] = ReadString(existing, "milestone_id", "") });
                }
            }
            return results;
        }

        private static List<Dictionary<string, object>> ApplyPressureUpdates(ReignDbConnection connection, string subjectId, string targetId, string eventId, double day, long ts, Dictionary<string, object> evaluation)
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> item in ReadDictionaryList(evaluation, "pressureUpdates").Take(6))
            {
                string kind = ReadString(item, "kind", "");
                string operation = ReadString(item, "operation", "reinforce").ToLowerInvariant();
                if (!RelationshipPressureKinds.Contains(kind)) continue;
                Dictionary<string, object> existing = QuerySql(connection, "SELECT * FROM relationship_pressures WHERE subject_id=$subject AND target_id=$target AND kind=$kind AND status='active' ORDER BY updated_ts DESC LIMIT 1;",
                    new Dictionary<string, object> { ["subject"] = subjectId, ["target"] = targetId, ["kind"] = kind }).FirstOrDefault();
                double delta = ClampDouble(ReadDouble(item, "intensityDelta", operation == "create" ? 25d : operation == "resolve" ? -100d : 10d), -100d, 100d);
                if (existing == null && operation != "resolve")
                {
                    string id = "rpr_" + Guid.NewGuid().ToString("N");
                    double intensity = ClampDouble(Math.Max(0d, delta), 0d, 100d);
                    ExecuteSql(connection, @"INSERT INTO relationship_pressures(pressure_id,subject_id,target_id,kind,status,intensity,secrecy,summary,related_entities_json,trigger_json,evidence_json,created_day,updated_day,updated_ts,payload_json)
VALUES($id,$subject,$target,$kind,'active',$intensity,$secrecy,$summary,$entities,$trigger,$evidence,$day,$day,$ts,$payload);",
                        new Dictionary<string, object> { ["id"] = id, ["subject"] = subjectId, ["target"] = targetId, ["kind"] = kind, ["intensity"] = intensity, ["secrecy"] = ClampDouble(ReadDouble(item, "secrecy", 0.5d), 0d, 1d), ["summary"] = ReadString(item, "summary", ""), ["entities"] = Json.Serialize(ReadStringList(item, "relatedEntityIds")), ["trigger"] = Json.Serialize(new Dictionary<string, object> { ["triggered"] = ReadBool(item, "triggered", false) }), ["evidence"] = Json.Serialize(new[] { eventId }), ["day"] = day, ["ts"] = ts, ["payload"] = Json.Serialize(item) });
                    results.Add(new Dictionary<string, object> { ["kind"] = kind, ["operation"] = "created", ["intensity"] = intensity, ["pressureId"] = id });
                }
                else if (existing != null)
                {
                    double intensity = ClampDouble(ReadDouble(existing, "intensity", 0d) + delta, 0d, 100d);
                    string status = operation == "resolve" || intensity <= 0d ? "resolved" : "active";
                    ExecuteSql(connection, "UPDATE relationship_pressures SET status=$status,intensity=$intensity,summary=$summary,updated_day=$day,updated_ts=$ts,payload_json=$payload WHERE pressure_id=$id;",
                        new Dictionary<string, object> { ["status"] = status, ["intensity"] = intensity, ["summary"] = FirstNonEmpty(ReadString(item, "summary", ""), ReadString(existing, "summary", "")), ["day"] = day, ["ts"] = ts, ["payload"] = Json.Serialize(item), ["id"] = ReadString(existing, "pressure_id", "") });
                    results.Add(new Dictionary<string, object> { ["kind"] = kind, ["operation"] = status == "resolved" ? "resolved" : operation, ["intensity"] = intensity, ["pressureId"] = ReadString(existing, "pressure_id", "") });
                }
            }
            return results;
        }

        private static Dictionary<string, object> ApplyRelationshipDevelopment(ReignDbConnection connection, string subjectId, string targetId, string eventId, double day, long ts, Dictionary<string, object> evaluation, Dictionary<string, object> facets, List<Dictionary<string, object>> milestones, List<Dictionary<string, object>> pressures)
        {
            Dictionary<string, object> requested = ReadDictionary(evaluation, "development") ?? new Dictionary<string, object>();
            bool triggeredPressure = pressures.Any(item => ReadDouble(item, "intensity", 0d) >= 70d);
            bool milestoneActivated = milestones.Any(item => ReadString(item, "operation", "") == "activated");
            string kind = ReadString(requested, "kind", milestoneActivated ? "milestone_turning_point" : triggeredPressure ? "pressure_turning_point" : "");
            if (string.IsNullOrWhiteSpace(kind)) return new Dictionary<string, object>();
            string id = "rdev_" + Guid.NewGuid().ToString("N");
            string mode = ReadString(requested, "deliveryMode", ReadBool(requested, "requiresPhysical", false) ? "in_person" : "letter").ToLowerInvariant();
            if (mode != "in_person" && mode != "letter" && mode != "offscreen") mode = "in_person";
            ExecuteSql(connection, @"INSERT INTO relationship_developments(development_id,subject_id,target_id,kind,polarity,status,delivery_mode,requires_physical,priority,summary,trigger_event_id,related_entities_json,available_day,expires_day,payload_json,updated_ts)
VALUES($id,$subject,$target,$kind,$polarity,'ready',$mode,$physical,$priority,$summary,$event,$entities,$day,$expires,$payload,$ts);",
                new Dictionary<string, object> { ["id"] = id, ["subject"] = subjectId, ["target"] = targetId, ["kind"] = kind, ["polarity"] = ReadString(requested, "polarity", "mixed"), ["mode"] = mode, ["physical"] = ReadBool(requested, "requiresPhysical", false) ? 1 : 0, ["priority"] = ClampDouble(ReadDouble(requested, "priority", 0.7d), 0d, 1d), ["summary"] = FirstNonEmpty(ReadString(requested, "summary", ""), ReadString(evaluation, "reasoning", "")), ["event"] = eventId, ["entities"] = Json.Serialize(ReadStringList(requested, "relatedEntityIds")), ["day"] = day, ["expires"] = day + 30d, ["payload"] = Json.Serialize(requested), ["ts"] = ts });
            return new Dictionary<string, object> { ["developmentId"] = id, ["kind"] = kind, ["deliveryMode"] = mode, ["status"] = "ready" };
        }

        private static Dictionary<string, object> RelationshipContextApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string subjectId = ReadFirstString(payload, "subjectId", "npcId", "heroStringId");
            string targetId = ReadFirstString(payload, "targetId", "playerId");
            double day = ReadDouble(payload, "worldDay", 0d);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                string timelineId = ReadString(payload, "timelineId", "main");
                double standingDay = day > 0d
                    ? day
                    : LatestKnownWorldDay(connection, campaignId);
                string pairKey = AmbientPairKey(subjectId, targetId);
                Dictionary<string, object> chemistry = QuerySql(connection,
                    "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
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
                List<Dictionary<string, object>> incidents = QuerySql(connection, @"SELECT kind,world_day,summary,rumor_id
FROM relationship_incidents WHERE pair_key=$pair ORDER BY world_day DESC,created_ts DESC LIMIT 12;",
                    new Dictionary<string, object> { ["pair"] = pairKey });
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
                    ["subjectPublicStanding"] = ReadPublicStandingTrait(connection,
                        campaignId, timelineId, subjectId, standingDay),
                    ["targetPublicStanding"] = ReadPublicStandingTrait(connection,
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

        private static List<Dictionary<string, object>> EvaluateCompletedActionRelationships(Dictionary<string, object> payload)
        {
            List<Dictionary<string, object>> changes = new List<Dictionary<string, object>>();
            string status = ReadString(payload, "status", "");
            string actorId = ReadFirstString(payload, "actorHeroStringId", "actorHeroId");
            string targetId = ReadFirstString(payload, "targetHeroStringId", "targetHeroId");
            if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(targetId) || actorId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
            {
                return changes;
            }

            string campaignId = ReadString(payload, "campaignId", "default");
            string actionId = ReadFirstString(payload, "serverActionId", "actionId", "id");
            string actionType = ReadFirstString(payload, "type", "command");
            if (IsTemporaryPartyGuestLifecycleAction(CanonicalCommand(actionType)))
            {
                return changes;
            }
            string summary = FirstNonEmpty(ReadFirstString(payload, "message"), ReadFirstString(ReadDictionary(payload, "result"), "message", "debugMessage"), actionType + " completed");
            Dictionary<string, object> actor = ReadDictionary(payload, "actorHero") ?? new Dictionary<string, object>();
            Dictionary<string, object> target = ReadDictionary(payload, "targetHero") ?? new Dictionary<string, object>();
            List<int> nativeDeltas = new List<int>();
            foreach (Tuple<string, string, Dictionary<string, object>, Dictionary<string, object>> pair in new[]
            {
                Tuple.Create(actorId, targetId, actor, target),
                Tuple.Create(targetId, actorId, target, actor)
            })
            {
                Dictionary<string, object> evaluation = RelationshipEvaluateApi(new Dictionary<string, object>
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
                });
                int delta = ReadInt(evaluation, "nativeRelationDelta", 0);
                if (delta != 0 && !ReadBool(evaluation, "idempotent", false)) nativeDeltas.Add(delta);
            }
            if (nativeDeltas.Count > 0)
            {
                int sharedDelta = Clamp((int)Math.Round(nativeDeltas.Average(), MidpointRounding.AwayFromZero), -25, 15);
                if (sharedDelta != 0)
                {
                    changes.Add(new Dictionary<string, object> { ["subjectId"] = actorId, ["targetId"] = targetId, ["delta"] = sharedDelta, ["eventId"] = "action_relationship_" + actionId });
                }
            }
            return changes;
        }

        private static bool IsTemporaryPartyGuestLifecycleAction(string actionType)
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                List<Dictionary<string, object>> candidates = QuerySql(connection, "SELECT * FROM relationship_developments WHERE status='ready' AND available_day<=$day AND (expires_day=0 OR expires_day>=$day) ORDER BY priority DESC,updated_ts ASC LIMIT 30;", new Dictionary<string, object> { ["day"] = day });
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
                ExecuteSql(connection, "UPDATE relationship_developments SET status='claimed',claimed_day=$day,updated_ts=$ts WHERE development_id=$id AND status='ready';", new Dictionary<string, object> { ["day"] = day, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = ReadString(selected, "development_id", "") });
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                ExecuteSql(connection, "UPDATE relationship_developments SET status=$status,resolved_day=$day,payload_json=$payload,updated_ts=$ts WHERE development_id=$id;", new Dictionary<string, object> { ["status"] = status, ["day"] = ReadDouble(payload, "worldDay", 0d), ["payload"] = Json.Serialize(payload), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = id });
            }
            return new Dictionary<string, object> { ["ok"] = true, ["developmentId"] = id, ["status"] = status };
        }

        private static Dictionary<string, object> CorrespondenceSendApi(Dictionary<string, object> payload)
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
            double deliveryDay = urgentCampaignReport
                ? dispatchDay
                : Math.Max(dispatchDay + 0.25d, ReadDouble(payload, "deliveryDay", dispatchDay + 3d));
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                string reason = ReadString(payload, "reason", "");
                if ((source.Equals("court_economic_transfer", StringComparison.OrdinalIgnoreCase)
                        || urgentCampaignReport)
                    && !string.IsNullOrWhiteSpace(reason))
                {
                    Dictionary<string, object> existing = QuerySql(connection,
                        "SELECT * FROM letters WHERE sender_id=$sender AND recipient_id=$recipient AND source=$source AND reason=$reason ORDER BY created_ts ASC LIMIT 1;",
                        new Dictionary<string, object> { ["sender"] = senderId, ["recipient"] = recipientId, ["source"] = source, ["reason"] = reason })
                        .FirstOrDefault();
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
                if (urgentCampaignReport)
                {
                    string letterId = ReadString(inserted, "letterId", "");
                    ExecuteSql(connection,
                        "UPDATE letters SET status='delivered',delivery_day=$day,delivered_day=$day,updated_ts=$ts WHERE letter_id=$id;",
                        new Dictionary<string, object>
                        {
                            ["day"] = dispatchDay,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["id"] = letterId
                        });
                    inserted["status"] = "delivered";
                    inserted["deliveryDay"] = dispatchDay;
                    inserted["deliveredDay"] = dispatchDay;
                    Dictionary<string, object> delivered = QuerySql(connection,
                        "SELECT * FROM letters WHERE letter_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = letterId }).FirstOrDefault();
                    if (delivered != null) AppendDeliveredLetterHistory(campaignId, recipientId, delivered, dispatchDay);
                }
                List<Dictionary<string, object>> queuedActions = source.Equals("player", StringComparison.OrdinalIgnoreCase)
                    ? QueueImmediateRebellionLetterAction(campaignId, payload, senderId, recipientId, body)
                    : new List<Dictionary<string, object>>();
                if (source.Equals("player", StringComparison.OrdinalIgnoreCase))
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
            string command = LooksLikeExplicitPlayerRebellionDeclaration(body)
                ? "start_ruling_clan_rebellion"
                : LooksLikeExplicitPlayerRebellionSurrender(body) ? "surrender_rebellion" : string.Empty;
            if (string.IsNullOrWhiteSpace(command)) return new List<Dictionary<string, object>>();
            Dictionary<string, object> sender = ReadDictionary(payload, "sender") ?? new Dictionary<string, object>();
            Dictionary<string, object> recipient = ReadDictionary(payload, "recipient") ?? new Dictionary<string, object>();
            Dictionary<string, object> candidate = BuildRebellionActionCandidate(
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
            List<Dictionary<string, object>> queued = EvaluateAndQueueActionCandidates(
                campaignId, payload, body, new List<Dictionary<string, object>> { candidate },
                out List<string> errors, out List<Dictionary<string, object>> rejected);
            if (queued.Count == 0 && errors.Count > 0)
                LogOperational("correspondence.rebellion_action_failed",
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
            Dictionary<string, object> report = QuerySql(connection,
                @"SELECT * FROM letters
WHERE sender_id=$commander AND recipient_id=$player
  AND source='campaign_command_urgent_report'
ORDER BY dispatch_day DESC,created_ts DESC LIMIT 1;",
                new Dictionary<string, object>
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
            List<Dictionary<string, object>> queued = EvaluateAndQueueActionCandidates(
                campaignId, payload, body, new List<Dictionary<string, object>> { candidate },
                out List<string> errors, out List<Dictionary<string, object>> rejected);
            if (queued.Count == 0 && errors.Count > 0)
                LogOperational("correspondence.campaign_guidance_action_failed",
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
            string q = NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(q)
                || ContainsAnyPhrase(q, new[] { "should i", "could i", "would i", "maybe", "perhaps" }))
                return string.Empty;
            if (ContainsAnyPhrase(q, new[] { "cancel the order", "cancel this order", "abort the order" })) return "cancel";
            if (ContainsAnyPhrase(q, new[] { "withdraw", "retreat", "fall back" })) return "withdraw";
            if (ContainsAnyPhrase(q, new[] { "hold position", "hold your position", "wait there", "stay put" })) return "hold";
            if (ContainsAnyPhrase(q, new[] { "adapt", "change the plan", "use your judgment", "use your judgement" })) return "adapt";
            if (ContainsAnyPhrase(q, new[] { "continue", "proceed", "carry on", "execute the order" })) return "continue";
            return string.Empty;
        }

        private static Dictionary<string, object> InsertLetter(ReignDbConnection connection, string senderId, string senderName, string recipientId, string recipientName, string body, string source, string reason, string parentLetterId, string originId, string destinationId, double dispatchDay, double deliveryDay, Dictionary<string, object> payload)
        {
            string a = string.Compare(senderId, recipientId, StringComparison.OrdinalIgnoreCase) <= 0 ? senderId : recipientId;
            string b = a == senderId ? recipientId : senderId;
            Dictionary<string, object> thread = QuerySql(connection, "SELECT * FROM correspondence_threads WHERE participant_a=$a AND participant_b=$b LIMIT 1;", new Dictionary<string, object> { ["a"] = a, ["b"] = b }).FirstOrDefault();
            string threadId = thread == null ? "thread_" + Guid.NewGuid().ToString("N") : ReadString(thread, "thread_id", "");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (thread == null)
                ExecuteSql(connection, "INSERT INTO correspondence_threads(thread_id,participant_a,participant_b,last_activity_day,created_ts,updated_ts) VALUES($id,$a,$b,$day,$ts,$ts);", new Dictionary<string, object> { ["id"] = threadId, ["a"] = a, ["b"] = b, ["day"] = dispatchDay, ["ts"] = ts });
            string letterId = "letter_" + Guid.NewGuid().ToString("N");
            ExecuteSql(connection, @"INSERT INTO letters(letter_id,thread_id,sender_id,sender_name,recipient_id,recipient_name,body,status,source,reason,parent_letter_id,origin_id,destination_id,dispatch_day,delivery_day,payload_json,created_ts,updated_ts)
VALUES($id,$thread,$sender,$senderName,$recipient,$recipientName,$body,'in_transit',$source,$reason,$parent,$origin,$destination,$dispatch,$delivery,$payload,$ts,$ts);",
                new Dictionary<string, object> { ["id"] = letterId, ["thread"] = threadId, ["sender"] = senderId, ["senderName"] = senderName, ["recipient"] = recipientId, ["recipientName"] = recipientName, ["body"] = body, ["source"] = source, ["reason"] = reason, ["parent"] = parentLetterId, ["origin"] = originId, ["destination"] = destinationId, ["dispatch"] = dispatchDay, ["delivery"] = deliveryDay, ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()), ["ts"] = ts });
            ExecuteSql(connection, "UPDATE correspondence_threads SET last_letter_id=$letter,last_activity_day=$day,updated_ts=$ts WHERE thread_id=$thread;", new Dictionary<string, object> { ["letter"] = letterId, ["day"] = dispatchDay, ["ts"] = ts, ["thread"] = threadId });
            return new Dictionary<string, object> { ["letterId"] = letterId, ["threadId"] = threadId, ["status"] = "in_transit", ["dispatchDay"] = dispatchDay, ["deliveryDay"] = deliveryDay };
        }

        private static Dictionary<string, object> CorrespondenceThreadsApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId");
            HashSet<string> contacts = new HashSet<string>(ReadStringList(payload, "contactIds"), StringComparer.OrdinalIgnoreCase);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                List<Dictionary<string, object>> letters = QuerySql(connection, "SELECT * FROM letters WHERE sender_id=$player OR recipient_id=$player ORDER BY dispatch_day ASC,created_ts ASC LIMIT 1000;", new Dictionary<string, object> { ["player"] = playerId })
                    .Where(row => contacts.Count == 0
                        || contacts.Contains(ReadString(row, "sender_id", ""))
                        || contacts.Contains(ReadString(row, "recipient_id", ""))
                        || ReadString(row, "source", "").Equals("campaign_command_urgent_report", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return new Dictionary<string, object> { ["ok"] = true, ["letters"] = letters, ["unreadCount"] = letters.Count(row => ReadString(row, "recipient_id", "").Equals(playerId, StringComparison.OrdinalIgnoreCase) && ReadString(row, "status", "") == "delivered") };
            }
        }

        private static Dictionary<string, object> CorrespondenceTickApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId");
            double day = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> playerDeliveries = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> queuedReplies = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> nativeRelationChanges = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> queuedActions = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                List<Dictionary<string, object>> due = QuerySql(connection, "SELECT * FROM letters WHERE status='in_transit' AND delivery_day<=$day ORDER BY delivery_day ASC LIMIT 30;", new Dictionary<string, object> { ["day"] = day });
                foreach (Dictionary<string, object> letter in due)
                {
                    string letterId = ReadString(letter, "letter_id", "");
                    string senderId = ReadString(letter, "sender_id", "");
                    string recipientId = ReadString(letter, "recipient_id", "");
                    bool toPlayer = recipientId.Equals(playerId, StringComparison.OrdinalIgnoreCase);
                    string status = toPlayer ? "delivered" : "read";
                    ExecuteSql(connection, "UPDATE letters SET status=$status,delivered_day=$day,read_day=$read,updated_ts=$ts WHERE letter_id=$id;", new Dictionary<string, object> { ["status"] = status, ["day"] = day, ["read"] = toPlayer ? 0d : day, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = letterId });
                    letter["status"] = status;
                    letter["delivered_day"] = day;
                    RecordCompletedFavorCorrespondence(connection, campaignId, ReadString(payload, "timelineId", "main"), letter, day);
                    AppendDeliveredLetterHistory(campaignId, playerId, letter, day);
                    if (toPlayer)
                    {
                        playerDeliveries.Add(letter);
                    }
                    else
                    {
                        Dictionary<string, object> storedPayload = TryParseJsonObject(ReadString(letter, "payload_json", "{}")) ?? new Dictionary<string, object>();
                        Dictionary<string, object> recipientProfile = ReadDictionary(storedPayload, "recipient") ?? new Dictionary<string, object>();
                        Dictionary<string, object> relationshipPayload = new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId, ["eventId"] = "mail_delivery_" + letterId, ["eventType"] = "delivered_letter",
                            ["subjectId"] = recipientId, ["targetId"] = senderId, ["summary"] = ReadString(letter, "body", ""), ["worldDay"] = day,
                            ["source"] = "correspondence", ["letterId"] = letterId,
                            ["nativeRelation"] = ReadInt(recipientProfile, "relationToPlayer", 0),
                            ["subject"] = recipientProfile,
                            ["target"] = ReadDictionary(storedPayload, "sender") ?? new Dictionary<string, object>()
                        };
                        Dictionary<string, object> storedMotive = ReadDictionary(storedPayload, "motiveDecision");
                        relationshipPayload["motiveDecision"] = storedMotive ?? BuildConversationDecisionContext(
                            campaignId, "correspondence", recipientId, recipientProfile, LoadCharacterStack(campaignId, recipientId), new Dictionary<string, object>(),
                            ReadString(letter, "body", ""), "Private written correspondence.",
                            new Dictionary<string, object> { ["playerHeroStringId"] = senderId, ["recipientId"] = senderId, ["worldDay"] = day, ["mode"] = "correspondence", ["sceneOpportunity"] = new Dictionary<string, object> { ["private"] = true, ["exposure"] = 0.08d, ["witnessIds"] = new List<string>() } },
                            new Dictionary<string, object> { ["identityState"] = "known" });
                        Dictionary<string, object> relationshipResult = RelationshipEvaluateApi(relationshipPayload);
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
                            Dictionary<string, object> reply = GenerateNpcLetter(campaignId, recipientId, senderId, letter, day);
                            foreach (Dictionary<string, object> queued in QueueAcceptedRebellionLetterReply(
                                campaignId, storedPayload, letter, reply, senderId, recipientId))
                            {
                                queuedActions.Add(queued);
                            }
                            foreach (Dictionary<string, object> queued in QueueAcceptedCampaignOrderLetterReply(
                                campaignId, storedPayload, letter, reply, senderId, recipientId))
                            {
                                queuedActions.Add(queued);
                            }
                            if (ReadBool(reply, "shouldReply", false) && !string.IsNullOrWhiteSpace(ReadString(reply, "body", "")))
                            {
                                double transit = Math.Max(0.25d, ReadDouble(letter, "delivery_day", day) - ReadDouble(letter, "dispatch_day", day));
                                queuedReplies.Add(InsertLetter(connection, recipientId, ReadString(letter, "recipient_name", recipientId), senderId, ReadString(letter, "sender_name", senderId), ReadString(reply, "body", ""), "npc_reply", ReadString(reply, "reason", "reply"), letterId, ReadString(letter, "destination_id", ""), ReadString(letter, "origin_id", ""), day, day + transit, reply));
                            }
                        }
                    }
                }
            }
            return new Dictionary<string, object> { ["ok"] = true, ["deliveredLetters"] = playerDeliveries, ["queuedReplies"] = queuedReplies, ["nativeRelationChanges"] = nativeRelationChanges, ["queuedActions"] = queuedActions };
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
            if (candidates.Count == 0) return candidates;
            string request = ReadString(receivedLetter, "body", "");
            string replyBody = ReadString(npcReply, "body", "");
            return EvaluateAndQueueActionCandidates(campaignId, storedPayload,
                request + "\n" + replyBody, candidates,
                out List<string> errors, out List<Dictionary<string, object>> rejected);
        }

        private static List<Dictionary<string, object>> BuildRebellionLetterReplyCandidates(
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
                BuildRebellionPreparationDialogueCandidates(preparationPayload, recipient,
                    request, replyBody, actionGate);
            if (preparation.Count > 0) return preparation;

            string decision = ReadString(npcReply, "rebellionDecision", "none").ToLowerInvariant();
            string command = string.Empty;
            bool npcIsActor = false;
            if (LooksLikeRebellionRecruitmentRequest(request)
                && (decision == "accept_recruitment" || (decision == "none" && IsUnambiguousActionAcceptance(replyBody))))
            {
                command = "recruit_lord_to_rebellion";
            }
            else if (LooksLikePlayerJoinRebellionRequest(request)
                && (decision == "accept_player_join" || (decision == "none" && IsUnambiguousActionAcceptance(replyBody))))
            {
                command = "join_rebellion";
            }
            else if (LooksLikeRebellionSurrenderDemand(request)
                && (decision == "surrender" || LooksLikeExplicitNpcRebellionSurrender(replyBody)))
            {
                command = "surrender_rebellion";
                npcIsActor = true;
            }
            if (string.IsNullOrWhiteSpace(command)) return new List<Dictionary<string, object>>();

            Dictionary<string, object> player = ReadDictionary(storedPayload, "sender") ?? new Dictionary<string, object>();
            Dictionary<string, object> candidate = npcIsActor
                ? BuildRebellionActionCandidate(command, npcId, ReadString(recipient, "kingdomId", ""), ReadString(recipient, "clanId", ""),
                    playerId, ReadString(player, "kingdomId", ""), ReadString(player, "clanId", ""),
                    "The NPC's delivered written reply explicitly surrendered their named rebellion leadership.")
                : BuildRebellionActionCandidate(command, playerId, ReadString(player, "kingdomId", ""), ReadString(player, "clanId", ""),
                    npcId, ReadString(recipient, "kingdomId", ""), ReadString(recipient, "clanId", ""),
                    command == "recruit_lord_to_rebellion"
                        ? "The NPC's delivered written reply explicitly accepted recruitment into the player's active rebellion."
                        : "The NPC rebel leader's delivered written reply explicitly accepted the player clan into the rebellion.");
            return new List<Dictionary<string, object>> { candidate };
        }

        private static Dictionary<string, object> CorrespondenceReadApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string letterId = ReadFirstString(payload, "letterId", "letter_id");
            double day = ReadDouble(payload, "worldDay", 0d);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                ExecuteSql(connection, "UPDATE letters SET status='read',read_day=$day,updated_ts=$ts WHERE letter_id=$id AND status='delivered';", new Dictionary<string, object> { ["day"] = day, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = letterId });
            }
            return new Dictionary<string, object> { ["ok"] = true, ["letterId"] = letterId, ["status"] = "read" };
        }

        private static Dictionary<string, object> GenerateNpcLetter(string campaignId, string senderId, string recipientId, Dictionary<string, object> receivedLetter, double day)
        {
            Dictionary<string, object> profile = ReadJsonObject(CharacterFile(campaignId, senderId, "profile.json"));
            profile = MergeCorrespondenceRecipientProfile(profile, receivedLetter, senderId);
            Dictionary<string, object> characteristics = LoadCharacterStack(campaignId, senderId);
            Dictionary<string, object> relationship = RelationshipContextApi(new Dictionary<string, object> { ["campaignId"] = campaignId, ["subjectId"] = senderId, ["targetId"] = recipientId });
            string senderName = ReadString(profile, "name", senderId);
            string recipientName = ReadString(receivedLetter, "sender_name", recipientId);
            string letterBody = ReadString(receivedLetter, "body", "");
            Dictionary<string, object> memoryContextPayload = new Dictionary<string, object>(profile, StringComparer.OrdinalIgnoreCase)
            {
                ["worldDay"] = day, ["playerHeroStringId"] = recipientId, ["interactionMode"] = "correspondence",
            };
            string memoryContext = ReadString(BuildNpcMemoryPacket(campaignId, senderId, recipientId, "", letterBody, 1200, memoryContextPayload), "memoryPacket", "");
            PromptEnvelope promptEnvelope = BuildCorrespondencePromptEnvelope(campaignId, senderId, senderName, recipientId, recipientName, day, letterBody, profile, characteristics, relationship, memoryContext);
            Dictionary<string, object> motiveDecision = ReadDictionary(promptEnvelope.Diagnostics, "motiveDecision") ?? new Dictionary<string, object>();
            Dictionary<string, object> llmRequest = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["heroStringId"] = senderId,
                ["requestType"] = "correspondence", ["maxTokens"] = 900, ["temperature"] = 0.65d,
                ["messages"] = promptEnvelope.Messages,
                ["promptEnvelope"] = promptEnvelope.Diagnostics,
                ["promptCacheEligible"] = true,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };
            string correlationId = EnsureCorrelationId(llmRequest);
            Dictionary<string, object> llm = ChatWithLlm(llmRequest);
            llm = RetryMalformedStructuredResponse(
                llm, llmRequest, campaignId, correlationId, "correspondence", senderId,
                "mail_" + ReadString(receivedLetter, "letter_id", ""));
            Dictionary<string, object> parsed = ReadBool(llm, "ok", false) ? TryParseJsonObject(ReadString(llm, "content", "")) : null;
            if (parsed != null)
            {
                string body = RemoveSpokenNumericSkillLevels(ReadFirstString(parsed, "body", "response", "letter"), out bool skillNumericRepairApplied);
                parsed["body"] = LimitText(body, 4000);
                parsed["skillNumericRepairApplied"] = skillNumericRepairApplied;
                parsed["shouldReply"] = ReadBool(parsed, "shouldReply", true);
                parsed["motiveDecision"] = motiveDecision;
                return parsed;
            }
            return new Dictionary<string, object> { ["shouldReply"] = false, ["body"] = "", ["error"] = ReadString(llm, "error", "") };
        }

        private static Dictionary<string, object> MergeCorrespondenceRecipientProfile(
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

        private static void TryQueueRelationshipMaintenanceLetter(ReignDbConnection connection, string campaignId, string playerId, double day, List<Dictionary<string, object>> queued)
        {
            if (string.IsNullOrWhiteSpace(playerId) || ((int)Math.Floor(day)) % 3 != 0) return;
            List<Dictionary<string, object>> candidates = QuerySql(connection, "SELECT * FROM relationships WHERE target_id=$player ORDER BY updated_ts DESC LIMIT 30;", new Dictionary<string, object> { ["player"] = playerId });
            foreach (Dictionary<string, object> state in candidates)
            {
                string senderId = ReadString(state, "subject_id", "");
                double strongest = RelationshipFacetKeys.Select(key => Math.Abs(ReadDouble(state, key, 0d))).DefaultIfEmpty(0d).Max();
                if (strongest < 60d) continue;
                Dictionary<string, object> recent = QuerySql(connection, "SELECT * FROM letters WHERE sender_id=$sender AND recipient_id=$player ORDER BY dispatch_day DESC LIMIT 1;", new Dictionary<string, object> { ["sender"] = senderId, ["player"] = playerId }).FirstOrDefault();
                if (recent != null && day - ReadDouble(recent, "dispatch_day", 0d) < 14d) continue;
                Dictionary<string, object> profile = ReadJsonObject(CharacterFile(campaignId, senderId, "profile.json"));
                Dictionary<string, object> synthetic = new Dictionary<string, object> { ["sender_name"] = ReadString(ReadJsonObject(CharacterFile(campaignId, playerId, "profile.json")), "name", "Player"), ["body"] = "No new letter has arrived. Decide whether this relationship now gives you a concrete reason to write.", ["reason"] = "relationship maintenance" };
                Dictionary<string, object> generated = GenerateNpcLetter(campaignId, senderId, playerId, synthetic, day);
                if (!ReadBool(generated, "shouldReply", false) || string.IsNullOrWhiteSpace(ReadString(generated, "body", ""))) return;
                queued.Add(InsertLetter(connection, senderId, ReadString(profile, "name", senderId), playerId, ReadString(synthetic, "sender_name", "Player"), ReadString(generated, "body", ""), "npc_initiated", ReadString(generated, "reason", "relationship maintenance"), "", ReadString(profile, "currentSettlementId", ""), "", day, day + 3d, generated));
                return;
            }
        }

        private static void TryQueueRelationshipDevelopmentLetter(ReignDbConnection connection, string campaignId, string playerId, double day, List<Dictionary<string, object>> queued)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;
            Dictionary<string, object> development = QuerySql(connection, @"SELECT * FROM relationship_developments
WHERE status='ready' AND delivery_mode='letter' AND target_id=$player AND available_day<=$day AND (expires_day=0 OR expires_day>=$day)
ORDER BY priority DESC,updated_ts ASC LIMIT 1;", new Dictionary<string, object> { ["player"] = playerId, ["day"] = day }).FirstOrDefault();
            if (development == null) return;

            string senderId = ReadString(development, "subject_id", "");
            Dictionary<string, object> profile = ReadJsonObject(CharacterFile(campaignId, senderId, "profile.json"));
            Dictionary<string, object> playerProfile = ReadJsonObject(CharacterFile(campaignId, playerId, "profile.json"));
            Dictionary<string, object> synthetic = new Dictionary<string, object>
            {
                ["sender_name"] = ReadString(playerProfile, "name", "Player"),
                ["body"] = "A relationship development gives you a concrete reason to write now: " + ReadString(development, "summary", ""),
                ["reason"] = ReadString(development, "kind", "relationship development")
            };
            Dictionary<string, object> generated = GenerateNpcLetter(campaignId, senderId, playerId, synthetic, day);
            if (!ReadBool(generated, "shouldReply", false) || string.IsNullOrWhiteSpace(ReadString(generated, "body", ""))) return;

            Dictionary<string, object> letter = InsertLetter(connection, senderId, ReadString(profile, "name", senderId), playerId, ReadString(playerProfile, "name", "Player"), ReadString(generated, "body", ""), "relationship_development", ReadString(generated, "reason", ReadString(development, "kind", "relationship development")), "", ReadString(profile, "currentSettlementId", ""), ReadString(playerProfile, "currentSettlementId", ""), day, day + 3d, generated);
            queued.Add(letter);
            ExecuteSql(connection, "UPDATE relationship_developments SET status='resolved',resolved_day=$day,payload_json=$payload,updated_ts=$ts WHERE development_id=$id;", new Dictionary<string, object>
            {
                ["day"] = day,
                ["payload"] = Json.Serialize(new Dictionary<string, object> { ["letterId"] = ReadString(letter, "letterId", ""), ["resolution"] = "correspondence_dispatched" }),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["id"] = ReadString(development, "development_id", "")
            });
        }

        private static void AppendDeliveredLetterHistory(string campaignId, string playerId, Dictionary<string, object> letter, double day)
        {
            string senderId = ReadString(letter, "sender_id", "");
            string recipientId = ReadString(letter, "recipient_id", "");
            string npcId = senderId.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? recipientId : senderId;
            if (string.IsNullOrWhiteSpace(npcId)) return;
            Dictionary<string, object> line = DialogueLine(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), senderId.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? "player" : "npc", ReadString(letter, "sender_name", senderId), ReadString(letter, "body", ""));
            line["channel"] = "correspondence";
            line["letterId"] = ReadString(letter, "letter_id", "");
            line["threadId"] = ReadString(letter, "thread_id", "");
            line["senderId"] = senderId;
            line["recipientId"] = recipientId;
            line["worldDay"] = day;
            line["direction"] = senderId.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? "sent" : "received";
            AppendJsonLineToPath(CharacterFile(campaignId, npcId, "history", "dialogue.jsonl"), line);
            StoreCorrespondenceTurn(campaignId, npcId, playerId, letter, day);
        }

        private static Dictionary<string, object> RelationshipFacetSnapshot(Dictionary<string, object> row)
        {
            return RelationshipFacetKeys.ToDictionary(key => key, key => (object)Math.Round(ReadDouble(row, key, 0d), 2), StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> RelationshipFacetDescriptions(Dictionary<string, object> row)
        {
            return RelationshipFacetKeys.ToDictionary(key => key, key => (object)RelationshipFacetDescriptor(key, ReadDouble(row, key, 0d)), StringComparer.OrdinalIgnoreCase);
        }

        private static string RelationshipFacetDescriptor(string key, double value)
        {
            string[] levels;
            switch ((key ?? "").ToLowerInvariant())
            {
                case "trust": levels = new[] { "is certain they will betray them", "deeply distrusts them", "expects deception", "remains guarded", "has no settled trust", "offers cautious trust", "trusts them", "relies on them deeply", "trusts them completely" }; break;
                case "respect": levels = new[] { "holds them in absolute contempt", "despises their standing or ability", "dismisses them", "shows little respect", "is neutral about their worth", "grants measured respect", "respects them", "holds them in high esteem", "reveres them" }; break;
                case "affection": levels = new[] { "hates them", "feels profound aversion", "strongly dislikes them", "feels cool toward them", "has no strong affection", "feels some warmth", "is fond of them", "loves them deeply", "is consumed by love for them" }; break;
                case "loyalty": levels = new[] { "is ready to betray them", "would abandon them readily", "is disloyal", "offers weak conditional allegiance", "has no settled loyalty", "offers conditional support", "is loyal", "is devoted", "would sacrifice almost anything for them" }; break;
                case "fear": levels = new[] { "feels completely safe and unintimidated", "feels strongly secure around them", "rarely regards them as a threat", "feels mostly safe", "feels neither safe nor afraid", "is wary of them", "fears them", "is deeply afraid", "is terrified of them" }; break;
                case "resentment": levels = new[] { "has fully forgiven and released every grievance", "feels deeply reconciled", "has largely forgiven them", "is inclined to let grievances go", "holds no active grievance", "carries mild resentment", "is bitter toward them", "nurses a powerful grievance", "is consumed by resentment" }; break;
                case "rivalry": levels = new[] { "sees their success as completely shared", "actively wants them to succeed", "feels strongly cooperative", "does not compete with them", "feels no rivalry", "compares themselves against them", "treats them as a rival", "must surpass them", "is obsessed with defeating them" }; break;
                case "envy": levels = new[] { "is wholly content with their advantages", "admiringly accepts their success", "rarely compares themselves", "feels little envy", "feels no settled envy", "notices what they possess", "envies them", "finds their advantages intolerable", "is consumed by envy" }; break;
                case "attraction": levels = new[] { "feels active physical or romantic repulsion", "is strongly repelled", "finds them unattractive", "feels little attraction", "feels no settled attraction", "notices some attraction", "desires them", "feels intense desire", "is consumed by desire" }; break;
                case "jealousy": levels = new[] { "feels completely secure and nonpossessive", "actively welcomes their independence", "is secure about rivals", "feels little jealousy", "feels no settled jealousy", "is sensitive to divided attention", "is jealous", "is highly possessive", "is obsessively jealous" }; break;
                case "dependence": levels = new[] { "is deliberately disentangled from them", "rejects relying on them", "is strongly independent", "needs little from them", "has no settled dependence", "relies on them somewhat", "depends on them", "struggles to function without them", "cannot imagine functioning without them" }; break;
                case "interest_alignment": levels = new[] { "has completely incompatible interests", "sees their interests as directly opposed", "expects conflict between their aims", "shares few interests", "has no clear alignment", "shares some useful interests", "has aligned interests", "sees their fortunes as joined", "believes their interests are inseparable" }; break;
                default: levels = new[] { "believes they are owed an enormous debt", "believes they are owed greatly", "believes the other owes them", "expects some repayment", "feels no debt either way", "feels they owe a modest favor", "feels indebted", "feels profoundly indebted", "believes they owe nearly everything" }; break;
            }
            int index = value <= -76 ? 0 : value <= -51 ? 1 : value <= -26 ? 2 : value <= -6 ? 3 : value < 6 ? 4 : value < 26 ? 5 : value < 51 ? 6 : value < 76 ? 7 : 8;
            return levels[index];
        }

        private static double ClampRelationship(double value) => ClampDouble(value, -100d, 100d);
        private static double ValueAsDouble(object value)
        {
            if (value == null) return 0d;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0d;
        }
    }
}
