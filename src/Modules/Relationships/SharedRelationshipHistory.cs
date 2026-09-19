using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const int PostgreSqlSharedRelationshipHistorySchemaRevision = 1;
        private static readonly object SharedRelationshipHistoryGenerationLocksGuard = new object();
        private static readonly Dictionary<string, object> SharedRelationshipHistoryGenerationLocks =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private static void EnsureSharedRelationshipHistorySchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_shared_relationship_history_schema_revision";
            if (IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlSharedRelationshipHistorySchemaRevision))
                return;
            EnsureSharedRelationshipHistorySchemaCore(connection);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_shared_relationship_history_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    new Dictionary<string, object>
                    {
                        ["revision"] =
                            PostgreSqlSharedRelationshipHistorySchemaRevision
                                .ToString()
                    });
                MarkPostgreSqlComponentSchemaReady(connection, marker,
                    PostgreSqlSharedRelationshipHistorySchemaRevision);
            }
        }

        private static void EnsureSharedRelationshipHistorySchemaCore(
            ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS shared_relationship_history_chapters (
history_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
chapter_ordinal INTEGER NOT NULL,state_fingerprint TEXT NOT NULL,
objective_summary TEXT NOT NULL DEFAULT '',a_interpretation TEXT NOT NULL DEFAULT '',
b_interpretation TEXT NOT NULL DEFAULT '',canon_status TEXT NOT NULL DEFAULT 'soft_relational_canon',
source_kind TEXT NOT NULL DEFAULT 'lazy_dialogue_generation',source_basis_json TEXT NOT NULL DEFAULT '{}',
generation_status TEXT NOT NULL DEFAULT 'generated',provider TEXT NOT NULL DEFAULT '',model TEXT NOT NULL DEFAULT '',
created_world_day REAL NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
UNIQUE(pair_key,chapter_ordinal));");
            ExecuteSql(connection,
                "CREATE INDEX IF NOT EXISTS idx_shared_relationship_history_pair ON shared_relationship_history_chapters(pair_key,chapter_ordinal DESC);");
            ExecuteSql(connection,
                "CREATE INDEX IF NOT EXISTS idx_shared_relationship_history_heroes ON shared_relationship_history_chapters(hero_a_id,hero_b_id,chapter_ordinal DESC);");
        }

        private static object SharedRelationshipHistoryGenerationLock(string campaignId)
        {
            string key = string.IsNullOrWhiteSpace(campaignId) ? "default" : campaignId;
            lock (SharedRelationshipHistoryGenerationLocksGuard)
            {
                if (!SharedRelationshipHistoryGenerationLocks.TryGetValue(key, out object value))
                {
                    value = new object();
                    SharedRelationshipHistoryGenerationLocks[key] = value;
                }
                return value;
            }
        }

        private static Dictionary<string, object> SharedRelationshipHistoryEnsureApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            List<string> heroIds = MergeStringLists(
                ReadStringList(payload, "heroIds"),
                ReadStringList(payload, "participantHeroIds"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10).ToList();
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId is required." };
            if (heroIds.Count < 2)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "At least two heroIds are required." };
            Dictionary<string, Dictionary<string, object>> profiles = heroIds.ToDictionary(
                id => id,
                id => ReadJsonObject(CharacterFile(campaignId, id, "profile.json")),
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> requests = SharedRelationshipHistoryPairRequests(
                heroIds, new List<string>(), "", profiles);
            Dictionary<string, object> result = EnsureSharedRelationshipHistoryBatch(
                campaignId, requests, profiles, payload, ReadBool(payload, "force", false));
            result["ok"] = true;
            return result;
        }

        private static Dictionary<string, object> SharedRelationshipHistoryQueryApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            string heroId = ReadFirstString(payload, "heroStringId", "heroId");
            string otherId = ReadFirstString(payload, "otherHeroStringId", "otherHeroId");
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId is required." };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSharedRelationshipHistorySchema(connection);
                List<Dictionary<string, object>> rows;
                if (!string.IsNullOrWhiteSpace(heroId) && !string.IsNullOrWhiteSpace(otherId))
                {
                    rows = QuerySql(connection, @"SELECT * FROM shared_relationship_history_chapters
WHERE pair_key=$pair ORDER BY chapter_ordinal DESC LIMIT 100;",
                        new Dictionary<string, object> { ["pair"] = AmbientPairKey(heroId, otherId) });
                }
                else if (!string.IsNullOrWhiteSpace(heroId))
                {
                    rows = SharedRelationshipHistoryForCharacter(campaignId, heroId, connection);
                }
                else
                {
                    rows = QuerySql(connection, @"SELECT * FROM shared_relationship_history_chapters
ORDER BY created_ts DESC LIMIT 250;");
                }
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId,
                    ["otherHeroStringId"] = otherId,
                    ["chapters"] = rows,
                    ["count"] = rows.Count
                };
            }
        }

        private static Dictionary<string, object> EnsureSharedRelationshipHistoriesForConversation(
            string campaignId,
            string speakerHeroId,
            IEnumerable<string> presentNpcIds,
            IEnumerable<string> mentionedNpcIds,
            Dictionary<string, Dictionary<string, object>> profiles,
            Dictionary<string, object> eventPayload)
        {
            eventPayload = eventPayload ?? new Dictionary<string, object>();
            if (ReadBool(eventPayload, "skipSharedRelationshipHistoryGeneration", false))
            {
                return new Dictionary<string, object>
                {
                    ["requestedPairCount"] = 0, ["generatedCount"] = 0, ["reusedCount"] = 0,
                    ["fallbackCount"] = 0, ["reason"] = "generation_skipped"
                };
            }
            List<string> present = (presentNpcIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
            List<string> mentioned = (mentionedNpcIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id)
                    && !present.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
            List<Dictionary<string, object>> requests = SharedRelationshipHistoryPairRequests(
                present, mentioned, speakerHeroId, profiles);
            return EnsureSharedRelationshipHistoryBatch(
                campaignId, requests, profiles, eventPayload, false);
        }

        private static List<Dictionary<string, object>> SharedRelationshipHistoryPairRequests(
            IList<string> present,
            IList<string> mentioned,
            string speakerHeroId,
            Dictionary<string, Dictionary<string, object>> profiles)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string, string> add = (left, right, relevance) =>
            {
                string pairKey = AmbientPairKey(left, right);
                if (string.IsNullOrWhiteSpace(pairKey) || !seen.Add(pairKey)) return;
                string[] ids = pairKey.Split('|');
                if (ids.Length != 2) return;
                result.Add(new Dictionary<string, object>
                {
                    ["pairKey"] = pairKey,
                    ["heroAId"] = ids[0],
                    ["heroBId"] = ids[1],
                    ["relevance"] = relevance
                });
            };
            for (int i = 0; i < (present?.Count ?? 0); i++)
                for (int j = i + 1; j < present.Count; j++)
                    add(present[i], present[j], "present_group_pair");
            if (!string.IsNullOrWhiteSpace(speakerHeroId))
                foreach (string mentionedId in mentioned ?? new List<string>())
                    add(speakerHeroId, mentionedId, "speaker_mentioned_absent_pair");
            return result;
        }

        private static Dictionary<string, object> EnsureSharedRelationshipHistoryBatch(
            string campaignId,
            List<Dictionary<string, object>> requests,
            Dictionary<string, Dictionary<string, object>> profiles,
            Dictionary<string, object> context,
            bool force)
        {
            Stopwatch totalTimer = Stopwatch.StartNew();
            requests = requests ?? new List<Dictionary<string, object>>();
            profiles = profiles ?? new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            context = context ?? new Dictionary<string, object>();
            if (requests.Count == 0)
            {
                Dictionary<string, object> empty = SharedRelationshipHistoryGenerationResult(
                    0, 0, 0, 0, new List<Dictionary<string, object>>(), "");
                empty["durationMs"] = totalTimer.ElapsedMilliseconds;
                empty["providerDurationMs"] = 0L;
                empty["providerCallCount"] = 0;
                return empty;
            }

            lock (SharedRelationshipHistoryGenerationLock(campaignId))
            {
                List<Dictionary<string, object>> pending = new List<Dictionary<string, object>>();
                int reused = 0;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureSharedRelationshipHistorySchema(connection);
                    foreach (Dictionary<string, object> request in requests.Take(10))
                    {
                        string pairKey = ReadString(request, "pairKey", "");
                        Dictionary<string, object> chemistry = QuerySql(connection,
                            "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                            new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                        if (chemistry == null) continue;
                        Dictionary<string, object> latest = QuerySql(connection, @"SELECT *
FROM shared_relationship_history_chapters WHERE pair_key=$pair
ORDER BY chapter_ordinal DESC LIMIT 1;",
                            new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                        Dictionary<string, object> basis = BuildSharedRelationshipHistoryBasis(
                            connection, campaignId, chemistry, latest, request, profiles, context);
                        string fingerprint = SharedRelationshipHistoryStateFingerprint(basis);
                        if (!force && latest != null
                            && string.Equals(ReadString(latest, "state_fingerprint", ""), fingerprint,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            reused++;
                            continue;
                        }
                        basis["stateFingerprint"] = fingerprint;
                        pending.Add(basis);
                    }
                }
                if (pending.Count == 0)
                {
                    Dictionary<string, object> reuseOnly = SharedRelationshipHistoryGenerationResult(
                        requests.Count, 0, reused, 0, new List<Dictionary<string, object>>(), "");
                    reuseOnly["durationMs"] = totalTimer.ElapsedMilliseconds;
                    reuseOnly["providerDurationMs"] = 0L;
                    reuseOnly["providerCallCount"] = 0;
                    return reuseOnly;
                }

                Dictionary<string, object> llm = GenerateSharedRelationshipHistoryBatch(pending);
                Dictionary<string, Dictionary<string, object>> generatedByPair =
                    ReadDictionaryList(TryParseJsonObject(ReadString(llm, "content", "")), "histories")
                    .Where(row => !string.IsNullOrWhiteSpace(ReadString(row, "pairKey", "")))
                    .GroupBy(row => ReadString(row, "pairKey", ""), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                int generated = 0, fallback = 0;
                List<Dictionary<string, object>> stored = new List<Dictionary<string, object>>();
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSharedRelationshipHistorySchema(connection);
                    foreach (Dictionary<string, object> basis in pending)
                    {
                        string pairKey = ReadString(basis, "pairKey", "");
                        generatedByPair.TryGetValue(pairKey, out Dictionary<string, object> candidate);
                        Dictionary<string, object> normalized = NormalizeSharedRelationshipHistoryCandidate(
                            candidate, basis, out string rejection);
                        bool usedFallback = normalized == null;
                        if (usedFallback)
                            normalized = DeterministicSharedRelationshipHistory(basis, rejection);
                        Dictionary<string, object> row = InsertSharedRelationshipHistoryChapter(
                            connection, campaignId, basis, normalized, llm, usedFallback);
                        stored.Add(row);
                        if (usedFallback) fallback++; else generated++;
                    }
                }
                Dictionary<string, object> completed = SharedRelationshipHistoryGenerationResult(
                    requests.Count, generated, reused, fallback, stored, ReadString(llm, "error", ""));
                completed["durationMs"] = totalTimer.ElapsedMilliseconds;
                completed["providerDurationMs"] = ReadLong(llm, "durationMs", 0);
                completed["providerCallCount"] = 1;
                completed["provider"] = ReadString(llm, "provider", "");
                completed["model"] = ReadString(llm, "model", "");
                return completed;
            }
        }

        private static Dictionary<string, object> BuildSharedRelationshipHistoryBasis(
            ReignDbConnection connection,
            string campaignId,
            Dictionary<string, object> chemistry,
            Dictionary<string, object> prior,
            Dictionary<string, object> request,
            Dictionary<string, Dictionary<string, object>> profiles,
            Dictionary<string, object> context)
        {
            string pairKey = ReadString(chemistry, "pair_key", ReadString(request, "pairKey", ""));
            string heroAId = ReadString(chemistry, "hero_a_id", ReadString(request, "heroAId", ""));
            string heroBId = ReadString(chemistry, "hero_b_id", ReadString(request, "heroBId", ""));
            Dictionary<string, object> profileA = SharedRelationshipHistoryProfile(
                campaignId, heroAId, profiles.TryGetValue(heroAId, out Dictionary<string, object> suppliedA) ? suppliedA : null);
            Dictionary<string, object> profileB = SharedRelationshipHistoryProfile(
                campaignId, heroBId, profiles.TryGetValue(heroBId, out Dictionary<string, object> suppliedB) ? suppliedB : null);
            Dictionary<string, object> lifecycle = RelationshipLifecycleView(connection, heroAId, heroBId);
            Dictionary<string, object> latestIncident = QuerySql(connection, @"SELECT incident_id,kind,world_day,summary
FROM relationship_incidents WHERE pair_key=$pair ORDER BY world_day DESC,created_ts DESC LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault()
                ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["pairKey"] = pairKey,
                ["heroAId"] = heroAId,
                ["heroBId"] = heroBId,
                ["heroA"] = SharedRelationshipHistoryCharacterBrief(campaignId, heroAId, profileA),
                ["heroB"] = SharedRelationshipHistoryCharacterBrief(campaignId, heroBId, profileB),
                ["affinityAtoB"] = EffectiveDirectionalAffinity(chemistry, true, 0),
                ["affinityBtoA"] = EffectiveDirectionalAffinity(chemistry, false, 0),
                ["tagAtoB"] = ReadString(chemistry, "tag_a_to_b", "neutral"),
                ["tagBtoA"] = ReadString(chemistry, "tag_b_to_a", "neutral"),
                ["bandAtoB"] = RelationshipBand(EffectiveDirectionalAffinity(chemistry, true, 0)),
                ["bandBtoA"] = RelationshipBand(EffectiveDirectionalAffinity(chemistry, false, 0)),
                ["sharedTag"] = ReadString(chemistry, "shared_tag", ""),
                ["lifecycleTags"] = ReadStringList(lifecycle, "tags")
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                ["firstDay"] = ReadDouble(chemistry, "first_day", 0),
                ["lastDay"] = ReadDouble(chemistry, "last_day", 0),
                ["consecutiveDays"] = ReadInt(chemistry, "consecutive_days", 0),
                ["weightedExposure"] = ReadDouble(chemistry, "weighted_exposure", 0),
                ["lastContextKind"] = ReadString(chemistry, "last_context_kind", ""),
                ["lastContextId"] = ReadString(chemistry, "last_context_id", ""),
                ["latestHardIncident"] = latestIncident,
                ["priorChapter"] = prior == null ? new Dictionary<string, object>() : new Dictionary<string, object>
                {
                    ["objectiveSummary"] = ReadString(prior, "objective_summary", ""),
                    ["aInterpretation"] = ReadString(prior, "a_interpretation", ""),
                    ["bInterpretation"] = ReadString(prior, "b_interpretation", ""),
                    ["chapterOrdinal"] = ReadInt(prior, "chapter_ordinal", 0)
                },
                ["relevance"] = ReadString(request, "relevance", ""),
                ["worldDay"] = ReadDouble(context, "worldDay", ReadDouble(chemistry, "last_day", 0))
            };
        }

        private static Dictionary<string, object> SharedRelationshipHistoryProfile(
            string campaignId, string heroId, Dictionary<string, object> supplied)
        {
            Dictionary<string, object> stored = ReadJsonObject(CharacterFile(campaignId, heroId, "profile.json"));
            Dictionary<string, object> result = new Dictionary<string, object>(stored, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> pair in supplied ?? new Dictionary<string, object>())
                result[pair.Key] = pair.Value;
            result["heroStringId"] = heroId;
            return result;
        }

        private static Dictionary<string, object> SharedRelationshipHistoryCharacterBrief(
            string campaignId, string heroId, Dictionary<string, object> profile)
        {
            Dictionary<string, object> traits = ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"));
            Dictionary<string, object> portrait = ReadDictionary(traits, "personalityPortrait")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> mbti = ResolveCharacterMbtiProfile(campaignId, heroId, profile, traits);
            return new Dictionary<string, object>
            {
                ["heroId"] = heroId,
                ["name"] = ReadString(profile, "name", heroId),
                ["occupation"] = ReadFirstString(profile, "occupation", "title"),
                ["cultureId"] = ReadFirstString(profile, "cultureId", "culture"),
                ["clanId"] = ReadString(profile, "clanId", ""),
                ["kingdomId"] = ReadString(profile, "kingdomId", ""),
                ["spouseId"] = ReadString(profile, "spouseId", ""),
                ["fatherId"] = ReadString(profile, "fatherId", ""),
                ["motherId"] = ReadString(profile, "motherId", ""),
                ["mbti"] = ReadString(mbti, "type", "XXXX"),
                ["personality"] = LimitText(FirstNonEmpty(
                    ReadString(traits, "basePersonalitySummary", ""),
                    string.Join(" ", portrait.Values.Select(Convert.ToString))), 1200)
            };
        }

        private static string SharedRelationshipHistoryStateFingerprint(Dictionary<string, object> basis)
        {
            string canonical = string.Join("|", new[]
            {
                ReadString(basis, "pairKey", ""),
                ReadString(basis, "bandAtoB", ""),
                ReadString(basis, "tagAtoB", ""),
                ReadString(basis, "bandBtoA", ""),
                ReadString(basis, "tagBtoA", ""),
                ReadString(basis, "sharedTag", ""),
                string.Join(",", ReadStringList(basis, "lifecycleTags")
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                ReadString(ReadDictionary(basis, "latestHardIncident"), "incident_id", "")
            });
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", "").ToLowerInvariant().Substring(0, 24);
        }

        private static Dictionary<string, object> GenerateSharedRelationshipHistoryBatch(
            List<Dictionary<string, object>> pending)
        {
            List<Dictionary<string, object>> compact = pending.Select(basis => new Dictionary<string, object>
            {
                ["pairKey"] = ReadString(basis, "pairKey", ""),
                ["heroA"] = ReadDictionary(basis, "heroA"),
                ["heroB"] = ReadDictionary(basis, "heroB"),
                ["aToB"] = new Dictionary<string, object>
                {
                    ["affinity"] = ReadInt(basis, "affinityAtoB", 0),
                    ["band"] = ReadString(basis, "bandAtoB", ""),
                    ["tag"] = ReadString(basis, "tagAtoB", "")
                },
                ["bToA"] = new Dictionary<string, object>
                {
                    ["affinity"] = ReadInt(basis, "affinityBtoA", 0),
                    ["band"] = ReadString(basis, "bandBtoA", ""),
                    ["tag"] = ReadString(basis, "tagBtoA", "")
                },
                ["sharedTag"] = ReadString(basis, "sharedTag", ""),
                ["lifecycleTags"] = ReadStringList(basis, "lifecycleTags"),
                ["exposure"] = new Dictionary<string, object>
                {
                    ["firstDay"] = ReadDouble(basis, "firstDay", 0),
                    ["lastDay"] = ReadDouble(basis, "lastDay", 0),
                    ["consecutiveDays"] = ReadInt(basis, "consecutiveDays", 0),
                    ["weightedExposure"] = ReadDouble(basis, "weightedExposure", 0),
                    ["lastContextKind"] = ReadString(basis, "lastContextKind", ""),
                    ["lastContextId"] = ReadString(basis, "lastContextId", "")
                },
                ["latestHardIncident"] = ReadDictionary(basis, "latestHardIncident"),
                ["priorChapter"] = ReadDictionary(basis, "priorChapter")
            }).ToList();
            string prompt =
                "Create compact shared relationship-history chapters for the supplied Bannerlord NPC pairs. "
                + "Return strict JSON only as {\"histories\":[{\"pairKey\":\"\",\"objectiveSummary\":\"\","
                + "\"aInterpretation\":\"\",\"bInterpretation\":\"\"}]}.\n\n"
                + "The objective summary is soft relational canon shared by both participants. It must describe only modest observable conduct: "
                + "conversations, minor assistance, avoidance, disagreement, ordinary social contact, counsel, courtesy, slights, or habits. "
                + "Do not state private emotions, motives, beliefs, love, hatred, resentment, fear, or secret intentions in the objective summary. "
                + "Put those only in the directional interpretations. Do not invent marriages, affairs, children, pregnancies, deaths, crimes, "
                + "major battles, titles, offices, property, treaties, imprisonment, exile, or changes to the campaign world. "
                + "Hard incidents and lifecycle tags may be acknowledged but never altered. High affinity is not romance unless a supplied lifecycle tag supports it. "
                + "Make one objective history compatible with both directional scores even when they are sharply asymmetric. "
                + "A's interpretation is private to A; B's interpretation is private to B. Neither interpretation knows the other's private feelings. "
                + "If a prior chapter exists, append a plausible development rather than rewriting it. Keep each field under 500 characters.\n\nPairs:\n"
                + Json.Serialize(compact);
            return ChatWithLlm(new Dictionary<string, object>
            {
                ["requestType"] = "relationship",
                ["maxTokens"] = Math.Min(5000, 900 + pending.Count * 360),
                ["temperature"] = 0.45d,
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] = "You create internally consistent soft relationship history for Reign. Return strict JSON only. Objective conduct and private interpretation must remain separate."
                    },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = prompt }
                },
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            });
        }

        private static Dictionary<string, object> NormalizeSharedRelationshipHistoryCandidate(
            Dictionary<string, object> candidate,
            Dictionary<string, object> basis,
            out string rejection)
        {
            rejection = "";
            if (candidate == null)
            {
                rejection = "missing_model_row";
                return null;
            }
            if (!string.Equals(ReadString(candidate, "pairKey", ""), ReadString(basis, "pairKey", ""),
                StringComparison.OrdinalIgnoreCase))
            {
                rejection = "pair_key_mismatch";
                return null;
            }
            string objective = LimitText(ReadString(candidate, "objectiveSummary", "").Trim(), 1200);
            string a = LimitText(ReadString(candidate, "aInterpretation", "").Trim(), 1000);
            string b = LimitText(ReadString(candidate, "bInterpretation", "").Trim(), 1000);
            if (objective.Length < 20 || a.Length < 12 || b.Length < 12)
            {
                rejection = "required_text_missing";
                return null;
            }
            string forbiddenObjective =
                @"\b(secretly|felt|believed|thought|loved|hated|resented|feared|desired|envied|was jealous|fell in love|"
                + @"murdered|killed|gave birth|pregnant|wedding|married|divorced|crowned|became king|became queen|"
                + @"captured a|conquered|inherited|was exiled|was imprisoned|committed treason)\b";
            if (Regex.IsMatch(objective, forbiddenObjective, RegexOptions.IgnoreCase))
            {
                rejection = "objective_contains_private_or_major_claim";
                return null;
            }
            return new Dictionary<string, object>
            {
                ["objectiveSummary"] = objective,
                ["aInterpretation"] = a,
                ["bInterpretation"] = b
            };
        }

        private static Dictionary<string, object> DeterministicSharedRelationshipHistory(
            Dictionary<string, object> basis, string reason)
        {
            string nameA = ReadString(ReadDictionary(basis, "heroA"), "name", ReadString(basis, "heroAId", "A"));
            string nameB = ReadString(ReadDictionary(basis, "heroB"), "name", ReadString(basis, "heroBId", "B"));
            int a = ReadInt(basis, "affinityAtoB", 0), b = ReadInt(basis, "affinityBtoA", 0);
            string objective;
            if (a >= 10 && b >= 10)
                objective = nameA + " and " + nameB + " repeatedly made time for private conversation and offered small practical assistance when they were together.";
            else if (a <= -10 && b <= -10)
                objective = nameA + " and " + nameB + " repeatedly contradicted one another over ordinary decisions and became less willing to volunteer assistance.";
            else if ((a >= 10 && b <= -10) || (b >= 10 && a <= -10))
                objective = (a > b ? nameA : nameB) + " frequently sought conversation and offered help, while "
                    + (a > b ? nameB : nameA) + " usually answered briefly and ended their exchanges first.";
            else
                objective = nameA + " and " + nameB + " shared ordinary company and formal conversation without one decisive incident defining their acquaintance.";
            return new Dictionary<string, object>
            {
                ["objectiveSummary"] = objective,
                ["aInterpretation"] = SharedRelationshipDirectionalInterpretation(
                    nameA, nameB, ReadString(basis, "tagAtoB", "neutral"), a),
                ["bInterpretation"] = SharedRelationshipDirectionalInterpretation(
                    nameB, nameA, ReadString(basis, "tagBtoA", "neutral"), b),
                ["fallbackReason"] = reason
            };
        }

        private static string SharedRelationshipDirectionalInterpretation(
            string observerName, string targetName, string tag, int affinity)
        {
            string normalized = (tag ?? "neutral").Replace("_", " ");
            if (affinity >= 50)
                return observerName + " regards " + targetName + " as " + normalized
                    + " and gives the relationship substantial personal importance.";
            if (affinity >= 10)
                return observerName + " regards " + targetName + " as " + normalized
                    + " and considers their familiarity worth maintaining.";
            if (affinity <= -50)
                return observerName + " regards " + targetName + " as " + normalized
                    + " and interprets their past contact through deep hostility.";
            if (affinity <= -10)
                return observerName + " regards " + targetName + " as " + normalized
                    + " and expects further contact to be difficult.";
            return observerName + " regards " + targetName + " as " + normalized
                + " and has not assigned their acquaintance a stronger meaning.";
        }

        private static Dictionary<string, object> InsertSharedRelationshipHistoryChapter(
            ReignDbConnection connection,
            string campaignId,
            Dictionary<string, object> basis,
            Dictionary<string, object> normalized,
            Dictionary<string, object> llm,
            bool fallback)
        {
            string pairKey = ReadString(basis, "pairKey", "");
            int ordinal = ReadInt(QuerySql(connection, @"SELECT COALESCE(MAX(chapter_ordinal),0) value
FROM shared_relationship_history_chapters WHERE pair_key=$pair;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault(), "value", 0) + 1;
            string historyId = "relhist_" + Guid.NewGuid().ToString("N");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> args = new Dictionary<string, object>
            {
                ["id"] = historyId,
                ["pair"] = pairKey,
                ["a"] = ReadString(basis, "heroAId", ""),
                ["b"] = ReadString(basis, "heroBId", ""),
                ["ordinal"] = ordinal,
                ["fingerprint"] = ReadString(basis, "stateFingerprint", ""),
                ["objective"] = ReadString(normalized, "objectiveSummary", ""),
                ["aView"] = ReadString(normalized, "aInterpretation", ""),
                ["bView"] = ReadString(normalized, "bInterpretation", ""),
                ["basis"] = Json.Serialize(basis),
                ["status"] = fallback ? "deterministic_fallback" : "generated",
                ["provider"] = ReadString(llm, "provider", ""),
                ["model"] = ReadString(llm, "model", ""),
                ["day"] = ReadDouble(basis, "worldDay", ReadDouble(basis, "lastDay", 0)),
                ["ts"] = ts
            };
            ExecuteSql(connection, @"INSERT INTO shared_relationship_history_chapters(
history_id,pair_key,hero_a_id,hero_b_id,chapter_ordinal,state_fingerprint,
objective_summary,a_interpretation,b_interpretation,canon_status,source_kind,source_basis_json,
generation_status,provider,model,created_world_day,created_ts,updated_ts)
VALUES($id,$pair,$a,$b,$ordinal,$fingerprint,$objective,$aView,$bView,
'soft_relational_canon','lazy_dialogue_generation',$basis,$status,$provider,$model,$day,$ts,$ts);", args);
            return QuerySql(connection,
                "SELECT * FROM shared_relationship_history_chapters WHERE history_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = historyId }).FirstOrDefault()
                ?? new Dictionary<string, object>();
        }

        private static Dictionary<string, object> SharedRelationshipHistoryGenerationResult(
            int requested, int generated, int reused, int fallback,
            List<Dictionary<string, object>> stored, string error)
        {
            return new Dictionary<string, object>
            {
                ["requestedPairCount"] = requested,
                ["generatedCount"] = generated,
                ["reusedCount"] = reused,
                ["fallbackCount"] = fallback,
                ["storedChapters"] = stored ?? new List<Dictionary<string, object>>(),
                ["error"] = error ?? "",
                ["dailyWorkerCalls"] = 0,
                ["dailyWorkerUnaffected"] = true
            };
        }

        private static string BuildSharedRelationshipHistoryPromptBlock(
            ReignDbConnection connection,
            string speakerHeroId,
            string targetHeroId,
            out List<string> historyIds)
        {
            historyIds = new List<string>();
            string pairKey = AmbientPairKey(speakerHeroId, targetHeroId);
            List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT *
FROM shared_relationship_history_chapters WHERE pair_key=$pair
ORDER BY chapter_ordinal DESC LIMIT 3;",
                new Dictionary<string, object> { ["pair"] = pairKey });
            if (rows.Count == 0) return "";
            rows.Reverse();
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Established shared relationship history (soft relational canon):");
            foreach (Dictionary<string, object> row in rows)
            {
                bool speakerIsA = ReadString(row, "hero_a_id", "")
                    .Equals(speakerHeroId, StringComparison.OrdinalIgnoreCase);
                builder.AppendLine("- Objective conduct: " + ReadString(row, "objective_summary", ""));
                builder.AppendLine("  Your private interpretation: "
                    + ReadString(row, speakerIsA ? "a_interpretation" : "b_interpretation", ""));
                historyIds.Add(ReadString(row, "history_id", ""));
            }
            builder.AppendLine("Treat objective conduct as shared history, but do not claim access to the other person's private interpretation. Current relationship level and lifecycle facts remain authoritative.");
            return builder.ToString().Trim();
        }

        private static List<string> ResolveMentionedRelationshipHeroIds(
            string campaignId,
            Dictionary<string, object> eventPayload,
            string speakerHeroId,
            string playerId)
        {
            eventPayload = eventPayload ?? new Dictionary<string, object>();
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[]
            {
                "mentionedHeroIds", "referencedHeroIds", "relationshipTargetHeroIds"
            })
                result.UnionWith(ReadStringList(eventPayload, key));
            string text = ReadFirstString(eventPayload, "playerText", "text", "message");
            Dictionary<string, object> index = ReadDictionary(eventPayload, "actionResolutionIndex")
                ?? new Dictionary<string, object>();
            foreach (Dictionary<string, object> row in MentionedResolverEntries(index, "heroes", "name", text, 10))
            {
                string id = ReadFirstString(row, "heroStringId", "heroId", "id");
                if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
            }
            result.Remove(speakerHeroId);
            result.Remove(playerId);
            return result.Where(id => !string.IsNullOrWhiteSpace(id)).Take(5).ToList();
        }

        private static List<Dictionary<string, object>> SharedRelationshipHistoryForCharacter(
            string campaignId, string heroId, ReignDbConnection connection)
        {
            EnsureSharedRelationshipHistorySchema(connection);
            List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT *
FROM shared_relationship_history_chapters WHERE hero_a_id=$id OR hero_b_id=$id
ORDER BY created_world_day DESC,chapter_ordinal DESC LIMIT 500;",
                new Dictionary<string, object> { ["id"] = heroId });
            foreach (Dictionary<string, object> row in rows)
            {
                bool isA = ReadString(row, "hero_a_id", "").Equals(heroId, StringComparison.OrdinalIgnoreCase);
                string otherId = ReadString(row, isA ? "hero_b_id" : "hero_a_id", "");
                Dictionary<string, object> other = ReadJsonObject(CharacterFile(campaignId, otherId, "profile.json"));
                row["otherHeroId"] = otherId;
                row["otherHeroName"] = ReadString(other, "name", otherId);
                row["thisCharacterInterpretation"] = ReadString(row,
                    isA ? "a_interpretation" : "b_interpretation", "");
                row["otherPrivateInterpretation"] = ReadString(row,
                    isA ? "b_interpretation" : "a_interpretation", "");
            }
            return rows;
        }

        private static List<Dictionary<string, object>> RunSharedRelationshipHistorySelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => rows.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "shared_relationship_history",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            Dictionary<string, object> mixed = new Dictionary<string, object>
            {
                ["pairKey"] = "a|b", ["heroAId"] = "a", ["heroBId"] = "b",
                ["heroA"] = new Dictionary<string, object> { ["name"] = "A" },
                ["heroB"] = new Dictionary<string, object> { ["name"] = "B" },
                ["affinityAtoB"] = 62, ["affinityBtoA"] = -34,
                ["tagAtoB"] = "confidant", ["tagBtoA"] = "resentful",
                ["bandAtoB"] = "close_friend", ["bandBtoA"] = "rival",
                ["lifecycleTags"] = new List<string>(), ["latestHardIncident"] = new Dictionary<string, object>()
            };
            Dictionary<string, object> fallback = DeterministicSharedRelationshipHistory(mixed, "fixture");
            add("asymmetric_fallback_is_one_objective_two_views",
                ReadString(fallback, "objectiveSummary", "").Length > 20
                && ReadString(fallback, "aInterpretation", "").Contains("confidant")
                && ReadString(fallback, "bInterpretation", "").Contains("resentful"),
                "A sharply asymmetric pair receives one action-focused background and two directional interpretations.");
            string fingerprint = SharedRelationshipHistoryStateFingerprint(mixed);
            mixed["affinityAtoB"] = 68;
            add("same_band_number_change_keeps_fingerprint",
                fingerprint == SharedRelationshipHistoryStateFingerprint(mixed),
                "Ordinary daily number movement inside the same bands does not generate another chapter.");
            mixed["bandAtoB"] = "devoted";
            add("meaningful_band_change_invalidates_fingerprint",
                fingerprint != SharedRelationshipHistoryStateFingerprint(mixed),
                "A meaningful relationship-band transition makes a new chapter eligible on the next dialogue lookup.");
            Dictionary<string, object> valid = NormalizeSharedRelationshipHistoryCandidate(
                new Dictionary<string, object>
                {
                    ["pairKey"] = "a|b",
                    ["objectiveSummary"] = "A repeatedly offered practical help, while B answered courteously but ended their meetings first.",
                    ["aInterpretation"] = "A considers the repeated contact a meaningful bond.",
                    ["bInterpretation"] = "B considers the contact an obligation rather than friendship."
                }, mixed, out string validReason);
            add("objective_and_private_meaning_separated",
                valid != null && string.IsNullOrWhiteSpace(validReason),
                "Action-focused objective history and private directional meaning pass validation.");
            Dictionary<string, object> invalid = NormalizeSharedRelationshipHistoryCandidate(
                new Dictionary<string, object>
                {
                    ["pairKey"] = "a|b",
                    ["objectiveSummary"] = "A secretly loved B and believed B returned that love.",
                    ["aInterpretation"] = "A wants closeness.",
                    ["bInterpretation"] = "B wants distance."
                }, mixed, out string invalidReason);
            add("private_feelings_rejected_from_objective_history",
                invalid == null && invalidReason == "objective_contains_private_or_major_claim",
                "Private feelings cannot be promoted into the shared objective account.");
            Dictionary<string, object> result = SharedRelationshipHistoryGenerationResult(
                10, 0, 10, 0, new List<Dictionary<string, object>>(), "");
            add("daily_worker_remains_unmodified",
                ReadInt(result, "dailyWorkerCalls", -1) == 0
                && ReadBool(result, "dailyWorkerUnaffected", false),
                "Lazy relationship-history generation performs no daily-worker calls.");
            string sourceRoot = FindVerificationSourceRoot();
            string serverRoot = string.IsNullOrWhiteSpace(sourceRoot)
                ? "" : Path.Combine(sourceRoot, "ReignServer");
            string workerSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "ContinuousRelationshipWorker.cs", "src"));
            string promptSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "RelationshipPrompts.cs", "src"));
            string promptCachingSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "PromptCaching.cs", "src"));
            string programSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "Program.cs", "src"));
            add("history_generation_is_dialogue_lazy_not_daily",
                !string.IsNullOrWhiteSpace(workerSource)
                && !workerSource.Contains("EnsureSharedRelationshipHistoriesForConversation", StringComparison.Ordinal)
                && !workerSource.Contains("EnsureSharedRelationshipHistoryBatch", StringComparison.Ordinal)
                && promptSource.Contains("EnsureSharedRelationshipHistoriesForConversation", StringComparison.Ordinal)
                && promptCachingSource.Contains("precomputedNpcRelationshipPrompt", StringComparison.Ordinal)
                && programSource.Contains("relationship_history.loaded", StringComparison.Ordinal)
                && programSource.Contains("BuildNpcRelationshipPromptContext(campaignId, heroId, payload)", StringComparison.Ordinal),
                "Source contracts keep model-backed relationship history out of the intensive daily worker, pre-resolve it in both production request paths, and reuse it during local prompt assembly.");

            string campaignId = "_shared_relationship_history_test_" + Guid.NewGuid().ToString("N");
            try
            {
                Dictionary<string, object> persistedBasis = new Dictionary<string, object>(mixed)
                {
                    ["stateFingerprint"] = SharedRelationshipHistoryStateFingerprint(mixed),
                    ["worldDay"] = 22d,
                    ["lastDay"] = 22d
                };
                Dictionary<string, object> persisted = new Dictionary<string, object>
                {
                    ["objectiveSummary"] = "A offered B practical help during several meetings, while B remained formally courteous.",
                    ["aInterpretation"] = "A treats those meetings as the beginning of a trusted friendship.",
                    ["bInterpretation"] = "B treats the same meetings as an obligation requiring careful distance."
                };
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    InsertSharedRelationshipHistoryChapter(connection, campaignId, persistedBasis, persisted,
                        new Dictionary<string, object> { ["provider"] = "fixture", ["model"] = "fixture" }, false);
                    string promptA = BuildSharedRelationshipHistoryPromptBlock(connection, "a", "b", out List<string> idsA);
                    string promptB = BuildSharedRelationshipHistoryPromptBlock(connection, "b", "a", out List<string> idsB);
                    List<Dictionary<string, object>> profileA =
                        SharedRelationshipHistoryForCharacter(campaignId, "a", connection);
                    List<Dictionary<string, object>> profileB =
                        SharedRelationshipHistoryForCharacter(campaignId, "b", connection);
                    add("persisted_chapter_is_linked_to_both_profiles",
                        profileA.Count == 1 && profileB.Count == 1
                        && ReadString(profileA[0], "history_id", "") == ReadString(profileB[0], "history_id", ""),
                        "One persisted objective chapter is linked to both character profiles rather than duplicated into competing histories.");
                    add("speaker_prompt_receives_only_own_private_view",
                        idsA.Count == 1 && idsB.Count == 1
                        && promptA.Contains(ReadString(persisted, "aInterpretation", ""), StringComparison.Ordinal)
                        && !promptA.Contains(ReadString(persisted, "bInterpretation", ""), StringComparison.Ordinal)
                        && promptB.Contains(ReadString(persisted, "bInterpretation", ""), StringComparison.Ordinal)
                        && !promptB.Contains(ReadString(persisted, "aInterpretation", ""), StringComparison.Ordinal),
                        "Both speakers receive the shared objective conduct, but each prompt contains only that speaker's directional interpretation.");
                }
            }
            catch (Exception ex)
            {
                add("shared_relationship_history_persistence_exception", false, ex.Message);
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }
            string fixtureCampaignId =
                "_relationship_fixture_test_" + Guid.NewGuid().ToString("N");
            string armPath = LiveTestArmPath();
            bool armExisted = File.Exists(armPath);
            Dictionary<string, object> originalArm =
                armExisted ? ReadJsonObject(armPath) : null;
            try
            {
                WriteJsonObject(armPath, new Dictionary<string, object>
                {
                    ["expiresUtc"] = DateTimeOffset.UtcNow
                        .AddMinutes(5).ToString("o")
                });
                WriteJsonObject(
                    ConversationReadinessStatePath(fixtureCampaignId),
                    new Dictionary<string, object>
                    {
                        ["schemaVersion"] = 1,
                        ["qualificationId"] = "fixture-qualification",
                        ["campaignId"] = fixtureCampaignId,
                        ["buildVersion"] = CurrentConversationBuildVersion(),
                        ["status"] = "running"
                    });
                using (ReignDbConnection connection =
                    OpenCampaignConnection(fixtureCampaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,
affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,projected_native_relation,updated_ts)
VALUES('fixture_a|fixture_b','fixture_a','fixture_b','INTJ','ESFP',
5,-3,'neutral','neutral',1,1,1,$ts);",
                        new Dictionary<string, object>
                        {
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                }
                Dictionary<string, object> applied =
                    ConversationQualificationRelationshipFixtureApi(
                        new Dictionary<string, object>
                        {
                            ["confirmation"] = "qualification_fixture",
                            ["operation"] = "set",
                            ["campaignId"] = fixtureCampaignId,
                            ["qualificationId"] = "fixture-qualification",
                            ["heroAId"] = "fixture_a",
                            ["heroBId"] = "fixture_b",
                            ["affinityAtoB"] = 62,
                            ["affinityBtoA"] = -34
                        });
                Dictionary<string, object> restored =
                    ConversationQualificationRelationshipFixtureApi(
                        new Dictionary<string, object>
                        {
                            ["confirmation"] = "qualification_fixture",
                            ["operation"] = "restore",
                            ["campaignId"] = fixtureCampaignId,
                            ["qualificationId"] = "fixture-qualification"
                        });
                Dictionary<string, object> finalRow;
                using (ReignDbConnection connection =
                    OpenCampaignConnection(fixtureCampaignId))
                {
                    finalRow = QuerySql(connection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key='fixture_a|fixture_b' LIMIT 1;")
                        .FirstOrDefault();
                }
                add("qualification_fixture_is_reversible",
                    ReadBool(applied, "ok", false)
                    && ReadBool(restored, "ok", false)
                    && finalRow != null
                    && ReadInt(finalRow, "affinity_a_to_b", 999) == 5
                    && ReadInt(finalRow, "affinity_b_to_a", 999) == -3
                    && ReadInt(finalRow, "projected_native_relation", 999) == 1,
                    "The armed qualification fixture snapshots an existing pair, applies an exact asymmetric transition, and restores the original directional/native projection.");
            }
            catch (Exception ex)
            {
                add("qualification_fixture_is_reversible", false,
                    "Fixture verification failed: " + ex.Message);
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(fixtureCampaignId));
                if (armExisted)
                    WriteJsonObject(armPath,
                        originalArm ?? new Dictionary<string, object>());
                else
                    TryDeleteFile(armPath);
            }
            return rows;
        }
    }
}
