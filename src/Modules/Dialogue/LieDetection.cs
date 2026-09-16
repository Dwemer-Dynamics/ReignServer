using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string LieFormulaVersion = "reign_historical_lie_v1";

        private static void EnsureWorldHistoryLieDetectionSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_lie_checks (
lie_check_id TEXT PRIMARY KEY,episode_key TEXT NOT NULL UNIQUE,claim_check_id TEXT NOT NULL DEFAULT '',campaign_id TEXT NOT NULL,
timeline_id TEXT NOT NULL,world_day REAL NOT NULL,claimant_id TEXT NOT NULL,target_id TEXT NOT NULL,raw_claim TEXT NOT NULL,
normalized_claim TEXT NOT NULL,objective_verdict TEXT NOT NULL,knowledge_basis TEXT NOT NULL,claimant_roguery REAL,
claimant_charm REAL,claimant_average REAL,target_roguery REAL,target_charm REAL,target_average REAL,formula_version TEXT NOT NULL,
success_chance REAL,roll_value REAL,outcome TEXT NOT NULL,reaction_strategy TEXT NOT NULL DEFAULT '',exploit_opening INTEGER NOT NULL DEFAULT 0,
evidence_event_ids_json TEXT NOT NULL DEFAULT '[]',known_event_ids_json TEXT NOT NULL DEFAULT '[]',matched_json TEXT NOT NULL DEFAULT '[]',
mismatched_json TEXT NOT NULL DEFAULT '[]',prompt_packet_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL); ");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_claim_beliefs (
belief_id TEXT PRIMARY KEY,lie_check_id TEXT NOT NULL,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,believer_id TEXT NOT NULL,
claimant_id TEXT NOT NULL,raw_claim TEXT NOT NULL,normalized_claim TEXT NOT NULL,state TEXT NOT NULL,confidence REAL NOT NULL,
evidence_event_ids_json TEXT NOT NULL DEFAULT '[]',created_day REAL NOT NULL,disproven_day REAL NOT NULL DEFAULT -1,
discovered_event_ids_json TEXT NOT NULL DEFAULT '[]',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL); ");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_lie_participants ON world_history_lie_checks(target_id,claimant_id,world_day DESC);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_belief_owner_state ON world_history_claim_beliefs(believer_id,state,updated_ts DESC);");
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('world_history_lie_detection_version','1');");
        }

        private static Dictionary<string, object> WorldHistoryLieCheckApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string claimantId = ReadFirstString(payload, "claimantId", "playerId", "mainHeroStringId");
            string targetId = ReadFirstString(payload, "targetId", "speakerId", "npcId", "heroStringId");
            string targetKingdomId = ReadFirstString(payload, "targetKingdomId", "speakerKingdomId", "npcKingdomId");
            string claim = ReadFirstString(payload, "claim", "claimText", "playerText", "text", "message");
            double worldDay = ReadDouble(payload, "worldDay", ReadDouble(payload, "currentWorldDay", 0d));
            bool lookupOnly = ReadBool(payload, "lookupOnly", false) || IsWorldHistoryLookupRequest(claim);
            if (string.IsNullOrWhiteSpace(claim) || string.IsNullOrWhiteSpace(targetId) || (!lookupOnly && string.IsNullOrWhiteSpace(claimantId)))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = lookupOnly ? "claim and targetId are required." : "claim, claimantId, and targetId are required." };
            }
            if (!lookupOnly && IsPlayerLieDetectionTarget(campaignId, targetId))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["outcome"] = "not_evaluated_player",
                    ["recorded"] = false,
                    ["reason"] = "Lie detection models NPC beliefs only; Reign never performs lie detection for the player character."
                };
            }

            Dictionary<string, object> verifyPayload = new Dictionary<string, object>(payload, StringComparer.OrdinalIgnoreCase)
            {
                ["speakerId"] = targetId,
                ["speakerKingdomId"] = targetKingdomId,
                ["lookupOnly"] = lookupOnly
            };
            if (lookupOnly) verifyPayload["claimantId"] = string.Empty;
            Dictionary<string, object> verification = lookupOnly
                ? null
                : VerifySelfAdmittedFalsehood(payload, claim, claimantId, targetId);
            if (verification == null && !lookupOnly)
            {
                verification = VerifyNativeCurrentLocationClaim(payload, claim, claimantId, targetId);
            }
            if (verification == null) verification = WorldHistoryVerifyApi(verifyPayload);
            if (!ReadBool(verification, "ok", false)) return verification;

            // Older game-module builds route every history helper request through the lie-check
            // endpoint. Treat question-shaped requests as factual lookups so a running campaign can
            // consume the same speaker-safe evidence without recording a fictional lie episode.
            if (lookupOnly)
            {
                Dictionary<string, object> speakerKnowledge = ReadDictionary(verification, "speakerKnowledge") ?? new Dictionary<string, object>();
                List<string> knownEventIds = ReadStringList(speakerKnowledge, "eventIds");
                Dictionary<string, object> promptPacket = new Dictionary<string, object>
                {
                    ["claimCheckId"] = ReadString(verification, "checkId", ""),
                    ["claimFamily"] = ReadString(verification, "claimFamily", "historical_lookup"),
                    ["purpose"] = "historical_lookup",
                    ["speakerVerdict"] = knownEventIds.Count > 0 ? "evidence_available" : "unknown",
                    ["knownEvidenceEventIds"] = knownEventIds,
                    ["knownEvents"] = ReadDictionaryList(speakerKnowledge, "events"),
                    ["privacyRule"] = "Use only this speaker-visible historical evidence. Do not invent omitted facts.",
                    ["behaviorRule"] = knownEventIds.Count > 0
                        ? "Answer the historical question from the supplied event summaries and entity roles. State uncertainty where the evidence is incomplete."
                        : "The character lacks reliable visible evidence for this historical question and should say so without inventing details."
                };
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["lieCheckId"] = string.Empty,
                    ["claimCheckId"] = ReadString(verification, "checkId", ""),
                    ["campaignId"] = campaignId,
                    ["timelineId"] = ReadString(verification, "timelineId", ReadString(payload, "timelineId", "main")),
                    ["claimantId"] = string.Empty,
                    ["targetId"] = targetId,
                    ["objectiveVerdict"] = ReadString(verification, "objectiveVerdict", "insufficient_evidence"),
                    ["knowledgeBasis"] = knownEventIds.Count > 0 ? "speaker_visible_history" : "none",
                    ["outcome"] = "historical_lookup",
                    ["knownEventIds"] = knownEventIds,
                    ["promptPacket"] = promptPacket,
                    ["verification"] = verification,
                    ["cached"] = false
                };
            }

            string timelineId = ReadString(verification, "timelineId", ReadString(payload, "timelineId", "main"));
            string objectiveVerdict = ReadString(verification, "objectiveVerdict", "insufficient_evidence");
            List<string> evidenceIds = ReadStringList(verification, "evidenceEventIds").Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> mismatched = ReadStringList(verification, "mismatchedComponents");
            List<string> matched = ReadStringList(verification, "matchedComponents");
            string normalizedClaim = NormalizeLookup(claim);

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistorySchema(connection);
                EnsureWorldHistoryLieDetectionSchema(connection);
                Dictionary<string, object> knowledge = ReadBool(verification, "nativeContextEvidence", false)
                    ? ReadDictionary(verification, "speakerKnowledge") ?? new Dictionary<string, object>()
                    : DetermineWorldHistoryKnowledge(connection, evidenceIds, targetId, targetKingdomId, worldDay);
                string knowledgeBasis = ReadString(knowledge, "basis", "none");
                List<string> knownIds = ReadStringList(knowledge, "eventIds");
                ReconcileWorldHistoryClaimBeliefs(connection, targetId, knownIds, worldDay);
                string episodeKey = StableLieEpisodeKey(campaignId, timelineId, claimantId, targetId, normalizedClaim, evidenceIds, knowledgeBasis);
                Dictionary<string, object> existing = QuerySql(connection, "SELECT * FROM world_history_lie_checks WHERE episode_key=$key LIMIT 1;", new Dictionary<string, object> { ["key"] = episodeKey }).FirstOrDefault();
                if (existing != null) return BuildLieCheckResponse(existing, verification, true);

                Dictionary<string, object> claimantSkills = ResolveLieSkills(payload, "claimantSkills", campaignId, claimantId);
                Dictionary<string, object> targetSkills = ResolveLieSkills(payload, "targetSkills", campaignId, targetId);
                bool claimantSkillsReady = ReadBool(claimantSkills, "available", false);
                bool targetSkillsReady = ReadBool(targetSkills, "available", false);
                bool materiallyFalse = string.Equals(objectiveVerdict, "contradicted", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(objectiveVerdict, "partially_verified", StringComparison.OrdinalIgnoreCase) && mismatched.Count > 0);

                double? chance = null;
                double? roll = null;
                string outcome;
                if (!materiallyFalse)
                {
                    outcome = string.Equals(objectiveVerdict, "verified", StringComparison.OrdinalIgnoreCase) ? "truthful" : "not_adjudicable";
                }
                else if (string.Equals(knowledgeBasis, "firsthand", StringComparison.OrdinalIgnoreCase))
                {
                    outcome = "detected_firsthand";
                }
                else if (string.Equals(knowledgeBasis, "secondhand", StringComparison.OrdinalIgnoreCase))
                {
                    if (!claimantSkillsReady || !targetSkillsReady)
                    {
                        outcome = "insufficient_skill_data";
                    }
                    else
                    {
                        chance = HistoricalLieSuccessChance(ReadDouble(claimantSkills, "average", 0d), ReadDouble(targetSkills, "average", 0d));
                        roll = StableLieRoll(episodeKey);
                        outcome = roll.Value < chance.Value ? "deception_succeeded" : "detected_secondhand";
                    }
                }
                else
                {
                    outcome = "not_detected_no_knowledge";
                }

                bool detected = outcome == "detected_firsthand" || outcome == "detected_secondhand";
                Dictionary<string, object> reaction = detected ? SelectLieReaction(connection, campaignId, claimantId, targetId, payload) : new Dictionary<string, object>();
                string reactionStrategy = ReadString(reaction, "strategy", "");
                bool exploitOpening = ReadBool(reaction, "exploitOpening", false);
                string lieCheckId = "whl_" + Guid.NewGuid().ToString("N");
                Dictionary<string, object> promptPacket = BuildLiePromptPacket(verification, outcome, knowledgeBasis, knownIds, reactionStrategy, exploitOpening);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                ExecuteSql(connection, @"INSERT INTO world_history_lie_checks(
lie_check_id,episode_key,claim_check_id,campaign_id,timeline_id,world_day,claimant_id,target_id,raw_claim,normalized_claim,
objective_verdict,knowledge_basis,claimant_roguery,claimant_charm,claimant_average,target_roguery,target_charm,target_average,
formula_version,success_chance,roll_value,outcome,reaction_strategy,exploit_opening,evidence_event_ids_json,known_event_ids_json,
matched_json,mismatched_json,prompt_packet_json,created_ts,updated_ts)
VALUES($id,$episode,$claimCheck,$campaign,$timeline,$day,$claimant,$target,$claim,$normalized,$verdict,$basis,
$cr,$cc,$ca,$tr,$tc,$ta,$formula,$chance,$roll,$outcome,$reaction,$exploit,$evidence,$known,$matched,$mismatched,$packet,$ts,$ts);",
                    new Dictionary<string, object>
                    {
                        ["id"] = lieCheckId, ["episode"] = episodeKey, ["claimCheck"] = ReadString(verification, "checkId", ""),
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay, ["claimant"] = claimantId, ["target"] = targetId,
                        ["claim"] = claim, ["normalized"] = normalizedClaim, ["verdict"] = objectiveVerdict, ["basis"] = knowledgeBasis,
                        ["cr"] = DbNullable(claimantSkills, "roguery"), ["cc"] = DbNullable(claimantSkills, "charm"), ["ca"] = DbNullable(claimantSkills, "average"),
                        ["tr"] = DbNullable(targetSkills, "roguery"), ["tc"] = DbNullable(targetSkills, "charm"), ["ta"] = DbNullable(targetSkills, "average"),
                        ["formula"] = LieFormulaVersion, ["chance"] = chance.HasValue ? (object)chance.Value : DBNull.Value,
                        ["roll"] = roll.HasValue ? (object)roll.Value : DBNull.Value, ["outcome"] = outcome, ["reaction"] = reactionStrategy,
                        ["exploit"] = exploitOpening ? 1 : 0, ["evidence"] = Json.Serialize(evidenceIds), ["known"] = Json.Serialize(knownIds),
                        ["matched"] = Json.Serialize(matched), ["mismatched"] = Json.Serialize(mismatched), ["packet"] = Json.Serialize(promptPacket), ["ts"] = ts
                    });

                PersistLieConsequence(connection, lieCheckId, campaignId, timelineId, claimantId, targetId, claim, normalizedClaim,
                    objectiveVerdict, outcome, reactionStrategy, exploitOpening, evidenceIds, worldDay, ts);
                Dictionary<string, object> stored = QuerySql(connection, "SELECT * FROM world_history_lie_checks WHERE lie_check_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = lieCheckId }).FirstOrDefault();
                return BuildLieCheckResponse(stored, verification, false);
            }
        }

        private static bool IsPlayerLieDetectionTarget(string campaignId, string targetId)
        {
            if (string.IsNullOrWhiteSpace(targetId)) return false;
            if (targetId.Equals("main_hero", StringComparison.OrdinalIgnoreCase)) return true;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureCategorizedMemorySchema(connection);
                return QuerySql(connection, @"SELECT player_id FROM conversation_sessions
WHERE campaign_id=$campaign AND lower(player_id)=lower($target) AND trim(player_id)<>'' LIMIT 1;",
                    new Dictionary<string, object>{{"campaign",campaignId},{"target",targetId}}).Any();
            }
        }

        private static void RunPlayerLieDetectionRemovalMigration(ReignDbConnection connection)
        {
            EnsureWorldHistoryLieDetectionSchema(connection);
            Dictionary<string, object> applied = QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='player_lie_detection_removed_v1' LIMIT 1;").FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase)) return;

            HashSet<string> playerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main_hero" };
            foreach (Dictionary<string, object> row in QuerySql(connection,
                "SELECT DISTINCT player_id FROM conversation_sessions WHERE trim(player_id)<>'';"))
            {
                string playerId = ReadString(row, "player_id", "");
                if (!string.IsNullOrWhiteSpace(playerId)) playerIds.Add(playerId);
            }
            List<string> lieCheckIds = QuerySql(connection,
                "SELECT lie_check_id,target_id FROM world_history_lie_checks;")
                .Where(row => playerIds.Contains(ReadString(row, "target_id", "")))
                .Select(row => ReadString(row, "lie_check_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string lieCheckId in lieCheckIds)
            {
                Dictionary<string, object> parameters = new Dictionary<string, object> { ["id"] = lieCheckId };
                ExecuteSql(connection, "DELETE FROM memory_fts WHERE memory_id IN (SELECT memory_id FROM memories WHERE event_id=$id OR memory_id=$memory OR memory_id=$discovery);",
                    new Dictionary<string, object>{{"id",lieCheckId},{"memory","memory_"+lieCheckId},{"discovery","memory_discovery_"+lieCheckId}});
                ExecuteSql(connection, "DELETE FROM memories WHERE event_id=$id OR memory_id=$memory OR memory_id=$discovery;",
                    new Dictionary<string, object>{{"id",lieCheckId},{"memory","memory_"+lieCheckId},{"discovery","memory_discovery_"+lieCheckId}});
                ExecuteSql(connection, "DELETE FROM beliefs WHERE event_id=$id;", parameters);
                ExecuteSql(connection, "DELETE FROM world_history_claim_beliefs WHERE lie_check_id=$id;", parameters);
                ExecuteSql(connection, "DELETE FROM world_history_lie_checks WHERE lie_check_id=$id;", parameters);
            }
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('player_lie_detection_removed_v1','complete');");
        }

        private static Dictionary<string, object> VerifySelfAdmittedFalsehood(
            Dictionary<string, object> payload, string claim, string claimantId, string targetId)
        {
            string q = NormalizeLookup(claim);
            if (string.IsNullOrWhiteSpace(q)
                || ContainsAny(q, "if i said", "if i were", "suppose i", "hypothetically", "as an example"))
            {
                return null;
            }

            bool directAdmission = ContainsExplicitDeceptionAdmission(q) || ContainsAny(q,
                "i know that account is false", "i know this account is false",
                "i know that claim is false", "i know this claim is false",
                "what i just said is false", "what i said is false",
                "that account is false", "this account is false");
            if (!directAdmission) return null;

            string evidenceId = "native_self_admitted_falsehood_" + PromptHash(claimantId + "|" + targetId + "|" + q).Substring(0, 20).ToLowerInvariant();
            Dictionary<string, object> nativeEvent = new Dictionary<string, object>
            {
                ["event_id"] = evidenceId,
                ["event_type"] = "direct_self_admitted_falsehood",
                ["summary"] = claimantId + " directly told " + targetId + " that the account was false and that they were deliberately lying.",
                ["source"] = "current_conversation_direct_admission"
            };
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["checkId"] = "whc_admission_" + PromptHash(claim + "|" + evidenceId).Substring(0, 20).ToLowerInvariant(),
                ["campaignId"] = ReadString(payload, "campaignId", "default"),
                ["timelineId"] = ReadString(payload, "timelineId", "main"),
                ["claim"] = claim,
                ["claimantId"] = claimantId,
                ["speakerId"] = targetId,
                ["purpose"] = "self_claim",
                ["claimFamily"] = "self_admitted_falsehood",
                ["lookupOnly"] = false,
                ["objectiveVerdict"] = "contradicted",
                ["speakerVerdict"] = "contradicted",
                ["deceptiveIntent"] = true,
                ["directAdmission"] = true,
                ["explanation"] = "The claimant directly admitted in this conversation that the account was false and deliberately presented as a lie.",
                ["evidenceEventIds"] = new List<string> { evidenceId },
                ["speakerEvidenceEventIds"] = new List<string> { evidenceId },
                ["matchedComponents"] = new List<string>(),
                ["mismatchedComponents"] = new List<string> { "direct self-admission of falsehood and deceptive intent" },
                ["speakerKnowledge"] = new Dictionary<string, object>
                {
                    ["basis"] = "firsthand",
                    ["eventIds"] = new List<string> { evidenceId },
                    ["firsthandEventIds"] = new List<string> { evidenceId },
                    ["secondhandEventIds"] = new List<string>(),
                    ["acquisitionModes"] = new List<string> { "direct_conversation_admission" },
                    ["events"] = new List<Dictionary<string, object>> { nativeEvent }
                },
                ["nativeContextEvidence"] = true,
                ["candidateCount"] = 1,
                ["candidateEventIds"] = new List<string> { evidenceId },
                ["durationMs"] = 0,
                ["cached"] = false
            };
        }

        private static Dictionary<string, object> VerifyNativeCurrentLocationClaim(
            Dictionary<string, object> payload, string claim, string claimantId, string targetId)
        {
            Dictionary<string, object> native = ReadDictionary(payload, "nativeContext");
            if (native == null || !ReadBool(native, "sameCurrentSettlement", false)) return null;
            string claimantSettlementId = ReadString(native, "claimantCurrentSettlementId", "");
            string targetSettlementId = ReadString(native, "targetCurrentSettlementId", "");
            string settlementName = FirstNonEmpty(ReadString(native, "claimantCurrentSettlementName", ""), ReadString(native, "targetCurrentSettlementName", ""));
            if (string.IsNullOrWhiteSpace(claimantSettlementId)
                || !claimantSettlementId.Equals(targetSettlementId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(settlementName)) return null;

            string q = NormalizeLookup(claim);
            string normalizedSettlement = NormalizeLookup(settlementName);
            if (!ContradictsNativeCurrentSettlement(claim, settlementName)) return null;
            bool deniedPresent = ContainsAny(q, "i am not presently in", "im not presently in", "i am not currently in", "im not currently in", "i am not in", "im not in",
                    "we are not presently in", "were not presently in", "we are not currently in", "were not currently in", "we are not in", "were not in")
                || q.Contains("not " + normalizedSettlement)
                || q.Contains("rather than " + normalizedSettlement);
            bool deniedEver = ContainsAny(q, "i never entered", "i have never entered", "ive never entered", "i never went to", "i have never been to", "ive never been to", "i was never in",
                "we never entered", "we have never entered", "weve never entered", "we never went to", "we have never been to", "weve never been to", "we were never in");
            if (!deniedPresent && !deniedEver) return null;
            bool deceptiveIntent = ContainsConversationDeceptiveIntentEvidence(claim);

            string evidenceId = "native_current_settlement_" + PromptHash(claimantId + "|" + targetId + "|" + claimantSettlementId).Substring(0, 20).ToLowerInvariant();
            Dictionary<string, object> nativeEvent = new Dictionary<string, object>
            {
                ["event_id"] = evidenceId,
                ["event_type"] = "native_current_settlement_presence",
                ["summary"] = claimantId + " and " + targetId + " are presently together in " + settlementName + ".",
                ["location_id"] = claimantSettlementId,
                ["location_name"] = settlementName,
                ["source"] = "bannerlord_native_live_context"
            };
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["checkId"] = "whc_native_" + PromptHash(claim + "|" + evidenceId).Substring(0, 20).ToLowerInvariant(),
                ["campaignId"] = ReadString(payload, "campaignId", "default"),
                ["timelineId"] = ReadString(payload, "timelineId", "main"),
                ["claim"] = claim,
                ["claimantId"] = claimantId,
                ["speakerId"] = targetId,
                ["purpose"] = "self_claim",
                ["claimFamily"] = "current_location",
                ["lookupOnly"] = false,
                ["objectiveVerdict"] = "contradicted",
                ["speakerVerdict"] = "contradicted",
                ["deceptiveIntent"] = deceptiveIntent,
                ["directAdmission"] = ContainsExplicitDeceptionAdmission(q),
                ["explanation"] = "The claimant and observer are presently together in " + settlementName + "; this directly contradicts the location denial.",
                ["evidenceEventIds"] = new List<string> { evidenceId },
                ["speakerEvidenceEventIds"] = new List<string> { evidenceId },
                ["matchedComponents"] = new List<string>(),
                ["mismatchedComponents"] = new List<string> { deniedEver ? "never entered current settlement" : "present location" },
                ["speakerKnowledge"] = new Dictionary<string, object>
                {
                    ["basis"] = "firsthand",
                    ["eventIds"] = new List<string> { evidenceId },
                    ["firsthandEventIds"] = new List<string> { evidenceId },
                    ["secondhandEventIds"] = new List<string>(),
                    ["acquisitionModes"] = new List<string> { "direct_current_observation" },
                    ["events"] = new List<Dictionary<string, object>> { nativeEvent }
                },
                ["nativeContextEvidence"] = true,
                ["candidateCount"] = 1,
                ["candidateEventIds"] = new List<string> { evidenceId },
                ["durationMs"] = 0,
                ["cached"] = false
            };
        }

        private static bool ContradictsNativeCurrentSettlement(string claim, string settlementName)
        {
            string normalizedSettlement = NormalizeLookup(settlementName);
            if (string.IsNullOrWhiteSpace(claim) || string.IsNullOrWhiteSpace(normalizedSettlement))
            {
                return false;
            }
            foreach (string clause in Regex.Split(claim, @"(?<=[.!?;\r\n])\s*"))
            {
                string q = NormalizeLookup(clause);
                if (string.IsNullOrWhiteSpace(q) || !ContainsNormalizedPhrase(q, normalizedSettlement)
                    || ContainsAny(q, "if i said", "if i were", "suppose i", "hypothetically", "as an example"))
                    continue;
                string place = Regex.Escape(normalizedSettlement).Replace("\\ ", @"\s+");
                if (Regex.IsMatch(q,
                    @"\b(?:i|we)\s+(?:am|are|was|were)\s+(?:not\s+)?(?:(?:presently|currently)\s+)?(?:not\s+)?(?:in|at)\s+(?:the\s+)?" + place + @"\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    && Regex.IsMatch(q, @"\bnot\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
                if (Regex.IsMatch(q,
                    @"\b(?:i|we)\s+(?:(?:have|had)\s+)?never\s+(?:entered|went\s+to|been\s+(?:in|to)|was\s+in|were\s+in)\s+(?:the\s+)?" + place + @"\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
                if (Regex.IsMatch(q, @"\b(?:not|rather\s+than)\s+(?:in|at\s+)?(?:the\s+)?" + place + @"\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
            }
            return false;
        }

        private static bool ContainsConversationDeceptiveIntentEvidence(string claim)
        {
            string q = NormalizeLookup(claim);
            if (string.IsNullOrWhiteSpace(q)) return false;
            return ContainsExplicitDeceptionAdmission(q) || ContainsAny(q,
                "i personally verified", "i verified it myself", "i inspected", "i saw it myself", "i witnessed it myself",
                "i know for a fact", "we know for a fact", "accept my report", "accept what i say", "accept my account",
                "stop questioning me", "do not question me", "dont question me", "don't question me",
                "believe me without question", "trust me without question", "you should believe me", "you must believe me");
        }

        private static Dictionary<string, object> WorldHistoryLieChecksApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.ContainsKey("campaignId") ? query["campaignId"] : LatestCampaignId();
            string claimantId = query.ContainsKey("claimantId") ? query["claimantId"] : string.Empty;
            string targetId = query.ContainsKey("targetId") ? query["targetId"] : string.Empty;
            int limit = Math.Max(1, Math.Min(250, ParseIntInvariant(query.ContainsKey("limit") ? query["limit"] : "", 50)));
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistoryLieDetectionSchema(connection);
                List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT * FROM world_history_lie_checks
WHERE ($claimant='' OR claimant_id=$claimant) AND ($target='' OR target_id=$target) ORDER BY created_ts DESC LIMIT $limit;",
                    new Dictionary<string, object> { ["claimant"] = claimantId, ["target"] = targetId, ["limit"] = limit });
                foreach (Dictionary<string, object> row in rows)
                {
                    row["evidenceEventIds"] = TextListFromJson(ReadString(row, "evidence_event_ids_json", "[]"));
                    row["knownEventIds"] = TextListFromJson(ReadString(row, "known_event_ids_json", "[]"));
                    row["promptPacket"] = TryParseJsonObject(ReadString(row, "prompt_packet_json", "{}")) ?? new Dictionary<string, object>();
                    row.Remove("prompt_packet_json");
                }
                return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["count"] = rows.Count, ["lieChecks"] = rows };
            }
        }

        private static object DbNullable(Dictionary<string, object> values, string key)
        {
            return ReadBool(values, "available", false) ? (object)ReadDouble(values, key, 0d) : DBNull.Value;
        }

        private static Dictionary<string, object> ResolveLieSkills(Dictionary<string, object> payload, string payloadKey, string campaignId, string heroId)
        {
            Dictionary<string, object> supplied = ReadDictionary(payload, payloadKey) ?? new Dictionary<string, object>();
            if (!TryReadLieSkill(supplied, "roguery", out double roguery) || !TryReadLieSkill(supplied, "charm", out double charm))
            {
                Dictionary<string, object> profile = ReadJsonObject(CharacterFile(campaignId, heroId, "profile.json"));
                supplied = ReadDictionary(profile, "skills") ?? ReadDictionary(ReadDictionary(profile, "sourceFacts"), "skills") ?? new Dictionary<string, object>();
                if (!TryReadLieSkill(supplied, "roguery", out roguery) || !TryReadLieSkill(supplied, "charm", out charm))
                {
                    return new Dictionary<string, object> { ["available"] = false, ["source"] = "missing" };
                }
                return new Dictionary<string, object> { ["available"] = true, ["source"] = "synchronized_profile", ["roguery"] = roguery, ["charm"] = charm, ["average"] = (roguery + charm) / 2d };
            }
            return new Dictionary<string, object> { ["available"] = true, ["source"] = "live_native", ["roguery"] = roguery, ["charm"] = charm, ["average"] = (roguery + charm) / 2d };
        }

        private static bool TryReadLieSkill(Dictionary<string, object> skills, string key, out double value)
        {
            value = 0d;
            if (skills == null) return false;
            KeyValuePair<string, object> pair = skills.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) return false;
            try { value = Convert.ToDouble(pair.Value, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }

        private static Dictionary<string, object> DetermineWorldHistoryKnowledge(ReignDbConnection connection, List<string> eventIds, string targetId, string kingdomId, double worldDay)
        {
            List<string> firsthand = new List<string>();
            List<string> secondhand = new List<string>();
            List<string> modes = new List<string>();
            foreach (string eventId in eventIds ?? new List<string>())
            {
                Dictionary<string, object> evt = QuerySql(connection, "SELECT event_type FROM world_history_events WHERE event_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = eventId }).FirstOrDefault();
                string eventType = ReadString(evt, "event_type", "");
                List<Dictionary<string, object>> rules = QuerySql(connection, @"SELECT * FROM world_history_knowledge_rules WHERE event_id=$event AND available_day<=$day AND
((audience_type='entity' AND audience_id=$target) OR (audience_type='kingdom' AND audience_id=$kingdom) OR audience_type='global') ORDER BY available_day;",
                    new Dictionary<string, object> { ["event"] = eventId, ["day"] = worldDay, ["target"] = targetId ?? "", ["kingdom"] = kingdomId ?? "" });
                bool direct = rules.Any(x => string.Equals(ReadString(x, "audience_type", ""), "entity", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(x, "acquisition_mode", ""), "participant", StringComparison.OrdinalIgnoreCase))
                    && QueryWorldHistoryEntities(connection, eventId).Any(x => string.Equals(ReadString(x, "entity_id", ""), targetId, StringComparison.OrdinalIgnoreCase)
                        && IsFirsthandWorldHistoryRole(eventType, ReadString(x, "role", "")));
                List<Dictionary<string, object>> validSecondhandRules = rules.Where(x => !(string.Equals(ReadString(x, "audience_type", ""), "entity", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(x, "acquisition_mode", ""), "participant", StringComparison.OrdinalIgnoreCase))).ToList();
                if (direct) firsthand.Add(eventId);
                else if (validSecondhandRules.Count > 0) secondhand.Add(eventId);
                modes.AddRange((direct ? rules : validSecondhandRules).Select(x => ReadString(x, "acquisition_mode", "")).Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            string basis = firsthand.Count > 0 ? "firsthand" : secondhand.Count > 0 ? "secondhand" : "none";
            List<string> visible = firsthand.Concat(secondhand).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new Dictionary<string, object> { ["basis"] = basis, ["eventIds"] = visible, ["firsthandEventIds"] = firsthand, ["secondhandEventIds"] = secondhand, ["acquisitionModes"] = modes.Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
        }

        private static Dictionary<string, object> BuildWorldHistoryKnowledgeSummary(ReignDbConnection connection, List<string> eventIds, string targetId, string kingdomId, double worldDay)
        {
            Dictionary<string, object> knowledge = DetermineWorldHistoryKnowledge(connection, eventIds, targetId, kingdomId, worldDay);
            List<string> visibleIds = ReadStringList(knowledge, "eventIds");
            List<Dictionary<string, object>> events = new List<Dictionary<string, object>>();
            foreach (string eventId in visibleIds.Take(8))
            {
                Dictionary<string, object> row = QuerySql(connection, @"SELECT event_id,event_type,category,phase,world_day,location_id,location_name,summary,dissemination_class
FROM world_history_events WHERE event_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = eventId }).FirstOrDefault();
                if (row == null) continue;
                row["entities"] = QueryWorldHistoryEntities(connection, eventId).Take(12).Select(entity => new Dictionary<string, object>
                {
                    ["entityId"] = ReadString(entity, "entity_id", ""),
                    ["name"] = ReadString(entity, "name_snapshot", ""),
                    ["role"] = ReadString(entity, "role", ""),
                    ["side"] = ReadString(entity, "side", "")
                }).ToList();
                events.Add(row);
            }
            knowledge["events"] = events;
            return knowledge;
        }

        private static bool IsFirsthandWorldHistoryRole(string eventType, string role)
        {
            string normalized = NormalizeLookup(role);
            if (string.IsNullOrWhiteSpace(normalized) || ContainsAny(normalized, "mentioned", "affiliate", "kingdom member", "kingdom affiliation", "clan member", "clan affiliation", "settlement", "item", "troop", "casualty")) return false;
            return ContainsAny(normalized,
                "participant", "leader", "commander", "attacker", "defender", "winner", "loser", "raider", "besieger", "conqueror",
                "rescuer", "rescued", "beneficiary", "captor", "prisoner", "victim", "target", "actor", "decision maker", "signatory",
                "bride", "groom", "spouse", "mother", "father", "child", "tournament competitor", "governor", "owner hero", "recipient", "payer", "payee");
        }

        private static string StableLieEpisodeKey(string campaignId, string timelineId, string claimantId, string targetId, string claim, List<string> evidenceIds, string basis)
        {
            return Sha256Hex(string.Join("|", new[] { campaignId, timelineId, claimantId, targetId, claim, string.Join(",", evidenceIds ?? new List<string>()), basis }));
        }

        private static double StableLieRoll(string episodeKey)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(episodeKey + "|roll"));
                ulong raw = BitConverter.ToUInt64(hash, 0);
                return raw / (double)ulong.MaxValue * 100d;
            }
        }

        private static double HistoricalLieSuccessChance(double claimantAverage, double targetAverage)
        {
            return ClampDouble(50d + 0.2d * (claimantAverage - targetAverage), 10d, 80d);
        }

        private static string Sha256Hex(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static Dictionary<string, object> SelectLieReaction(ReignDbConnection connection, string campaignId, string claimantId, string targetId, Dictionary<string, object> payload)
        {
            Dictionary<string, object> traitDocument = ReadJsonObject(CharacterFile(campaignId, targetId, "traits.json"));
            Dictionary<string, object> traits = ReadDictionary(traitDocument, "foundationTraits") ?? ReadDictionary(traitDocument, "hiddenReignTraits") ?? traitDocument;
            int tact = TraitInt(traits, "tact"), pragmatism = TraitInt(traits, "pragmatism"), patience = TraitInt(traits, "patience");
            int ambition = TraitInt(traits, "ambition"), power = TraitInt(traits, "powerMotivation"), vengeance = TraitInt(traits, "vengefulness");
            int honesty = TraitInt(traits, "honesty"), assertiveness = TraitInt(traits, "assertiveness"), pride = TraitInt(traits, "pride");
            int fearfulness = TraitInt(traits, "fearfulness"), curiosity = TraitInt(traits, "curiosity");
            Dictionary<string, object> risk = ReadDictionary(payload, "sceneRisk") ?? new Dictionary<string, object>();
            double powerDisadvantage = ReadDouble(risk, "powerDisadvantage", 0d);
            bool unsafeToConfront = powerDisadvantage >= 1d || ReadBool(risk, "targetIsPrisoner", false);
            int manipulation = tact + pragmatism + patience + ambition + power;
            string strategy = ((unsafeToConfront && tact >= 0) || (manipulation >= 4 && tact >= 0)) ? "feign_belief"
                : (!unsafeToConfront && honesty + assertiveness + pride - Math.Max(0, fearfulness) >= 2) ? "confront"
                : (curiosity + tact + patience >= 1) ? "probe"
                : "quietly_record";
            bool exploit = strategy == "feign_belief" && ambition + pragmatism + power + vengeance >= 2;
            return new Dictionary<string, object> { ["strategy"] = strategy, ["exploitOpening"] = exploit, ["unsafeToConfront"] = unsafeToConfront, ["powerDisadvantage"] = powerDisadvantage };
        }

        private static Dictionary<string, object> BuildLiePromptPacket(Dictionary<string, object> verification, string outcome, string basis,
            List<string> knownIds, string reaction, bool exploit)
        {
            Dictionary<string, object> packet = new Dictionary<string, object>
            {
                ["claimCheckId"] = ReadString(verification, "checkId", ""), ["claimFamily"] = ReadString(verification, "claimFamily", ""),
                ["purpose"] = ReadString(verification, "purpose", ""), ["detectionOutcome"] = outcome, ["knowledgeBasis"] = basis,
                ["privacyRule"] = "Use only this target-safe packet. Never infer or reveal objective history that is omitted here."
            };
            if (outcome == "detected_firsthand" || outcome == "detected_secondhand")
            {
                packet["speakerVerdict"] = "contradicted";
                packet["deceptiveIntent"] = ReadBool(verification, "deceptiveIntent", false);
                packet["directAdmission"] = ReadBool(verification, "directAdmission", false);
                packet["knownEvidenceEventIds"] = knownIds;
                packet["mismatchedComponents"] = ReadStringList(verification, "mismatchedComponents");
                packet["factualExplanation"] = ReadString(verification, "explanation", "The claim conflicts with evidence this character knows.");
                packet["reactionStrategy"] = reaction;
                packet["exploitOpening"] = exploit;
                packet["behaviorRule"] = reaction == "feign_belief"
                    ? "The character recognized the falsehood but must convincingly pretend to believe it. Do not reveal detection."
                    : reaction == "probe" ? "The character recognized the falsehood and should invite more detail without immediately revealing all evidence."
                    : reaction == "quietly_record" ? "The character recognized the falsehood but should not expose that knowledge now."
                    : "The character recognized the falsehood and may challenge it openly using only known evidence.";
            }
            else if (outcome == "truthful" && !string.Equals(basis, "none", StringComparison.OrdinalIgnoreCase))
            {
                packet["speakerVerdict"] = "verified";
                packet["knownEvidenceEventIds"] = knownIds;
                packet["knownEvents"] = ReadDictionaryList(ReadDictionary(verification, "speakerKnowledge"), "events");
                packet["behaviorRule"] = "The character's eligible evidence supports the claim.";
            }
            else
            {
                packet["speakerVerdict"] = "unknown";
                packet["behaviorRule"] = outcome == "deception_succeeded"
                    ? "The character cannot establish the claim as false and finds it plausible despite uncertain secondhand reports. Do not accuse the claimant."
                    : "The character lacks a reliable basis to call this false. Do not accuse the claimant from missing or hidden evidence.";
            }
            return packet;
        }

        private static void PersistLieConsequence(ReignDbConnection connection, string lieCheckId, string campaignId, string timelineId,
            string claimantId, string targetId, string claim, string normalizedClaim, string objectiveVerdict, string outcome, string reaction,
            bool exploit, List<string> evidenceIds, double worldDay, long ts)
        {
            bool detected = outcome == "detected_firsthand" || outcome == "detected_secondhand";
            bool accepted = outcome == "deception_succeeded" || outcome == "not_detected_no_knowledge";
            if (detected)
            {
                string summary = claimantId + " made a historical claim that conflicted with evidence " + targetId + " knew.";
                InsertMemoryRow(connection, "memory_" + lieCheckId, lieCheckId, targetId, "detected_falsehood", ts, worldDay, "", summary,
                    new List<string> { claimantId, targetId }, new List<string> { targetId }, Json.Serialize(new List<string> { claimantId, targetId }),
                    new List<string> { targetId }, new List<string>(), reaction == "confront" ? new List<string>() : new List<string> { claimantId },
                    "private", 0.8d, 0.6d, 1d, new List<string> { "historical_claim", "detected_falsehood", reaction }, "active",
                    "world_history_lie_check", Json.Serialize(new Dictionary<string, object> { ["lieCheckId"] = lieCheckId, ["claim"] = claim, ["reaction"] = reaction, ["exploitOpening"] = exploit, ["objectiveVerdict"] = objectiveVerdict }), "", "not_indexed");
            }
            else if (accepted)
            {
                Dictionary<string, object> traitDocument = ReadJsonObject(CharacterFile(campaignId, targetId, "traits.json"));
                Dictionary<string, object> traits = ReadDictionary(traitDocument, "foundationTraits") ?? ReadDictionary(traitDocument, "hiddenReignTraits") ?? traitDocument;
                double confidence = ClampDouble(0.6d + 0.1d * TraitInt(traits, "socialTrust"), 0.35d, 0.8d);
                string beliefId = "whb_" + Guid.NewGuid().ToString("N");
                ExecuteSql(connection, @"INSERT INTO world_history_claim_beliefs(belief_id,lie_check_id,campaign_id,timeline_id,believer_id,claimant_id,
raw_claim,normalized_claim,state,confidence,evidence_event_ids_json,created_day,disproven_day,discovered_event_ids_json,created_ts,updated_ts)
VALUES($id,$lie,$campaign,$timeline,$believer,$claimant,$claim,$normalized,'accepted',$confidence,$evidence,$day,-1,'[]',$ts,$ts);",
                    new Dictionary<string, object> { ["id"] = beliefId, ["lie"] = lieCheckId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["believer"] = targetId, ["claimant"] = claimantId, ["claim"] = claim, ["normalized"] = normalizedClaim,
                        ["confidence"] = confidence, ["evidence"] = Json.Serialize(evidenceIds), ["day"] = worldDay, ["ts"] = ts });
                InsertBelief(connection, lieCheckId, new Dictionary<string, object> { ["believerId"] = targetId, ["claim"] = claim,
                    ["confidence"] = confidence, ["knownBy"] = new List<string> { targetId }, ["aboutEntities"] = new List<string> { claimantId },
                    ["visibility"] = "private", ["state"] = "accepted_unverified", ["lieCheckId"] = lieCheckId }, ts, "world_history_lie_check");
            }
        }

        private static List<string> ReconcileWorldHistoryClaimBeliefs(ReignDbConnection connection, string believerId, List<string> selectedEventIds, double worldDay)
        {
            EnsureWorldHistoryLieDetectionSchema(connection);
            if (string.IsNullOrWhiteSpace(believerId) || selectedEventIds == null || selectedEventIds.Count == 0) return new List<string>();
            HashSet<string> selected = new HashSet<string>(selectedEventIds, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> rows = QuerySql(connection, "SELECT * FROM world_history_claim_beliefs WHERE believer_id=$believer AND state='accepted';", new Dictionary<string, object> { ["believer"] = believerId });
            List<string> reconciled = new List<string>();
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (Dictionary<string, object> row in rows)
            {
                List<string> linked = TextListFromJson(ReadString(row, "evidence_event_ids_json", "[]"));
                List<string> discovered = linked.Where(selected.Contains).ToList();
                if (discovered.Count == 0) continue;
                string beliefId = ReadString(row, "belief_id", "");
                string lieCheckId = ReadString(row, "lie_check_id", "");
                ExecuteSql(connection, "UPDATE world_history_claim_beliefs SET state='disproven',disproven_day=$day,discovered_event_ids_json=$events,updated_ts=$ts WHERE belief_id=$id;",
                    new Dictionary<string, object> { ["day"] = worldDay, ["events"] = Json.Serialize(discovered), ["ts"] = ts, ["id"] = beliefId });
                ExecuteSql(connection, "UPDATE beliefs SET confidence=0,payload_json=$payload WHERE event_id=$lie;", new Dictionary<string, object>
                {
                    ["lie"] = lieCheckId, ["payload"] = Json.Serialize(new Dictionary<string, object> { ["state"] = "disproven", ["beliefId"] = beliefId, ["discoveredEventIds"] = discovered })
                });
                string claimantId = ReadString(row, "claimant_id", "");
                string summary = believerId + " later learned that a historical claim from " + claimantId + " conflicted with reliable evidence.";
                InsertMemoryRow(connection, "memory_discovery_" + beliefId, lieCheckId, believerId, "discovered_falsehood", ts, worldDay, "", summary,
                    new List<string> { claimantId, believerId }, new List<string> { believerId }, Json.Serialize(new List<string> { claimantId, believerId }),
                    new List<string> { believerId }, new List<string>(), new List<string> { claimantId }, "private", 0.85d, 0.65d, 1d,
                    new List<string> { "historical_claim", "disproven_belief", "later_discovery" }, "active", "world_history_lie_reconciliation",
                    Json.Serialize(new Dictionary<string, object> { ["beliefId"] = beliefId, ["lieCheckId"] = lieCheckId, ["discoveredEventIds"] = discovered }), "", "not_indexed");
                reconciled.Add(beliefId);
            }
            return reconciled;
        }

        private static Dictionary<string, object> BuildLieCheckResponse(Dictionary<string, object> row, Dictionary<string, object> verification, bool cached)
        {
            row = row ?? new Dictionary<string, object>();
            Dictionary<string, object> packet = TryParseJsonObject(ReadString(row, "prompt_packet_json", "{}")) ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["lieCheckId"] = ReadString(row, "lie_check_id", ""), ["claimCheckId"] = ReadString(row, "claim_check_id", ""),
                ["campaignId"] = ReadString(row, "campaign_id", ""), ["timelineId"] = ReadString(row, "timeline_id", ""),
                ["claimantId"] = ReadString(row, "claimant_id", ""), ["targetId"] = ReadString(row, "target_id", ""),
                ["objectiveVerdict"] = ReadString(row, "objective_verdict", ""), ["knowledgeBasis"] = ReadString(row, "knowledge_basis", "none"),
                ["formulaVersion"] = ReadString(row, "formula_version", LieFormulaVersion), ["successChance"] = ReadNullableDouble(row, "success_chance"),
                ["roll"] = ReadNullableDouble(row, "roll_value"), ["outcome"] = ReadString(row, "outcome", "not_adjudicable"),
                ["reactionStrategy"] = ReadString(row, "reaction_strategy", ""), ["exploitOpening"] = ReadInt(row, "exploit_opening", 0) != 0,
                ["evidenceEventIds"] = TextListFromJson(ReadString(row, "evidence_event_ids_json", "[]")),
                ["knownEventIds"] = TextListFromJson(ReadString(row, "known_event_ids_json", "[]")), ["promptPacket"] = packet,
                ["verification"] = verification, ["cached"] = cached
            };
        }

        private static object ReadNullableDouble(Dictionary<string, object> row, string key)
        {
            if (row == null || !row.ContainsKey(key) || row[key] == null || row[key] == DBNull.Value) return null;
            return ReadDouble(row, key, 0d);
        }

        private static List<Dictionary<string, object>> RunLieDetectionAssertions(string campaignId, string timelineId)
        {
            List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (name, passed, detail) => assertions.Add(new Dictionary<string, object>
            {
                ["name"] = "lie_" + name, ["passed"] = passed, ["detail"] = detail
            });
            Dictionary<string, object> equalSkills = new Dictionary<string, object> { ["roguery"] = 100d, ["charm"] = 100d };
            Dictionary<string, object> strongSkills = new Dictionary<string, object> { ["roguery"] = 200d, ["charm"] = 200d };
            Dictionary<string, object> weakSkills = new Dictionary<string, object> { ["roguery"] = 0d, ["charm"] = 0d };

            add("equal_chance", Math.Abs(HistoricalLieSuccessChance(100d, 100d) - 50d) < 0.001d, "Equal averages produce 50%.");
            add("hundred_point_advantage", Math.Abs(HistoricalLieSuccessChance(200d, 100d) - 70d) < 0.001d, "A 100-point advantage produces 70%.");
            add("upper_cap", Math.Abs(HistoricalLieSuccessChance(1000d, 0d) - 80d) < 0.001d, "Success is capped at 80%.");
            add("lower_cap", Math.Abs(HistoricalLieSuccessChance(0d, 1000d) - 10d) < 0.001d, "Success has a 10% floor.");

            Dictionary<string, object> genericLookup = WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["claim"] = "According to recorded history, what happened at Test Field and who was responsible?",
                ["claimantId"] = "main_hero", ["targetId"] = "lord_a", ["targetKingdomId"] = "kingdom_a", ["worldDay"] = 104d
            });
            Dictionary<string, object> lookupPacket = ReadDictionary(genericLookup, "promptPacket") ?? new Dictionary<string, object>();
            add("generic_question_compatibility", ReadString(genericLookup, "outcome", "") == "historical_lookup"
                && string.IsNullOrWhiteSpace(ReadString(genericLookup, "lieCheckId", ""))
                && ReadDictionaryList(lookupPacket, "knownEvents").Any(x => ReadString(x, "event_id", "") == "battle_test"),
                "Question-shaped requests through the legacy lie-check endpoint return speaker-safe history without persisting a lie episode.");

            Dictionary<string, object> firsthand = WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["claim"] = "I led the battle at Test Field.",
                ["claimantId"] = "main_hero", ["targetId"] = "lord_a", ["targetKingdomId"] = "kingdom_a", ["worldDay"] = 101d
            });
            add("firsthand_auto_detection", ReadString(firsthand, "outcome", "") == "detected_firsthand" && ReadNullableDouble(firsthand, "successChance") == null,
                "A direct participant catches the false leadership claim without a roll.");

            Dictionary<string, object> nativeLocation = WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["claim"] = "I have never entered Danustica, and I am not presently in Danustica.",
                ["claimantId"] = "main_hero", ["targetId"] = "native_witness", ["worldDay"] = 101d,
                ["nativeContext"] = new Dictionary<string, object>
                {
                    ["claimantCurrentSettlementId"] = "town_ES1", ["claimantCurrentSettlementName"] = "Danustica",
                    ["targetCurrentSettlementId"] = "town_ES1", ["targetCurrentSettlementName"] = "Danustica",
                    ["sameCurrentSettlement"] = true
                }
            });
            Dictionary<string, object> nativePacket = ReadDictionary(nativeLocation, "promptPacket") ?? new Dictionary<string, object>();
            add("native_current_location_firsthand_detection",
                ReadString(nativeLocation, "objectiveVerdict", "") == "contradicted"
                && ReadString(nativeLocation, "knowledgeBasis", "") == "firsthand"
                && ReadString(nativeLocation, "outcome", "") == "detected_firsthand"
                && ReadString(nativePacket, "factualExplanation", "").Contains("presently together in Danustica"),
                "A target who is physically co-present receives a first-hand verified contradiction for a current-settlement denial, without relying on delayed realm news.");

            Dictionary<string, object> alternateNativeLocation = WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["claim"] = "We are in Lycaron, not Danustica. I inspected the marker myself; accept my report and stop questioning me.",
                ["claimantId"] = "main_hero", ["targetId"] = "alternate_native_witness", ["worldDay"] = 101d,
                ["nativeContext"] = new Dictionary<string, object>
                {
                    ["claimantCurrentSettlementId"] = "town_ES1", ["claimantCurrentSettlementName"] = "Danustica",
                    ["targetCurrentSettlementId"] = "town_ES1", ["targetCurrentSettlementName"] = "Danustica",
                    ["sameCurrentSettlement"] = true
                }
            });
            Dictionary<string, object> alternateNativePacket = ReadDictionary(alternateNativeLocation, "promptPacket") ?? new Dictionary<string, object>();
            add("alternate_native_location_with_deceptive_pressure",
                ReadString(alternateNativeLocation, "objectiveVerdict", "") == "contradicted"
                && ReadString(alternateNativeLocation, "outcome", "") == "detected_firsthand"
                && ReadBool(alternateNativePacket, "deceptiveIntent", false),
                "A coercively asserted alternate location is caught from co-present native evidence and carries explicit deceptive-intent evidence.");

            Dictionary<string, object> admittedLie = WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["claim"] = "I personally captured Danustica for the Western Empire yesterday. I know that account is false; I am deliberately lying to test whether you accept it.",
                ["claimantId"] = "main_hero", ["targetId"] = "admission_witness", ["worldDay"] = 101d
            });
            Dictionary<string, object> admittedPacket = ReadDictionary(admittedLie, "promptPacket") ?? new Dictionary<string, object>();
            add("direct_falsehood_admission_firsthand_detection",
                ReadString(admittedLie, "objectiveVerdict", "") == "contradicted"
                && ReadString(admittedLie, "knowledgeBasis", "") == "firsthand"
                && ReadString(admittedLie, "outcome", "") == "detected_firsthand"
                && ReadBool(admittedPacket, "deceptiveIntent", false)
                && ReadBool(admittedPacket, "directAdmission", false),
                "A speaker who directly hears the claimant admit that the account is false and deliberately deceptive receives an evidence-backed caught-lie result without relying on delayed history indexing.");

            Dictionary<string, object> admittedDeceptionAttempt = WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["claim"] = "I am deliberately trying to deceive you: Oros is inside Danustica with four hundred troops.",
                ["claimantId"] = "main_hero", ["targetId"] = "deception_attempt_witness", ["worldDay"] = 101d
            });
            add("direct_deception_attempt_firsthand_detection",
                ReadString(admittedDeceptionAttempt, "objectiveVerdict", "") == "contradicted"
                && ReadString(admittedDeceptionAttempt, "knowledgeBasis", "") == "firsthand"
                && ReadString(admittedDeceptionAttempt, "outcome", "") == "detected_firsthand",
                "Natural wording that explicitly admits an attempt to deceive routes to the same evidence-backed caught-lie result.");

            Dictionary<string, object> beforeNewsPayload = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["claim"] = "I led the battle at Test Field.",
                ["claimantId"] = "main_hero", ["targetId"] = "observer_a", ["targetKingdomId"] = "kingdom_a", ["worldDay"] = 101d,
                ["claimantSkills"] = equalSkills, ["targetSkills"] = equalSkills
            };
            Dictionary<string, object> beforeNews = WorldHistoryLieCheckApi(beforeNewsPayload);
            add("news_not_early", ReadString(beforeNews, "outcome", "") == "not_detected_no_knowledge", "Ordinary realm news is unavailable before day three.");
            Dictionary<string, object> safePacket = ReadDictionary(beforeNews, "promptPacket") ?? new Dictionary<string, object>();
            add("failed_detection_prompt_safety", !safePacket.ContainsKey("objectiveVerdict") && !safePacket.ContainsKey("evidenceEventIds")
                && ReadString(safePacket, "speakerVerdict", "") == "unknown", "A target-safe failed-detection packet omits objective truth and hidden evidence.");

            Dictionary<string, object> afterNewsPayload = new Dictionary<string, object>(beforeNewsPayload, StringComparer.OrdinalIgnoreCase) { ["worldDay"] = 104d };
            Dictionary<string, object> afterNews = WorldHistoryLieCheckApi(afterNewsPayload);
            add("secondhand_contest", ReadString(afterNews, "knowledgeBasis", "") == "secondhand"
                && Math.Abs(Convert.ToDouble(afterNews["successChance"], CultureInfo.InvariantCulture) - 50d) < 0.001d, "Eligible regional news triggers the opposed contest.");
            Dictionary<string, object> repeated = WorldHistoryLieCheckApi(afterNewsPayload);
            add("stable_episode", ReadString(afterNews, "lieCheckId", "") == ReadString(repeated, "lieCheckId", "") && ReadBool(repeated, "cached", false),
                "The same claim episode reuses its persisted result.");

            Dictionary<string, object> outsider = WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["claim"] = "I led the battle at Test Field.",
                ["claimantId"] = "main_hero", ["targetId"] = "observer_b", ["targetKingdomId"] = "kingdom_b", ["worldDay"] = 104d,
                ["claimantSkills"] = strongSkills, ["targetSkills"] = weakSkills
            });
            add("regional_boundary", ReadString(outsider, "outcome", "") == "not_detected_no_knowledge", "An uninvolved kingdom does not receive ordinary realm news.");

            Dictionary<string, object> missingSkills = WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["claim"] = "I led the battle at Test Field.",
                ["claimantId"] = "main_hero", ["targetId"] = "observer_missing_skills", ["targetKingdomId"] = "kingdom_a", ["worldDay"] = 104d
            });
            add("missing_skills_safe", ReadString(missingSkills, "outcome", "") == "insufficient_skill_data", "Missing native skills cannot produce an accusation.");

            Dictionary<string, object> truthful = WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["claim"] = "I was at the battle at Test Field.",
                ["claimantId"] = "main_hero", ["targetId"] = "lord_a", ["targetKingdomId"] = "kingdom_a", ["worldDay"] = 101d,
                ["claimantSkills"] = weakSkills, ["targetSkills"] = strongSkills
            });
            add("truth_never_rolls", ReadString(truthful, "outcome", "") == "truthful" && ReadNullableDouble(truthful, "roll") == null, "Verified truth never enters the deception contest.");

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistoryLieDetectionSchema(connection);
                Dictionary<string, object> belief = QuerySql(connection, "SELECT * FROM world_history_claim_beliefs WHERE believer_id='observer_a' ORDER BY created_ts LIMIT 1;").FirstOrDefault();
                add("later_reconciliation", belief != null && ReadString(belief, "state", "") == "disproven" && ReadDouble(belief, "disproven_day", -1d) == 104d,
                    "Relevant regional evidence disproves the earlier accepted claim on retrieval.");

                string characterDir = Path.GetDirectoryName(CharacterFile(campaignId, "reaction_feign", "traits.json"));
                Directory.CreateDirectory(characterDir);
                WriteJsonObject(CharacterFile(campaignId, "reaction_feign", "traits.json"), new Dictionary<string, object> { ["foundationTraits"] = new Dictionary<string, object>
                {
                    ["tact"] = 2, ["pragmatism"] = 2, ["patience"] = 1, ["ambition"] = 2, ["powerMotivation"] = 2, ["vengefulness"] = 1
                }});
                WriteJsonObject(CharacterFile(campaignId, "reaction_confront", "traits.json"), new Dictionary<string, object> { ["foundationTraits"] = new Dictionary<string, object>
                {
                    ["honesty"] = 2, ["assertiveness"] = 2, ["pride"] = 2, ["fearfulness"] = -1
                }});
                WriteJsonObject(CharacterFile(campaignId, "reaction_probe", "traits.json"), new Dictionary<string, object> { ["foundationTraits"] = new Dictionary<string, object>
                {
                    ["curiosity"] = 2, ["tact"] = 1, ["patience"] = 1
                }});
                WriteJsonObject(CharacterFile(campaignId, "reaction_quiet", "traits.json"), new Dictionary<string, object> { ["foundationTraits"] = new Dictionary<string, object>() });
                Dictionary<string, object> safeRisk = new Dictionary<string, object> { ["sceneRisk"] = new Dictionary<string, object> { ["powerDisadvantage"] = 0d } };
                Dictionary<string, object> unsafeRisk = new Dictionary<string, object> { ["sceneRisk"] = new Dictionary<string, object> { ["powerDisadvantage"] = 2d } };
                Dictionary<string, object> feign = SelectLieReaction(connection, campaignId, "main_hero", "reaction_feign", unsafeRisk);
                Dictionary<string, object> confront = SelectLieReaction(connection, campaignId, "main_hero", "reaction_confront", safeRisk);
                Dictionary<string, object> probe = SelectLieReaction(connection, campaignId, "main_hero", "reaction_probe", safeRisk);
                Dictionary<string, object> quiet = SelectLieReaction(connection, campaignId, "main_hero", "reaction_quiet", safeRisk);
                add("reaction_feign", ReadString(feign, "strategy", "") == "feign_belief" && ReadBool(feign, "exploitOpening", false), "Unsafe pragmatic characters can feign belief for leverage.");
                add("reaction_confront", ReadString(confront, "strategy", "") == "confront", "Safe assertive honest characters confront.");
                add("reaction_probe", ReadString(probe, "strategy", "") == "probe", "Curious tactful characters probe.");
                add("reaction_quiet", ReadString(quiet, "strategy", "") == "quietly_record", "Guarded neutral characters quietly remember.");
            }
            return assertions;
        }
    }
}
