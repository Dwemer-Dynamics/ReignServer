using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReignShared.Spymaster;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureSpymasterSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS spymaster_mission_memory (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,mission_id TEXT NOT NULL,spymaster_hero_id TEXT NOT NULL,
phase TEXT NOT NULL,mission_type TEXT NOT NULL,target_type TEXT NOT NULL,target_id TEXT NOT NULL,target_name TEXT NOT NULL,
started_day REAL NOT NULL,due_day REAL NOT NULL,resolved_day REAL NOT NULL DEFAULT -1,request_summary TEXT NOT NULL DEFAULT '',
result_summary TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,mission_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_spymaster_memory_owner ON spymaster_mission_memory(campaign_id,timeline_id,spymaster_hero_id,started_day DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS foreign_agent_assignments (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,agent_hero_id TEXT NOT NULL,sponsor_kingdom_id TEXT NOT NULL,
sponsor_ruler_id TEXT NOT NULL DEFAULT '',settlement_id TEXT NOT NULL DEFAULT '',is_notable INTEGER NOT NULL DEFAULT 0,
status TEXT NOT NULL DEFAULT 'dormant',recruited_day REAL NOT NULL,next_action_day REAL NOT NULL DEFAULT -1,
last_action_day REAL NOT NULL DEFAULT -1,payload_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,agent_hero_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_foreign_agents_sponsor ON foreign_agent_assignments(campaign_id,timeline_id,sponsor_kingdom_id,status);");
        }

        private static void AppendSpymasterRelevantMemoryBundle(
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

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSpymasterSchema(connection);
                List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT mission_id,phase,mission_type,target_type,target_id,target_name,
started_day,due_day,resolved_day,request_summary,result_summary
FROM spymaster_mission_memory
WHERE campaign_id=$campaign AND timeline_id=$timeline AND spymaster_hero_id=$hero
ORDER BY started_day DESC,updated_ts DESC LIMIT 12;", new Dictionary<string, object>
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

        private static Dictionary<string, object> SpymasterMemoryApi(Dictionary<string, object> payload)
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSpymasterSchema(connection);
                ExecuteSql(connection, @"INSERT INTO spymaster_mission_memory(campaign_id,timeline_id,mission_id,spymaster_hero_id,
phase,mission_type,target_type,target_id,target_name,started_day,due_day,resolved_day,request_summary,result_summary,payload_json,updated_ts)
VALUES($campaign,$timeline,$mission,$spymaster,$phase,$type,$targetType,$target,$targetName,$started,$due,$resolved,$request,$result,$payload,$ts)
ON CONFLICT(campaign_id,timeline_id,mission_id) DO UPDATE SET phase=$phase,resolved_day=$resolved,
result_summary=$result,payload_json=$payload,updated_ts=$ts;", new Dictionary<string, object>
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
            StoreWorldMemoryEvent(new Dictionary<string, object>
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
                StoreWorldMemoryEvent(new Dictionary<string, object>
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

        private static Dictionary<string, object> SpymasterSocialStatusApi(Dictionary<string, object> payload)
        {
            return SocialCharacterStatusApi(payload ?? new Dictionary<string, object>());
        }

        private static Dictionary<string, object> SpymasterSocialApplyApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            Dictionary<string, object> mission = ReadDictionary(payload, "mission") ?? new Dictionary<string, object>();
            string type = ReadString(mission, "MissionType", ReadString(mission, "missionType", ""));
            string subjectId = ReadString(mission, "TargetStringId", ReadString(mission, "targetStringId", ""));
            string itemId = ReadString(mission, "SocialItemId", ReadString(mission, "socialItemId", ""));
            string fabricated = ReadString(mission, "FabricatedText", ReadString(mission, "fabricatedText", ""));
            string missionId = ReadString(mission, "MissionId", ReadString(mission, "missionId", Guid.NewGuid().ToString("N")));
            double worldDay = ReadDouble(mission, "ResolvedDay", ReadDouble(mission, "resolvedDay", 0d));
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                double roll = StableUnit(string.Join("|", campaignId, timelineId, missionId, "social_apply"));
                if (type.Contains("mitigate") && type.Contains("rumor"))
                {
                    string status = ReignSpymasterCore.SocialMitigationOutcome(type, roll);
                    if (status == "disproven")
                        ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='disproven',updated_ts=$ts
WHERE subject_id=$subject AND tag_id=$tag AND status='active';", new Dictionary<string, object> { ["ts"] = ts, ["subject"] = subjectId, ["tag"] = itemId });
                    else
                        ExecuteSql(connection, @"UPDATE rumor_subject_tags SET rumor_value=CAST(rumor_value*$factor AS INTEGER),updated_ts=$ts
WHERE subject_id=$subject AND tag_id=$tag AND status='active';", new Dictionary<string, object> { ["ts"] = ts, ["subject"] = subjectId, ["tag"] = itemId, ["factor"] = ReignSpymasterCore.SocialMitigationFactor });
                    ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                    return new Dictionary<string, object> { ["ok"] = true, ["outcome"] = status, ["roll"] = roll };
                }
                if (type.Contains("mitigate") && type.Contains("reputation"))
                {
                    bool removed = ReignSpymasterCore.SocialMitigationOutcome(type, roll) == "removed";
                    if (removed)
                        ExecuteSql(connection, @"UPDATE character_reputations SET status='mitigated',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active';",
                            new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = itemId, ["ts"] = ts });
                    else
                        ExecuteSql(connection, @"UPDATE character_reputations SET reputation_value=CAST(reputation_value*$factor AS INTEGER),updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active';",
                            new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = itemId, ["factor"] = ReignSpymasterCore.SocialMitigationFactor, ["ts"] = ts });
                    ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                    return new Dictionary<string, object> { ["ok"] = true, ["outcome"] = removed ? "removed" : "greatly_mitigated", ["roll"] = roll };
                }

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
                    ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,'',$archetype,'target',$description,$value,$day,0,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET description=$description,reputation_value=$value,
acquired_day=$day,status='active',updated_ts=$ts;", new Dictionary<string, object>
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
                    ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_occurrences(occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,
source_event_id,world_day,expires_day,status,exposure_chance,exposure_roll,catalog_revision,participants_json,provenance_summary,snapshot_json,created_ts,updated_ts)
VALUES($occurrence,$campaign,$timeline,'spymaster_fabrication',$thread,'',$day,$expires,'active',1,0,0,$participants,
'An unattributed story is circulating.',$snapshot,$ts,$ts);", new Dictionary<string, object>
                    {
                        ["occurrence"] = occurrenceId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["thread"] = "spymaster:" + subjectId, ["day"] = worldDay, ["expires"] = worldDay + 45d,
                        ["participants"] = Json.Serialize(new[] { subjectId }), ["snapshot"] = Json.Serialize(new Dictionary<string, object>
                        { ["fabricated"] = true, ["missionId"] = missionId }), ["ts"] = ts
                    });
                    ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_subject_tags(occurrence_id,subject_id,tag_id,subject_role,description,
rumor_value,reputation_value,status,co_participants_json,snapshot_json,updated_ts)
VALUES($occurrence,$subject,$tag,'target',$description,$value,0,'active','[]',$snapshot,$ts);",
                        new Dictionary<string, object> { ["occurrence"] = occurrenceId, ["subject"] = subjectId, ["tag"] = tagId,
                            ["description"] = description, ["value"] = value, ["snapshot"] = Json.Serialize(new Dictionary<string, object>
                            { ["label"] = "Fabricated rumor", ["description"] = description, ["fabricated"] = true }), ["ts"] = ts });
                }
                ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                return new Dictionary<string, object> { ["ok"] = true, ["outcome"] = positive ? "positive_reputation_created" : severe ? "harmful_reputation_created" : "harmful_rumor_created", ["tagId"] = tagId };
            }
        }

        private static Dictionary<string, object> SpymasterExposureApi(Dictionary<string, object> payload)
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
                after = ApplyPoliticalPressureDelta(campaignId, timelineId, ReadDouble(payload, "worldDay", 0d),
                    new Dictionary<string, object> { ["kingdomId"] = actorId }, new Dictionary<string, object> { ["kingdomId"] = playerId },
                    delta, "covert_interference", "spymaster-exposure:" + ReadString(mission, "MissionId", ""));
            return new Dictionary<string, object> { ["ok"] = true, ["pressureDelta"] = delta, ["pressureAfter"] = after };
        }

        private static Dictionary<string, object> SpymasterAgentsStatusApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string playerKingdom = ReadString(payload, "playerKingdomId", "");
            string playerRuler = ReadString(payload, "playerRulerId", "");
            double day = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> candidates = ReadDictionaryList(payload, "candidates");
            List<Dictionary<string, object>> enemies = ReadDictionaryList(payload, "enemyKingdoms");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSpymasterSchema(connection);
                bool hasRelationshipTable = TableExists(connection, "relationships");
                List<Dictionary<string, object>> assignments = QuerySql(connection, @"SELECT agent_hero_id,sponsor_kingdom_id,sponsor_ruler_id,
settlement_id,is_notable,status,recruited_day,next_action_day,last_action_day,payload_json
FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY sponsor_kingdom_id,agent_hero_id;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
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
                        ? QuerySql(connection,
                            "SELECT loyalty FROM relationships WHERE subject_id=$hero AND target_id=$ruler LIMIT 1;",
                            new Dictionary<string, object> { ["hero"] = heroId, ["ruler"] = playerRuler }).FirstOrDefault()
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
                                .OrderBy(x => StableUnit(string.Join("|", campaignId, sponsor, Math.Floor(projectedDay / 5d), ReadString(x, "heroId", ""), "candidate"))).ToList();
                            foreach (Dictionary<string, object> candidate in ranked)
                            {
                                string heroId = ReadString(candidate, "heroId", "");
                                double chance = ReignSpymasterCore.RecruitmentChance(ReadDouble(candidate, "loyalty", 40d)) / 100d;
                                double roll = StableUnit(string.Join("|", campaignId, sponsor, Math.Floor(projectedDay / 5d), heroId, "recruit"));
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

        private static Dictionary<string, object> SpymasterAgentsTickApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string playerKingdom = ReadString(payload, "playerKingdomId", "");
            string playerRuler = ReadString(payload, "playerRulerId", "");
            double day = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> candidates = ReadDictionaryList(payload, "candidates");
            List<Dictionary<string, object>> enemies = ReadDictionaryList(payload, "enemyKingdoms");
            List<Dictionary<string, object>> actions = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSpymasterSchema(connection);
                bool hasRelationshipTable = TableExists(connection, "relationships");
                foreach (Dictionary<string, object> enemy in enemies)
                {
                    string sponsor = ReadString(enemy, "kingdomId", "");
                    string sponsorRuler = ReadString(enemy, "rulerId", "");
                    if (string.IsNullOrWhiteSpace(sponsor) || sponsor == playerKingdom) continue;
                    bool already = QuerySql(connection, @"SELECT 1 AS found FROM foreign_agent_assignments
WHERE campaign_id=$campaign AND timeline_id=$timeline AND sponsor_kingdom_id=$sponsor AND status IN ('dormant','active') LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["sponsor"] = sponsor }).Any();
                    if (!already)
                    {
                        HashSet<string> assigned = new HashSet<string>(QuerySql(connection, @"SELECT agent_hero_id FROM foreign_agent_assignments
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                            new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                            .Select(x => ReadString(x, "agent_hero_id", "")), StringComparer.OrdinalIgnoreCase);
                        List<Dictionary<string, object>> eligible = candidates.Where(x => !assigned.Contains(ReadString(x, "heroId", ""))
                                && string.IsNullOrWhiteSpace(ForeignAgentCandidateExclusionReason(x, playerRuler)))
                            .Select(x =>
                            {
                                string heroId = ReadString(x, "heroId", "");
                                Dictionary<string, object> relationship = hasRelationshipTable
                                    ? QuerySql(connection,
                                        "SELECT loyalty FROM relationships WHERE subject_id=$hero AND target_id=$ruler LIMIT 1;",
                                        new Dictionary<string, object> { ["hero"] = heroId, ["ruler"] = playerRuler }).FirstOrDefault()
                                    : null;
                                double loyalty = relationship == null ? ReadDouble(x, "nativeLoyaltyFallback", 50d) : ReadDouble(relationship, "loyalty", 50d);
                                x["_loyalty"] = loyalty;
                                x["_rank"] = StableUnit(string.Join("|", campaignId, sponsor, Math.Floor(day / 5d), heroId, "candidate"));
                                return x;
                            }).Where(x => ReadDouble(x, "_loyalty", 50d) <= 40d).OrderBy(x => ReadDouble(x, "_rank", 1d)).ToList();
                        foreach (Dictionary<string, object> candidate in eligible)
                        {
                            string heroId = ReadString(candidate, "heroId", "");
                            double loyalty = Math.Max(0d, Math.Min(40d, ReadDouble(candidate, "_loyalty", 40d)));
                            double chance = ReignSpymasterCore.RecruitmentChance(loyalty) / 100d;
                            double roll = StableUnit(string.Join("|", campaignId, sponsor, Math.Floor(day / 5d), heroId, "recruit"));
                            if (roll >= chance) continue;
                            ExecuteSql(connection, @"INSERT INTO foreign_agent_assignments(campaign_id,timeline_id,agent_hero_id,sponsor_kingdom_id,
sponsor_ruler_id,settlement_id,is_notable,status,recruited_day,next_action_day,last_action_day,payload_json,updated_ts)
VALUES($campaign,$timeline,$agent,$sponsor,$ruler,$settlement,$notable,'dormant',$day,$next,-1,$payload,$ts);",
                                new Dictionary<string, object>
                                {
                                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = heroId, ["sponsor"] = sponsor,
                                    ["ruler"] = sponsorRuler, ["settlement"] = ReadString(candidate, "settlementId", ""),
                                    ["notable"] = ReadBool(candidate, "isNotable", false) ? 1 : 0, ["day"] = day,
                                    ["next"] = day + 1d + Math.Floor(StableUnit(heroId + "|first_action") * 20d),
                                    ["payload"] = Json.Serialize(new Dictionary<string, object> { ["hiddenAffiliation"] = sponsor, ["loyaltyAtRecruitment"] = loyalty }),
                                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                });
                            break;
                        }
                    }

                    Dictionary<string, object> agent = QuerySql(connection, @"SELECT * FROM foreign_agent_assignments
WHERE campaign_id=$campaign AND timeline_id=$timeline AND sponsor_kingdom_id=$sponsor AND status IN ('dormant','active') LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["sponsor"] = sponsor }).FirstOrDefault();
                    if (agent == null) continue;
                    Dictionary<string, object> outlook = ForeignAgentSponsorOutlook(connection,
                        sponsorRuler, playerRuler);
                    double affinity = ReadDouble(outlook, "value", 0d);
                    string agentStatus = ReadString(agent, "status", "dormant");
                    bool canDisrupt = ReadBool(outlook, "available", false)
                        && ReignSpymasterCore.ForeignAgentCanDisrupt(affinity);
                    if (canDisrupt && agentStatus == "dormant")
                    {
                        ExecuteSql(connection, @"UPDATE foreign_agent_assignments SET status='active',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;", new Dictionary<string, object>
                        { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = ReadString(agent, "agent_hero_id", "") });
                        agent["status"] = "active";
                    }
                    else if (!canDisrupt && agentStatus == "active")
                    {
                        ExecuteSql(connection, @"UPDATE foreign_agent_assignments SET status='dormant',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;", new Dictionary<string, object>
                        { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = ReadString(agent, "agent_hero_id", "") });
                        agent["status"] = "dormant";
                    }
                    if (ReadString(agent, "status", "") != "active" || ReadDouble(agent, "next_action_day", day + 1d) > day) continue;
                    string agentId = ReadString(agent, "agent_hero_id", "");
                    double productionActionRoll = StableUnit(string.Join("|", campaignId, agentId, Math.Floor(day), "action"));
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
                        int index = Math.Min(types.Length - 1, (int)Math.Floor(StableUnit(agentId + "|" + day.ToString(CultureInfo.InvariantCulture) + "|type") * types.Length));
                        string effect = types[index];
                        string actionId = "foreign-agent-" + agentId + "-" + Math.Floor(day).ToString("0", CultureInfo.InvariantCulture) + "-" + effect;
                        createdActionId = actionId;
                        double detectRoll = StableUnit(actionId + "|detect");
                        bool detected = detectRoll < 0.25d;
                        bool attributed = detected && StableUnit(actionId + "|attribute") < 0.35d;
                        actions.Add(new Dictionary<string, object>
                        {
                            ["actionId"] = actionId,
                            ["agentHeroStringId"] = agentId, ["sponsorKingdomStringId"] = sponsor,
                            ["settlementStringId"] = ReadString(agent, "settlement_id", ""), ["effectType"] = effect,
                            ["durationDays"] = effect == "construction" ? 14d : 10d,
                            ["magnitude"] = effect == "food" ? -0.20d : effect == "construction" ? -0.30d : -1d,
                            ["detected"] = detected, ["sponsorAttributed"] = attributed,
                            ["summary"] = detected
                                ? attributed ? "The settlement detected a foreign disruption and identified its sponsoring kingdom."
                                : "The settlement detected covert disruption, but the sponsor remained unknown."
                                : "A hidden foreign agent disrupted the settlement without detection."
                        });
                    }
                    double next = day + 1d + Math.Floor(StableUnit(agentId + "|" + day.ToString(CultureInfo.InvariantCulture) + "|next") * 20d);
                    ExecuteSql(connection, @"UPDATE foreign_agent_assignments SET last_action_day=$day,next_action_day=$next,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;", new Dictionary<string, object>
                    { ["day"] = day, ["next"] = next, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = agentId });
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

                List<Dictionary<string, object>> agents = QuerySql(connection, @"SELECT agent_hero_id,sponsor_kingdom_id,settlement_id,is_notable,status,recruited_day,next_action_day
FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline AND status IN ('dormant','active');",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["agents"] = agents.Select(x => new Dictionary<string, object>
                    {
                        ["agentHeroStringId"] = ReadString(x, "agent_hero_id", ""),
                        ["sponsorKingdomStringId"] = ReadString(x, "sponsor_kingdom_id", ""),
                        ["settlementStringId"] = ReadString(x, "settlement_id", ""), ["isNotable"] = ReadInt(x, "is_notable", 0) != 0,
                        ["activated"] = ReadString(x, "status", "") == "active", ["recruitedDay"] = ReadDouble(x, "recruited_day", 0d),
                        ["nextActionDay"] = ReadDouble(x, "next_action_day", 0d)
                    }).ToList(),
                    ["actions"] = actions,
                    ["controlledAgentAction"] = payload.TryGetValue("_controlledAgentActionReceipt", out object controlledReceipt)
                        ? controlledReceipt : null
                };
            }
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
            if (!TableExists(connection, "relationship_pair_chemistry"))
            {
                result["source"] = "missing_relationship_pair_table";
                return result;
            }

            Dictionary<string, object> pair = QuerySql(connection, @"SELECT pair_key,hero_a_id,hero_b_id,
affinity_a_to_b,affinity_b_to_a,effective_affinity_a_to_b,effective_affinity_b_to_a,
last_day,last_context_kind,last_context_id,state_revision
FROM relationship_pair_chemistry
WHERE (hero_a_id=$sponsor AND hero_b_id=$player)
   OR (hero_a_id=$player AND hero_b_id=$sponsor)
LIMIT 1;", new Dictionary<string, object>
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

        private static Dictionary<string, object> SpymasterAgentExposeApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string agentId = ReadString(payload, "agentHeroId", "");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSpymasterSchema(connection);
                ExecuteSql(connection, @"UPDATE foreign_agent_assignments SET status='exposed',payload_json=$payload,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;", new Dictionary<string, object>
                {
                    ["payload"] = Json.Serialize(new Dictionary<string, object> { ["exposureReason"] = ReadString(payload, "reason", "exposed") }),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId, ["timeline"] = timelineId, ["agent"] = agentId
                });
            }
            return new Dictionary<string, object> { ["ok"] = true, ["agentHeroId"] = agentId, ["status"] = "exposed" };
        }
    }
}
