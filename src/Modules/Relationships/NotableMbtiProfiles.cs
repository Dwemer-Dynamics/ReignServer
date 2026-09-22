using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int NotableMbtiTemplateVersion = 1;

        private sealed class MbtiDefinition
        {
            public string Type;
            public string Title;
            public string Description;
        }

        private sealed class MbtiTraitVector
        {
            public int E;
            public int N;
            public int T;
            public int J;

            public MbtiTraitVector(int e, int n, int t, int j)
            {
                E = e;
                N = n;
                T = t;
                J = j;
            }
        }

        private static readonly Dictionary<string, MbtiDefinition> MbtiDefinitions =
            new Dictionary<string, MbtiDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["INTJ"] = MbtiDefinitionOf("INTJ", "Strategic Independent", "They are private, self-directed, and focused on long-term outcomes. They prefer deliberate plans, question inefficient customs, and may become impatient when disorder, sentiment, or weaker reasoning obstructs their goals."),
                ["INTP"] = MbtiDefinitionOf("INTP", "Analytical Explorer", "They are reserved, curious, and driven to understand how things work. They test assumptions, consider unusual possibilities, and may seem detached or indecisive while examining a problem from every side."),
                ["ENTJ"] = MbtiDefinitionOf("ENTJ", "Commanding Strategist", "They naturally organize people and resources toward ambitious objectives. They speak decisively, value competence, and may become controlling or blunt when others hesitate, waste time, or resist their direction."),
                ["ENTP"] = MbtiDefinitionOf("ENTP", "Inventive Challenger", "They are energetic thinkers who notice opportunities, contradictions, and unconventional solutions. They enjoy testing people and ideas through debate, but may lose patience with routine, restrictions, or plans that no longer interest them."),
                ["INFJ"] = MbtiDefinitionOf("INFJ", "Principled Visionary", "They are private but intensely attentive to people, motives, and long-term consequences. They pursue deeply held ideals with quiet determination and may conceal strong convictions until circumstances force them to act."),
                ["INFP"] = MbtiDefinitionOf("INFP", "Idealistic Individualist", "They are imaginative, inwardly passionate, and guided by personal values. They usually prefer understanding over confrontation, but can become unexpectedly stubborn when loyalty, dignity, or conscience is violated."),
                ["ENFJ"] = MbtiDefinitionOf("ENFJ", "Inspiring Organizer", "They are socially perceptive and naturally draw people toward shared purposes. They invest heavily in relationships, encouragement, and group harmony, but may become intrusive, manipulative, or wounded when their guidance is rejected."),
                ["ENFP"] = MbtiDefinitionOf("ENFP", "Enthusiastic Visionary", "They are expressive, imaginative, and energized by possibilities and personal connections. They inspire others easily but may act impulsively, resist confinement, or abandon routine when a more meaningful opportunity appears."),
                ["ISTJ"] = MbtiDefinitionOf("ISTJ", "Dutiful Traditionalist", "They are orderly, dependable, and guided by evidence, responsibility, and established obligations. They respect proven customs and careful preparation, but may become rigid or judgmental when others disregard rules or commitments."),
                ["ISFJ"] = MbtiDefinitionOf("ISFJ", "Loyal Protector", "They are quiet, observant, and attentive to practical needs, remembered favors, and personal obligations. They protect familiar people and traditions faithfully, though neglect or ingratitude can produce private resentment."),
                ["ESTJ"] = MbtiDefinitionOf("ESTJ", "Practical Commander", "They are direct, organized, and comfortable enforcing rules, hierarchy, and clear expectations. They act decisively and value visible results, but may dismiss emotional concerns or unconventional methods too quickly."),
                ["ESFJ"] = MbtiDefinitionOf("ESFJ", "Social Steward", "They are warm, attentive, and strongly aware of relationships, reputation, and communal expectations. They work to preserve belonging and cooperation, but may depend heavily on approval or pressure others to conform."),
                ["ISTP"] = MbtiDefinitionOf("ISTP", "Independent Problem-Solver", "They are calm, observant, and skilled at responding to immediate practical problems. They value freedom and direct experience, but may withdraw from emotional demands, distant planning, or obligations they consider unnecessary."),
                ["ISFP"] = MbtiDefinitionOf("ISFP", "Quiet Individualist", "They are reserved, sensitive, and guided by personal loyalties and immediate experience. They usually avoid imposing themselves on others, yet can resist coercion fiercely when their freedom, values, or loved ones are threatened."),
                ["ESTP"] = MbtiDefinitionOf("ESTP", "Bold Opportunist", "They are energetic, adaptable, and quick to act when danger or opportunity appears. They rely on nerve, charm, and immediate evidence, but may underestimate long-term consequences or become impatient with caution."),
                ["ESFP"] = MbtiDefinitionOf("ESFP", "Expressive Companion", "They are warm, spontaneous, and highly responsive to people, excitement, beauty, and visible emotion. They readily create energy and connection, but may chase attention, pleasure, or immediate relief at the expense of longer plans.")
            };

        // Each vector describes how the four MBTI poles influence one Reign trait.
        // Positive values favor E/N/T/J; negative values favor I/S/F/P.
        private static readonly Dictionary<string, MbtiTraitVector> MbtiTraitVectors =
            new Dictionary<string, MbtiTraitVector>(StringComparer.OrdinalIgnoreCase)
            {
                ["curiosity"] = V(0, 3, 0, -1), ["ambition"] = V(1, 1, 1, 1),
                ["honesty"] = V(0, 0, -1, 2), ["compassion"] = V(0, 0, -3, 0),
                ["courage"] = V(2, 0, 1, -1), ["discipline"] = V(0, -1, 2, 3),
                ["sociability"] = V(4, 0, -1, 0), ["emotionalStability"] = V(0, 0, 2, 2),
                ["pride"] = V(1, 0, 1, 1), ["patience"] = V(-1, 0, 0, 3),
                ["socialTrust"] = V(1, 0, -2, 0), ["flirtatiousness"] = V(3, 0, -1, -1),
                ["authorityRespect"] = V(0, -1, 0, 3), ["assertiveness"] = V(3, 0, 2, 1),
                ["tact"] = V(-1, 0, -2, 1), ["loyalty"] = V(0, 0, -2, 2),
                ["vengefulness"] = V(1, 0, 2, 0), ["wealthMotivation"] = V(1, -1, 2, 1),
                ["powerMotivation"] = V(2, 1, 2, 2), ["familyMotivation"] = V(0, -1, -2, 2),
                ["fameMotivation"] = V(2, 1, 0, 0), ["knowledgeMotivation"] = V(-1, 3, 2, 0),
                ["religionMotivation"] = V(0, -1, -1, 2), ["revengeMotivation"] = V(1, 0, 2, 0),
                ["dutyMotivation"] = V(0, -1, 0, 4), ["survivalMotivation"] = V(-1, -1, -1, 1),
                ["legacyMotivation"] = V(0, 2, 0, 2), ["riskTolerance"] = V(2, 0, 1, -3),
                ["impulsiveness"] = V(2, 0, -1, -4), ["pragmatism"] = V(0, -2, 4, 1),
                ["traditionalism"] = V(0, -3, 0, 3), ["mercy"] = V(0, 0, -4, 0),
                ["aggression"] = V(2, 0, 3, -1), ["generosity"] = V(1, 0, -3, -1),
                ["greed"] = V(1, -1, 3, -1), ["jealousy"] = V(1, 0, -1, -1),
                ["empathy"] = V(0, 0, -4, 0), ["envy"] = V(1, 0, 1, -1),
                ["optimism"] = V(2, 1, -1, -1), ["fearfulness"] = V(-2, 0, -1, 1),
                ["irritability"] = V(1, 0, 2, -2), ["confidence"] = V(3, 0, 2, 1),
                ["shame"] = V(-2, 0, -1, 2)
            };

        private static MbtiDefinition MbtiDefinitionOf(string type, string title, string description)
        {
            return new MbtiDefinition { Type = type, Title = title, Description = description };
        }

        private static MbtiTraitVector V(int e, int n, int t, int j)
        {
            return new MbtiTraitVector(e, n, t, j);
        }

        private static void EnsureNotableMbtiSchema(ReignDbConnection connection)
        {
            EnsureCharacterEditorSchema(connection);
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS notable_mbti_profiles (
hero_id TEXT PRIMARY KEY,mbti_type TEXT NOT NULL,title TEXT NOT NULL,description TEXT NOT NULL,
foundation_json TEXT NOT NULL,percentages_json TEXT NOT NULL,native_traits_json TEXT NOT NULL,
source TEXT NOT NULL DEFAULT 'notable_mbti_template',template_version INTEGER NOT NULL,
first_observed_day REAL NOT NULL,native_sync_status TEXT NOT NULL DEFAULT 'pending',
native_action_id TEXT NOT NULL DEFAULT '',native_action_created_day REAL NOT NULL DEFAULT -1,
last_native_observed_json TEXT NOT NULL DEFAULT '{}',last_native_observed_day REAL NOT NULL DEFAULT -1,
updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_notable_mbti_type ON notable_mbti_profiles(mbti_type);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_notable_mbti_sync ON notable_mbti_profiles(native_sync_status);");
        }

        private static Dictionary<string, object> EnsureNotableMbtiProfile(
            ReignDbConnection connection,
            string campaignId,
            double worldDay,
            Dictionary<string, object> hero,
            bool materialize,
            bool schemaReady = false,
            ReignDbTransaction transaction = null)
        {
            if (hero == null || !ReadBool(hero, "isNotable", false)) return new Dictionary<string, object>();
            string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            if (string.IsNullOrWhiteSpace(heroId)) return new Dictionary<string, object>();
            if (!schemaReady) EnsureNotableMbtiSchema(connection);

            Dictionary<string, object> row = QuerySql(connection,
                "SELECT * FROM notable_mbti_profiles WHERE hero_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = heroId },
                transaction).FirstOrDefault();
            bool created = row == null;
            if (row == null)
            {
                int index = StableDie(campaignId + "|" + heroId + "|notable_mbti_template_v"
                    + NotableMbtiTemplateVersion.ToString(CultureInfo.InvariantCulture), MbtiTypes.Length) - 1;
                string type = MbtiTypes[Clamp(index, 0, MbtiTypes.Length - 1)];
                MbtiDefinition definition = MbtiDefinitions[type];
                Dictionary<string, object> traits = BuildNotableMbtiTraitDocument(campaignId, heroId, ReadString(hero, "name", heroId), type);
                Dictionary<string, object> foundation = ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>();
                Dictionary<string, object> percentages = ReadDictionary(traits, "traitPercentages") ?? new Dictionary<string, object>();
                Dictionary<string, object> native = ReadDictionary(traits, "mbtiNativeTraits") ?? new Dictionary<string, object>();
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection, @"INSERT OR IGNORE INTO notable_mbti_profiles(
hero_id,mbti_type,title,description,foundation_json,percentages_json,native_traits_json,source,template_version,
first_observed_day,native_sync_status,updated_ts)
VALUES($id,$type,$title,$description,$foundation,$percentages,$native,'notable_mbti_template',$version,$day,'pending',$ts);",
                    new Dictionary<string, object>
                    {
                        ["id"] = heroId, ["type"] = type, ["title"] = definition.Title,
                        ["description"] = definition.Description, ["foundation"] = Json.Serialize(foundation),
                        ["percentages"] = Json.Serialize(percentages), ["native"] = Json.Serialize(native),
                        ["version"] = NotableMbtiTemplateVersion, ["day"] = worldDay, ["ts"] = ts
                    },
                    transaction);
                row = QuerySql(connection, "SELECT * FROM notable_mbti_profiles WHERE hero_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = heroId },
                    transaction).FirstOrDefault();
            }

            Dictionary<string, object> profile = NotableMbtiRowToProfile(row);
            if (materialize && (created
                || !File.Exists(CharacterFile(campaignId, heroId, "profile.json"))
                || !File.Exists(CharacterFile(campaignId, heroId, "traits.json"))
                || !File.Exists(CharacterFile(campaignId, heroId, "constructed.json"))))
            {
                MaterializeNotableMbtiProfile(campaignId, hero, profile);
            }
            ObserveAndQueueNotableNativeTraits(connection, campaignId, worldDay, hero, profile, transaction);
            row = QuerySql(connection, "SELECT * FROM notable_mbti_profiles WHERE hero_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = heroId },
                transaction).FirstOrDefault();
            return NotableMbtiRowToProfile(row);
        }

        private static Dictionary<string, object> NotableMbtiInitializeApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> heroes = ReadDictionaryList(payload, "heroes")
                .Where(x => ReadBool(x, "isNotable", false) && ReadBool(x, "isAlive", true))
                .GroupBy(x => ReadFirstString(x, "heroStringId", "heroId", "id"), StringComparer.OrdinalIgnoreCase)
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .Select(x => x.First())
                .ToList();
            List<Dictionary<string, object>> assignments = new List<Dictionary<string, object>>();
            List<KeyValuePair<Dictionary<string, object>, Dictionary<string, object>>> materialization =
                new List<KeyValuePair<Dictionary<string, object>, Dictionary<string, object>>>();
            int obsoleteAssignments = 0;
            Stopwatch totalTimer = Stopwatch.StartNew();
            long databaseMs;
            long materializationMs;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureNotableMbtiSchema(connection);
                Stopwatch databaseTimer = Stopwatch.StartNew();
                using (ReignDbTransaction transaction = connection.BeginTransaction())
                {
                    foreach (Dictionary<string, object> hero in heroes)
                    {
                        string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
                        Dictionary<string, object> profile =
                            EnsureNotableMbtiProfile(
                                connection,
                                campaignId,
                                worldDay,
                                hero,
                                false,
                                true,
                                transaction);
                        if (profile.Count == 0) continue;
                        materialization.Add(
                            new KeyValuePair<Dictionary<string, object>, Dictionary<string, object>>(hero, profile));
                        assignments.Add(new Dictionary<string, object>
                        {
                            ["heroId"] = heroId,
                            ["type"] = ReadString(profile, "type", "XXXX"),
                            ["title"] = ReadString(profile, "title", ""),
                            ["description"] = ReadString(profile, "description", ""),
                            ["nativeTraits"] = ReadDictionary(profile, "nativeTraits") ?? new Dictionary<string, object>(),
                            ["nativeActionId"] = ReadString(profile, "nativeActionId", ""),
                            ["nativeSyncStatus"] = ReadString(profile, "nativeSyncStatus", "pending")
                        });
                    }
                    HashSet<string> livingIds = new HashSet<string>(
                        heroes.Select(x => ReadFirstString(x, "heroStringId", "heroId", "id")),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (Dictionary<string, object> stale in QuerySql(connection, @"
SELECT hero_id,native_action_id FROM notable_mbti_profiles
WHERE native_sync_status='failed';", null, transaction))
                    {
                        string staleHeroId = ReadString(stale, "hero_id", "");
                        if (string.IsNullOrWhiteSpace(staleHeroId) || livingIds.Contains(staleHeroId))
                            continue;
                        string staleActionId = ReadString(stale, "native_action_id", "");
                        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        ExecuteSql(connection, @"UPDATE notable_mbti_profiles
SET native_sync_status='obsolete',updated_ts=$ts WHERE hero_id=$hero;",
                            new Dictionary<string, object>
                            {
                                ["ts"] = ts, ["hero"] = staleHeroId
                            },
                            transaction);
                        if (!string.IsNullOrWhiteSpace(staleActionId))
                            ExecuteSql(connection, @"UPDATE character_editor_native_commands
SET status='completed',resolved_ts=$ts,result_json=$result WHERE command_id=$id;",
                                new Dictionary<string, object>
                                {
                                    ["ts"] = ts, ["id"] = staleActionId,
                                    ["result"] = Json.Serialize(new Dictionary<string, object>
                                    {
                                        ["status"] = "completed",
                                        ["source"] = "campaign_start_notable_roster",
                                        ["reason"] = "hero_no_longer_living"
                                    })
                                },
                                transaction);
                        obsoleteAssignments++;
                    }
                    transaction.Commit();
                }
                databaseTimer.Stop();
                databaseMs = databaseTimer.ElapsedMilliseconds;
            }

            Stopwatch materializationTimer = Stopwatch.StartNew();
            ConcurrentBag<Dictionary<string, object>> materializedProfiles =
                new ConcurrentBag<Dictionary<string, object>>();
            Parallel.ForEach(materialization,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1,
                        Math.Min(12, Environment.ProcessorCount - 2))
                },
                item =>
                {
                    Dictionary<string, object> profile =
                        MaterializeNotableMbtiProfile(campaignId,
                            item.Key, item.Value, false);
                    if (profile.Count > 0)
                        materializedProfiles.Add(profile);
                });
            List<Dictionary<string, object>> indexProfiles =
                materializedProfiles.ToList();
            UpdateCharacterIndexBatch(campaignId, indexProfiles);
            materializationTimer.Stop();
            materializationMs = materializationTimer.ElapsedMilliseconds;
            totalTimer.Stop();
            LogOperational("notable_mbti.initialize.performance", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["requestedCount"] = heroes.Count,
                ["assignmentCount"] = assignments.Count,
                ["databaseMs"] = databaseMs,
                ["materializationMs"] = materializationMs,
                ["totalMs"] = totalTimer.ElapsedMilliseconds,
                ["batchedIndex"] = true,
                ["transactional"] = true
            });
            return new Dictionary<string, object>
            {
                ["ok"] = assignments.Count == heroes.Count,
                ["campaignId"] = campaignId,
                ["requestedCount"] = heroes.Count,
                ["assignmentCount"] = assignments.Count,
                ["obsoleteAssignmentCount"] = obsoleteAssignments,
                ["databaseMs"] = databaseMs,
                ["materializationMs"] = materializationMs,
                ["totalMs"] = totalTimer.ElapsedMilliseconds,
                ["assignments"] = assignments
            };
        }

        private static Dictionary<string, object> NotableMbtiConfirmBatchApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> observations = ReadDictionaryList(payload, "observations");
            int confirmed = 0;
            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            Stopwatch timer = Stopwatch.StartNew();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureNotableMbtiSchema(connection);
                using (ReignDbTransaction transaction = connection.BeginTransaction())
                {
                    foreach (Dictionary<string, object> observation in observations)
                    {
                        string heroId = ReadFirstString(observation, "heroStringId", "heroId", "id");
                        Dictionary<string, object> row = QuerySql(connection,
                            "SELECT * FROM notable_mbti_profiles WHERE hero_id=$id LIMIT 1;",
                            new Dictionary<string, object> { ["id"] = heroId },
                            transaction).FirstOrDefault();
                        if (row == null)
                        {
                            failures.Add(new Dictionary<string, object> { ["heroId"] = heroId, ["error"] = "assignment_not_found" });
                            continue;
                        }
                        Dictionary<string, object> expected =
                            TryParseJsonObject(ReadString(row, "native_traits_json", "{}")) ?? new Dictionary<string, object>();
                        Dictionary<string, object> observed =
                            ReadDictionary(observation, "traits") ?? new Dictionary<string, object>();
                        string observationStatus = NormalizeLookup(
                            ReadString(observation, "status", "observed")).Replace(' ', '_');
                        bool obsolete = observationStatus == "obsolete";
                        bool hasCompleteObservation = expected.Count == 5
                            && expected.Keys.All(x => observed.ContainsKey(x));
                        bool matches = hasCompleteObservation
                            && expected.All(x => ReadInt(observed, x.Key, int.MinValue)
                                == Convert.ToInt32(x.Value, CultureInfo.InvariantCulture));
                        string actionId = ReadString(row, "native_action_id", "");
                        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (obsolete)
                        {
                            if (!string.IsNullOrWhiteSpace(actionId))
                            {
                                ExecuteSql(connection, @"UPDATE character_editor_native_commands
SET status='completed',resolved_ts=$ts,result_json=$result WHERE command_id=$id;",
                                    new Dictionary<string, object>
                                    {
                                        ["ts"] = ts, ["id"] = actionId,
                                        ["result"] = Json.Serialize(new Dictionary<string, object>
                                        {
                                            ["status"] = "completed",
                                            ["source"] = "campaign_start_notable_batch",
                                            ["reason"] = "hero_no_longer_alive"
                                        })
                                    },
                                    transaction);
                            }
                            ExecuteSql(connection, @"UPDATE notable_mbti_profiles
SET native_sync_status='obsolete',last_native_observed_day=$day,updated_ts=$ts
WHERE hero_id=$hero;",
                                new Dictionary<string, object>
                                {
                                    ["day"] = worldDay, ["ts"] = ts, ["hero"] = heroId
                                },
                                transaction);
                            confirmed++;
                            continue;
                        }
                        if (!hasCompleteObservation)
                        {
                            failures.Add(new Dictionary<string, object>
                            {
                                ["heroId"] = heroId,
                                ["error"] = "native_trait_observation_incomplete"
                            });
                            ExecuteSql(connection, @"UPDATE notable_mbti_profiles
SET native_sync_status='awaiting_confirmation',updated_ts=$ts WHERE hero_id=$hero;",
                                new Dictionary<string, object>
                                {
                                    ["ts"] = ts, ["hero"] = heroId
                                },
                                transaction);
                            continue;
                        }
                        if (!matches)
                        {
                            failures.Add(new Dictionary<string, object> { ["heroId"] = heroId, ["error"] = "native_trait_mismatch" });
                            ExecuteSql(connection, @"UPDATE notable_mbti_profiles SET native_sync_status='contradicted',
last_native_observed_json=$observed,last_native_observed_day=$day,updated_ts=$ts WHERE hero_id=$hero;",
                                new Dictionary<string, object>
                                {
                                    ["observed"] = Json.Serialize(observed), ["day"] = worldDay,
                                    ["ts"] = ts, ["hero"] = heroId
                                },
                                transaction);
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(actionId))
                        {
                            ExecuteSql(connection, @"UPDATE character_editor_native_commands
SET status='completed',resolved_ts=$ts,result_json=$result WHERE command_id=$id;",
                                new Dictionary<string, object>
                                {
                                    ["ts"] = ts, ["id"] = actionId,
                                    ["result"] = Json.Serialize(new Dictionary<string, object>
                                    {
                                        ["status"] = "completed", ["source"] = "campaign_start_notable_batch",
                                        ["applied"] = new ArrayList { "traits" }
                                    })
                                },
                                transaction);
                        }
                        ExecuteSql(connection, @"UPDATE notable_mbti_profiles SET native_sync_status='synchronized',
last_native_observed_json=$observed,last_native_observed_day=$day,updated_ts=$ts WHERE hero_id=$hero;",
                            new Dictionary<string, object>
                            {
                                ["observed"] = Json.Serialize(observed), ["day"] = worldDay,
                                ["ts"] = ts, ["hero"] = heroId
                            },
                            transaction);
                        confirmed++;
                    }
                    transaction.Commit();
                }
            }
            timer.Stop();
            LogOperational("notable_mbti.confirm.performance", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["observationCount"] = observations.Count,
                ["confirmedCount"] = confirmed,
                ["failureCount"] = failures.Count,
                ["totalMs"] = timer.ElapsedMilliseconds,
                ["transactional"] = true
            });
            return new Dictionary<string, object>
            {
                ["ok"] = failures.Count == 0 && confirmed == observations.Count,
                ["confirmedCount"] = confirmed,
                ["failureCount"] = failures.Count,
                ["totalMs"] = timer.ElapsedMilliseconds,
                ["failures"] = failures
            };
        }

        private static Dictionary<string, object> NotableMbtiRowToProfile(Dictionary<string, object> row)
        {
            if (row == null) return new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["type"] = ReadString(row, "mbti_type", "XXXX"),
                ["title"] = ReadString(row, "title", ""),
                ["description"] = ReadString(row, "description", ""),
                ["source"] = ReadString(row, "source", "notable_mbti_template"),
                ["templateVersion"] = ReadInt(row, "template_version", 0),
                ["assignedDay"] = ReadDouble(row, "first_observed_day", -1d),
                ["nativeSyncStatus"] = ReadString(row, "native_sync_status", "pending"),
                ["nativeActionId"] = ReadString(row, "native_action_id", ""),
                ["foundationTraits"] = TryParseJsonObject(ReadString(row, "foundation_json", "{}")) ?? new Dictionary<string, object>(),
                ["traitPercentages"] = TryParseJsonObject(ReadString(row, "percentages_json", "{}")) ?? new Dictionary<string, object>(),
                ["nativeTraits"] = TryParseJsonObject(ReadString(row, "native_traits_json", "{}")) ?? new Dictionary<string, object>()
            };
        }

        private static Dictionary<string, object> CompactNotableRelationshipProfile(Dictionary<string, object> row)
        {
            // Daily simulation needs the immutable type and five observed native traits.
            // Keep the authoritative row for the uncommon assignment path; never replace
            // its detailed trait document with an empty or approximate profile.
            return new Dictionary<string, object>
            {
                ["type"] = ReadString(row, "mbti_type", "XXXX"),
                ["nativeSyncStatus"] = ReadString(row, "native_sync_status", "pending"),
                ["nativeTraits"] = TryParseJsonObject(ReadString(row, "native_traits_json", "{}"))
                    ?? new Dictionary<string, object>(),
                ["relationshipSourceRow"] = row
            };
        }

        private static Dictionary<string, object> BuildNotableMbtiTraitDocument(
            string campaignId,
            string heroId,
            string heroName,
            string type)
        {
            type = (type ?? "").ToUpperInvariant();
            if (!MbtiDefinitions.TryGetValue(type, out MbtiDefinition definition))
                throw new InvalidOperationException("Unknown MBTI type: " + type);
            int e = type[0] == 'E' ? 1 : -1;
            int n = type[1] == 'N' ? 1 : -1;
            int t = type[2] == 'T' ? 1 : -1;
            int j = type[3] == 'J' ? 1 : -1;
            Dictionary<string, object> percentages = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> foundation = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string trait in CoreTraitKeys)
            {
                MbtiTraitVector vector = MbtiTraitVectors[trait];
                int weighted = vector.E * e + vector.N * n + vector.T * t + vector.J * j;
                int jitter = StableDie(campaignId + "|" + heroId + "|" + type + "|" + trait + "|mbti_jitter_v1", 9) - 5;
                int percentage = Clamp(50 + weighted * 6 + jitter, 5, 95);
                percentages[trait] = percentage;
                foundation[trait] = PercentageToFoundationLevel(percentage);
            }

            // Preserve the declared type even for combinations whose secondary
            // correlations pull a shared trait toward another axis.
            EnforceMbtiAxis(percentages, foundation, type, "sociability", e);
            EnforceMbtiAxis(percentages, foundation, type, "curiosity", n);
            EnforceMbtiAxis(percentages, foundation, type, "empathy", -t);
            EnforceMbtiAxis(percentages, foundation, type, "dutyMotivation", j);

            Dictionary<string, object> nativeTraits = new Dictionary<string, object>
            {
                ["valor"] = ClampTrait(ReadInt(foundation, "courage", 0)),
                ["generosity"] = ClampTrait(ReadInt(foundation, "generosity", 0)),
                ["honor"] = ClampTrait(ReadInt(foundation, "honesty", 0)),
                ["mercy"] = ClampTrait(ReadInt(foundation, "mercy", 0)),
                ["calculating"] = ClampTrait((ReadInt(foundation, "pragmatism", 0)
                    + ReadInt(foundation, "discipline", 0)) / 2)
            };
            Dictionary<string, object> document = new Dictionary<string, object>
            {
                ["version"] = CoreTraitDocumentVersion,
                ["model"] = "reign_notable_mbti_template_v1",
                ["scale"] = "integer -2..2",
                ["scaleMeaning"] = new Dictionary<string, object>
                {
                    ["-2"] = "strong defining inverse", ["-1"] = "clear inverse tendency",
                    ["0"] = "balanced, ordinary, or situational", ["1"] = "clear positive tendency",
                    ["2"] = "strong defining tendency"
                },
                ["foundationTraits"] = foundation,
                ["hiddenReignTraits"] = new Dictionary<string, object>(foundation, StringComparer.OrdinalIgnoreCase),
                ["visibleBannerlordTraits"] = new Dictionary<string, object>(nativeTraits, StringComparer.OrdinalIgnoreCase),
                ["foundationTraitModel"] = FoundationTraitModelDefinition(false),
                ["traitPercentages"] = percentages,
                ["traitPercentageModifierSnapshot"] = new Dictionary<string, object>(foundation, StringComparer.OrdinalIgnoreCase),
                ["mbtiProfile"] = new Dictionary<string, object>
                {
                    ["type"] = type, ["title"] = definition.Title, ["description"] = definition.Description,
                    ["source"] = "notable_mbti_template", ["templateVersion"] = NotableMbtiTemplateVersion
                },
                ["mbtiNativeTraits"] = nativeTraits,
                ["assignmentProtocol"] = new Dictionary<string, object>
                {
                    ["precedence"] = new ArrayList { "permanent notable MBTI assignment", "campaign-stable individual variation", "explicit Character Editor override" },
                    ["nativeSync"] = "Valor, Generosity, Honor, Mercy, and Calculating are synchronized to this MBTI-seeded profile.",
                    ["editorRule"] = "Explicit Character Editor changes are retained and are not overwritten by passive regeneration."
                },
                ["definitions"] = TraitDefinitions(),
                ["attractiveness"] = new Dictionary<string, object> { ["version"] = 1, ["score"] = 0, ["source"] = "notable_mbti_baseline" }
            };
            EnsureTraitPercentageData(document, heroId);
            RebuildPersonalityPortrait(document, heroName, definition.Description, "notable_mbti_template");
            Dictionary<string, object> portrait = ReadDictionary(document, "personalityPortrait") ?? new Dictionary<string, object>();
            portrait["mbtiType"] = type;
            portrait["mbtiTitle"] = definition.Title;
            document["personalityPortrait"] = portrait;
            return document;
        }

        private static void EnforceMbtiAxis(
            Dictionary<string, object> percentages,
            Dictionary<string, object> foundation,
            string type,
            string anchor,
            int sign)
        {
            int current = ReadInt(percentages, anchor, 50);
            int target = sign > 0 ? Math.Max(current, 72) : Math.Min(current, 28);
            percentages[anchor] = target;
            foundation[anchor] = PercentageToFoundationLevel(target);
        }

        private static int PercentageToFoundationLevel(int percentage)
        {
            if (percentage <= 20) return -2;
            if (percentage <= 40) return -1;
            if (percentage <= 60) return 0;
            if (percentage <= 80) return 1;
            return 2;
        }

        private static Dictionary<string, object> MaterializeNotableMbtiProfile(
            string campaignId,
            Dictionary<string, object> hero,
            Dictionary<string, object> mbtiProfile,
            bool updateIndex = true)
        {
            string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            if (string.IsNullOrWhiteSpace(heroId)) return new Dictionary<string, object>();
            string directory = CharacterDirectory(campaignId, heroId);
            Directory.CreateDirectory(directory);
            string profilePath = Path.Combine(directory, "profile.json");
            Dictionary<string, object> existingProfile = ReadJsonObject(profilePath);
            bool created = existingProfile.Count == 0;
            Dictionary<string, object> profile = new Dictionary<string, object>(existingProfile, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> pair in hero) profile[pair.Key] = pair.Value;
            profile["campaignId"] = campaignId;
            profile["heroStringId"] = heroId;
            profile["updatedUtc"] = DateTime.UtcNow.ToString("o");
            if (created) profile["createdUtc"] = DateTime.UtcNow.ToString("o");
            WriteJsonObject(profilePath, profile);
            if (created && updateIndex) UpdateCharacterIndex(campaignId, profile);

            string traitsPath = CharacterFile(campaignId, heroId, "traits.json");
            Dictionary<string, object> existing = ReadJsonObject(traitsPath);
            Dictionary<string, object> existingMbti = ReadDictionary(existing, "mbtiProfile") ?? new Dictionary<string, object>();
            bool editorOwned = CharacterHasEditorCorrections(campaignId, heroId);
            if (!editorOwned && (!ReadString(existingMbti, "source", "").Equals("notable_mbti_template", StringComparison.OrdinalIgnoreCase)
                || ReadInt(existingMbti, "templateVersion", 0) != NotableMbtiTemplateVersion))
            {
                Dictionary<string, object> traits = BuildNotableMbtiTraitDocument(
                    campaignId, heroId, ReadString(hero, "name", heroId), ReadString(mbtiProfile, "type", "INTJ"));
                WriteJsonObject(traitsPath, traits);
            }
            else if (!editorOwned && RepairNotableMbtiTraitDocument(existing, mbtiProfile, hero, heroId))
            {
                existing["updatedUtc"] = DateTime.UtcNow.ToString("o");
                WriteJsonObject(traitsPath, existing);
            }
            Dictionary<string, object> constructed = ReadJsonObject(CharacterFile(campaignId, heroId, "constructed.json"));
            if (!IsUsableCharacterStatus(ReadString(constructed, "status", "")) || ReadBool(constructed, "llmUsed", false) == false)
            {
                constructed["ok"] = true;
                constructed["status"] = "first_contact_pending";
                constructed["source"] = "notable_mbti_template";
                constructed["requiresFirstContactConstruction"] = true;
                constructed["usableForNonInteractiveSystems"] = true;
                constructed["schemaVersion"] = CharacterSchemaVersion;
                constructed["updatedUtc"] = DateTime.UtcNow.ToString("o");
                WriteJsonObject(CharacterFile(campaignId, heroId, "constructed.json"), constructed);
            }
            return profile;
        }

        private static bool RepairNotableMbtiTraitDocument(
            Dictionary<string, object> traits,
            Dictionary<string, object> mbtiProfile,
            Dictionary<string, object> hero,
            string heroId)
        {
            if (traits == null || traits.Count == 0) return false;
            bool changed = false;
            if (!traits.ContainsKey("visibleBannerlordTraits"))
            {
                Dictionary<string, object> native = DeepCloneProfileDictionary(ReadDictionary(traits, "mbtiNativeTraits"));
                if (native.Count == 0) native = DeepCloneProfileDictionary(ReadDictionary(mbtiProfile, "nativeTraits"));
                if (native.Count == 0) native = DeepCloneProfileDictionary(ReadDictionary(hero, "traits"));
                traits["visibleBannerlordTraits"] = native;
                changed = true;
            }
            if (!FoundationTraitModelCurrent(traits))
            {
                traits["foundationTraitModel"] = FoundationTraitModelDefinition(false);
                changed = true;
            }
            changed = EnsureTraitPercentageData(traits, heroId) || changed;
            changed = EnsurePersonalityPortraitData(traits, ReadString(hero, "name", heroId), false) || changed;
            return changed;
        }

        private static void ObserveAndQueueNotableNativeTraits(
            ReignDbConnection connection,
            string campaignId,
            double worldDay,
            Dictionary<string, object> hero,
            Dictionary<string, object> profile,
            ReignDbTransaction transaction = null)
        {
            string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            if (CharacterHasEditorCorrections(campaignId, heroId))
            {
                ExecuteSql(connection, @"UPDATE notable_mbti_profiles SET native_sync_status='manual_override',
last_native_observed_json=$observed,last_native_observed_day=$day,updated_ts=$ts WHERE hero_id=$hero;",
                    new Dictionary<string, object>
                    {
                        ["observed"] = Json.Serialize(ReadDictionary(hero, "traits") ?? new Dictionary<string, object>()),
                        ["day"] = worldDay, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["hero"] = heroId
                    },
                    transaction);
                return;
            }
            Dictionary<string, object> expected = ReadDictionary(profile, "nativeTraits") ?? new Dictionary<string, object>();
            Dictionary<string, object> observed = ReadDictionary(hero, "traits") ?? new Dictionary<string, object>();
            bool hasCompleteObservation = expected.Count == 5
                && expected.Keys.All(x => observed.ContainsKey(x));
            bool matches = hasCompleteObservation
                && expected.All(pair => ReadInt(observed, pair.Key, int.MinValue)
                    == Convert.ToInt32(pair.Value, CultureInfo.InvariantCulture));
            string actionId = "notable_mbti_native_" + heroId + "_v" + NotableMbtiTemplateVersion.ToString(CultureInfo.InvariantCulture);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> command = QuerySql(connection,
                "SELECT * FROM character_editor_native_commands WHERE command_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = actionId },
                transaction).FirstOrDefault();
            string commandStatus = ReadString(command, "status", "");
            string priorStatus = ReadString(profile, "nativeSyncStatus", "pending");
            string status;
            if (matches)
            {
                status = "synchronized";
            }
            else if (!hasCompleteObservation
                && (string.Equals(priorStatus, "synchronized", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(priorStatus, "obsolete", StringComparison.OrdinalIgnoreCase)))
            {
                // Compact daily relationship payloads intentionally omit native
                // traits. Absence is not new evidence and must not downgrade a
                // previously confirmed or obsolete notable.
                status = priorStatus;
            }
            else if (hasCompleteObservation
                && (string.Equals(commandStatus, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(commandStatus, "partially_applied", StringComparison.OrdinalIgnoreCase))
                )
            {
                status = "contradicted";
            }
            else if (string.Equals(commandStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                status = "failed";
            }
            else
            {
                status = !hasCompleteObservation
                    && (string.Equals(commandStatus, "completed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(commandStatus, "partially_applied", StringComparison.OrdinalIgnoreCase))
                    ? "awaiting_confirmation"
                    : string.Equals(commandStatus, "claimed", StringComparison.OrdinalIgnoreCase)
                        ? "claimed"
                        : "pending";
                if (command == null)
                {
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO character_editor_native_commands(
command_id,revision_id,hero_id,status,changes_json,inverse_json,dangerous,confirmation,created_ts)
VALUES($id,$revision,$hero,'pending',$changes,$inverse,0,'automatic_notable_mbti_sync',$ts);",
                        new Dictionary<string, object>
                        {
                            ["id"] = actionId, ["revision"] = "notable_mbti_v" + NotableMbtiTemplateVersion.ToString(CultureInfo.InvariantCulture),
                            ["hero"] = heroId, ["changes"] = Json.Serialize(new Dictionary<string, object> { ["traits"] = expected }),
                            ["inverse"] = Json.Serialize(new Dictionary<string, object> { ["traits"] = observed }), ["ts"] = ts
                        },
                        transaction);
                }
            }
            bool hasNativeAction = command != null || !matches;
            ExecuteSql(connection, @"UPDATE notable_mbti_profiles SET native_sync_status=$status,native_action_id=$action,
native_action_created_day=CASE WHEN $hasAction=1 AND native_action_created_day<0 THEN $day ELSE native_action_created_day END,
last_native_observed_json=CASE WHEN $hasObservation=1 THEN $observed ELSE last_native_observed_json END,
last_native_observed_day=CASE WHEN $hasObservation=1 THEN $day ELSE last_native_observed_day END,
updated_ts=$ts WHERE hero_id=$hero;",
                new Dictionary<string, object>
                {
                    ["status"] = status, ["action"] = hasNativeAction ? actionId : "", ["hasAction"] = hasNativeAction ? 1 : 0, ["day"] = worldDay,
                    ["hasObservation"] = hasCompleteObservation ? 1 : 0,
                    ["observed"] = Json.Serialize(observed), ["ts"] = ts, ["hero"] = heroId
                },
                transaction);
        }

        private static Dictionary<string, object> ResolveCharacterMbtiProfile(
            string campaignId,
            string heroId,
            Dictionary<string, object> profile,
            Dictionary<string, object> traitDocument)
        {
            profile = profile ?? new Dictionary<string, object>();
            traitDocument = traitDocument ?? ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"));
            Dictionary<string, object> embedded = ReadDictionary(traitDocument, "mbtiProfile") ?? new Dictionary<string, object>();
            if (ReadBool(profile, "isNotable", false))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureNotableMbtiSchema(connection);
                    Dictionary<string, object> row = QuerySql(connection,
                        "SELECT * FROM notable_mbti_profiles WHERE hero_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = heroId }).FirstOrDefault();
                    if (row != null) return NotableMbtiRowToProfile(row);
                }
            }
            Dictionary<string, object> foundation = ReadDictionary(traitDocument, "foundationTraits") ?? new Dictionary<string, object>();
            Dictionary<string, object> percentages = ReadDictionary(traitDocument, "traitPercentages") ?? new Dictionary<string, object>();
            if (foundation.Count == 0 && percentages.Count == 0)
                return new Dictionary<string, object> { ["type"] = "XXXX", ["title"] = "Unavailable", ["description"] = "This character does not yet have enough personality data for an MBTI profile.", ["source"] = "insufficient_data", ["templateVersion"] = 0, ["assignedDay"] = -1d, ["nativeSyncStatus"] = "not_applicable" };
            Dictionary<string, object> profileEmbedded = ReadDictionary(profile, "mbtiProfile")
                ?? new Dictionary<string, object>();
            string type = ReadString(profileEmbedded, "type", ReadString(embedded, "type", "XXXX"));
            if (!MbtiDefinitions.TryGetValue(type, out MbtiDefinition definition))
                return new Dictionary<string, object> { ["type"] = "XXXX", ["title"] = "Unavailable", ["description"] = "This character does not yet have enough personality data for an MBTI profile.", ["source"] = "insufficient_data", ["templateVersion"] = 0, ["assignedDay"] = -1d, ["nativeSyncStatus"] = "not_applicable" };
            return new Dictionary<string, object>
            {
                ["type"] = type, ["title"] = definition.Title, ["description"] = definition.Description,
                ["source"] = ReadString(profileEmbedded, "source", ReadString(embedded, "source", "pregenerated_noble_catalog")),
                ["templateVersion"] = ReadInt(profileEmbedded, "templateVersion", ReadInt(embedded, "templateVersion", NotableMbtiTemplateVersion)),
                ["assignedDay"] = -1d, ["nativeSyncStatus"] = "not_applicable"
            };
        }

        private static string BuildCharacterMbtiPromptBlock(
            string campaignId,
            string heroId,
            Dictionary<string, object> profile,
            Dictionary<string, object> traitDocument)
        {
            Dictionary<string, object> mbti = ResolveCharacterMbtiProfile(campaignId, heroId, profile, traitDocument);
            string type = ReadString(mbti, "type", "XXXX");
            if (type == "XXXX") return "";
            return "MBTI behavioral archetype - always apply when this character speaks or decides:\n"
                + type + " - " + ReadString(mbti, "title", "") + "\n"
                + ReadString(mbti, "description", "");
        }

        private static List<Dictionary<string, object>> RunNotableMbtiSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => rows.Add(new Dictionary<string, object>
            {
                ["id"] = "notable_mbti_" + id, ["suite"] = "notable_mbti", ["passed"] = passed, ["summary"] = summary
            });
            bool catalog = MbtiDefinitions.Count == 16 && MbtiTypes.All(MbtiDefinitions.ContainsKey)
                && MbtiDefinitions.Values.All(x => !string.IsNullOrWhiteSpace(x.Title) && x.Description.Length >= 100);
            add("catalog", catalog, "All 16 MBTI types have one shared UI and prompt description.");
            bool complete = true, derives = true;
            foreach (string type in MbtiTypes)
            {
                Dictionary<string, object> traits = BuildNotableMbtiTraitDocument("mbti_selftest", "hero_" + type, type, type);
                complete &= (ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>()).Count == CoreTraitKeys.Length
                    && (ReadDictionary(traits, "traitPercentages") ?? new Dictionary<string, object>()).Count == CoreTraitKeys.Length
                    && (ReadDictionary(traits, "mbtiNativeTraits") ?? new Dictionary<string, object>()).Count == 5
                    && (ReadDictionary(traits, "visibleBannerlordTraits") ?? new Dictionary<string, object>()).Count == 5
                    && TraitDocumentReady(traits);
                derives &= ReadString(DeriveMbtiAxes(traits), "type", "") == type;
            }
            add("complete_templates", complete, "Every type produces a validator-ready 43-trait document and five visible native Bannerlord trait targets.");
            add("declared_type_preserved", derives, "Every generated template re-derives to its declared MBTI type.");
            Dictionary<string, object> a = BuildNotableMbtiTraitDocument("campaign_a", "hero_a", "A", "ENFP");
            Dictionary<string, object> b = BuildNotableMbtiTraitDocument("campaign_a", "hero_a", "A", "ENFP");
            Dictionary<string, object> c = BuildNotableMbtiTraitDocument("campaign_b", "hero_a", "A", "ENFP");
            add("stable_variation", Json.Serialize(a) == Json.Serialize(b) && Json.Serialize(a) != Json.Serialize(c),
                "Individual variation is stable within one campaign and changes with the campaign seed.");
            Dictionary<string, object> legacy = BuildNotableMbtiTraitDocument("legacy_campaign", "legacy_hero", "Legacy Hero", "INTJ");
            legacy.Remove("visibleBannerlordTraits");
            legacy.Remove("foundationTraitModel");
            bool legacyRepaired = RepairNotableMbtiTraitDocument(
                legacy,
                new Dictionary<string, object> { ["type"] = "INTJ", ["nativeTraits"] = ReadDictionary(legacy, "mbtiNativeTraits") },
                new Dictionary<string, object> { ["name"] = "Legacy Hero" },
                "legacy_hero");
            add("legacy_trait_migration", legacyRepaired && TraitDocumentReady(legacy),
                "Legacy notable MBTI documents gain visible native traits and the active foundation model before interaction.");

            int[] counts = new int[MbtiTypes.Length];
            List<string> campaignA = new List<string>();
            List<string> campaignB = new List<string>();
            for (int i = 0; i < 16000; i++)
            {
                string heroId = "uniform_" + i.ToString(CultureInfo.InvariantCulture);
                int aIndex = StableDie("campaign_a|" + heroId + "|notable_mbti_template_v" + NotableMbtiTemplateVersion, MbtiTypes.Length) - 1;
                int bIndex = StableDie("campaign_b|" + heroId + "|notable_mbti_template_v" + NotableMbtiTemplateVersion, MbtiTypes.Length) - 1;
                counts[aIndex]++;
                if (i < 100) { campaignA.Add(MbtiTypes[aIndex]); campaignB.Add(MbtiTypes[bIndex]); }
            }
            bool uniform = counts.All(x => x >= 850 && x <= 1150);
            add("uniform_seeded_assignment", uniform && !campaignA.SequenceEqual(campaignB),
                "Seeded assignment is approximately uniform and changes with the campaign seed.");

            string persistenceCampaign = "__notable_persistence_"
                + Guid.NewGuid().ToString("N").Substring(0, 12);
            using (ReignDbConnection connection =
                OpenCampaignConnection(persistenceCampaign))
            {
                Dictionary<string, object> notable = new Dictionary<string, object>
                {
                    ["heroStringId"] = "notable_test", ["name"] = "Notable Test", ["isNotable"] = true,
                    ["traits"] = new Dictionary<string, object>
                    {
                        ["valor"] = 0, ["generosity"] = 0, ["honor"] = 0, ["mercy"] = 0, ["calculating"] = 0
                    }
                };
                Dictionary<string, object> first = EnsureNotableMbtiProfile(connection, persistenceCampaign, 1d, notable, false);
                Dictionary<string, object> second = EnsureNotableMbtiProfile(connection, persistenceCampaign, 2d, notable, false);
                int profiles = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS total FROM notable_mbti_profiles;", null).FirstOrDefault(), "total", 0);
                int commands = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS total FROM character_editor_native_commands;", null).FirstOrDefault(), "total", 0);
                bool stableAssignment = ReadString(first, "type", "") == ReadString(second, "type", "")
                    && Math.Abs(ReadDouble(second, "assignedDay", -1d) - 1d) < 0.001d;
                add("persistent_idempotent_assignment", stableAssignment && profiles == 1 && commands <= 1,
                    "Repeated observations retain one assignment, its original day, and at most one native command.");

                string actionId = ReadString(second, "nativeActionId", "");
                if (!string.IsNullOrWhiteSpace(actionId))
                    ExecuteSql(connection, "UPDATE character_editor_native_commands SET status='completed' WHERE command_id=$id;",
                        new Dictionary<string, object> { ["id"] = actionId });
                Dictionary<string, object> contradicted = EnsureNotableMbtiProfile(connection, persistenceCampaign, 3d, notable, false);
                add("native_contradiction", string.IsNullOrWhiteSpace(actionId)
                        || ReadString(contradicted, "nativeSyncStatus", "") == "contradicted",
                    "A completed native command followed by contradictory observed traits is classified as contradicted.");

                Dictionary<string, object> compactNotable = new Dictionary<string, object>
                {
                    ["heroStringId"] = "compact_notable_test",
                    ["name"] = "Compact Notable Test",
                    ["isNotable"] = true
                };
                Dictionary<string, object> compactFirst =
                    EnsureNotableMbtiProfile(connection, persistenceCampaign, 3d, compactNotable, false);
                string compactActionId = ReadString(compactFirst, "nativeActionId", "");
                if (!string.IsNullOrWhiteSpace(compactActionId))
                    ExecuteSql(connection,
                        "UPDATE character_editor_native_commands SET status='completed' WHERE command_id=$id;",
                        new Dictionary<string, object> { ["id"] = compactActionId });
                ExecuteSql(connection,
                    "UPDATE notable_mbti_profiles SET native_sync_status='synchronized' WHERE hero_id=$hero;",
                    new Dictionary<string, object> { ["hero"] = "compact_notable_test" });
                Dictionary<string, object> compactRepeated =
                    EnsureNotableMbtiProfile(connection, persistenceCampaign, 4d, compactNotable, false);
                add("missing_native_observation_is_not_contradiction",
                    ReadString(compactRepeated, "nativeSyncStatus", "") == "synchronized",
                    "A compact relationship input without native traits preserves a previously confirmed synchronization instead of downgrading it or fabricating a contradiction.");
            }
            ReignPostgreSqlStorage.DropCampaign(persistenceCampaign);

            string materializeCampaign = "mbti_materialize_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                using (ReignDbConnection connection =
                    OpenCampaignConnection(materializeCampaign))
                {
                    Dictionary<string, object> notable = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "notable_materialize_test",
                        ["name"] = "Materialize Test",
                        ["isNotable"] = true,
                        ["traits"] = new Dictionary<string, object>
                        {
                            ["valor"] = 0, ["generosity"] = 0, ["honor"] = 0, ["mercy"] = 0, ["calculating"] = 0
                        }
                    };
                    Dictionary<string, object> assigned =
                        EnsureNotableMbtiProfile(connection, materializeCampaign, 1d, notable, true);
                    Dictionary<string, object> storedProfile = ReadJsonObject(
                        CharacterFile(materializeCampaign, "notable_materialize_test", "profile.json"));
                    Dictionary<string, object> storedTraits = ReadJsonObject(
                        CharacterFile(materializeCampaign, "notable_materialize_test", "traits.json"));
                    add("passive_materialization_no_recursion",
                        ReadString(assigned, "type", "XXXX") != "XXXX"
                            && ReadString(storedProfile, "heroStringId", "") == "notable_materialize_test"
                            && (ReadDictionary(storedTraits, "foundationTraits") ?? new Dictionary<string, object>()).Count == CoreTraitKeys.Length,
                        "Passive notable observation materializes the minimal profile and all traits without recursively rebuilding the character.");
                }
            }
            finally
            {
                string generatedDirectory = CampaignDirectory(materializeCampaign);
                ReignPostgreSqlStorage.ClearAllPools();
                try { ReignPostgreSqlStorage.DropCampaign(materializeCampaign); }
                catch { }
                if (Directory.Exists(generatedDirectory)) Directory.Delete(generatedDirectory, true);
            }

            string batchCampaign = "mbti_batch_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                List<Dictionary<string, object>> batchHeroes = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "batch_notable_a", ["name"] = "Batch Notable A",
                        ["isNotable"] = true, ["isAlive"] = true,
                        ["traits"] = new Dictionary<string, object>
                        {
                            ["valor"] = 0, ["generosity"] = 0, ["honor"] = 0, ["mercy"] = 0, ["calculating"] = 0
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "batch_notable_b", ["name"] = "Batch Notable B",
                        ["isNotable"] = true, ["isAlive"] = true,
                        ["traits"] = new Dictionary<string, object>
                        {
                            ["valor"] = 0, ["generosity"] = 0, ["honor"] = 0, ["mercy"] = 0, ["calculating"] = 0
                        }
                    }
                };
                Dictionary<string, object> initialized = NotableMbtiInitializeApi(new Dictionary<string, object>
                {
                    ["campaignId"] = batchCampaign, ["worldDay"] = 1d, ["heroes"] = batchHeroes
                });
                List<Dictionary<string, object>> assignments = ReadDictionaryList(initialized, "assignments");
                List<Dictionary<string, object>> observations = assignments.Select(x =>
                    new Dictionary<string, object>
                    {
                        ["heroId"] = ReadString(x, "heroId", ""),
                        ["traits"] = ReadDictionary(x, "nativeTraits") ?? new Dictionary<string, object>()
                    }).ToList();
                Dictionary<string, object> confirmed = NotableMbtiConfirmBatchApi(new Dictionary<string, object>
                {
                    ["campaignId"] = batchCampaign, ["worldDay"] = 1d, ["observations"] = observations
                });
                Dictionary<string, object> repeated = NotableMbtiInitializeApi(new Dictionary<string, object>
                {
                    ["campaignId"] = batchCampaign, ["worldDay"] = 2d,
                    ["heroes"] = observations.Select(x => new Dictionary<string, object>
                    {
                        ["heroStringId"] = ReadString(x, "heroId", ""), ["name"] = ReadString(x, "heroId", ""),
                        ["isNotable"] = true, ["isAlive"] = true, ["traits"] = ReadDictionary(x, "traits")
                    }).ToList()
                });
                using (ReignDbConnection connection = OpenCampaignConnection(batchCampaign))
                {
                    Dictionary<string, object> departed = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "batch_departed_notable",
                        ["name"] = "Departed Notable",
                        ["isNotable"] = true,
                        ["isAlive"] = true,
                        ["traits"] = new Dictionary<string, object>
                        {
                            ["valor"] = 0, ["generosity"] = 0, ["honor"] = 0,
                            ["mercy"] = 0, ["calculating"] = 0
                        }
                    };
                    Dictionary<string, object> departedProfile =
                        EnsureNotableMbtiProfile(connection, batchCampaign, 1d, departed, false);
                    string departedAction = ReadString(departedProfile, "nativeActionId", "");
                    ExecuteSql(connection,
                        "UPDATE character_editor_native_commands SET status='failed' WHERE command_id=$id;",
                        new Dictionary<string, object> { ["id"] = departedAction });
                    ExecuteSql(connection,
                        "UPDATE notable_mbti_profiles SET native_sync_status='failed' WHERE hero_id=$hero;",
                        new Dictionary<string, object> { ["hero"] = "batch_departed_notable" });
                }
                Dictionary<string, object> reconciled = NotableMbtiInitializeApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = batchCampaign, ["worldDay"] = 2d,
                        ["heroes"] = batchHeroes
                    });
                int profiles, commands;
                string departedStatus;
                using (ReignDbConnection connection = OpenCampaignConnection(batchCampaign))
                {
                    profiles = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS total FROM notable_mbti_profiles;").FirstOrDefault(), "total", 0);
                    commands = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS total FROM character_editor_native_commands;").FirstOrDefault(), "total", 0);
                    departedStatus = ReadString(QuerySql(connection,
                        "SELECT native_sync_status FROM notable_mbti_profiles WHERE hero_id='batch_departed_notable' LIMIT 1;")
                        .FirstOrDefault(), "native_sync_status", "");
                }
                add("campaign_start_batch",
                    ReadBool(initialized, "ok", false) && assignments.Count == 2
                    && ReadBool(confirmed, "ok", false) && ReadInt(confirmed, "confirmedCount", 0) == 2
                    && ReadBool(repeated, "ok", false)
                    && ReadInt(reconciled, "obsoleteAssignmentCount", 0) == 1
                    && departedStatus == "obsolete"
                    && profiles == 3 && commands == 3,
                    "Campaign-start initialization assigns, confirms, safely repeats the living batch, and retires failed commands for notables no longer living.");
            }
            finally
            {
                string generatedDirectory = CampaignDirectory(batchCampaign);
                ReignPostgreSqlStorage.ClearAllPools();
                if (Directory.Exists(generatedDirectory)) Directory.Delete(generatedDirectory, true);
            }

            string bulkCampaign = "mbti_bulk_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                const int bulkCount = 300;
                List<Dictionary<string, object>> bulkHeroes =
                    Enumerable.Range(0, bulkCount).Select(index =>
                        new Dictionary<string, object>
                        {
                            ["heroStringId"] = "bulk_notable_" + index.ToString(CultureInfo.InvariantCulture),
                            ["name"] = "Bulk Notable " + index.ToString(CultureInfo.InvariantCulture),
                            ["isNotable"] = true,
                            ["isAlive"] = true,
                            ["traits"] = new Dictionary<string, object>
                            {
                                ["valor"] = 0, ["generosity"] = 0, ["honor"] = 0,
                                ["mercy"] = 0, ["calculating"] = 0
                            }
                        }).ToList();
                Stopwatch bulkTimer = Stopwatch.StartNew();
                Dictionary<string, object> bulkInitialized = NotableMbtiInitializeApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = bulkCampaign,
                        ["worldDay"] = 1d,
                        ["heroes"] = bulkHeroes
                    });
                List<Dictionary<string, object>> bulkAssignments =
                    ReadDictionaryList(bulkInitialized, "assignments");
                Dictionary<string, object> bulkConfirmed = NotableMbtiConfirmBatchApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = bulkCampaign,
                        ["worldDay"] = 1d,
                        ["observations"] = bulkAssignments.Select(x =>
                            new Dictionary<string, object>
                            {
                                ["heroId"] = ReadString(x, "heroId", ""),
                                ["traits"] = ReadDictionary(x, "nativeTraits")
                                    ?? new Dictionary<string, object>()
                            }).ToList()
                    });
                bulkTimer.Stop();
                int indexed = ReadJsonArray(
                    Path.Combine(CampaignDirectory(bulkCampaign), "characters", "index.json")).Count;
                add("bulk_initialization_performance",
                    ReadBool(bulkInitialized, "ok", false)
                    && bulkAssignments.Count == bulkCount
                    && indexed == bulkCount
                    && ReadBool(bulkConfirmed, "ok", false)
                    && ReadInt(bulkConfirmed, "confirmedCount", 0) == bulkCount
                    && ReadLong(bulkInitialized, "databaseMs", long.MaxValue) < 5000L
                    && ReadLong(bulkInitialized, "materializationMs", long.MaxValue) < 10000L
                    && ReadLong(bulkConfirmed, "totalMs", long.MaxValue) < 5000L
                    && bulkTimer.ElapsedMilliseconds < 15000L,
                    "A 300-character campaign-day-one pass uses one index rewrite and transactional assignment/confirmation within bounded offline time.");
            }
            finally
            {
                string generatedDirectory = CampaignDirectory(bulkCampaign);
                ReignPostgreSqlStorage.ClearAllPools();
                if (Directory.Exists(generatedDirectory)) Directory.Delete(generatedDirectory, true);
            }
            return rows;
        }
    }
}
