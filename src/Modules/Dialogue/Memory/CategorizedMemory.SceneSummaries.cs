using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NpgsqlTypes;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static Dictionary<string, object> CreateConversationSceneSummary(string campaignId, Dictionary<string, object> session,
            List<Dictionary<string, object>> turns, long now, Dictionary<string, object> settingsOverride = null,
            bool retrievalEligible = true, Action<IReadOnlyList<string>> captureSourceRecords = null,
            Func<Dictionary<string, object>, Dictionary<string, object>> responder = null)
        {
            turns = turns ?? new List<Dictionary<string, object>>();
            turns = turns.Where(t => ReadString(t, "status", "active") == "active").ToList();
            if (turns.Count == 0)
            {
                return new Dictionary<string, object> { ["ok"] = true, ["summaryId"] = "", ["reason"] = "empty_session" };
            }
            string npcId = ReadString(session, "npc_id", "");
            string sessionId = ReadString(session, "session_id", "");
            string playerId = ReadString(session, "player_id", "");
            Dictionary<string, object> identitySource =
                TryParseJsonObject(ReadString(session, "payload_json", "")) ?? new Dictionary<string, object>();
            foreach (Dictionary<string, object> turn in turns)
            {
                Dictionary<string, object> turnPayload =
                    TryParseJsonObject(ReadString(turn, "payload_json", "")) ?? new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> pair in turnPayload)
                {
                    identitySource[pair.Key] = pair.Value;
                }
                if (ReadString(turn, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase))
                {
                    if (!identitySource.ContainsKey("mainHeroName"))
                        identitySource["mainHeroName"] = ReadString(turn, "speaker_name", "");
                    if (!identitySource.ContainsKey("mainHeroStringId"))
                        identitySource["mainHeroStringId"] = ReadString(turn, "speaker_id", playerId);
                }
            }
            DialogueService.KnowledgeAccessContext identityKnowledge = DialogueService.BuildKnowledgeAccessContext(
                npcId, playerId, ReadString(session, "location_id", ""), identitySource);
            List<Dictionary<string, object>> pseudoRows = turns.Select(turn =>
            {
                string role = ReadString(turn, "role", "");
                string provenance = role.Equals("player", StringComparison.OrdinalIgnoreCase)
                    ? "PLAYER LINE (authoritative only that the player said or requested this; factual claims remain claims)"
                    : "NPC LINE (authoritative only that this NPC said it; autobiography, alibis, eyewitness claims, actions, and world facts remain unverified reported speech)";
                // A shared scene summary must contain only the shared observation.
                // The author's complete literary narration remains in the original
                // turn and is available through that author's own exact recall.
                var sharedTurn = new Dictionary<string, object>(turn);
                sharedTurn["text"] = ObserverConversationText(ReadString(turn, "text", ""), ReadString(turn, "speaker_id", ""), "", role == "player");
                string renderedTurn = RenderExactHistoryTurn(sharedTurn, identityKnowledge);
                return new Dictionary<string, object>
                {
                    ["summary"] = provenance + " — " + renderedTurn,
                    ["memory_type"] = "interpersonal_experience",
                    ["importance"] = 0.6d, ["ts"] = ReadLong(turn, "ts", 0)
                };
            }).ToList();
            List<Dictionary<string, object>> verifiedLieEvidence = LoadConversationVerifiedLieEvidence(campaignId, turns);
            pseudoRows.AddRange(verifiedLieEvidence.Select(item => new Dictionary<string, object>
            {
                ["summary"] = "INDEPENDENT SYSTEM EVIDENCE (authoritative for the adjudicated component) — "
                    + DialogueService.SanitizeUnknownIdentityEvidenceText(
                        ReadString(item, "evidenceSummary", "A conversation claim was independently adjudicated."),
                        identityKnowledge),
                ["memory_type"] = "verified_system_evidence",
                ["importance"] = 1d,
                ["ts"] = ReadLong(item, "created_ts", now)
            }));
            Dictionary<string, object> summarySettings = settingsOverride ?? SettingsService.LoadSettings();
            string PublishSceneText(string text, out bool continuityNormalized)
            {
                text = DialogueService.EnsureConversationSummaryProvenance(text,
                    Math.Clamp(ReadInt(summarySettings, "memoryConsolidationMaxSummaryChars", 1800), 500, 5000));
                text = DialogueService.SanitizeUnknownIdentityEvidenceText(text, identityKnowledge);
                string normalized = NormalizeHistoricalRoleplayContinuityText(text,
                    Math.Max(text.Length + 4096, 5000), false, purpose: HistoricalTextPurpose.StoredArchive);
                continuityNormalized = !string.Equals(text, normalized, StringComparison.Ordinal);
                return normalized;
            }
            string summaryCorrelationId = ReadFirstString(
                identitySource,
                "correlationId",
                "correlation_id",
                "liveTestCorrelationId");
            Dictionary<string, object> compact = DialogueService.BuildMiddleTermSummary(summarySettings, npcId, "scene:" + sessionId, pseudoRows,
                120000,
                "This is the canonical transcript of one closed conversation session. Consider all turns through the final listed turn. "
                + "Never claim a participant had not answered when a later listed turn contains that participant's response. "
                + "Player-authored lines are authoritative for what the player originally disclosed; NPC recollections are evidence of what those NPCs said, not proof that added reconstruction details are true. "
                + "A session participant list proves only who heard or spoke in this conversation, not who witnessed an older event described inside it. If one NPC denies witnessing an older event while another speaker says 'we', 'everyone', or 'all three', preserve that contradiction explicitly and never silently promote the collective wording into firsthand evidence. "
                + "An INDEPENDENT SYSTEM EVIDENCE record is authoritative for the component it adjudicates. A first-hand caught-lie receipt makes that contradicted component false rather than unresolved; preserve its lie-check lineage and do not flatten it back into two equally unsupported claims. "
                + "Phrase unverified world claims as reports or beliefs, not objective campaign facts, and do not call a recollection wholly correct when it adds unsupported detail. "
                + "Keep each consequential promise, permission, refusal, question, or correction with its actual reporter, underlying actor, bound person, object, conditions, reason, scope, and status wherever the source supplies them. Preserve stated cause and effect without joining unrelated clauses into a new explanation. Keep distinct undertakings separate; withdrawing a dated permission does not cancel a separate standing condition. Preserve unanswered questions and explicit uncertainty rather than supplying a plausible answer. A source date or time of day dates the utterance, not automatically an older event recalled inside it. "
                + "Treat any claim that the current speaker is absent, permanently withdrawn, or will never re-engage as performative refusal limited to this historical scene; do not turn it into continuing physical state. "
                + (identityKnowledge.PlayerIdentityUnknown
                    ? "The player remains unidentified to this memory owner. Use only 'the unidentified interlocutor' or 'the stranger'; do not restore a canonical player name, clan, realm, title, tier, fiefs, wealth, inventory, or political status from speaker metadata or an NPC reconstruction. "
                    : "")
                + "Narrated gifts, payments, item handoffs, orders, or other gameplay actions prove only what was proposed or said unless a supplied production action receipt confirms completion; never convert narrated acceptance into a completed native transfer.",
                summaryCorrelationId, responder: responder, captureSourceRecords: captureSourceRecords, campaignId: campaignId,
                publicationText: value => PublishSceneText(value, out _));
            string summaryText = ReadString(compact, "summary", "");
            if (string.IsNullOrWhiteSpace(summaryText))
                summaryText = string.Join("\n---\n", pseudoRows.Select(row => ReadString(row, "summary", "")));
            summaryText = PublishSceneText(summaryText, out bool continuityNormalized);
            string routingText = RemoveNegatedSceneCommitmentTerms(summaryText);
            Dictionary<string, object> sceneRoute = BuildMemoryRetrievalRoute(summarySettings, routingText,
                new Dictionary<string, object> { ["worldDay"] = ReadDouble(session, "end_world_day", ReadDouble(session, "start_world_day", 0d)) });
            string memoryLane = SelectConversationSceneMemoryLane(sceneRoute);
            string summaryId = "scene_" + PromptHash(sessionId + "|" + MemorySourceHash(turns) + "|" + MemoryPrecisionVersion).Substring(0, 32);
            List<string> participants = DialogueService.TextListFromJson(ReadString(session, "participants_json", "[]"));
            List<string> sourceEventIds = turns
                .Select(turn => ReadString(turn, "event_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> knownBy = participants.Where(id => turns.All(turn => ReadString(turn,"speaker_id","") == id
                || DialogueService.TextListFromJson(ReadString(turn,"participants_json","[]")).Contains(id,StringComparer.OrdinalIgnoreCase))).ToList();
            participants = knownBy.ToList();
            string summaryOwner = knownBy.Contains(npcId,StringComparer.OrdinalIgnoreCase) ? npcId : "";
            List<string> tags = DialogueService.MergeStringLists(ReadStringList(compact, "tags"),
                new[] { "scene", "conversation", "reported_speech", "claim_attribution", memoryLane });
            if (verifiedLieEvidence.Count > 0)
            {
                tags = DialogueService.MergeStringLists(tags, new[] { "verified_lie", "independent_evidence" });
            }
            if (!retrievalEligible)
            {
                tags = DialogueService.MergeStringLists(tags, new[] { "recall_reconstruction", "audit_only", "non_source" });
            }
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            using (var publication = connection.BeginTransaction())
            {
                VerifyMemoryPublicationFence(connection);
                VerifyMemorySourceRows(connection, "conversation_turns", "turn_id", turns);
                DialogueStore.UpsertSummaries(connection, new Dictionary<string, object>
                    {
                        ["id"] = summaryId, ["scope"] = "scene:" + sessionId, ["owner"] = summaryOwner, ["summary"] = summaryText,
                        ["events"] = Json.Serialize(sourceEventIds),
                        ["start"] = turns.Min(turn => ReadLong(turn, "ts", 0)), ["end"] = turns.Max(turn => ReadLong(turn, "ts", 0)),
                        ["count"] = turns.Select(turn => ReadString(turn, "exchange_id", "")).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        ["location"] = ReadString(session, "location_id", ""), ["participants"] = Json.Serialize(participants),
                        ["known"] = Json.Serialize(knownBy), ["importance"] = 0.65d,
                        ["confidence"] = ReadDouble(compact, "confidence", 0.8d), ["tags"] = Json.Serialize(tags), ["updated"] = now,
                        ["payload"] = Json.Serialize(new Dictionary<string, object>
                        {
                            ["timelineId"] = ReadString(TryParseJsonObject(ReadString(session,"payload_json","{}")),"timelineId",""),
                            ["worldDay"] = turns.Max(t => ReadDouble(t,"world_day",0d)),
                            ["sessionId"] = sessionId, ["method"] = ReadString(compact, "method", "deterministic"),
                            ["interpretationStatus"] = ReadString(compact, "interpretationStatus", "source_extract"),
                            ["originalSourceRecords"] = ReadStringList(compact, "originalSourceRecords"),
                            ["coverageComplete"] = ReadBool(compact, "coverageComplete", false),
                            ["compactionStatus"] = ReadString(compact, "compactionStatus", "incomplete"),
                            ["sourceCoverage"] = ReadString(compact, "sourceCoverage", "unverified"),
                            ["selectedSourceIndices"] = compact.TryGetValue("selectedSourceIndices", out object selectedIndices) ? selectedIndices : Array.Empty<int>(),
                            ["sourceItemCount"] = ReadInt(compact, "sourceItemCount", pseudoRows.Count),
                            ["retryable"] = ReadBool(compact, "retryable", true),
                            ["compactionDiagnostic"] = ReadDictionary(compact, "compactionDiagnostic") ?? TestDict(),
                            ["candidatePublication"] = ReadString(compact, "candidatePublication", ""),
                            ["candidateHash"] = ReadString(compact, "candidateHash", ""),
                            ["uncoveredSourceHashes"] = ReadStringList(compact, "uncoveredSourceHashes"),
                            ["sourceHash"] = MemorySourceHash(turns), ["processingVersion"] = MemoryPrecisionVersion,
                            ["sourceReferences"] = ReadDictionaryList(PrepareStagedSceneMemory(session, turns,
                                ReadString(TryParseJsonObject(ReadString(session,"payload_json","{}")),"timelineId","")), "sourceReferences"),
                            ["retrievalRoute"] = sceneRoute, ["retrievalEligible"] = retrievalEligible,
                            ["recallReconstruction"] = !retrievalEligible,
                            ["evidenceClass"] = "dialogue_reported_speech",
                            ["speakerClaimsRequireIndependentCorroboration"] = true,
                            ["correlationId"] = summaryCorrelationId,
                            ["roleplayContinuityNormalized"] = continuityNormalized,
                            ["playerIdentityKnown"] = !identityKnowledge.PlayerIdentityUnknown,
                            ["playerIdentityState"] = identityKnowledge.PlayerIdentityUnknown ? "unknown" : "known",
                            ["verifiedLieCheckIds"] = verifiedLieEvidence.Select(item => ReadString(item, "lie_check_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList(),
                            ["verifiedRelationshipReceiptIds"] = verifiedLieEvidence.Select(item => ReadString(item, "receipt_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList()
                        }),
                        ["source"] = !retrievalEligible ? "conversation_recall_audit" : "conversation_scene",
                        ["status"] = retrievalEligible ? "active" : "audit_only",
                        ["embedding"] = retrievalEligible ? "not_indexed" : "skipped",
                        ["lane"] = memoryLane
                    });
                if (retrievalEligible)
                {
                    DialogueService.InsertSummaryFts(connection, summaryId, summaryText, tags, participants);
                }
                int ordinal = 0;
                foreach (Dictionary<string, object> turn in turns)
                {
                    LinkMemorySource(connection, "summary", summaryId, "turn", ReadString(turn, "turn_id", ""), ordinal++);
                }
                int eventOrdinal = 0;
                foreach (string sourceEventId in sourceEventIds)
                {
                    LinkMemorySource(connection, "summary", summaryId, "event", sourceEventId, eventOrdinal++);
                }
                int evidenceOrdinal = 0;
                foreach (Dictionary<string, object> item in verifiedLieEvidence)
                {
                    LinkMemorySource(connection, "summary", summaryId, "lie_check", ReadString(item, "lie_check_id", ""), evidenceOrdinal);
                    LinkMemorySource(connection, "summary", summaryId, "relationship_receipt", ReadString(item, "receipt_id", ""), evidenceOrdinal++);
                }
                LinkMemorySource(connection, "summary", summaryId, "session", sessionId, 0);
                DialogueStore.UpdateConversationSessions3(connection, new Dictionary<string, object> { ["summary"] = summaryId, ["id"] = sessionId });
                publication.Commit();
            }
            return new Dictionary<string, object> { ["ok"] = true, ["summaryId"] = summaryId, ["summary"] = summaryText, ["memoryLane"] = memoryLane,
                ["compactionStatus"] = ReadString(compact, "compactionStatus", "incomplete"),
                ["sourceCoverage"] = ReadString(compact, "sourceCoverage", "unverified"),
                ["retryable"] = ReadBool(compact, "retryable", true),
                ["compactionDiagnostic"] = ReadDictionary(compact, "compactionDiagnostic") ?? TestDict(),
                ["sourceTurnIds"] = turns.Select(turn => ReadString(turn, "turn_id", "")).ToList(), ["retrievalEligible"] = retrievalEligible,
                ["sourceEventIds"] = sourceEventIds,
                ["knowledgeBoundary"] = new Dictionary<string, object>
                {
                    ["playerIdentityKnown"] = !identityKnowledge.PlayerIdentityUnknown,
                    ["playerIdentityState"] = identityKnowledge.PlayerIdentityUnknown ? "unknown" : "known",
                    ["playerId"] = playerId,
                    ["playerName"] = identityKnowledge.PlayerName,
                    ["playerClanId"] = identityKnowledge.PlayerClanId,
                    ["playerClanName"] = identityKnowledge.PlayerClanName,
                    ["playerKingdomId"] = identityKnowledge.PlayerKingdomId,
                    ["playerKingdomName"] = identityKnowledge.PlayerKingdomName
                },
                ["status"] = retrievalEligible ? "active" : "audit_only" };
        }

        private static List<Dictionary<string, object>> LoadConversationVerifiedLieEvidence(
            string campaignId, List<Dictionary<string, object>> turns)
        {
            List<string> exchangeIds = (turns ?? new List<Dictionary<string, object>>())
                .Select(turn => ReadString(turn, "exchange_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<Dictionary<string, object>> evidence = new List<Dictionary<string, object>>();
            if (exchangeIds.Count == 0) return evidence;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                RelationshipsService.EnsureConversationRelationshipSchema(connection);
                foreach (string exchangeId in exchangeIds)
                {
                    List<Dictionary<string, object>> rows = DialogueStore.SelectConversationRelationshipReceipts4(connection, new Dictionary<string, object> { ["exchange"] = exchangeId });
                    foreach (Dictionary<string, object> row in rows)
                    {
                        Dictionary<string, object> packet = TryParseJsonObject(ReadString(row, "prompt_packet_json", "{}"))
                            ?? new Dictionary<string, object>();
                        string explanation = ReadString(packet, "factualExplanation", "");
                        row["evidenceSummary"] = "Lie check " + ReadString(row, "lie_check_id", "")
                            + " found the claim by " + ReadString(row, "target_id", "the claimant")
                            + " — “" + DialogueService.LimitText(ReadString(row, "raw_claim", ""), 500) + "” — "
                            + ReadString(row, "objective_verdict", "contradicted") + " and "
                            + ReadString(row, "outcome", "detected") + " for observer " + ReadString(row, "observer_id", "")
                            + (string.IsNullOrWhiteSpace(explanation) ? "." : ". " + explanation)
                            + " Treat the contradicted component as false, not as an unresolved disagreement.";
                        evidence.Add(row);
                    }
                }
            }
            return evidence;
        }

        private static string RemoveNegatedSceneCommitmentTerms(string text)
        {
            string value = text ?? "";
            value = Regex.Replace(value,
                @"\bno\s+(?:(?:plans?|orders?|commitments?|promises?|agreements?)(?:\s*(?:,|or|and)\s*)?)+(?:\s+(?:were\s+)?made)?\b",
                "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value,
                @"\b(?:do|did)\s+not\s+(?:make\s+plans?|issue\s+orders?|make\s+commitments?|promise|agree)(?:\s+(?:or|and)\s+(?:make\s+plans?|issue\s+orders?))*\b",
                "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value,
                @"\b(?:no[-\s]?strings?(?:\s+attached)?|no\s+(?:favor|favour|obligation|debt)s?\s+(?:was\s+)?expected|without\s+(?:any\s+)?(?:favor|favour|obligation|debt)s?|free\s+of\s+(?:any\s+)?(?:favor|favour|obligation|debt)s?)\b",
                "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value,
                @"\b(?:no\s+one|nobody)\s+(?:named|made|stated|requested|demanded|accepted|created)\s+(?:a\s+|any\s+)?(?:specific\s+)?(?:favor|favour|obligation|debt|promise|commitment)s?(?:\s+(?:or|and)\s+(?:a\s+|any\s+)?(?:specific\s+)?(?:favor|favour|obligation|debt|promise|commitment)s?)*\b",
                "", RegexOptions.IgnoreCase);
            return value;
        }

        internal static string SelectConversationSceneMemoryLane(Dictionary<string, object> sceneRoute)
        {
            string lane = ReadString(sceneRoute, "primaryLane", "interpersonal_history");
            if (lane == "exact_history") return "interpersonal_history";
            if (lane == "personal_state")
            {
                Dictionary<string, object> scores = ReadDictionary(sceneRoute, "scores") ?? new Dictionary<string, object>();
                double personal = ReadDouble(scores, "personal_state", 0d);
                double interpersonal = ReadDouble(scores, "interpersonal_history", 0d);
                // The retrieval router's no-cue fallback is personal=1/interpersonal=.75.
                // A closed conversation with no explicit personal-state cue belongs to
                // interpersonal history; otherwise ordinary discussion scenes fragment
                // into the private mood lane merely because no stronger keyword fired.
                if (personal <= 1d && interpersonal <= 0.75d) return "interpersonal_history";
            }
            return MemoryLaneIds.Contains(lane, StringComparer.OrdinalIgnoreCase) ? lane : "interpersonal_history";
        }

        private static Dictionary<string, object> UpdateRollingConversationArc(string campaignId, string npcId, string playerId,
            string memoryLane, long now, Dictionary<string, object> knowledgeBoundary = null)
        {
            memoryLane = MemoryLaneIds.Contains(memoryLane, StringComparer.OrdinalIgnoreCase) && memoryLane != "exact_history"
                ? memoryLane : "interpersonal_history";
            List<Dictionary<string, object>> scenes;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                scenes = DialogueStore.SelectSummaries(connection, new Dictionary<string, object> { ["npc"] = npcId, ["lane"] = memoryLane, ["player"] = playerId ?? "", ["player_like"] = "%" + (playerId ?? "") + "%" });
            }
            bool identityUnknown = knowledgeBoundary != null
                && !ReadBool(knowledgeBoundary, "playerIdentityKnown", true);
            DialogueService.KnowledgeAccessContext identityKnowledge = null;
            int withheldScenes = 0;
            if (identityUnknown)
            {
                Dictionary<string, object> source =
                    new Dictionary<string, object>(knowledgeBoundary, StringComparer.OrdinalIgnoreCase)
                    {
                        ["mainHeroStringId"] = FirstNonEmpty(
                            ReadString(knowledgeBoundary, "playerId", ""), playerId),
                        ["mainHeroName"] = ReadString(knowledgeBoundary, "playerName", ""),
                        ["identityView"] = new Dictionary<string, object> { ["knowsIdentity"] = false }
                    };
                identityKnowledge = DialogueService.BuildKnowledgeAccessContext(npcId, playerId, "", source);
                int before = scenes.Count;
                scenes = scenes
                    .Where(row => DialogueService.UnknownIdentityMemoryEvidenceAllowed(row, identityKnowledge,
                        ReadString(row, "summary", "")))
                    .Select(row => DialogueService.SanitizeUnknownIdentityMemoryRow(row, identityKnowledge))
                    .ToList();
                withheldScenes = before - scenes.Count;
            }
            if (scenes.Count < 4)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["updated"] = false,
                    ["reason"] = "fewer_than_four_related_scenes",
                    ["identityBoundaryWithheldScenes"] = withheldScenes
                };
            }
            scenes = scenes.Take(8).OrderBy(row => ReadLong(row, "updated_ts", 0)).ToList();
            Dictionary<string, object> arcSettings = SettingsService.LoadSettings();
            string PublishArcText(string value, out bool continuityNormalized)
            {
                if (identityUnknown)
                    value = DialogueService.SanitizeUnknownIdentityEvidenceText(value, identityKnowledge);
                string normalized = NormalizeHistoricalRoleplayContinuityText(value,
                    Math.Max(value.Length + 4096, 5000), true, purpose: HistoricalTextPurpose.StoredArchive);
                continuityNormalized = !string.Equals(value, normalized, StringComparison.Ordinal);
                return normalized;
            }
            string arcCorrelationId = scenes
                .Select(DialogueService.StoredConversationArtifactCorrelationId)
                .LastOrDefault(value =>
                    !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            Dictionary<string, object> compact = DialogueService.BuildMiddleTermSummary(arcSettings, npcId,
                "arc:" + npcId + ":" + playerId + ":" + memoryLane, scenes, 60000,
                "These records are chronological closed conversation scenes. A departure, silence, refusal, or closure in an older scene applies only to that scene. If a later scene exists, it proves the participants subsequently re-engaged; never preserve an older scene ending as current physical absence or permanent non-engagement.",
                arcCorrelationId, campaignId: campaignId, publicationText: value => PublishArcText(value, out _));
            string text = PublishArcText(ReadString(compact, "summary", ""), out bool continuityNormalized);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new Dictionary<string, object> { ["ok"] = true, ["updated"] = false, ["reason"] = "empty_summary" };
            }
            string arcId = "arc_" + SafeMemoryKey(npcId) + "_" + SafeMemoryKey(playerId) + "_" + SafeMemoryKey(memoryLane);
            List<string> tags = DialogueService.MergeStringLists(ReadStringList(compact, "tags"), new[] { "rolling_arc", memoryLane });
            List<string> participants = DialogueService.MergeStringLists(new[] { npcId, playerId }, new List<string>());
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            using (var publication = connection.BeginTransaction())
            {
                VerifyMemoryPublicationFence(connection);
                VerifyMemorySourceRows(connection, "summaries", "summary_id", scenes);
                DialogueStore.UpsertSummaries2(connection, new Dictionary<string, object>
                    {
                        ["id"] = arcId, ["scope"] = "arc:" + npcId + ":" + playerId + ":" + memoryLane, ["owner"] = npcId, ["summary"] = text,
                        ["start"] = scenes.Min(row => ReadLong(row, "start_ts", 0)), ["end"] = scenes.Max(row => ReadLong(row, "end_ts", 0)),
                        ["count"] = scenes.Count, ["participants"] = Json.Serialize(participants), ["known"] = Json.Serialize(new List<string> { npcId }),
                        ["confidence"] = ReadDouble(compact, "confidence", 0.8d), ["tags"] = Json.Serialize(tags), ["updated"] = now,
                        ["payload"] = Json.Serialize(new Dictionary<string, object>
                        {
                            ["sourceSummaryIds"] = scenes.Select(row => ReadString(row, "summary_id", "")).ToList(),
                            ["method"] = ReadString(compact, "method", ""),
                            ["interpretationStatus"] = ReadString(compact, "interpretationStatus", ""),
                            ["originalSourceRecords"] = ReadStringList(compact, "originalSourceRecords"),
                            ["coverageComplete"] = ReadBool(compact, "coverageComplete", false),
                            ["compactionStatus"] = ReadString(compact, "compactionStatus", "incomplete"),
                            ["sourceCoverage"] = ReadString(compact, "sourceCoverage", "unverified"),
                            ["selectedSourceIndices"] = compact.TryGetValue("selectedSourceIndices", out object selectedIndices) ? selectedIndices : Array.Empty<int>(),
                            ["sourceItemCount"] = ReadInt(compact, "sourceItemCount", scenes.Count),
                            ["retryable"] = ReadBool(compact, "retryable", false),
                            ["compactionDiagnostic"] = ReadDictionary(compact, "compactionDiagnostic") ?? TestDict(),
                            ["sourceHash"] = MemorySourceHash(scenes), ["processingVersion"] = MemoryPrecisionVersion,
                            ["correlationId"] = arcCorrelationId,
                            ["roleplayContinuityNormalized"] = continuityNormalized,
                            ["latestSceneProvesReengagement"] = true
                        })
                    });
                DialogueService.InsertSummaryFts(connection, arcId, text, tags, participants);
                DialogueStore.UpdateSummaries(connection, new Dictionary<string, object> { ["lane"] = memoryLane, ["id"] = arcId });
                DialogueStore.DeleteMemorySources(connection, new Dictionary<string, object> { ["id"] = arcId });
                int ordinal = 0;
                foreach (Dictionary<string, object> scene in scenes)
                {
                    LinkMemorySource(connection, "summary", arcId, "summary", ReadString(scene, "summary_id", ""), ordinal++);
                }
                publication.Commit();
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["updated"] = true,
                ["summaryId"] = arcId,
                ["summary"] = text,
                ["compactionStatus"] = ReadString(compact, "compactionStatus", "incomplete"),
                ["compactionDiagnostic"] = ReadDictionary(compact, "compactionDiagnostic") ?? TestDict(),
                ["roleplayContinuityNormalized"] = continuityNormalized,
                ["identityBoundaryWithheldScenes"] = withheldScenes
            };
        }

        private static Dictionary<string, object> RepairExistingRollingConversationArcsAfterReengagement(
            string campaignId,
            string npcId,
            string playerId,
            long now)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(npcId))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["repairedCount"] = 0
                };
            }
            int maxChars = Math.Max(500, Math.Min(5000,
                ReadInt(SettingsService.LoadSettings(), "memoryConsolidationMaxSummaryChars", 1800)));
            List<string> repairedIds = new List<string>();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                List<Dictionary<string, object>> arcs = DialogueStore.SelectSummaries2(connection, new Dictionary<string, object>
                    {
                        ["npc"] = npcId,
                        ["player"] = playerId ?? "",
                        ["player_like"] = "%\"" + (playerId ?? "") + "\"%"
                    });
                foreach (Dictionary<string, object> row in arcs)
                {
                    string original = ReadString(row, "summary", "");
                    string normalized = NormalizeHistoricalRoleplayContinuityText(
                        original, maxChars, true, purpose: HistoricalTextPurpose.StoredArchive);
                    if (string.Equals(original, normalized, StringComparison.Ordinal)) continue;
                    string summaryId = ReadString(row, "summary_id", "");
                    if (string.IsNullOrWhiteSpace(summaryId)) continue;
                    Dictionary<string, object> payload =
                        TryParseJsonObject(ReadString(row, "payload_json", ""))
                        ?? new Dictionary<string, object>();
                    payload["roleplayContinuityNormalized"] = true;
                    payload["latestSceneProvesReengagement"] = true;
                    payload["continuityNormalizedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    DialogueStore.UpdateSummaries2(connection, new Dictionary<string, object>
                    {
                        ["summary"] = normalized,
                        ["payload"] = Json.Serialize(payload),
                        ["updated"] = now,
                        ["id"] = summaryId
                    });
                    DialogueService.InsertSummaryFts(connection, summaryId, normalized,
                        DialogueService.TextListFromJson(ReadString(row, "tags_json", "[]")),
                        DialogueService.TextListFromJson(ReadString(row, "participants_json", "[]")));
                    repairedIds.Add(summaryId);
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["repairedCount"] = repairedIds.Count,
                ["summaryIds"] = repairedIds
            };
        }

        internal static void LinkMemorySource(ReignDbConnection connection, string documentType, string documentId, string sourceType, string sourceId, int ordinal)
        {
            if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(sourceId))
            {
                return;
            }
            DialogueStore.UpsertMemorySources(connection, new Dictionary<string, object>
            {
                ["document_type"] = documentType ?? "", ["document_id"] = documentId,
                ["source_type"] = sourceType ?? "", ["source_id"] = sourceId, ["ordinal"] = ordinal
            });
        }

        internal static void UpsertKnowledgeReceipt(ReignDbConnection connection, string eventId, string npcId, string acquisitionType,
            double confidence, double reliability, double worldDay, string sourceId, long ts)
        {
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(npcId))
            {
                return;
            }
            string receiptId = "knowledge_" + Guid.NewGuid().ToString("N");
            DialogueStore.InsertOrIgnoreKnowledgeReceipts(connection, new Dictionary<string, object>
            {
                ["id"] = receiptId, ["event"] = eventId, ["npc"] = npcId, ["type"] = acquisitionType ?? "known",
                ["confidence"] = DialogueService.ClampDouble(confidence, 0d, 1d), ["reliability"] = DialogueService.ClampDouble(reliability, 0d, 1d),
                ["day"] = worldDay, ["source"] = sourceId ?? "", ["ts"] = ts
            });
        }

        internal static string ClassifyEventCategory(string eventType, Dictionary<string, object> payload, string summary)
        {
            string value = NormalizeMemoryCategory(eventType + " " + summary);
            if (DialogueService.ContainsAny(value, "conversation", "dialogue_turn", "private_conversation", "party_chat")) return "conversation";
            if (DialogueService.ContainsAny(value, "letter", "mail", "correspondence")) return "correspondence";
            if (DialogueService.ContainsAny(value, "action_result", "action_completed", "action_failed")) return "action_result";
            if (DialogueService.ContainsAny(value, "nearby", "visible_army", "scout", "local_observation")) return "local_observation";
            if (DialogueService.ContainsAny(value, "social_event", "tournament", "feast", "celebration", "court_event")) return "social_event";
            return "world_event";
        }

        internal static string ClassifyEventSubtype(string eventType, string summary)
        {
            string value = NormalizeMemoryCategory(eventType + " " + summary);
            if (value.Contains("tournament") && DialogueService.ContainsAny(value, "winner", "won", "result", "victory")) return "tournament_result";
            if (value.Contains("tournament")) return "tournament_announcement";
            if (DialogueService.ContainsAny(value, "war_declared", "declared_war", "war declaration")) return "war_declared";
            if (DialogueService.ContainsAny(value, "siege_result", "siege ended", "siege won")) return "siege_result";
            if (value.Contains("marriage")) return "marriage";
            if (DialogueService.ContainsAny(value, "nearby_army", "army nearby", "enemy army")) return "nearby_army";
            return NormalizeMemoryCategory(eventType);
        }

        internal static string ClassifyMemoryDomain(Dictionary<string, object> row)
        {
            string explicitDomain = ReadFirstString(row, "memory_domain", "memoryDomain", "lane");
            if (!string.IsNullOrWhiteSpace(explicitDomain)) return NormalizeMemoryCategory(explicitDomain);
            string type = NormalizeMemoryCategory(ReadFirstString(row, "memory_type", "memoryType", "type"));
            string blob = NormalizeMemoryCategory(type + " " + ReadString(row, "source", "") + " " + ReadString(row, "tags_json", "") + " " + ReadString(row, "summary", ""));
            if (DialogueService.ContainsAny(blob, "world_knowledge", "war", "tournament", "siege", "alliance", "marriage", "kingdom")) return "world_affairs";
            if (DialogueService.ContainsAny(blob, "local_observation", "nearby", "scout", "visible_army")) return "local_awareness";
            if (DialogueService.ContainsAny(blob, "private_reflection", "comprehension", "feeling", "mood", "fear", "dream", "desire")) return "personal_state";
            if (DialogueService.ContainsAny(blob, "interpersonal", "conversation", "dialogue", "letter", "correspondence")) return "interpersonal_history";
            if (DialogueService.ContainsAny(blob, "obligation", "promise", "debt", "oath", "threat", "blackmail", "spymaster", "intelligence_mission", "covert_operation")) return "commitments_and_plots";
            if (DialogueService.ContainsAny(blob, "belief", "rumor", "gossip")) return "beliefs_and_rumors";
            return "personal_state";
        }

        internal static string NormalizeMemoryCategory(string value)
        {
            return Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        }

        internal static string SafeMemoryKey(string value)
        {
            string key = Regex.Replace((value ?? "unknown").Trim(), @"[^A-Za-z0-9_-]+", "_");
            return string.IsNullOrWhiteSpace(key) ? "unknown" : key;
        }
    }
}
