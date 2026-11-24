using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReignShared.Spymaster;

namespace ReignBetaServer
{
    internal static partial class SpymasterService
    {
        private static void EnsureSpymasterSchema(ReignDbConnection connection)
        {
            SpymasterSchema.CreateSpymasterMissionMemory(connection);
            SpymasterSchema.CreateIndexSpymasterMemoryOwner(connection);
        }

        internal static void AppendSpymasterRelevantMemoryBundle(
            string campaignId,
            string timelineId,
            string heroId,
            List<Dictionary<string, object>> selectedContextPulls,
            List<Dictionary<string, object>> contextBundles)
        {
            if (string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(timelineId)
                || string.IsNullOrWhiteSpace(heroId)
                || selectedContextPulls == null
                || contextBundles == null
                || !selectedContextPulls.Any(x => string.Equals(
                    ReadFirstString(x, "id", "pullId", "contextPullId"),
                    "relevant_memory", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSpymasterSchema(connection);
                List<Dictionary<string, object>> rows = SpymasterStore.SelectSpymasterMissionMemory(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["hero"] = heroId
                });
                if (rows.Count == 0)
                {
                    return;
                }

                contextBundles.Add(new Dictionary<string, object>
                {
                    ["id"] = "relevant_memory",
                    ["source"] = "server_spymaster_mission_ledger",
                    ["ok"] = true,
                    ["durationMs"] = 0,
                    ["data"] = BuildSpymasterMissionMemoryData(
                        campaignId, timelineId, heroId, rows)
                });
            }
        }

        private static Dictionary<string, object> BuildSpymasterMissionMemoryData(
            string campaignId,
            string timelineId,
            string heroId,
            List<Dictionary<string, object>> rows)
        {
            List<Dictionary<string, object>> missions = (rows
                    ?? new List<Dictionary<string, object>>())
                .Take(12)
                .Select(row => new Dictionary<string, object>
                {
                    ["missionId"] = ReadString(row, "mission_id", ""),
                    ["phase"] = ReadString(row, "phase", ""),
                    ["missionType"] = ReadString(row, "mission_type", ""),
                    ["targetType"] = ReadString(row, "target_type", ""),
                    ["targetId"] = ReadString(row, "target_id", ""),
                    ["targetName"] = ReadString(row, "target_name", ""),
                    ["startedDay"] = ReadDouble(row, "started_day", 0d),
                    ["dueDay"] = ReadDouble(row, "due_day", 0d),
                    ["resolvedDay"] = ReadDouble(row, "resolved_day", -1d),
                    ["requestSummary"] = ReadString(row, "request_summary", ""),
                    ["resultSummary"] = ReadString(row, "result_summary", "")
                })
                .ToList();
            return new Dictionary<string, object>
            {
                ["campaignId"] = campaignId ?? "",
                ["timelineId"] = timelineId ?? "",
                ["spymasterHeroId"] = heroId ?? "",
                ["missionCount"] = missions.Count,
                ["missions"] = missions,
                ["knowledgeBoundary"] = "These are authoritative operations personally assigned to this exact Spymaster in this exact campaign timeline. The Spymaster may accurately recall their orders and recorded outcomes."
            };
        }

        internal static Dictionary<string, object> SpymasterMemoryApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string phase = ReadString(payload, "phase", "updated");
            Dictionary<string, object> mission = ReadDictionary(payload, "mission") ?? new Dictionary<string, object>();
            string missionId = ReadString(mission, "MissionId", ReadString(mission, "missionId", ""));
            string spymasterId = ReadString(mission, "SpymasterHeroStringId", ReadString(mission, "spymasterHeroStringId", ""));
            if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(spymasterId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "missionId and spymasterHeroStringId are required." };
            string missionType = ReadString(mission, "MissionType", ReadString(mission, "missionType", "operation"));
            string targetType = ReadString(mission, "TargetType", ReadString(mission, "targetType", ""));
            string targetId = ReadString(mission, "TargetStringId", ReadString(mission, "targetStringId", ""));
            string targetName = ReadString(mission, "TargetName", ReadString(mission, "targetName", targetId));
            string request = ReadString(mission, "RequestSummary", ReadString(mission, "requestSummary", ""));
            string result = ReadString(mission, "ResultSummary", ReadString(mission, "resultSummary", ""));
            double startedDay = ReadDouble(mission, "StartedDay", ReadDouble(mission, "startedDay", 0d));
            double dueDay = ReadDouble(mission, "DueDay", ReadDouble(mission, "dueDay", startedDay));
            double resolvedDay = ReadDouble(mission, "ResolvedDay", ReadDouble(mission, "resolvedDay", -1d));
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSpymasterSchema(connection);
                SpymasterStore.UpsertSpymasterMissionMemory(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["mission"] = missionId, ["spymaster"] = spymasterId,
                    ["phase"] = phase, ["type"] = missionType, ["targetType"] = targetType, ["target"] = targetId,
                    ["targetName"] = targetName, ["started"] = startedDay, ["due"] = dueDay, ["resolved"] = resolvedDay,
                    ["request"] = request, ["result"] = result, ["payload"] = Json.Serialize(mission), ["ts"] = ts
                });
            }

