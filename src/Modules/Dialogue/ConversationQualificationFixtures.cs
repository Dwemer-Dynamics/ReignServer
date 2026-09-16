using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> ConversationQualificationRelationshipFixtureApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!IsLiveTestArmed(ReadJsonObject(LiveTestArmPath())))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "The live-test bridge is not armed."
                };
            if (!ReadString(payload, "confirmation", "")
                .Equals("qualification_fixture", StringComparison.Ordinal))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "confirmation=qualification_fixture is required."
                };
            }

            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            Dictionary<string, object> state =
                ReadJsonObject(ConversationReadinessStatePath(campaignId));
            string qualificationId = ReadString(state, "qualificationId", "");
            string requestedQualificationId =
                ReadString(payload, "qualificationId", qualificationId);
            if (state.Count == 0
                || string.IsNullOrWhiteSpace(qualificationId)
                || !qualificationId.Equals(
                    requestedQualificationId, StringComparison.OrdinalIgnoreCase)
                || !ReadString(state, "buildVersion", "").Equals(
                    CurrentConversationBuildVersion(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "A same-build active conversation qualification is required."
                };
            }

            string operation = ReadString(payload, "operation", "set")
                .Trim().ToLowerInvariant();
            if (operation == "restore")
                return RestoreConversationQualificationRelationshipFixtures(
                    campaignId, qualificationId, state);
            if (operation != "set")
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "operation must be set or restore."
                };

            string firstId = ReadFirstString(payload, "heroAId", "observerHeroId");
            string secondId = ReadFirstString(payload, "heroBId", "targetHeroId");
            if (string.IsNullOrWhiteSpace(firstId)
                || string.IsNullOrWhiteSpace(secondId)
                || firstId.Equals(secondId, StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Two different hero ids are required."
                };
            }

            int firstToSecond = Clamp(
                ReadInt(payload, "affinityAtoB", 62), -100, 100);
            int secondToFirst = Clamp(
                ReadInt(payload, "affinityBtoA", -34), -100, 100);
            string pairKey = AmbientPairKey(firstId, secondId);
            Dictionary<string, object> before;
            Dictionary<string, object> after;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                before = QuerySql(connection,
                    "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pairKey })
                    .FirstOrDefault();
                if (ReadBool(payload, "preserveIfExists", false)
                    && before != null)
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["pairKey"] = pairKey,
                        ["preservedExisting"] = true,
                        ["before"] = before,
                        ["after"] = before
                    };
                }
                List<Dictionary<string, object>> fixtures =
                    ReadDictionaryList(state, "relationshipQualificationFixtures");
                if (!fixtures.Any(row => ReadString(row, "pairKey", "")
                    .Equals(pairKey, StringComparison.OrdinalIgnoreCase)))
                {
                    fixtures.Add(new Dictionary<string, object>
                    {
                        ["fixtureId"] = "relationship-fixture-"
                            + Guid.NewGuid().ToString("N"),
                        ["pairKey"] = pairKey,
                        ["heroAId"] = firstId,
                        ["heroBId"] = secondId,
                        ["originalExists"] = before != null,
                        ["originalRow"] = before
                            ?? new Dictionary<string, object>(),
                        ["createdUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                        ["restored"] = false
                    });
                    state["relationshipQualificationFixtures"] = fixtures;
                    state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                    // Persist the recovery manifest before touching relationship
                    // state so an interrupted qualification can still restore it.
                    WriteJsonObject(
                        ConversationReadinessStatePath(campaignId), state);
                }

                int currentFirstToSecond =
                    DirectionalFixtureAffinity(before, firstId, secondId);
                int currentSecondToFirst =
                    DirectionalFixtureAffinity(before, secondId, firstId);
                int firstDelta = firstToSecond - currentFirstToSecond;
                int secondDelta = secondToFirst - currentSecondToFirst;
                double worldDay = ReadDouble(payload, "worldDay", 0d);
                if (firstDelta != 0)
                    ApplyAuthoritativeRelationshipDelta(
                        connection, campaignId, firstId, secondId,
                        firstDelta, worldDay, "qualification_fixture",
                        ReadString(payload, "timelineId", "main"));
                if (secondDelta != 0)
                    ApplyAuthoritativeRelationshipDelta(
                        connection, campaignId, secondId, firstId,
                        secondDelta, worldDay, "qualification_fixture",
                        ReadString(payload, "timelineId", "main"));
                after = QuerySql(connection,
                    "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pairKey })
                    .FirstOrDefault();
            }

            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            WriteJsonObject(ConversationReadinessStatePath(campaignId), state);
            return new Dictionary<string, object>
            {
                ["ok"] = after != null,
                ["campaignId"] = campaignId,
                ["qualificationId"] = qualificationId,
                ["pairKey"] = pairKey,
                ["requestedAffinityAtoB"] = firstToSecond,
                ["requestedAffinityBtoA"] = secondToFirst,
                ["before"] = before ?? new Dictionary<string, object>(),
                ["after"] = after ?? new Dictionary<string, object>()
            };
        }

        private static int DirectionalFixtureAffinity(
            Dictionary<string, object> row,
            string observerId,
            string targetId)
        {
            if (row == null) return 0;
            bool observerIsA = ReadString(row, "hero_a_id", "")
                .Equals(observerId, StringComparison.OrdinalIgnoreCase)
                && ReadString(row, "hero_b_id", "")
                    .Equals(targetId, StringComparison.OrdinalIgnoreCase);
            return ReadInt(row,
                observerIsA ? "affinity_a_to_b" : "affinity_b_to_a", 0);
        }

        private static Dictionary<string, object>
            ConversationQualificationBenefitFixtureApi(
                Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!IsLiveTestArmed(ReadJsonObject(LiveTestArmPath())))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "The live-test bridge is not armed."
                };
            if (!ReadString(payload, "confirmation", "")
                .Equals("qualification_fixture", StringComparison.Ordinal))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "confirmation=qualification_fixture is required."
                };

            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            Dictionary<string, object> state =
                ReadJsonObject(
                    ConversationReadinessStatePath(campaignId));
            string qualificationId =
                ReadString(state, "qualificationId", "");
            if (state.Count == 0
                || !qualificationId.Equals(
                    ReadString(payload, "qualificationId", ""),
                    StringComparison.OrdinalIgnoreCase)
                || !ReadString(state, "buildVersion", "").Equals(
                    CurrentConversationBuildVersion(),
                    StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "A same-build active conversation qualification is required."
                };

            string operation = ReadString(payload, "operation", "set")
                .Trim().ToLowerInvariant();
            List<Dictionary<string, object>> fixtures =
                ReadDictionaryList(
                    state, "conversationBenefitQualificationFixtures");
            if (operation == "restore")
            {
                int removed = 0;
                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaignId))
                {
                    EnsureMemorySchema(connection);
                    foreach (Dictionary<string, object> fixture in fixtures)
                    {
                        if (ReadBool(fixture, "restored", false))
                            continue;
                        string fixtureEventId =
                            ReadString(fixture, "eventId", "");
                        if (!string.IsNullOrWhiteSpace(fixtureEventId))
                        {
                            ExecuteSql(connection,
                                @"DELETE FROM events
WHERE event_id=$id
AND event_type='verified_qualification_property_transfer';",
                                new Dictionary<string, object>
                                {
                                    ["id"] = fixtureEventId
                                });
                            removed++;
                        }
                        fixture["restored"] = true;
                        fixture["restoredUtc"] =
                            DateTimeOffset.UtcNow.ToString("o");
                    }
                }
                state["conversationBenefitQualificationFixtures"] =
                    fixtures;
                state["updatedUtc"] =
                    DateTimeOffset.UtcNow.ToString("o");
                WriteJsonObject(
                    ConversationReadinessStatePath(campaignId), state);
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["qualificationId"] = qualificationId,
                    ["removedCount"] = removed
                };
            }
            if (operation != "set")
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "operation must be set or restore."
                };

            string giverId =
                ReadFirstString(payload, "giverId", "playerHeroId");
            string recipientId =
                ReadFirstString(payload, "recipientId", "targetHeroId");
            if (string.IsNullOrWhiteSpace(giverId)
                || string.IsNullOrWhiteSpace(recipientId)
                || giverId.Equals(
                    recipientId, StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "Different giverId and recipientId values are required."
                };

            string eventId = "qualification_benefit_"
                + PromptHash(
                    qualificationId + "|" + giverId + "|"
                    + recipientId + "|furnished_home")
                    .Substring(0, 24).ToLowerInvariant();
            if (!fixtures.Any(row =>
                ReadString(row, "eventId", "").Equals(
                    eventId, StringComparison.OrdinalIgnoreCase)))
            {
                fixtures.Add(new Dictionary<string, object>
                {
                    ["fixtureId"] = "benefit-fixture-"
                        + Guid.NewGuid().ToString("N"),
                    ["eventId"] = eventId,
                    ["giverId"] = giverId,
                    ["recipientId"] = recipientId,
                    ["createdUtc"] =
                        DateTimeOffset.UtcNow.ToString("o"),
                    ["restored"] = false
                });
                state["conversationBenefitQualificationFixtures"] =
                    fixtures;
                state["updatedUtc"] =
                    DateTimeOffset.UtcNow.ToString("o");
                // Persist recovery metadata before creating the evidence row.
                WriteJsonObject(
                    ConversationReadinessStatePath(campaignId), state);
            }

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            string participants = Json.Serialize(
                new List<string> { giverId, recipientId });
            string summary =
                "A completed and verified property transfer gave "
                + recipientId
                + " a life-changing fully furnished city home from "
                + giverId + ".";
            using (ReignDbConnection connection =
                OpenCampaignConnection(campaignId))
            {
                EnsureMemorySchema(connection);
                ExecuteSql(connection, @"INSERT INTO events(
event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
witnesses_json,about_entities_json,known_by_json,visibility,importance,
source_reliability,confidence,payload_json,embedding_status,created_utc)
VALUES($id,$campaign,$ts,$day,'verified_qualification_property_transfer',
$summary,$participants,$participants,$participants,$participants,'private',
0.95,1,1,$payload,'not_indexed',$utc)
ON CONFLICT(event_id) DO UPDATE SET
ts=$ts,world_day=$day,summary=$summary,participants_json=$participants,
witnesses_json=$participants,about_entities_json=$participants,
known_by_json=$participants,payload_json=$payload,created_utc=$utc;",
                    new Dictionary<string, object>
                    {
                        ["id"] = eventId,
                        ["campaign"] = campaignId,
                        ["ts"] = ts,
                        ["day"] = worldDay,
                        ["summary"] = summary,
                        ["participants"] = participants,
                        ["payload"] = Json.Serialize(
                            new Dictionary<string, object>
                            {
                                ["verified"] = true,
                                ["completed"] = true,
                                ["transferKind"] =
                                    "property_transfer",
                                ["property"] =
                                    "fully furnished city home",
                                ["giverId"] = giverId,
                                ["recipientId"] = recipientId,
                                ["qualificationFixture"] = true
                            }),
                        ["utc"] =
                            DateTimeOffset.UtcNow.ToString("o")
                    });
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["qualificationId"] = qualificationId,
                ["eventId"] = eventId,
                ["giverId"] = giverId,
                ["recipientId"] = recipientId,
                ["worldDay"] = worldDay
            };
        }

        private static Dictionary<string, object>
            RestoreConversationQualificationRelationshipFixtures(
                string campaignId,
                string qualificationId,
                Dictionary<string, object> state)
        {
            List<Dictionary<string, object>> fixtures =
                ReadDictionaryList(state, "relationshipQualificationFixtures");
            int restored = 0;
            List<string> errors = new List<string>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                foreach (Dictionary<string, object> fixture in fixtures)
                {
                    if (ReadBool(fixture, "restored", false)) continue;
                    string pairKey = ReadString(fixture, "pairKey", "");
                    try
                    {
                        Dictionary<string, object> original =
                            ReadDictionary(fixture, "originalRow")
                            ?? new Dictionary<string, object>();
                        if (!ReadBool(fixture, "originalExists", false))
                        {
                            ExecuteSql(connection,
                                "DELETE FROM relationship_native_targets WHERE pair_key=$pair;",
                                new Dictionary<string, object> { ["pair"] = pairKey });
                            ExecuteSql(connection,
                                "DELETE FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                                new Dictionary<string, object> { ["pair"] = pairKey });
                        }
                        else
                        {
                            ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=$ab,affinity_b_to_a=$ba,tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,
shared_tag=$shared,projected_native_relation=$projected,
native_action_pending=1,native_action_id=$action,updated_ts=$ts
WHERE pair_key=$pair;",
                                new Dictionary<string, object>
                                {
                                    ["pair"] = pairKey,
                                    ["ab"] = ReadInt(original, "affinity_a_to_b", 0),
                                    ["ba"] = ReadInt(original, "affinity_b_to_a", 0),
                                    ["tagAB"] = ReadString(original, "tag_a_to_b", "neutral"),
                                    ["tagBA"] = ReadString(original, "tag_b_to_a", "neutral"),
                                    ["shared"] = ReadString(original, "shared_tag", ""),
                                    ["projected"] = ReadInt(
                                        original, "projected_native_relation", 0),
                                    ["action"] = "native_target:" + pairKey,
                                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                });
                            ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,
attempt_count,claimed_ts,last_error,updated_ts,revision)
VALUES($pair,$a,$b,$target,0,'pending',$day,-1000,0,0,'',$ts,1)
ON CONFLICT(pair_key) DO UPDATE SET
target_relation=$target,status='pending',world_day=$day,claimed_ts=0,last_error='',
revision=CASE WHEN relationship_native_targets.target_relation=$target
THEN relationship_native_targets.revision
ELSE relationship_native_targets.revision+1 END,
updated_ts=$ts;",
                                new Dictionary<string, object>
                                {
                                    ["pair"] = pairKey,
                                    ["a"] = ReadString(
                                        original, "hero_a_id", ""),
                                    ["b"] = ReadString(
                                        original, "hero_b_id", ""),
                                    ["target"] = ReadInt(
                                        original,
                                        "projected_native_relation", 0),
                                    ["day"] = ReadDouble(
                                        original, "last_day", 0d),
                                    ["ts"] =
                                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                });
                        }
                        fixture["restored"] = true;
                        fixture["restoredUtc"] =
                            DateTimeOffset.UtcNow.ToString("o");
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(pairKey + ": " + LimitText(ex.Message, 300));
                    }
                }
            }
            state["relationshipQualificationFixtures"] = fixtures;
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            WriteJsonObject(ConversationReadinessStatePath(campaignId), state);
            return new Dictionary<string, object>
            {
                ["ok"] = errors.Count == 0,
                ["campaignId"] = campaignId,
                ["qualificationId"] = qualificationId,
                ["restoredCount"] = restored,
                ["errors"] = errors
            };
        }

        private static Dictionary<string, object>
            ConversationQualificationManipulationFixtureApi(
                Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!IsLiveTestArmed(ReadJsonObject(LiveTestArmPath())))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "The live-test bridge is not armed."
                };
            if (!ReadString(payload, "confirmation", "")
                .Equals("qualification_fixture", StringComparison.Ordinal))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "confirmation=qualification_fixture is required."
                };

            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            Dictionary<string, object> state =
                ReadJsonObject(ConversationReadinessStatePath(campaignId));
            string qualificationId = ReadString(state, "qualificationId", "");
            if (state.Count == 0
                || !qualificationId.Equals(
                    ReadString(payload, "qualificationId", ""),
                    StringComparison.OrdinalIgnoreCase)
                || !ReadString(state, "buildVersion", "").Equals(
                    CurrentConversationBuildVersion(),
                    StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "A same-build active conversation qualification is required."
                };
            string operation = ReadString(payload, "operation", "prepare")
                .Trim().ToLowerInvariant();
            if (operation == "restore")
                return RestoreConversationQualificationManipulationFixture(
                    campaignId,
                    qualificationId,
                    state,
                    ReadBool(payload, "force", false));
            if (operation != "prepare")
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "operation must be prepare or restore."
                };

            string playerId = ReadFirstString(
                payload, "playerHeroId", "subjectHeroId");
            string playerName = LimitText(
                ReadString(payload, "playerName", ""), 80);
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            List<string> targetIds = ReadStringList(payload, "targetHeroIds")
                .Where(id => !string.IsNullOrWhiteSpace(id)
                    && !id.Equals(playerId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2000).ToList();
            if (string.IsNullOrWhiteSpace(playerId)
                || string.IsNullOrWhiteSpace(playerName)
                || targetIds.Count == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "playerHeroId, playerName, and targetHeroIds are required."
                };

            string correlationId = EnsureCorrelationId(payload);
            Dictionary<string, Dictionary<string, object>>
                targetSnapshots =
                    ReadDictionaryList(payload, "targetSnapshots")
                        .Where(snapshot =>
                            !string.IsNullOrWhiteSpace(
                                ReadFirstString(
                                    snapshot,
                                    "heroId",
                                    "heroStringId")))
                        .GroupBy(
                            snapshot => ReadFirstString(
                                snapshot,
                                "heroId",
                                "heroStringId"),
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => group.First(),
                            StringComparer.OrdinalIgnoreCase);
            bool verifyIdentity = ReadBool(payload, "verifyIdentity", true);
            string profileCacheKey = Sha256Hex(
                "manipulation-profile-scan-v1|"
                + CurrentConversationBuildVersion()
                + "|"
                + Json.Serialize(targetSnapshots
                    .OrderBy(pair => pair.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Value).ToList()));
            string profileCachePath = Path.Combine(
                ConversationReadinessRoot(campaignId),
                "manipulation-profile-scan.json");
            if (!verifyIdentity)
            {
                Dictionary<string, object> cached =
                    ReadJsonObject(profileCachePath);
                List<Dictionary<string, object>> cachedTargets =
                    ReadDictionaryList(cached, "targets");
                if (ReadString(cached, "cacheKey", "").Equals(
                        profileCacheKey,
                        StringComparison.OrdinalIgnoreCase)
                    && cachedTargets.Count == targetIds.Count)
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["playerHeroId"] = playerId,
                        ["identityPrepared"] = false,
                        ["verifiedObserverCount"] = 0,
                        ["materializedBaselineCount"] = 0,
                        ["lowHonorTargetCount"] =
                            cachedTargets.Count(row =>
                                ReadInt(row, "honorLevel", 0) < 0
                                && ReadBool(
                                    row,
                                    "courtCharacterAvailable",
                                    false)),
                        ["profileCacheHit"] = true,
                        ["targets"] = cachedTargets
                    };
                }
            }
            if (verifyIdentity)
            {
                List<Dictionary<string, object>> identityFixtures =
                    ReadDictionaryList(state, "manipulationIdentityFixtures");
                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaignId))
                {
                    EnsureIdentitySchema(connection);
                    Dictionary<string, object> subject =
                        new Dictionary<string, object>
                        {
                            ["heroStringId"] = playerId,
                            ["name"] = playerName
                        };
                    foreach (string targetId in targetIds)
                    {
                        Dictionary<string, object> fixture =
                            identityFixtures.FirstOrDefault(row =>
                                ReadString(row, "observerId", "").Equals(
                                    targetId,
                                    StringComparison.OrdinalIgnoreCase)
                                && ReadString(row, "subjectId", "").Equals(
                                    playerId,
                                    StringComparison.OrdinalIgnoreCase));
                        if (fixture == null)
                        {
                            Dictionary<string, object> original =
                                ReadAcquaintance(
                                    connection, targetId, playerId);
                            identityFixtures.Add(
                                new Dictionary<string, object>
                                {
                                    ["observerId"] = targetId,
                                    ["subjectId"] = playerId,
                                    ["originalExists"] = original != null,
                                    ["originalRow"] = original
                                        ?? new Dictionary<string, object>(),
                                    ["restored"] = false
                                });
                        }
                        else if (ReadBool(fixture, "restored", false))
                        {
                            Dictionary<string, object> original =
                                ReadAcquaintance(
                                    connection, targetId, playerId);
                            fixture["originalExists"] =
                                original != null;
                            fixture["originalRow"] =
                                original
                                    ?? new Dictionary<string, object>();
                            fixture["restored"] = false;
                        }
                    }
                    state["manipulationIdentityFixtures"] =
                        identityFixtures;
                    state["updatedUtc"] =
                        DateTimeOffset.UtcNow.ToString("o");
                    // Persist the recovery manifest before changing identity
                    // state. A profile-only scan never creates a fixture.
                    WriteJsonObject(
                        ConversationReadinessStatePath(campaignId), state);
                    foreach (string targetId in targetIds)
                    {
                        UpsertVerifiedIdentity(
                            connection,
                            new Dictionary<string, object>
                            {
                                ["heroStringId"] = targetId
                            },
                            subject,
                            "qualification_manipulation_fixture",
                            "qualification_controller",
                            worldDay,
                            correlationId);
                    }
                }
            }

            List<Dictionary<string, object>> rows =
                new List<Dictionary<string, object>>();
            int materializedBaselineCount = 0;
            foreach (string targetId in targetIds)
            {
                string traitsPath = CharacterFile(
                    campaignId, targetId, "traits.json");
                Dictionary<string, object> profile = ReadJsonObject(
                    CharacterFile(campaignId, targetId, "profile.json"));
                Dictionary<string, object> traits =
                    ReadJsonObject(traitsPath);
                if ((profile.Count == 0 || traits.Count == 0)
                    && targetSnapshots.TryGetValue(
                        targetId,
                        out Dictionary<string, object> targetSnapshot))
                {
                    Dictionary<string, object> runtimeProfile =
                        QualificationRuntimeProfileFromTarget(
                            targetId, targetSnapshot);
                    if (runtimeProfile.Count > 0)
                    {
                        if (verifyIdentity)
                        {
                            MaterializeCharacterProfile(
                                campaignId,
                                targetId,
                                runtimeProfile,
                                false,
                                false);
                            profile = ReadJsonObject(
                                CharacterFile(
                                    campaignId,
                                    targetId,
                                    "profile.json"));
                            if (profile.Count == 0)
                                profile = runtimeProfile;
                            traits = ReadJsonObject(traitsPath);
                            if (traits.Count > 0)
                                materializedBaselineCount++;
                        }
                        else
                        {
                            // Roster discovery must not construct every noble
                            // in the campaign. Calculate the exact current
                            // deterministic Court Character in memory, then
                            // materialize only the selected live-test targets.
                            if (profile.Count == 0)
                                profile = runtimeProfile;
                            if (traits.Count == 0)
                                traits = BuildTraitDocument(runtimeProfile);
                        }
                    }
                }
                if (traits.Count == 0)
                {
                    if (profile.Count > 0)
                    {
                        if (verifyIdentity)
                        {
                            MaterializeCharacterProfile(
                                campaignId,
                                targetId,
                                profile,
                                false,
                                false);
                            traits = ReadJsonObject(traitsPath);
                        }
                        else
                        {
                            traits = BuildTraitDocument(profile);
                        }
                    }
                }
                if (profile.Count > 0
                    && EnsureCourtCharacterData(
                        traits, profile, targetId))
                {
                    if (verifyIdentity)
                        WriteJsonObject(traitsPath, traits);
                }
                Dictionary<string, object> percentages =
                    TraitPercentageSnapshot(traits);
                double drive =
                    0.20d * ReadInt(percentages, "ambition", 50)
                    + 0.20d * ReadInt(
                        percentages, "powerMotivation", 50)
                    + 0.10d * ReadInt(
                        percentages, "wealthMotivation", 50)
                    + 0.10d * ReadInt(
                        percentages, "fameMotivation", 50)
                    + 0.15d * ReadInt(percentages, "pragmatism", 50)
                    + 0.15d * ReadInt(percentages, "tact", 50)
                    + 0.10d * ReadInt(percentages, "confidence", 50);
                Dictionary<string, object> virtues =
                    ReadDictionary(traits, "courtVirtues")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> courtCharacter =
                    ReadDictionary(traits, "courtCharacter")
                    ?? new Dictionary<string, object>();
                double scruple =
                    0.25d * ReadInt(percentages, "honesty", 50)
                    + 0.20d * ReadInt(percentages, "empathy", 50)
                    + 0.15d * ReadInt(percentages, "loyalty", 50)
                    + 0.15d * ReadInt(
                        percentages, "dutyMotivation", 50)
                    + 0.10d * ReadInt(percentages, "compassion", 50)
                    + 0.05d * ReadInt(percentages, "shame", 50)
                    + 0.10d * ReadInt(virtues, "honor", 50);
                rows.Add(new Dictionary<string, object>
                {
                    ["heroId"] = targetId,
                    ["name"] = ReadString(profile, "name", ""),
                    ["isFemale"] = ReadBool(profile, "isFemale", false),
                    ["age"] = ReadDouble(profile, "age", 0d),
                    ["isChild"] = ReadBool(profile, "isChild", false),
                    ["clanTier"] = ReadInt(profile, "clanTier", 0),
                    ["drive"] = Math.Round(drive, 1),
                    ["scruple"] = Math.Round(scruple, 1),
                    ["driveScrupleMargin"] =
                        Math.Round(drive - scruple, 1),
                    ["influencePotential"] =
                        Math.Round(0.65d * drive - 0.30d * scruple, 1),
                    ["ambition"] = ReadInt(percentages, "ambition", 50),
                    ["tact"] = ReadInt(percentages, "tact", 50),
                    ["honesty"] = ReadInt(percentages, "honesty", 50),
                    ["pragmatism"] =
                        ReadInt(percentages, "pragmatism", 50),
                    ["courtCharacterAvailable"] = ReadBool(
                        courtCharacter, "available", false),
                    ["courtCharacterCell"] = ReadString(
                        courtCharacter, "cellId", ""),
                    ["courtCharacterTitle"] = ReadString(
                        courtCharacter, "title", ""),
                    ["honorScore"] = ReadInt(
                        courtCharacter, "honorScore",
                        ReadInt(virtues, "honor", 50)),
                    ["honorLevel"] = ReadInt(
                        courtCharacter, "honorLevel", 0),
                    ["boldnessScore"] = ReadInt(
                        courtCharacter, "boldnessScore",
                        ReadInt(virtues, "boldness", 50)),
                    ["boldnessLevel"] = ReadInt(
                        courtCharacter, "boldnessLevel", 0),
                    ["courtTactics"] = ReadStringList(
                        courtCharacter, "tacticIds"),
                    ["supportsManipulation"] =
                        ReadBool(courtCharacter, "available", false)
                        && ReadInt(courtCharacter, "honorLevel", 0) < 0
                });
            }
            rows = rows
                .OrderBy(row => ReadInt(row, "honorLevel", 0) < 0
                    ? 0 : 1)
                .ThenBy(row => ReadInt(row, "honorLevel", 0))
                .ThenByDescending(row =>
                    ReadDouble(row, "influencePotential", 0d))
                .ThenBy(row => ReadString(row, "heroId", ""),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!verifyIdentity)
            {
                WriteJsonObject(
                    profileCachePath,
                    new Dictionary<string, object>
                    {
                        ["cacheKey"] = profileCacheKey,
                        ["buildVersion"] =
                            CurrentConversationBuildVersion(),
                        ["targetCount"] = rows.Count,
                        ["createdUtc"] =
                            DateTimeOffset.UtcNow.ToString("o"),
                        ["targets"] = rows
                    });
            }
            WriteAudit(
                campaignId, correlationId, "server", "qualification",
                "qualification.fixture.manipulation", playerId, "", "",
                "completed", 0,
                "Verified player identity and ranked real personality evidence "
                    + "for the manipulation qualification fixture.",
                new Dictionary<string, object>
                {
                    ["qualificationId"] = qualificationId,
                    ["targetCount"] = rows.Count,
                    ["materializedBaselineCount"] =
                        materializedBaselineCount
                });
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["qualificationId"] = qualificationId,
                ["playerHeroId"] = playerId,
                ["identityPrepared"] = verifyIdentity,
                ["verifiedObserverCount"] =
                    verifyIdentity ? targetIds.Count : 0,
                ["materializedBaselineCount"] =
                    materializedBaselineCount,
                ["profileCacheHit"] = false,
                ["lowHonorTargetCount"] = rows.Count(row =>
                    ReadInt(row, "honorLevel", 0) < 0
                    && ReadBool(row, "courtCharacterAvailable", false)),
                ["targets"] = rows
            };
        }

        private static Dictionary<string, object>
            QualificationRuntimeProfileFromTarget(
                string targetId,
                Dictionary<string, object> snapshot)
        {
            snapshot = snapshot
                ?? new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(targetId)
                || snapshot.Count == 0)
                return new Dictionary<string, object>();
            Dictionary<string, object> profile =
                new Dictionary<string, object>(
                    snapshot,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["heroStringId"] = targetId,
                    ["heroId"] = targetId,
                    ["isAlive"] = true
                };
            double age = ReadDouble(profile, "age", 0d);
            profile["isChild"] =
                ReadBool(profile, "isChild", false)
                || (age > 0d && age < 18d);
            Dictionary<string, object> traits =
                ReadDictionary(profile, "traits")
                ?? new Dictionary<string, object>();
            if (traits.Count == 0)
            {
                foreach (string key in new[]
                    {
                        "valor",
                        "generosity",
                        "honor",
                        "mercy",
                        "calculating"
                    })
                {
                    traits[key] =
                        ReadInt(profile, key, 0);
                }
                profile["traits"] = traits;
            }
            if ((ReadDictionary(profile, "skills")
                    ?? new Dictionary<string, object>()).Count == 0)
            {
                profile["skills"] =
                    new Dictionary<string, object>();
            }
            return profile;
        }

        private static Dictionary<string, object>
            RestoreConversationQualificationManipulationFixture(
                string campaignId,
                string qualificationId,
                Dictionary<string, object> state,
                bool force = false)
        {
            List<Dictionary<string, object>> fixtures =
                ReadDictionaryList(state, "manipulationIdentityFixtures");
            int restored = 0;
            List<string> errors = new List<string>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureIdentitySchema(connection);
                foreach (Dictionary<string, object> fixture in fixtures)
                {
                    if (!force
                        && ReadBool(fixture, "restored", false))
                        continue;
                    string observer = ReadString(fixture, "observerId", "");
                    string subject = ReadString(fixture, "subjectId", "");
                    try
                    {
                        ExecuteSql(connection, @"DELETE FROM identity_evidence
WHERE observer_id=$observer AND subject_id=$subject
AND source='qualification_manipulation_fixture';",
                            new Dictionary<string, object>
                            {
                                ["observer"] = observer,
                                ["subject"] = subject
                            });
                        Dictionary<string, object> original =
                            ReadDictionary(fixture, "originalRow")
                            ?? new Dictionary<string, object>();
                        if (!ReadBool(fixture, "originalExists", false))
                        {
                            ExecuteSql(connection, @"DELETE FROM acquaintances
WHERE observer_id=$observer AND subject_id=$subject;",
                                new Dictionary<string, object>
                                {
                                    ["observer"] = observer,
                                    ["subject"] = subject
                                });
                        }
                        else
                        {
                            ExecuteSql(connection, @"INSERT OR REPLACE INTO acquaintances(
observer_id,subject_id,identity_state,canonical_name,claimed_name,aliases_json,
verification_source,source_entity_id,confidence,first_met_day,last_met_day,
encounter_count,last_encounter_id,recognition_attempts,last_recognition_result,
last_recognition_encounter_id,payload_json,updated_ts)
VALUES($observer,$subject,$state,$canonical,$claimed,$aliases,$source,$sourceEntity,
$confidence,$firstDay,$lastDay,$encounters,$lastEncounter,$attempts,$result,
$resultEncounter,$payload,$updated);",
                                new Dictionary<string, object>
                                {
                                    ["observer"] = observer,
                                    ["subject"] = subject,
                                    ["state"] = ReadString(
                                        original, "identity_state",
                                        "encountered_unknown"),
                                    ["canonical"] = ReadString(
                                        original, "canonical_name", ""),
                                    ["claimed"] = ReadString(
                                        original, "claimed_name", ""),
                                    ["aliases"] = ReadString(
                                        original, "aliases_json", "[]"),
                                    ["source"] = ReadString(
                                        original, "verification_source", ""),
                                    ["sourceEntity"] = ReadString(
                                        original, "source_entity_id", ""),
                                    ["confidence"] = ReadDouble(
                                        original, "confidence", 0d),
                                    ["firstDay"] = ReadDouble(
                                        original, "first_met_day", 0d),
                                    ["lastDay"] = ReadDouble(
                                        original, "last_met_day", 0d),
                                    ["encounters"] = ReadInt(
                                        original, "encounter_count", 0),
                                    ["lastEncounter"] = ReadString(
                                        original, "last_encounter_id", ""),
                                    ["attempts"] = ReadInt(
                                        original, "recognition_attempts", 0),
                                    ["result"] = ReadString(
                                        original,
                                        "last_recognition_result", ""),
                                    ["resultEncounter"] = ReadString(
                                        original,
                                        "last_recognition_encounter_id", ""),
                                    ["payload"] = ReadString(
                                        original, "payload_json", "{}"),
                                    ["updated"] = ReadLong(
                                        original, "updated_ts", 0)
                                });
                        }
                        fixture["restored"] = true;
                        fixture["restoredUtc"] =
                            DateTimeOffset.UtcNow.ToString("o");
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(observer + "->" + subject + ": "
                            + LimitText(ex.Message, 300));
                    }
                }
            }
            state["manipulationIdentityFixtures"] = fixtures;
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            WriteJsonObject(
                ConversationReadinessStatePath(campaignId), state);
            return new Dictionary<string, object>
            {
                ["ok"] = errors.Count == 0,
                ["campaignId"] = campaignId,
                ["qualificationId"] = qualificationId,
                ["restoredCount"] = restored,
                ["errors"] = errors
            };
        }
    }
}
