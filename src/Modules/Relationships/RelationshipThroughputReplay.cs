using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        [ThreadStatic] private static bool RelationshipThroughputOriginalReadPath;
        [ThreadStatic] private static RelationshipReplayConceptionInputs RelationshipReplayConceptions;

        private sealed class RelationshipReplayConceptionInputs
        {
            internal readonly string Campaign;
            internal readonly Dictionary<string, string> Identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            private bool recording;
            internal RelationshipReplayConceptionInputs(string campaign) { Campaign = campaign; }
            internal void BeginRoute(bool record) { recording = record; seen.Clear(); }
            internal string Next(string key)
            {
                if (!seen.Add(key)) throw new InvalidOperationException("Replay generated a duplicate conception attempt.");
                if (recording) Identifiers.Add(key, "conception_" + Guid.NewGuid().ToString("N"));
                if (!Identifiers.TryGetValue(key, out string identifier))
                    throw new InvalidOperationException("Replay generated a conception absent from the original route.");
                return identifier;
            }
            internal void CompleteRoute()
            {
                if (seen.Count != Identifiers.Count)
                    throw new InvalidOperationException("Replay omitted an original conception attempt.");
            }
        }

        private static string CreateLifecycleConceptionId(string campaign, string timeline, string pair, int day)
        {
            var inputs = RelationshipReplayConceptions;
            if (inputs == null) return "conception_" + Guid.NewGuid().ToString("N");
            if (!HasArg(Environment.GetCommandLineArgs(), "--run-verification") || ActiveServerPort > 0 || inputs.Campaign != campaign)
                throw new InvalidOperationException("Captured conception identifiers require the owning non-listening replay.");
            return inputs.Next(Json.Serialize(new object[] { campaign, timeline, pair, day }));
        }

        private static Dictionary<string, object> RunRelationshipThroughputReplay(string sandbox, int seed, bool smoke)
        {
            if (!HasArg(Environment.GetCommandLineArgs(), "--run-verification") || ActiveServerPort > 0)
                throw new InvalidOperationException("Relationship replay requires the isolated non-listening Verification Lab CLI.");
            const int population = 4305, historicalPairs = 82073;
            int measuredDays = smoke ? 5 : 100;
            string campaign = "__relationship_replay_" + seed + "_" + Guid.NewGuid().ToString("N");
            string artifact = Path.Combine(sandbox, "relationship-throughput");
            Directory.CreateDirectory(artifact);
            // Fixed read-only transport probes precede warm-up and every measured
            // route. They cannot alter campaign state or timed queue observations.
            string transportPath = Path.Combine(artifact, "transport.json");
            File.WriteAllText(transportPath, Json.Serialize(ProbeRelationshipDatabaseTransport()));
            var heroes = Enumerable.Range(0, population).Select(i => new Dictionary<string, object>
            {
                ["heroStringId"] = "replay_" + i.ToString("D4"), ["name"] = "Replay " + i,
                ["age"] = 20 + i % 40, ["isAdult"] = true, ["isAlive"] = true,
                ["isFemale"] = i % 2 == 1, ["isNotable"] = i >= 1000, ["isLord"] = i < 1000,
                ["kingdomId"] = "realm_" + i % 13, ["clanId"] = "clan_" + i % 180,
                ["traits"] = new Dictionary<string, object> { ["Mercy"] = 0, ["Valor"] = 0, ["Honor"] = 0, ["Generosity"] = 0, ["Calculating"] = 0 },
                ["traitPercentages"] = CoreTraitKeys.ToDictionary(key => key, key => (object)50),
                ["foundationTraits"] = new Dictionary<string, object>()
            }).ToList();
            var inputs = new List<Dictionary<string, object>>();
            for (int offset = 0; offset <= measuredDays; offset++)
            {
                int cursor = 0;
                var groups = new List<Dictionary<string, object>>();
                for (int group = 0; group < 650; group++)
                {
                    int size = group < 300 ? 6 : 5;
                    var members = Enumerable.Range(cursor, size).Select(i => "replay_"
                        + ((i + offset * 17) % population).ToString("D4")).ToList();
                    cursor += size;
                    groups.Add(new Dictionary<string, object> { ["kind"] = "party", ["id"] = "replay_group_" + group,
                        ["heroIds"] = members, ["nativeRelations"] = new List<object>() });
                }
                inputs.Add(new Dictionary<string, object> { ["campaignId"] = campaign, ["timelineId"] = "main",
                    ["worldDay"] = 1000 + offset, ["heroes"] = offset == 0 ? heroes : heroes.Skip(offset).Take(80).ToList(), ["presenceGroups"] = groups,
                    ["correlationId"] = "replay_" + seed + "_" + offset, ["continuousWorker"] = true });
            }
            string inputPath = Path.Combine(artifact, "inputs.json");
            File.WriteAllText(inputPath, Json.Serialize(inputs));
            string inputsSha = Sha256Hex(File.ReadAllText(inputPath));
            string conceptionInputsPath = Path.Combine(artifact, "conception-inputs.json");
            var conceptionInputs = new RelationshipReplayConceptionInputs(campaign);
            var routes = new List<Dictionary<string, object>>();
            try
            {
                for (int route = 0; route < 3; route++)
                {
                    string routeName = route == 0 ? "original" : route == 1 ? "candidate-equivalence" : "candidate";
                    bool timed = route == 2;
                    RelationshipThroughputOriginalReadPath = route == 0;
                    conceptionInputs.BeginRoute(route == 0);
                    RelationshipReplayConceptions = conceptionInputs;
                    RelationshipHeroDocuments.Clear();
                    var routeInputs = Json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(inputPath));
                    // Re-create the same isolated campaign ID and seed, including file state.
                    // This avoids comparing different campaign-dependent random streams.
                    SeedRelationshipThroughputCampaign(campaign, ReadDictionaryList(routeInputs[0], "heroes"), historicalPairs);
                    RunRelationshipReplayDay(routeInputs[0]); // Explicit unmeasured warm-up, identical for both paths.
                    var samples = new List<Dictionary<string, object>>();
                    var clock = Stopwatch.StartNew();
                    using (var arrivals = new BlockingCollection<int>())
                    using (var cancellation = new CancellationTokenSource())
                    {
                        // A producer keeps delivering independently of service time. This
                        // is a service-capacity test; HTTP/native transport is measured later.
                        var producer = Task.Run(() =>
                        {
                            try
                            {
                                for (int index = 1; index <= measuredDays; index++)
                                {
                                    long scheduled = timed ? (index - 1) * 2000L : 0;
                                    int delay = (int)Math.Max(0, scheduled - clock.ElapsedMilliseconds);
                                    if (delay > 0 && cancellation.Token.WaitHandle.WaitOne(delay)) break;
                                    cancellation.Token.ThrowIfCancellationRequested();
                                    arrivals.Add(index, cancellation.Token);
                                }
                            }
                            catch (OperationCanceledException) { }
                            finally { arrivals.CompleteAdding(); }
                        });
                        try
                        {
                            foreach (int index in arrivals.GetConsumingEnumerable())
                            {
                                long started = clock.ElapsedMilliseconds;
                                var service = Stopwatch.StartNew();
                                var result = RunRelationshipReplayDay(routeInputs[index]);
                                service.Stop();
                                var sample = new Dictionary<string, object>
                                {
                                    ["day"] = 1000 + index, ["scheduledArrivalMs"] = timed ? (index - 1) * 2000L : 0,
                                    ["serviceStartedMs"] = started, ["serviceMs"] = service.Elapsed.TotalMilliseconds,
                                    ["queueWaitMs"] = timed ? Math.Max(0, started - (index - 1) * 2000L) : 0,
                                    ["waitingDays"] = arrivals.Count, ["phases"] = result
                                };
                                if (!timed)
                                    using (var connection = OpenCampaignConnection(campaign))
                                    {
                                        try { sample["semanticState"] = ReadRelationshipReplaySemanticState(connection, 1000 + index); }
                                        catch { WriteRelationshipReplayReferences(connection, artifact, routeName); throw; }
                                    }
                                samples.Add(sample);
                                // Functional observers run in separate replays. The
                                // timed route retains in memory and writes only after
                                // the final arrival, so hash/I/O observers cannot create
                                // an artificial queue behind the actual worker.
                                if (!timed) File.AppendAllText(Path.Combine(artifact, routeName + "-samples.jsonl"),
                                    Json.Serialize(sample) + Environment.NewLine);
                            }
                        }
                        finally { cancellation.Cancel(); producer.GetAwaiter().GetResult(); }
                    }
                    conceptionInputs.CompleteRoute();
                    RelationshipReplayConceptions = null;
                    if (route == 0) File.WriteAllText(conceptionInputsPath, Json.Serialize(conceptionInputs.Identifiers));
                    double[] durations = samples.Select(x => ReadDouble(x, "serviceMs", 0)).OrderBy(x => x).ToArray();
                    Dictionary<string, object> finalState;
                    using (var connection = OpenCampaignConnection(campaign))
                    {
                        WriteRelationshipReplayReferences(connection, artifact, routeName);
                        finalState = ReadRelationshipReplaySemanticState(connection);
                    }
                    if (timed) File.WriteAllLines(Path.Combine(artifact, routeName + "-samples.jsonl"), samples.Select(Json.Serialize));
                    string[] phaseNames = { "durationMs", "lifecycleLoadMs", "heroHydrationMs", "matchingMs", "personalityLoadMs",
                        "decayMs", "stateLoadMs", "pairProcessingMs", "finalizationMs", "flingProcessingMs", "courtPopularityMs", "commitMs", "postgresCopyMs", "postgresMergeMs",
                        "stateLifecycleReadMs", "stateChemistryReadMs", "stateNativeReadMs", "stateStandingReadMs", "finalPairFlushMs", "finalProvenanceFlushMs", "finalTelemetryMs",
                        "sqlSetupMs", "sqlExecuteMs", "sqlMaterializeMs" };
                    routes.Add(new Dictionary<string, object> { ["route"] = routeName,
                        ["measuredDays"] = samples.Count, ["p95ServiceMs"] = durations[(int)Math.Ceiling(durations.Length * .95) - 1],
                        ["maxServiceMs"] = durations.Last(), ["maxQueueWaitMs"] = samples.Max(x => ReadDouble(x, "queueWaitMs", 0)),
                        ["finalSemanticState"] = finalState,
                        ["phaseAverageMs"] = phaseNames.ToDictionary(name => name, name => (object)samples.Average(sample => ReadDouble(ReadDictionary(sample, "phases"), name, 0))),
                        ["minEvaluatedPairs"] = samples.Min(sample => ReadInt(ReadDictionary(sample, "phases"), "evaluatedPairDays", 0)),
                        ["maxEvaluatedPairs"] = samples.Max(sample => ReadInt(ReadDictionary(sample, "phases"), "evaluatedPairDays", 0)),
                        ["samples"] = samples });
                    File.WriteAllText(Path.Combine(artifact, routeName + ".json"), Json.Serialize(routes.Last()));
                    ClearSaveSyncCaches(campaign);
                    ReignPostgreSqlStorage.DropCampaign(campaign);
                    TryDeleteDirectory(CampaignDirectory(campaign));
                }
                var original = ReadDictionaryList(routes[0], "samples");
                var candidate = ReadDictionaryList(routes[1], "samples");
                var mismatches = candidate.Where((sample, index) => Json.Serialize(ReadDictionary(sample, "semanticState"))
                    != Json.Serialize(ReadDictionary(original[index], "semanticState"))).Select(x => ReadInt(x, "day", 0)).ToList();
                bool equivalent = mismatches.Count == 0 && Json.Serialize(routes[0]["finalSemanticState"]) == Json.Serialize(routes[1]["finalSemanticState"])
                    && Json.Serialize(routes[1]["finalSemanticState"]) == Json.Serialize(routes[2]["finalSemanticState"]);
                bool budgetMet = measuredDays >= 100 && ReadDouble(routes[2], "p95ServiceMs", double.MaxValue) < 1000
                    && ReadDouble(routes[2], "maxQueueWaitMs", double.MaxValue) < 2000;
                var report = new Dictionary<string, object>
                {
                    ["schema"] = "reign_relationship_replay_v3", ["ok"] = equivalent && (smoke || budgetMet),
                    ["fixtureKind"] = "synthetic_mature_campaign", ["seed"] = seed, ["campaignId"] = campaign,
                    ["population"] = population, ["initialHistoricalPairs"] = historicalPairs, ["groupsPerDay"] = 650,
                    ["inputPath"] = inputPath, ["inputSha256"] = inputsSha, ["measuredDays"] = measuredDays,
                    ["identicalSemanticTrajectory"] = equivalent, ["mismatchedDays"] = mismatches,
                    ["launchReplayBudgetMet"] = budgetMet, ["originalP95Ms"] = routes[0]["p95ServiceMs"],
                    ["candidateP95Ms"] = routes[2]["p95ServiceMs"], ["maxQueueWaitMs"] = routes[2]["maxQueueWaitMs"],
                    ["originalPhaseAverageMs"] = routes[0]["phaseAverageMs"], ["candidatePhaseAverageMs"] = routes[2]["phaseAverageMs"],
                    ["minEvaluatedPairs"] = routes[2]["minEvaluatedPairs"], ["maxEvaluatedPairs"] = routes[2]["maxEvaluatedPairs"],
                    ["artifactDirectory"] = artifact,
                    ["transportProbePath"] = transportPath,
                    ["conceptionInputsPath"] = conceptionInputsPath,
                    ["conceptionInputsSha256"] = Sha256Hex(File.ReadAllText(conceptionInputsPath)),
                    ["coverageLimits"] = "Synthetic roster and persisted history; service includes ingestion/commit. Separate original/candidate functional routes compare daily chemistry/lifecycle rows processed that day and all other selected tables, then all rows at completion. A third candidate route measures independent two-second arrivals without daily hash or file observers and must match the complete final state. Opaque IDs are mapped to unique full semantic records; action/conception payloads, reservations and commitments are compared. Wall-clock timestamps and audit run IDs are excluded. Native time, HTTP, competing workers, save restore and captured player-campaign acceptance remain separate."
                };
                File.WriteAllText(Path.Combine(artifact, "report.json"), Json.Serialize(report));
                return report;
            }
            finally
            {
                RelationshipThroughputOriginalReadPath = false;
                RelationshipReplayConceptions = null;
                ClearSaveSyncCaches(campaign);
                ReignPostgreSqlStorage.DropCampaign(campaign);
                TryDeleteDirectory(CampaignDirectory(campaign));
            }
        }

        private static Dictionary<string, object> ProbeRelationshipDatabaseTransport()
        {
            if (!HasArg(Environment.GetCommandLineArgs(), "--run-verification") || ActiveServerPort > 0)
                throw new InvalidOperationException("Transport probes require the non-listening verification CLI.");
            var options = ReignPostgreSqlOptions.FromEnvironment();
            if (!options.IsValidation || options.Database != ReignPostgreSqlOptions.ValidationDatabaseName)
                throw new InvalidOperationException("Transport probes require ReignValidation.");
            if (!System.Net.IPAddress.TryParse(options.Host, out var address) || !System.Net.IPAddress.IsLoopback(address))
                return new Dictionary<string, object> { ["ok"] = false, ["skipped"] = true,
                    ["reason"] = "Transport probe requires a literal loopback validation endpoint." };
            var samples = new List<Dictionary<string, object>>();
            var deadline = Stopwatch.StartNew();
            foreach (int bufferBytes in new[] { 8192, 16384, 65536, 262144 })
            {
                var builder = new Npgsql.NpgsqlConnectionStringBuilder(options.BuildConnectionString())
                {
                    Pooling = false, MinPoolSize = 0, MaxAutoPrepare = 0,
                    WriteBufferSize = bufferBytes, Timeout = 10, CommandTimeout = 10
                };
                using (var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString))
                {
                    connection.Open();
                    bool tls;
                    using (var security = new Npgsql.NpgsqlCommand("SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid();", connection))
                        tls = Convert.ToBoolean(security.ExecuteScalar(), CultureInfo.InvariantCulture);
                    using (var command = new Npgsql.NpgsqlCommand("SELECT length(@payload);", connection))
                    {
                        var parameter = command.Parameters.Add("payload", NpgsqlTypes.NpgsqlDbType.Text);
                        foreach (int payloadBytes in new[] { 128, 1024, 4096, 8192, 16384, 32768, 65536, 131072 })
                        foreach (bool asynchronous in new[] { false, true })
                        {
                            parameter.Value = new string('x', payloadBytes);
                            var durations = new List<double>();
                            for (int iteration = 0; iteration < 9; iteration++)
                            {
                                if (deadline.ElapsedMilliseconds >= 30000)
                                    return new Dictionary<string, object> { ["schema"] = "reign_relationship_transport_probe_v2",
                                        ["ok"] = false, ["incomplete"] = true, ["reason"] = "Thirty-second request budget exceeded.", ["samples"] = samples };
                                var timer = Stopwatch.StartNew();
                                object result = asynchronous ? command.ExecuteScalarAsync().GetAwaiter().GetResult() : command.ExecuteScalar();
                                int observed = Convert.ToInt32(result, CultureInfo.InvariantCulture);
                                timer.Stop();
                                if (observed != payloadBytes) throw new InvalidOperationException("Transport probe payload changed.");
                                if (iteration > 0) durations.Add(timer.Elapsed.TotalMilliseconds);
                            }
                            durations.Sort();
                            samples.Add(new Dictionary<string, object>
                            {
                                ["writeBufferBytes"] = bufferBytes, ["payloadBytes"] = payloadBytes,
                                ["executionMode"] = asynchronous ? "asynchronous" : "synchronous",
                                ["tls"] = tls, ["measuredRequests"] = durations.Count,
                                ["medianMs"] = durations[durations.Count / 2], ["maxMs"] = durations.Last()
                            });
                        }
                    }
                }
            }
            return new Dictionary<string, object> { ["schema"] = "reign_relationship_transport_probe_v2",
                ["ok"] = true, ["database"] = options.Database, ["loopbackOnly"] = true, ["samples"] = samples };
        }

        private static Dictionary<string, object> RunRelationshipReplayDay(Dictionary<string, object> input)
        {
            var hydrated = PersistAndHydrateRelationshipDailyInput(input);
            hydrated["continuousWorker"] = true;
            hydrated["correlationId"] = input["correlationId"];
            var result = MbtiRelationshipSnapshotApi(hydrated);
            if (!ReadBool(result, "ok", false)) throw new InvalidOperationException("Replay day failed: " + Json.Serialize(result));
            MarkRelationshipDailyInputProcessed(ReadString(input, "campaignId", ""), "main", ReadInt(input, "worldDay", 0));
            return result;
        }

        private static Dictionary<string, object> ReadRelationshipReplaySemanticState(ReignDbConnection connection, int? day = null)
        {
            var state = new Dictionary<string, object>();
            var conceptions = QuerySql(connection, "SELECT * FROM conceptions;");
            var actions = QuerySql(connection, "SELECT * FROM relationship_director_actions;");
            var identities = BuildRelationshipReplayIdentityMap(conceptions, actions);
            string identityJson = Json.Serialize(identities);
            foreach (string table in new[] { "relationship_pair_chemistry", "relationship_pair_lifecycle", "relationship_native_targets",
                "relationship_personalities", "character_public_standing", "character_reputations" })
            {
                string filter = !day.HasValue ? "" : table == "relationship_pair_chemistry" ? " WHERE last_day=$day"
                    : table == "relationship_pair_lifecycle" ? " WHERE last_processed_day=$day" : "";
                string row = "to_jsonb(t)-'rowid'-'updated_ts'-'created_ts'-'issued_ts'-'claimed_ts'-'run_id'";
                if (table == "relationship_pair_lifecycle")
                    row = "(" + row + ") || jsonb_build_object(" + string.Join(",", new[] { "marriage_action_id", "divorce_action_id", "conception_action_id" }
                        .Select(column => "'" + column + "',COALESCE(CAST($identities AS jsonb)->>t." + column + ",t." + column + ")")) + ")";
                state[table] = ReadString(QuerySql(connection, @"SELECT md5(COALESCE(string_agg(value,'' ORDER BY value),'')) AS hash FROM (
 SELECT md5((" + row + ")::text) AS value FROM " + table + " t" + filter + ") rows;",
                    new Dictionary<string, object> { ["day"] = day ?? 0, ["identities"] = identityJson })
                    .FirstOrDefault(), "hash", "");
            }
            state["conceptions"] = RelationshipReplayRowsHash(conceptions, identities);
            state["relationship_director_actions"] = RelationshipReplayRowsHash(actions, identities);
            foreach (string table in new[] { "relationship_marriage_reservations", "relationship_conception_commitments", "relationship_affair_commitments" })
                state[table] = RelationshipReplayRowsHash(QuerySql(connection, "SELECT * FROM " + table + ";"), identities);
            return state;
        }

        private static void WriteRelationshipReplayReferences(ReignDbConnection connection, string artifact, string routeName)
        {
            File.WriteAllText(Path.Combine(artifact, routeName + "-referenced-state.json"), Json.Serialize(
                new Dictionary<string, object>
                {
                    ["actions"] = QuerySql(connection, "SELECT * FROM relationship_director_actions;"),
                    ["conceptions"] = QuerySql(connection, "SELECT * FROM conceptions;"),
                    ["conceptionCommitments"] = QuerySql(connection, "SELECT * FROM relationship_conception_commitments;"),
                    ["marriageReservations"] = QuerySql(connection, "SELECT * FROM relationship_marriage_reservations;"),
                    ["affairCommitments"] = QuerySql(connection, "SELECT * FROM relationship_affair_commitments;"),
                    ["lifecycle"] = QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle WHERE marriage_action_id<>'' OR divorce_action_id<>'' OR conception_action_id<>'';")
                }));
        }

        private static Dictionary<string, string> BuildRelationshipReplayIdentityMap(
            List<Dictionary<string, object>> conceptions, List<Dictionary<string, object>> actions)
        {
            var identities = new Dictionary<string, string>(StringComparer.Ordinal);
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var set in new[] { Tuple.Create("conception_id", conceptions), Tuple.Create("director_action_id", actions) })
                foreach (var row in set.Item2)
                {
                    string id = ReadString(row, set.Item1, "");
                    var content = new Dictionary<string, object>(row);
                    content.Remove(set.Item1);
                    string identity = set.Item1 + "/" + Sha256Hex(Json.Serialize(CanonicalRelationshipReplayValue(content, identities, true)));
                    // Ambiguous identical records cannot be assigned a safe identity
                    // by sorting random GUIDs. Preserve this as failed evidence.
                    if (string.IsNullOrEmpty(id) || !unique.Add(identity) || identities.ContainsKey(id))
                        throw new InvalidOperationException("Replay found an ambiguous or missing action/conception identity.");
                    identities.Add(id, identity);
                }
            return identities;
        }

        private static string RelationshipReplayRowsHash(List<Dictionary<string, object>> rows, Dictionary<string, string> identities)
        {
            return Sha256Hex(string.Join("\n", rows.Select(row => Json.Serialize(CanonicalRelationshipReplayValue(row, identities, true)))
                .OrderBy(value => value, StringComparer.Ordinal)));
        }

        private static object CanonicalRelationshipReplayValue(object value, Dictionary<string, string> identities, bool row = false)
        {
            if (value is Dictionary<string, object> dictionary)
            {
                var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var field in dictionary)
                {
                    if (row && new[] { "rowid", "updated_ts", "created_ts", "issued_ts", "claimed_ts", "resolved_ts", "run_id" }.Contains(field.Key)) continue;
                    object content = field.Value;
                    if (field.Key.EndsWith("_json", StringComparison.Ordinal) && content is string json)
                        content = Json.DeserializeObject(json);
                    result[field.Key] = CanonicalRelationshipReplayValue(content, identities);
                }
                return result;
            }
            if (value is string text) return identities.TryGetValue(text, out string identity) ? identity : text;
            if (value is System.Collections.IEnumerable sequence && !(value is byte[]))
                return sequence.Cast<object>().Select(item => CanonicalRelationshipReplayValue(item, identities)).ToArray();
            return value;
        }

        private static void SeedRelationshipThroughputCampaign(string campaign, List<Dictionary<string, object>> heroes, int pairCount)
        {
            using (var connection = OpenCampaignConnection(campaign))
            {
                EnsureMbtiRelationshipSchema(connection);
                EnsureRelationshipDirectorSchema(connection);
                EnsureRelationshipLifecycleSchema(connection);
                EnsureWorldTestSchema(connection);
                EnsureIdentitySchema(connection);
                EnsureSocialReputationSchema(connection);
                UpsertIdentityRosterHeroesBatch(connection, heroes, 1);
                ExecuteSql(connection, @"INSERT INTO relationship_personalities(hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
SELECT hero_id,CASE mod(CAST(substring(hero_id FROM 8) AS integer),4) WHEN 0 THEN 'ENFP' WHEN 1 THEN 'ISTJ' WHEN 2 THEN 'INTJ' ELSE 'ESFJ' END,
'Replay personality','Replay fixture','persisted',1,1,'{}',1,1 FROM identity_roster;");
                ExecuteSql(connection, @"INSERT INTO notable_mbti_profiles(hero_id,mbti_type,title,description,foundation_json,percentages_json,native_traits_json,template_version,first_observed_day,native_sync_status,updated_ts)
SELECT hero_id,CASE mod(CAST(substring(hero_id FROM 8) AS integer),4) WHEN 0 THEN 'ENFP' WHEN 1 THEN 'ISTJ' WHEN 2 THEN 'INTJ' ELSE 'ESFJ' END,
'Replay personality','Replay fixture','{}',$percentages,$native,1,1,'synchronized',1 FROM identity_roster WHERE is_notable=1;",
                    new Dictionary<string, object> { ["percentages"] = Json.Serialize(heroes[0]["traitPercentages"]), ["native"] = Json.Serialize(heroes[0]["traits"]) });
                ExecuteSql(connection, @"WITH numbers AS (
 SELECT a,mod(a+101*d,4305) AS b FROM generate_series(0,4304) a CROSS JOIN generate_series(1,40) d
 UNION SELECT a,a+d FROM generate_series(0,4304) a CROSS JOIN generate_series(1,3) d WHERE a+d<4305
), pairs AS (SELECT a,b FROM numbers WHERE a<b ORDER BY a,b LIMIT $count), named AS (
 SELECT 'replay_'||lpad(a::text,4,'0') AS a,'replay_'||lpad(b::text,4,'0') AS b,
 CASE WHEN b-a<=3 AND mod(a+b,7)=0 THEN 40 ELSE 0 END AS affinity FROM pairs)
INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,affinity_a_to_b,affinity_b_to_a,first_day,last_day,last_decay_period,updated_ts)
SELECT named.a||'|'||named.b,named.a,named.b,pa.mbti_type,pb.mbti_type,affinity,affinity,1,999,199,1 FROM named
JOIN relationship_personalities pa ON pa.hero_id=named.a JOIN relationship_personalities pb ON pb.hero_id=named.b;", new Dictionary<string, object> { ["count"] = pairCount });
                ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(pair_key,hero_a_id,hero_b_id,last_processed_day,version,updated_ts)
SELECT pair_key,hero_a_id,hero_b_id,999,$version,1 FROM relationship_pair_chemistry;",
                    new Dictionary<string, object> { ["version"] = RelationshipLifecycleVersion });
                ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET flirt_attempted=1,flirt_passed=0
WHERE mod(CAST(substring(hero_a_id FROM 8) AS integer)+CAST(substring(hero_b_id FROM 8) AS integer),29)=0;");
                if (TableCount(connection, "relationship_pair_chemistry") != pairCount)
                    throw new InvalidOperationException("Replay historical-pair fixture size differs from its declared population.");
            }
        }
    }
}