            string summary = phase.Equals("resolved", StringComparison.OrdinalIgnoreCase)
                ? "Spymaster operation " + missionId + ": I was ordered to conduct " + request + " Result: " + result
                : "Spymaster operation " + missionId + ": I personally received this order: " + request;
            DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["eventId"] = "spymaster_operation_" + missionId + "_" + phase,
                ["eventType"] = "spymaster_operation_" + phase,
                ["worldDay"] = phase.Equals("resolved", StringComparison.OrdinalIgnoreCase) ? resolvedDay : startedDay,
                ["summary"] = summary, ["participants"] = new[] { spymasterId }, ["known_by"] = new[] { spymasterId },
                ["visibility"] = "private", ["importance"] = 0.95d, ["sourceReliability"] = 1d,
                ["confidence"] = 1d, ["memoryDomain"] = "commitments_and_plots",
                ["tags"] = new[] { "spymaster", "intelligence", "mission", missionType, missionId },
                ["aboutEntityIds"] = new[] { targetId }, ["mission"] = mission
            }, "spymaster_mission_ledger");
            bool targetNoticed = ReadBool(mission, "TargetNoticed", ReadBool(mission, "targetNoticed", false));
            bool playerIdentified = ReadBool(mission, "PlayerIdentified", ReadBool(mission, "playerIdentified", false));
            if (phase.Equals("resolved", StringComparison.OrdinalIgnoreCase) && targetNoticed && !string.IsNullOrWhiteSpace(targetId))
            {
                DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["eventId"] = "spymaster_target_discovery_" + missionId,
                    ["eventType"] = "covert_investigation_discovered", ["worldDay"] = resolvedDay,
                    ["summary"] = playerIdentified
                        ? "I discovered that the player ruler ordered a covert operation against me: " + missionType.Replace('_', ' ') + "."
                        : "I discovered that someone ordered a covert operation against me, but I could not identify the sponsor.",
                    ["participants"] = new[] { targetId }, ["known_by"] = new[] { targetId }, ["visibility"] = "private",
                    ["importance"] = 0.82d, ["sourceReliability"] = 1d, ["confidence"] = 1d,
                    ["tags"] = new[] { "covert_investigation", "discovered", missionType, playerIdentified ? "player_identified" : "sponsor_unknown" },
                    ["aboutEntityIds"] = playerIdentified ? new[] { targetId, "player" } : new[] { targetId }
                }, "spymaster_target_discovery");
            }
            return new Dictionary<string, object> { ["ok"] = true, ["missionId"] = missionId, ["spymasterHeroStringId"] = spymasterId };
        }

        internal static Dictionary<string, object> SpymasterSocialStatusApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            return ReadBool(payload, "readMitigationReceipt", false)
                ? SpymasterMitigationReceiptApi(payload)
                : ReputationService.SocialCharacterStatusApi(payload);
        }

        internal static Dictionary<string, object> SpymasterSocialApplyApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            Dictionary<string, object> mission = ReadDictionary(payload, "mission") ?? new Dictionary<string, object>();
            string type = ReadString(mission, "MissionType", ReadString(mission, "missionType", ""));
            if (type.StartsWith("mitigate_", StringComparison.Ordinal))
                return ApplySpymasterMitigation(payload, mission, type);
            string subjectId = ReadString(mission, "TargetStringId", ReadString(mission, "targetStringId", ""));
            string fabricated = ReadString(mission, "FabricatedText", ReadString(mission, "fabricatedText", ""));
            string missionId = ReadString(mission, "MissionId", ReadString(mission, "missionId", Guid.NewGuid().ToString("N")));
            double worldDay = ReadDouble(mission, "ResolvedDay", ReadDouble(mission, "resolvedDay", 0d));
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                ReputationService.EnsureSocialReputationSchema(connection);

                bool positive = type == "fabricate_positive";
                bool severe = type == "fabricate_severe";
                string tagId = "spymaster_" + (positive ? "praised" : severe ? "infamous" : "suspected") + "_" + missionId.Substring(0, Math.Min(12, missionId.Length));
                string description = string.IsNullOrWhiteSpace(fabricated)
                    ? positive ? "The ruler's agents praise this person's character and service."
                    : severe ? "A grave and damaging reputation is being deliberately circulated."
                    : "A harmful unverified story is being deliberately circulated."
                    : fabricated;
                int value = positive ? 10 : severe ? -18 : -8;
                if (positive || severe)
                {
                    SpymasterStore.UpsertCharacterReputations(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId,
                        ["archetype"] = positive ? "spymaster_praise" : "spymaster_fabrication", ["description"] = description,
                        ["value"] = value, ["day"] = worldDay, ["snapshot"] = Json.Serialize(new Dictionary<string, object>
                        { ["label"] = positive ? "Promoted standing" : "Fabricated reputation", ["description"] = description,
                          ["fabricated"] = true, ["missionId"] = missionId }), ["ts"] = ts
                    });
                }
                else
                {
                    string occurrenceId = "spymaster_rumor_" + missionId;
                    SpymasterStore.UpsertRumorOccurrences(connection, new Dictionary<string, object>
                    {
                        ["occurrence"] = occurrenceId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["thread"] = "spymaster:" + subjectId, ["day"] = worldDay, ["expires"] = worldDay + 45d,
                        ["participants"] = Json.Serialize(new[] { subjectId }), ["snapshot"] = Json.Serialize(new Dictionary<string, object>
                        { ["fabricated"] = true, ["missionId"] = missionId }), ["ts"] = ts
                    });
                    SpymasterStore.UpsertRumorSubjectTags(connection, new Dictionary<string, object> { ["occurrence"] = occurrenceId, ["subject"] = subjectId, ["tag"] = tagId,
                            ["description"] = description, ["value"] = value, ["snapshot"] = Json.Serialize(new Dictionary<string, object>
                            { ["label"] = "Fabricated rumor", ["description"] = description, ["fabricated"] = true }), ["ts"] = ts });
                }
                ReputationService.ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                return new Dictionary<string, object> { ["ok"] = true, ["outcome"] = positive ? "positive_reputation_created" : severe ? "harmful_reputation_created" : "harmful_rumor_created", ["tagId"] = tagId };
            }
        }

        internal static Dictionary<string, object> SpymasterExposureApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            Dictionary<string, object> mission = ReadDictionary(payload, "mission") ?? new Dictionary<string, object>();
            string actorId = ReadString(mission, "SponsorKingdomStringId", ReadString(mission, "sponsorKingdomStringId", ""));
            string playerId = ReadString(payload, "playerKingdomId", "");
            int tier = ReadInt(mission, "ClanTier", ReadInt(mission, "clanTier", 0));
            bool harmful = ReadBool(mission, "Harmful", ReadBool(mission, "harmful", false));
            bool ruler = ReadBool(mission, "TargetIsRuler", ReadBool(mission, "targetIsRuler", false));
            int delta = ReignSpymasterCore.RelationLoss(tier, harmful, ruler);
            int after = 0;
            if (!string.IsNullOrWhiteSpace(actorId) && !string.IsNullOrWhiteSpace(playerId) && actorId != playerId)
                after = DiplomacyService.ApplyPoliticalPressureDelta(campaignId, timelineId, ReadDouble(payload, "worldDay", 0d),
                    new Dictionary<string, object> { ["kingdomId"] = actorId }, new Dictionary<string, object> { ["kingdomId"] = playerId },
                    delta, "covert_interference", "spymaster-exposure:" + ReadString(mission, "MissionId", ""));
            return new Dictionary<string, object> { ["ok"] = true, ["pressureDelta"] = delta, ["pressureAfter"] = after };
        }

        internal static Dictionary<string, object> SpymasterAgentsStatusApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string playerKingdom = ReadString(payload, "playerKingdomId", "");
            string playerRuler = ReadString(payload, "playerRulerId", "");
            double day = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> candidates = ReadDictionaryList(payload, "candidates");
            List<Dictionary<string, object>> enemies = ReadDictionaryList(payload, "enemyKingdoms");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSpymasterSchema(connection);
                bool hasRelationshipTable = WorldSimulationService.TableExists(connection, "relationships");
                List<Dictionary<string, object>> assignments = SpymasterStore.SelectForeignAgentAssignments(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
                // A character can belong to only one foreign network for this
                // campaign timeline, even after exposure. Sponsor kingdoms may
                // replace exposed agents, but may never recycle that NPC.
                HashSet<string> assignedHeroes = new HashSet<string>(assignments
                    .Select(x => ReadString(x, "agent_hero_id", "")), StringComparer.OrdinalIgnoreCase);
                List<Dictionary<string, object>> candidateDiagnostics = candidates.Select(source =>
                {
                    Dictionary<string, object> row = new Dictionary<string, object>(source);
                    string heroId = ReadString(row, "heroId", "");
                    string exclusionReason = ForeignAgentCandidateExclusionReason(row, playerRuler);
                    Dictionary<string, object> relationship = hasRelationshipTable
                        ? SpymasterStore.SelectRelationships(connection, new Dictionary<string, object> { ["hero"] = heroId, ["ruler"] = playerRuler }).FirstOrDefault()
                        : null;
                    double loyalty = relationship == null ? ReadDouble(row, "nativeLoyaltyFallback", 50d) : ReadDouble(relationship, "loyalty", 50d);
                    row["loyalty"] = loyalty;
                    row["loyaltySource"] = relationship == null ? "native_fallback" : "reign_relationship";
                    row["eligible"] = string.IsNullOrWhiteSpace(exclusionReason) && loyalty <= 40d && !assignedHeroes.Contains(heroId);
                    row["exclusionReason"] = exclusionReason;
                    row["alreadyAssigned"] = assignedHeroes.Contains(heroId);
                    row["recruitmentChance"] = ReignSpymasterCore.RecruitmentChance(loyalty);
                    return row;
                }).OrderBy(x => ReadDouble(x, "loyalty", 100d)).ThenBy(x => ReadString(x, "heroId", ""), StringComparer.OrdinalIgnoreCase).ToList();

                List<Dictionary<string, object>> sponsorDiagnostics = new List<Dictionary<string, object>>();
                foreach (Dictionary<string, object> enemy in enemies)
                {
                    string sponsor = ReadString(enemy, "kingdomId", "");
                    string sponsorRuler = ReadString(enemy, "rulerId", "");
                    if (string.IsNullOrWhiteSpace(sponsor) || sponsor == playerKingdom) continue;
                    Dictionary<string, object> assignment = assignments.FirstOrDefault(x =>
                        string.Equals(ReadString(x, "sponsor_kingdom_id", ""), sponsor, StringComparison.OrdinalIgnoreCase)
                        && (ReadString(x, "status", "") == "dormant" || ReadString(x, "status", "") == "active"));
                    Dictionary<string, object> outlook = ForeignAgentSponsorOutlook(connection,
                        sponsorRuler, playerRuler);
                    double affinity = ReadDouble(outlook, "value", 0d);
                    Dictionary<string, object> projected = null;
                    if (assignment == null)
                    {
                        for (int cycle = 1; cycle <= 24 && projected == null; cycle++)
                        {
                            double projectedDay = day + cycle * 5d;
                            List<Dictionary<string, object>> ranked = candidateDiagnostics.Where(x => ReadBool(x, "eligible", false))
                                .OrderBy(x => RelationshipsService.StableUnit(string.Join("|", campaignId, sponsor, Math.Floor(projectedDay / 5d), ReadString(x, "heroId", ""), "candidate"))).ToList();
                            foreach (Dictionary<string, object> candidate in ranked)
                            {
                                string heroId = ReadString(candidate, "heroId", "");
                                double chance = ReignSpymasterCore.RecruitmentChance(ReadDouble(candidate, "loyalty", 40d)) / 100d;
                                double roll = RelationshipsService.StableUnit(string.Join("|", campaignId, sponsor, Math.Floor(projectedDay / 5d), heroId, "recruit"));
                                if (roll >= chance) continue;
                                projected = new Dictionary<string, object>
                                {
                                    ["cycle"] = cycle, ["worldDay"] = projectedDay, ["agentHeroId"] = heroId,
                                    ["roll"] = roll * 100d, ["chance"] = chance * 100d
                                };
                                break;
                            }
                        }
                    }
                    sponsorDiagnostics.Add(new Dictionary<string, object>
                    {
                        ["kingdomId"] = sponsor, ["rulerId"] = sponsorRuler,
                        ["isAtWar"] = ReadBool(enemy, "isAtWar", false),
                        ["rulerOutlookTowardPlayer"] = affinity,
                        // Retain the old response key for consumers while making
                        // its canonical directional meaning explicit.
                        ["rulerRelationToPlayer"] = affinity,
                        ["outlook"] = outlook,
                        ["activationThresholdMet"] = ReadBool(outlook, "available", false)
                            && ReignSpymasterCore.ForeignAgentCanDisrupt(affinity), ["assignment"] = assignment,
                        ["projectedRecruitment"] = projected
                    });
                }
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["worldDay"] = day,
                    ["readOnly"] = true, ["candidateCount"] = candidateDiagnostics.Count,
                    ["eligibleCandidateCount"] = candidateDiagnostics.Count(x => ReadBool(x, "eligible", false)),
                    ["enemyKingdomCount"] = sponsorDiagnostics.Count, ["activeAssignmentCount"] = assignments.Count(x => ReadString(x, "status", "") == "active"),
                    ["dormantAssignmentCount"] = assignments.Count(x => ReadString(x, "status", "") == "dormant"),
                    ["exposedAssignmentCount"] = assignments.Count(x => ReadString(x, "status", "") == "exposed"),
                    ["candidates"] = candidateDiagnostics.Take(256).ToList(), ["sponsors"] = sponsorDiagnostics,
                    ["assignments"] = assignments, ["projectionHorizonCycles"] = 24
                };
            }
        }

        internal static Dictionary<string, object> SpymasterAgentsTickForClientApi(Dictionary<string, object> payload)
            => payload?.TryGetValue("restoreGeneration", out object generation) == true && generation is string
                ? SpymasterAgentsTickApi(payload)
                : RejectLegacyForeignAgentMutation(payload);

        internal static Dictionary<string, object> RejectLegacyForeignAgentMutation(Dictionary<string, object> _)
            => ForeignAgentAssignmentError("paired_spymaster_update_required");

        private static Dictionary<string, object> SpymasterAgentsTickApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string playerKingdom = ReadString(payload, "playerKingdomId", "");
            string playerRuler = ReadString(payload, "playerRulerId", "");
            double day = ReadDouble(payload, "worldDay", 0d);
            if (!double.IsFinite(day) || !float.IsFinite((float)day))
                return ForeignAgentAssignmentError("invalid_world_day");
            List<Dictionary<string, object>> candidates = ReadDictionaryList(payload, "candidates");
            List<Dictionary<string, object>> enemies = ReadDictionaryList(payload, "enemyKingdoms");
            List<Dictionary<string, object>> actions = new List<Dictionary<string, object>>();
            bool supportsDisruptionReplay = ReadString(payload, "disruptionReplay", "") == ForeignAgentDisruptionReplaySchema;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSpymasterSchema(connection);
                using var transaction = connection.BeginTransaction();
                // Serialize assignment selection, transitions, actions and exposure.
                AcquirePostgreSqlTransactionMutationLock(connection, transaction);
                if (!ForeignAgentScopeIsCurrent(connection, transaction, payload, campaignId, timelineId, out string generation))
                    return ForeignAgentAssignmentError("assignment_scope_changed");
                bool hasRelationshipTable = WorldSimulationService.TableExists(connection, "relationships");
                foreach (Dictionary<string, object> enemy in enemies)
                {
                    string sponsor = ReadString(enemy, "kingdomId", "");
                    string sponsorRuler = ReadString(enemy, "rulerId", "");
                    if (string.IsNullOrWhiteSpace(sponsor) || sponsor == playerKingdom
                        || (supportsDisruptionReplay && !ReadBool(enemy, "isAtWar", false))) continue;
                    bool already = SpymasterStore.SelectForeignAgentAssignments2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["sponsor"] = sponsor }, transaction).Any();
                    if (!already)
                    {
                        HashSet<string> assigned = new HashSet<string>(SpymasterStore.SelectForeignAgentAssignments3(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }, transaction)
                            .Select(x => ReadString(x, "agent_hero_id", "")), StringComparer.OrdinalIgnoreCase);
                        List<Dictionary<string, object>> eligible = candidates.Where(x => !assigned.Contains(ReadString(x, "heroId", ""))
                                && string.IsNullOrWhiteSpace(ForeignAgentCandidateExclusionReason(x, playerRuler)))
                            .Select(x =>
                            {
                                string heroId = ReadString(x, "heroId", "");
                                Dictionary<string, object> relationship = hasRelationshipTable
                                    ? SpymasterStore.SelectRelationships(connection, new Dictionary<string, object> { ["hero"] = heroId, ["ruler"] = playerRuler }).FirstOrDefault()
                                    : null;
                                double loyalty = relationship == null ? ReadDouble(x, "nativeLoyaltyFallback", 50d) : ReadDouble(relationship, "loyalty", 50d);
                                x["_loyalty"] = loyalty;
                                x["_rank"] = RelationshipsService.StableUnit(string.Join("|", campaignId, sponsor, Math.Floor(day / 5d), heroId, "candidate"));
                                return x;
                            }).Where(x => ReadDouble(x, "_loyalty", 50d) <= 40d).OrderBy(x => ReadDouble(x, "_rank", 1d)).ToList();
                        foreach (Dictionary<string, object> candidate in eligible)
                        {
                            string heroId = ReadString(candidate, "heroId", "");
                            double loyalty = Math.Max(0d, Math.Min(40d, ReadDouble(candidate, "_loyalty", 40d)));
                            double chance = ReignSpymasterCore.RecruitmentChance(loyalty) / 100d;
                            double roll = RelationshipsService.StableUnit(string.Join("|", campaignId, sponsor, Math.Floor(day / 5d), heroId, "recruit"));
                            if (roll >= chance) continue;
                            SpymasterStore.InsertForeignAgentAssignments(connection, new Dictionary<string, object>
                                {
                                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = heroId, ["sponsor"] = sponsor,
                                    ["ruler"] = sponsorRuler, ["settlement"] = ReadString(candidate, "settlementId", ""),
                                    ["notable"] = ReadBool(candidate, "isNotable", false) ? 1 : 0, ["day"] = day,
                                    ["next"] = day + 1d + Math.Floor(RelationshipsService.StableUnit(heroId + "|first_action") * 20d),
                                    ["assignment"] = Guid.NewGuid().ToString("N"),
                                    ["payload"] = Json.Serialize(new Dictionary<string, object> { ["hiddenAffiliation"] = sponsor, ["loyaltyAtRecruitment"] = loyalty }),
                                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                }, transaction);
                            break;
                        }
                    }

                    Dictionary<string, object> agent = SpymasterStore.SelectForeignAgentAssignments4(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["sponsor"] = sponsor }, transaction).FirstOrDefault();
                    if (agent == null) continue;
                    Dictionary<string, object> outlook = ForeignAgentSponsorOutlook(connection,
                        sponsorRuler, playerRuler);
                    double affinity = ReadDouble(outlook, "value", 0d);
                    string agentStatus = ReadString(agent, "status", "dormant");
                    bool canDisrupt = ReadBool(outlook, "available", false)
                        && ReignSpymasterCore.ForeignAgentCanDisrupt(affinity);
                    if (canDisrupt && agentStatus == "dormant")
                    {
                        SpymasterStore.UpdateForeignAgentAssignments(connection, new Dictionary<string, object>
                        { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = ReadString(agent, "agent_hero_id", "") }, transaction);
                        agent["status"] = "active";
                    }
                    else if (!canDisrupt && agentStatus == "active")
                    {
                        SpymasterStore.UpdateForeignAgentAssignments2(connection, new Dictionary<string, object>
                        { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = ReadString(agent, "agent_hero_id", "") }, transaction);
                        agent["status"] = "dormant";
                    }
                    double scheduledDueDay = ReadDouble(agent, "next_action_day", day + 1d);
                    if (ReadString(agent, "status", "") != "active" || !(scheduledDueDay <= day)) continue;
                    string agentId = ReadString(agent, "agent_hero_id", "");
                    double productionActionRoll = RelationshipsService.StableUnit(string.Join("|", campaignId, agentId, Math.Floor(day), "action"));
                    Dictionary<string, object> controlledAction = payload.TryGetValue("controlledAgentAction", out object controlledValue)
                        ? controlledValue as Dictionary<string, object> : null;
                    bool controlled = controlledAction != null
                        && ReadString(controlledAction, "confirmation", "") == "force one Reign Spymaster foreign-agent action on disposable save"
                        && string.Equals(ReadString(controlledAction, "agentHeroId", ""), agentId, StringComparison.OrdinalIgnoreCase);
                    double actionRoll = controlled
                        ? Math.Max(0d, Math.Min(0.999999d, ReadDouble(controlledAction, "unitRoll", 0d)))
                        : productionActionRoll;
                    string createdActionId = "";
                    if (actionRoll < 0.58d)
                    {
                        string[] types = { "food", "construction", "security", "loyalty" };
                        int index = Math.Min(types.Length - 1, (int)Math.Floor(RelationshipsService.StableUnit(agentId + "|" + day.ToString(CultureInfo.InvariantCulture) + "|type") * types.Length));
                        string effect = types[index];
                        string actionKey = "foreign-agent-" + agentId + "-" + Math.Floor(day).ToString("0", CultureInfo.InvariantCulture) + "-" + effect;
                        string storedAssignmentId = ReadString(agent, "assignment_id", "");
                        string actionId = string.IsNullOrEmpty(storedAssignmentId) ? actionKey : actionKey + "-" + storedAssignmentId;
                        createdActionId = actionId;
                        double detectRoll = RelationshipsService.StableUnit(actionKey + "|detect");
                        bool detected = detectRoll < 0.25d;
                        bool attributed = detected && RelationshipsService.StableUnit(actionKey + "|attribute") < 0.35d;
                        string assignmentId = ForeignAgentOccurrenceId(campaignId, timelineId, agent);
                        double duration = effect == "construction" ? 14d : 10d;
                        var action = new Dictionary<string, object>
                        {
                            ["actionId"] = actionId,
                            ["assignmentId"] = assignmentId,
                            ["exposureRevision"] = 0,
                            ["agentHeroStringId"] = agentId, ["sponsorKingdomStringId"] = sponsor,
                            ["settlementStringId"] = ReadString(agent, "settlement_id", ""), ["effectType"] = effect,
                            ["durationDays"] = duration,
                            ["magnitude"] = effect == "food" ? -0.20d : effect == "construction" ? -0.30d : -1d,
                            ["detected"] = detected, ["sponsorAttributed"] = attributed,
                            ["summary"] = detected
                                ? attributed ? "The settlement detected a foreign disruption and identified its sponsoring kingdom."
                                : "The settlement detected covert disruption, but the sponsor remained unknown."
                                : "A hidden foreign agent disrupted the settlement without detection."
                        };
                        // A committed receipt records issuance. The client can still lose the reply
                        // or load a save that never persisted the native effect.
                        SpymasterStore.InsertForeignAgentDisruptionReceipt(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["generation"] = generation,
                            ["action"] = actionId, ["assignment"] = assignmentId, ["due"] = scheduledDueDay,
                            ["issued"] = day, ["expires"] = (double)((float)day + (float)duration),
                            ["payload"] = Json.Serialize(action)
                        }, transaction);
                        if (!supportsDisruptionReplay) actions.Add(action);
                    }
                    double next = day + 1d + Math.Floor(RelationshipsService.StableUnit(agentId + "|" + day.ToString(CultureInfo.InvariantCulture) + "|next") * 20d);
                    SpymasterStore.UpdateForeignAgentAssignments3(connection, new Dictionary<string, object>
                    { ["day"] = day, ["next"] = next, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = agentId }, transaction);
                    if (controlled)
                    {
                        payload["_controlledAgentActionReceipt"] = new Dictionary<string, object>
                        {
                            ["consumed"] = true,
                            ["agentHeroId"] = agentId,
                            ["worldDay"] = day,
                            ["productionActionChance"] = 0.58d,
                            ["productionStableRoll"] = productionActionRoll,
                            ["appliedRoll"] = actionRoll,
                            ["actionCreated"] = !string.IsNullOrWhiteSpace(createdActionId),
                            ["actionId"] = createdActionId
                        };
                    }
                }

                var assignments = SpymasterStore.SelectForeignAgentAssignments5(connection,
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }, transaction);
                if (supportsDisruptionReplay)
                    actions = ReadUnexpiredForeignAgentDisruptions(connection, transaction, campaignId, timelineId, generation,
                        day, assignments, enemies, playerKingdom);
                transaction.Commit();
                var response = new Dictionary<string, object>
                {
                    ["ok"] = true, ["schema"] = ForeignAgentAssignmentSchema,
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["restoreGeneration"] = generation,
                    ["agents"] = assignments.Where(x => ReadString(x, "status", "") is "active" or "dormant")
                        .Select(x => ForeignAgentSnapshot(campaignId, timelineId, x)).ToList(),
                    ["exposedAgents"] = assignments.Where(x => ReadString(x, "status", "") == "exposed")
                        .Select(x => ForeignAgentSnapshot(campaignId, timelineId, x)).ToList(),
                    ["actions"] = actions,
                    ["controlledAgentAction"] = payload.TryGetValue("_controlledAgentActionReceipt", out object controlledReceipt)
                        ? controlledReceipt : null
                };
                if (supportsDisruptionReplay) response["disruptionReplay"] = ForeignAgentDisruptionReplaySchema;
                return response;
            }
        }

        private static List<Dictionary<string, object>> ReadUnexpiredForeignAgentDisruptions(ReignDbConnection connection,
            ReignDbTransaction transaction, string campaign, string timeline, string generation, double day,
            List<Dictionary<string, object>> assignments, List<Dictionary<string, object>> enemies, string playerKingdom)
        {
            var sponsors = new HashSet<string>(enemies.Where(row => ReadBool(row, "isAtWar", false))
                .Select(row => ReadString(row, "kingdomId", "")).Where(id => id.Length != 0 && id != playerKingdom),
                StringComparer.Ordinal);
            var active = assignments.Where(row => ReadString(row, "status", "") == "active"
                    && sponsors.Contains(ReadString(row, "sponsor_kingdom_id", "")))
                .GroupBy(row => ForeignAgentOccurrenceId(campaign, timeline, row), StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var actions = new List<Dictionary<string, object>>();
            if (active.Count == 0) return actions;
            foreach (var receipt in SpymasterStore.ReadForeignAgentDisruptionReceipts(connection,
                new Dictionary<string, object> { ["campaign"] = campaign, ["timeline"] = timeline, ["generation"] = generation,
                    ["day"] = day }, transaction))
            {
                string assignmentId = ReadString(receipt, "assignment_id", "");
                if (!active.TryGetValue(assignmentId, out var owner)) continue;
                Dictionary<string, object> action;
                try { action = Json.Deserialize<Dictionary<string, object>>(ReadString(receipt, "action_json", "")); }
                catch (System.Text.Json.JsonException) { continue; }
                string actionId = ReadString(receipt, "action_id", "");
                if (actionId.Length == 0 || ReadString(action, "actionId", "") != actionId
                    || ReadString(action, "assignmentId", "") != assignmentId
                    || ReadString(action, "agentHeroStringId", "") != ReadString(owner, "agent_hero_id", "")
                    || ReadString(action, "sponsorKingdomStringId", "") != ReadString(owner, "sponsor_kingdom_id", ""))
                    continue;
                action["issuedDay"] = ReadDouble(receipt, "issued_day", -1d);
                action["expiresDay"] = ReadDouble(receipt, "expires_day", -1d);
                actions.Add(action);
            }
            return actions;
        }

        private static string ForeignAgentCandidateExclusionReason(Dictionary<string, object> candidate, string playerRulerId)
        {
            string heroId = ReadString(candidate, "heroId", "");
            if (string.IsNullOrWhiteSpace(heroId))
                return "missing_identity";
            if (string.Equals(heroId, playerRulerId, StringComparison.OrdinalIgnoreCase))
                return "player_ruler";
            if (ReadBool(candidate, "excludedFromForeignAgentRecruitment", false))
                return "client_excluded";
            return string.Empty;
        }

        private static Dictionary<string, object> ForeignAgentSponsorOutlook(
            ReignDbConnection connection, string sponsorRuler, string playerRuler)
        {
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["available"] = false,
                ["value"] = 0d,
                ["source"] = "missing_directional_outlook",
                ["sponsorRulerId"] = sponsorRuler ?? string.Empty,
                ["playerRulerId"] = playerRuler ?? string.Empty
            };
            if (string.IsNullOrWhiteSpace(sponsorRuler)
                || string.IsNullOrWhiteSpace(playerRuler))
            {
                result["source"] = "missing_ruler_identity";
                return result;
            }
            if (!WorldSimulationService.TableExists(connection, "relationship_pair_chemistry"))
            {
                result["source"] = "missing_relationship_pair_table";
                return result;
            }

            Dictionary<string, object> pair = SpymasterStore.SelectRelationshipPairChemistry(connection, new Dictionary<string, object>
            {
                ["sponsor"] = sponsorRuler,
                ["player"] = playerRuler
            }).FirstOrDefault();
            if (pair == null) return result;

            bool sponsorIsA = ReadString(pair, "hero_a_id", "")
                .Equals(sponsorRuler, StringComparison.OrdinalIgnoreCase);
            string affinityColumn = sponsorIsA ? "affinity_a_to_b" : "affinity_b_to_a";
            string reverseColumn = sponsorIsA ? "affinity_b_to_a" : "affinity_a_to_b";
            string effectiveColumn = sponsorIsA
                ? "effective_affinity_a_to_b" : "effective_affinity_b_to_a";
            result["available"] = true;
            result["value"] = ReadDouble(pair, affinityColumn, 0d);
            result["source"] = "directional_underlying_affinity";
            result["pairKey"] = ReadString(pair, "pair_key", "");
            result["direction"] = sponsorIsA ? "a_to_b" : "b_to_a";
            result["reversePlayerOutlook"] = ReadDouble(pair, reverseColumn, 0d);
            result["effectiveOutlookDiagnosticOnly"] = ReadDouble(pair, effectiveColumn, 0d);
            result["lastChangedDay"] = ReadDouble(pair, "last_day", -1d);
            result["lastContextKind"] = ReadString(pair, "last_context_kind", "");
            result["lastContextId"] = ReadString(pair, "last_context_id", "");
            result["stateRevision"] = ReadLong(pair, "state_revision", 0L);
            return result;
        }

        private const string ForeignAgentAssignmentSchema = "reign-spymaster-foreign-agent-v1";
        private const string ForeignAgentDisruptionReplaySchema = "reign-spymaster-disruption-replay-v1";

        private static bool ForeignAgentScopeIsCurrent(ReignDbConnection connection, ReignDbTransaction transaction,
            Dictionary<string, object> payload, string campaignId, string timelineId, out string generation)
        {
            // Direct provider-free fixtures can use a campaign without a transport generation.
            generation = "";
            if (!payload.TryGetValue("restoreGeneration", out object requested))
                return true;
            generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
            if (requested is not string expected || expected != generation)
                return false;
            var scope = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId };
            var timelines = SpymasterStore.LockMitigationTimelines(connection, scope, transaction);
            return timelines.Count == 0 ? timelineId == "main"
                : timelines.Count == 1 && ReadString(timelines[0], "timeline_id", "") == timelineId
                    && ReadInt(timelines[0], "is_active", 0) == 1;
        }

        private static string ForeignAgentOccurrenceId(string campaignId, string timelineId, Dictionary<string, object> row)
        {
            string stored = ReadString(row, "assignment_id", "");
            string hero = ReadString(row, "agent_hero_id", "");
            if (!string.IsNullOrEmpty(stored))
                return Sha256Hex(Json.Serialize(new[] { "assigned", campaignId, timelineId, hero, stored }));

            // Exposed heroes are never recycled on a timeline. Old rows have no stored UUID,
            // so use the immutable recruitment record, independent of ticks and exposure.
            string sponsor = ReadString(row, "sponsor_kingdom_id", "");
            string recruited = ReadDouble(row, "recruited_day", 0d).ToString("R", CultureInfo.InvariantCulture);
            return Sha256Hex(Json.Serialize(new[] { "legacy", campaignId, timelineId, hero, sponsor, recruited }));
        }

        private static Dictionary<string, object> ForeignAgentSnapshot(string campaignId, string timelineId, Dictionary<string, object> row)
        {
            string status = ReadString(row, "status", "");
            var snapshot = new Dictionary<string, object>
            {
                ["assignmentId"] = ForeignAgentOccurrenceId(campaignId, timelineId, row),
                ["legacyOccurrence"] = string.IsNullOrEmpty(ReadString(row, "assignment_id", "")),
                ["exposureRevision"] = status == "exposed" ? 1 : 0,
                ["agentHeroStringId"] = ReadString(row, "agent_hero_id", ""),
                ["sponsorKingdomStringId"] = ReadString(row, "sponsor_kingdom_id", ""),
                ["settlementStringId"] = ReadString(row, "settlement_id", ""),
                ["isNotable"] = ReadInt(row, "is_notable", 0) != 0,
                ["activated"] = status == "active",
                ["recruitedDay"] = ReadDouble(row, "recruited_day", 0d),
                ["nextActionDay"] = ReadDouble(row, "next_action_day", 0d)
            };
            if (status == "exposed")
            {
                var details = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                snapshot["exposedDay"] = details == null ? -1d : ReadDouble(details, "exposedDay", -1d);
            }
            return snapshot;
        }

        private static Dictionary<string, object> ForeignAgentAssignmentError(string error)
            => new Dictionary<string, object> { ["ok"] = false, ["schema"] = ForeignAgentAssignmentSchema, ["error"] = error };

        internal static Dictionary<string, object> SpymasterAgentExposeApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string timelineId = ReadString(payload, "timelineId", "");
            string agentId = ReadString(payload, "agentHeroId", "");
            string assignmentId = ReadString(payload, "assignmentId", "");
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(timelineId)
                || string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(assignmentId)
                || !payload.ContainsKey("restoreGeneration")
                || !payload.TryGetValue("exposureRevision", out object revision) || revision is not 0)
                return ForeignAgentAssignmentError("invalid_assignment_exposure");

            using var connection = ReignDatabase.OpenCampaignConnection(campaignId);
            EnsureSpymasterSchema(connection);
            using var transaction = connection.BeginTransaction();
            AcquirePostgreSqlTransactionMutationLock(connection, transaction);
            if (!ForeignAgentScopeIsCurrent(connection, transaction, payload, campaignId, timelineId, out string generation))
                return ForeignAgentAssignmentError("assignment_scope_changed");
            var parameters = new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = agentId
            };
            var assignment = SpymasterStore.ReadForeignAgentAssignment(connection, parameters, transaction);
            if (assignment == null || ForeignAgentOccurrenceId(campaignId, timelineId, assignment) != assignmentId)
                return ForeignAgentAssignmentError("assignment_not_found");
            if (ReadString(assignment, "status", "") != "exposed")
            {
                var details = TryParseJsonObject(ReadString(assignment, "payload_json", "{}")) ?? new Dictionary<string, object>();
                details["exposureReason"] = ReadString(payload, "reason", "exposed");
                details["exposedDay"] = ReadDouble(payload, "worldDay", -1d);
                parameters["payload"] = Json.Serialize(details);
                parameters["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!SpymasterStore.ExposeForeignAgentAssignment(connection, parameters, transaction))
                    return ForeignAgentAssignmentError("assignment_exposure_not_applied");
            }
            transaction.Commit();
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["schema"] = ForeignAgentAssignmentSchema, ["status"] = "exposed",
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["restoreGeneration"] = generation,
                ["agentHeroId"] = agentId, ["assignmentId"] = assignmentId, ["exposureRevision"] = 1
            };
        }
    }
}
