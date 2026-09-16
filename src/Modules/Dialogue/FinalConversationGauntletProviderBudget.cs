using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Npgsql;

namespace ReignBetaServer
{
    internal sealed class FinalGauntletProviderBudgetExceededException
        : InvalidOperationException
    {
        internal FinalGauntletProviderBudgetExceededException(string message)
            : base(message)
        {
        }
    }

    internal static class FinalConversationGauntletProviderBudget
    {
        private const int MaximumPhysicalCalls = 500;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Scope> Scopes =
            new Dictionary<string, Scope>(StringComparer.OrdinalIgnoreCase);

        private sealed class Scope
        {
            public string ConnectionString;
            public string RunId;
            public string CaseInstanceId;
            public string CorrelationId;
            public bool PostgreSql;
        }

        public static void RegisterCorrelation(
            ReignDbConnection connection,
            string runId,
            string caseInstanceId,
            string correlationId)
        {
            if (connection == null
                || string.IsNullOrWhiteSpace(runId)
                || string.IsNullOrWhiteSpace(caseInstanceId)
                || string.IsNullOrWhiteSpace(correlationId))
                throw new ArgumentException(
                    "Connection, runId, caseInstanceId, and correlationId are required.");

            FinalGauntletStore.EnsureSchema(connection);
            lock (Gate)
            {
                Execute(
                    connection,
                    @"INSERT INTO final_gauntlet_provider_correlations(
run_id,case_instance_id,correlation_id,created_ts)
VALUES($run,$case,$correlation,$ts)
ON CONFLICT(run_id,correlation_id) DO UPDATE SET
case_instance_id=excluded.case_instance_id,
created_ts=excluded.created_ts;",
                    ("$run", runId),
                    ("$case", caseInstanceId),
                    ("$correlation", correlationId),
                    ("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Scopes[correlationId] = new Scope
                {
                    ConnectionString = connection.ConnectionString,
                    RunId = runId,
                    CaseInstanceId = caseInstanceId,
                    CorrelationId = correlationId,
                    PostgreSql =
                        ReignPostgreSqlDialect.IsPostgreSql(connection)
                };
            }
        }

        public static Dictionary<string, object> TryReservePhysicalDispatch(
            ReignDbConnection connection,
            string correlationId,
            string requestType,
            string model,
            int physicalAttempt)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            Scope scope = ResolveScope(connection, correlationId);
            if (scope == null)
                return Result(false, false, 0, 0, MaximumPhysicalCalls, "");
            return Reserve(
                connection,
                scope,
                correlationId,
                requestType,
                model,
                physicalAttempt);
        }

        public static void CompletePhysicalDispatch(
            ReignDbConnection connection,
            string providerCallId,
            bool succeeded,
            string error)
        {
            if (connection == null || string.IsNullOrWhiteSpace(providerCallId))
                return;
            lock (Gate)
            {
                Execute(
                    connection,
                    @"UPDATE final_gauntlet_provider_calls
SET status=$status,completed_ts=$ts,error=$error
WHERE provider_call_id=$id;",
                    ("$status", succeeded ? "completed" : "failed"),
                    ("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    ("$error", error ?? string.Empty),
                    ("$id", providerCallId));
            }
        }

        internal static Dictionary<string, object>
            TryReserveRegisteredPhysicalDispatch(
                string correlationId,
                string requestType,
                string model,
                int physicalAttempt)
        {
            Scope scope;
            lock (Gate)
            {
                scope = Scopes.Values
                    .Where(item => CorrelationMatches(
                        correlationId, item.CorrelationId))
                    .OrderByDescending(item => item.CorrelationId.Length)
                    .FirstOrDefault();
            }
            if (scope == null)
            {
                try
                {
                    using (ReignDbConnection lookup =
                        FinalGauntletStore.OpenControlConnection())
                    {
                        FinalGauntletStore.EnsureSchema(lookup);
                        scope = ResolveScope(lookup, correlationId);
                    }
                }
                catch
                {
                    scope = null;
                }
            }
            if (scope == null)
                return Result(false, true, 0, 0, MaximumPhysicalCalls, "");

            using (ReignDbConnection connection = OpenScopeConnection(scope))
            {
                FinalGauntletStore.EnsureSchema(connection);
                return Reserve(
                    connection,
                    scope,
                    correlationId,
                    requestType,
                    model,
                    physicalAttempt);
            }
        }

        internal static bool IsRegisteredCorrelation(
            string correlationId)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                return false;
            lock (Gate)
            {
                if (Scopes.Values.Any(item =>
                    CorrelationMatches(
                        correlationId, item.CorrelationId)))
                    return true;
            }
            try
            {
                using (ReignDbConnection connection =
                    FinalGauntletStore.OpenControlConnection())
                {
                    FinalGauntletStore.EnsureSchema(connection);
                    return ResolveScope(connection, correlationId) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static Dictionary<string, object>
            BackfillAuditedProviderCalls(
                ReignDbConnection connection,
                string runId)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            FinalGauntletStore.EnsureSchema(connection);
            JavaScriptSerializer serializer =
                new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue,
                    RecursionLimit = 512
                };
            List<(string CaseId, string Payload)> evidence =
                new List<(string CaseId, string Payload)>();
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.CommandText = ReignPostgreSqlDialect.Normalize(
                    connection,
                    @"SELECT case_instance_id,payload_json
FROM final_gauntlet_evidence
WHERE run_id=$run AND evidence_key='production_result'
ORDER BY case_instance_id;");
                command.Parameters.AddWithValue("$run", runId ?? string.Empty);
                using (ReignDbDataReader reader = command.ExecuteReader())
                    while (reader.Read())
                        evidence.Add((
                            reader.GetString(0),
                            reader.IsDBNull(1) ? "{}" : reader.GetString(1)));
            }

            int before = (int)ScalarLong(
                connection,
                @"SELECT COUNT(*) FROM final_gauntlet_provider_calls
WHERE run_id=$run;",
                ("$run", runId ?? string.Empty));
            int denied = 0;
            foreach ((string caseId, string payloadJson) in evidence)
            {
                Dictionary<string, object> payload;
                try
                {
                    payload = serializer.DeserializeObject(payloadJson)
                        as Dictionary<string, object>;
                }
                catch
                {
                    continue;
                }
                List<Dictionary<string, object>> auditEntries =
                    DictionaryList(payload, "sourceAuditEntries");
                Dictionary<string, Dictionary<string, object>> responses =
                    auditEntries
                        .Where(row => StringValue(row, "phase")
                            .Equals(
                                "llm.response",
                                StringComparison.OrdinalIgnoreCase))
                        .GroupBy(
                            row => StringValue(row, "correlationId"),
                            StringComparer.OrdinalIgnoreCase)
                        .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                        .ToDictionary(
                            group => group.Key,
                            group => group.Last(),
                            StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> request in auditEntries
                    .Where(row => StringValue(row, "phase").Equals(
                        "llm.request",
                        StringComparison.OrdinalIgnoreCase)))
                {
                    string correlation = StringValue(
                        request, "correlationId");
                    if (string.IsNullOrWhiteSpace(correlation))
                        continue;
                    Dictionary<string, object> data =
                        DictionaryValue(request, "data");
                    Dictionary<string, object> reservation =
                        TryReservePhysicalDispatch(
                            connection,
                            correlation,
                            StringValue(data, "requestType"),
                            StringValue(data, "model"),
                            1);
                    if (!ReadBool(reservation, "scoped", false))
                        continue;
                    if (!ReadBool(reservation, "allowed", false))
                    {
                        denied++;
                        continue;
                    }
                    responses.TryGetValue(
                        correlation,
                        out Dictionary<string, object> response);
                    bool succeeded = response != null
                        && StringValue(response, "status").Equals(
                            "completed",
                            StringComparison.OrdinalIgnoreCase);
                    CompletePhysicalDispatch(
                        connection,
                        ReadString(reservation, "providerCallId", ""),
                        succeeded,
                        succeeded
                            ? string.Empty
                            : response == null
                                ? "Backfilled audit has no matching llm.response."
                                : StringValue(
                                    DictionaryValue(response, "data"),
                                    "error"));
                }
            }
            int used = (int)ScalarLong(
                connection,
                @"SELECT COUNT(*) FROM final_gauntlet_provider_calls
WHERE run_id=$run;",
                ("$run", runId ?? string.Empty));
            return new Dictionary<string, object>
            {
                ["ok"] = denied == 0,
                ["runId"] = runId ?? string.Empty,
                ["backfilled"] = Math.Max(0, used - before),
                ["used"] = used,
                ["remaining"] = Math.Max(0, MaximumPhysicalCalls - used),
                ["denied"] = denied,
                ["error"] = denied == 0
                    ? string.Empty
                    : "Existing audited provider calls already exhaust the "
                        + MaximumPhysicalCalls + "-call gauntlet budget."
            };
        }

        internal static void ClearRegisteredScopesForTests()
        {
            lock (Gate)
                Scopes.Clear();
        }

        internal static void CompleteRegisteredPhysicalDispatch(
            Dictionary<string, object> reservation,
            bool succeeded,
            string error)
        {
            if (reservation == null
                || !ReadBool(reservation, "scoped", false))
                return;
            string connectionString = ReadString(
                reservation, "connectionString", "");
            string providerCallId = ReadString(
                reservation, "providerCallId", "");
            bool postgreSql = ReadBool(
                reservation, "postgreSql", false);
            if (string.IsNullOrWhiteSpace(connectionString)
                || string.IsNullOrWhiteSpace(providerCallId))
                return;
            Scope scope = new Scope
            {
                ConnectionString = connectionString,
                PostgreSql = postgreSql
            };
            using (ReignDbConnection connection = OpenScopeConnection(scope))
            {
                CompletePhysicalDispatch(
                    connection, providerCallId, succeeded, error);
            }
        }

        private static Dictionary<string, object> Reserve(
            ReignDbConnection connection,
            Scope scope,
            string actualCorrelationId,
            string requestType,
            string model,
            int physicalAttempt)
        {
            lock (Gate)
            {
                FinalGauntletStore.EnsureSchema(connection);
                if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                    return ReservePostgreSql(
                        connection,
                        scope,
                        actualCorrelationId,
                        requestType,
                        model,
                        physicalAttempt);
                long used = ScalarLong(
                    connection,
                    @"SELECT COUNT(*) FROM final_gauntlet_provider_calls
WHERE run_id=$run;",
                    ("$run", scope.RunId));
                if (used >= MaximumPhysicalCalls)
                    return Result(
                        true, false, 0, (int)used,
                        MaximumPhysicalCalls, "");

                int ordinal = checked((int)used + 1);
                string providerCallId = scope.RunId + "|"
                    + scope.CaseInstanceId + "|"
                    + (actualCorrelationId ?? string.Empty) + "|"
                    + (requestType ?? string.Empty) + "|"
                    + physicalAttempt.ToString();
                try
                {
                    Execute(
                        connection,
                        @"INSERT INTO final_gauntlet_provider_calls(
run_id,provider_call_id,case_instance_id,correlation_id,request_type,
model,physical_attempt,ordinal,status,started_ts,completed_ts,error)
VALUES($run,$id,$case,$correlation,$type,$model,$attempt,$ordinal,
'reserved',$ts,0,'');",
                        ("$run", scope.RunId),
                        ("$id", providerCallId),
                        ("$case", scope.CaseInstanceId),
                        ("$correlation", actualCorrelationId ?? string.Empty),
                        ("$type", requestType ?? string.Empty),
                        ("$model", model ?? string.Empty),
                        ("$attempt", physicalAttempt),
                        ("$ordinal", ordinal),
                        ("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                }
                catch (PostgresException ex) when (
                    ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    Dictionary<string, object> existing = QueryOne(
                        connection,
                        @"SELECT ordinal FROM final_gauntlet_provider_calls
WHERE run_id=$run AND provider_call_id=$id LIMIT 1;",
                        ("$run", scope.RunId),
                        ("$id", providerCallId));
                    if (existing.Count == 0) throw;
                    ordinal = Convert.ToInt32(existing["ordinal"]);
                }
                Dictionary<string, object> result = Result(
                    true, true, ordinal,
                    Math.Max(ordinal, (int)used),
                    MaximumPhysicalCalls, providerCallId);
                result["connectionString"] = connection.ConnectionString;
                result["postgreSql"] =
                    ReignPostgreSqlDialect.IsPostgreSql(connection);
                return result;
            }
        }

        private static Dictionary<string, object> ReservePostgreSql(
            ReignDbConnection connection,
            Scope scope,
            string actualCorrelationId,
            string requestType,
            string model,
            int physicalAttempt)
        {
            string providerCallId = scope.RunId + "|"
                + scope.CaseInstanceId + "|"
                + (actualCorrelationId ?? string.Empty) + "|"
                + (requestType ?? string.Empty) + "|"
                + physicalAttempt.ToString();
            using (ReignDbTransaction transaction =
                connection.BeginTransaction())
            {
                Execute(
                    connection,
                    transaction,
                    @"SELECT pg_advisory_xact_lock(
hashtextextended($run,4412));",
                    ("$run", scope.RunId));
                Dictionary<string, object> existing = QueryOne(
                    connection,
                    transaction,
                    @"SELECT ordinal FROM final_gauntlet_provider_calls
WHERE run_id=$run AND provider_call_id=$id LIMIT 1;",
                    ("$run", scope.RunId),
                    ("$id", providerCallId));
                long used = ScalarLong(
                    connection,
                    transaction,
                    @"SELECT COUNT(*) FROM final_gauntlet_provider_calls
WHERE run_id=$run;",
                    ("$run", scope.RunId));
                if (existing.Count > 0)
                {
                    int existingOrdinal =
                        Convert.ToInt32(existing["ordinal"]);
                    transaction.Commit();
                    Dictionary<string, object> replay = Result(
                        true, true, existingOrdinal, (int)used,
                        MaximumPhysicalCalls, providerCallId);
                    replay["connectionString"] =
                        connection.ConnectionString;
                    replay["postgreSql"] = true;
                    return replay;
                }
                if (used >= MaximumPhysicalCalls)
                {
                    transaction.Commit();
                    return Result(
                        true, false, 0, (int)used,
                        MaximumPhysicalCalls, "");
                }

                int ordinal = checked((int)used + 1);
                Execute(
                    connection,
                    transaction,
                    @"INSERT INTO final_gauntlet_provider_calls(
run_id,provider_call_id,case_instance_id,correlation_id,request_type,
model,physical_attempt,ordinal,status,started_ts,completed_ts,error)
VALUES($run,$id,$case,$correlation,$type,$model,$attempt,$ordinal,
'reserved',$ts,0,'');",
                    ("$run", scope.RunId),
                    ("$id", providerCallId),
                    ("$case", scope.CaseInstanceId),
                    ("$correlation",
                        actualCorrelationId ?? string.Empty),
                    ("$type", requestType ?? string.Empty),
                    ("$model", model ?? string.Empty),
                    ("$attempt", physicalAttempt),
                    ("$ordinal", ordinal),
                    ("$ts",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                transaction.Commit();
                Dictionary<string, object> result = Result(
                    true, true, ordinal, ordinal,
                    MaximumPhysicalCalls, providerCallId);
                result["connectionString"] =
                    connection.ConnectionString;
                result["postgreSql"] = true;
                return result;
            }
        }

        private static Scope ResolveScope(
            ReignDbConnection connection,
            string correlationId)
        {
            lock (Gate)
            {
                Scope memory = Scopes.Values
                    .Where(item => CorrelationMatches(
                        correlationId, item.CorrelationId))
                    .OrderByDescending(item => item.CorrelationId.Length)
                    .FirstOrDefault();
                if (memory != null) return memory;

                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT run_id,case_instance_id,
correlation_id FROM final_gauntlet_provider_correlations;";
                    using (ReignDbDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string registered = reader.GetString(2);
                            if (!CorrelationMatches(
                                    correlationId, registered))
                                continue;
                            Scope loaded = new Scope
                            {
                                ConnectionString = connection.ConnectionString,
                                RunId = reader.GetString(0),
                                CaseInstanceId = reader.GetString(1),
                                CorrelationId = registered,
                                PostgreSql =
                                    ReignPostgreSqlDialect.IsPostgreSql(
                                        connection)
                            };
                            Scopes[registered] = loaded;
                            if (memory == null
                                || registered.Length
                                    > memory.CorrelationId.Length)
                                memory = loaded;
                        }
                    }
                }
                return memory;
            }
        }

        private static bool CorrelationMatches(
            string actual,
            string registered)
        {
            if (string.IsNullOrWhiteSpace(actual)
                || string.IsNullOrWhiteSpace(registered))
                return false;
            string normalizedActual = actual.StartsWith(
                    "live-", StringComparison.OrdinalIgnoreCase)
                ? actual.Substring("live-".Length)
                : actual;
            return normalizedActual.Equals(
                    registered, StringComparison.OrdinalIgnoreCase)
                || normalizedActual.StartsWith(
                    registered + "-", StringComparison.OrdinalIgnoreCase)
                || normalizedActual.StartsWith(
                    registered + "|", StringComparison.OrdinalIgnoreCase)
                || normalizedActual.StartsWith(
                    registered + ":", StringComparison.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> DictionaryList(
            Dictionary<string, object> source,
            string key)
        {
            if (source == null
                || !source.TryGetValue(key, out object raw)
                || raw == null)
                return new List<Dictionary<string, object>>();
            IEnumerable values = raw as IEnumerable;
            if (values == null || raw is string)
                return new List<Dictionary<string, object>>();
            return values.Cast<object>()
                .Select(value => value as Dictionary<string, object>)
                .Where(value => value != null)
                .ToList();
        }

        private static Dictionary<string, object> DictionaryValue(
            Dictionary<string, object> source,
            string key)
        {
            if (source != null
                && source.TryGetValue(key, out object raw)
                && raw is Dictionary<string, object> value)
                return value;
            return new Dictionary<string, object>();
        }

        private static string StringValue(
            Dictionary<string, object> source,
            string key)
        {
            if (source != null
                && source.TryGetValue(key, out object raw)
                && raw != null)
                return Convert.ToString(raw) ?? string.Empty;
            return string.Empty;
        }

        private static ReignDbConnection OpenScopeConnection(Scope scope)
        {
            return FinalGauntletStore.OpenControlConnection();
        }

        private static Dictionary<string, object> Result(
            bool scoped,
            bool allowed,
            int ordinal,
            int used,
            int maximum,
            string providerCallId)
        {
            return new Dictionary<string, object>
            {
                ["scoped"] = scoped,
                ["allowed"] = allowed,
                ["ordinal"] = ordinal,
                ["used"] = used,
                ["remaining"] = Math.Max(0, maximum - used),
                ["maximum"] = maximum,
                ["providerCallId"] = providerCallId ?? string.Empty
            };
        }

        private static void Execute(
            ReignDbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            Execute(connection, null, sql, parameters);
        }

        private static void Execute(
            ReignDbConnection connection,
            ReignDbTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    ReignPostgreSqlDialect.Normalize(connection, sql);
                foreach ((string name, object value) in parameters)
                    command.Parameters.AddWithValue(
                        name, value ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private static long ScalarLong(
            ReignDbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            return ScalarLong(
                connection, null, sql, parameters);
        }

        private static long ScalarLong(
            ReignDbConnection connection,
            ReignDbTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    ReignPostgreSqlDialect.Normalize(connection, sql);
                foreach ((string name, object value) in parameters)
                    command.Parameters.AddWithValue(
                        name, value ?? DBNull.Value);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static Dictionary<string, object> QueryOne(
            ReignDbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            return QueryOne(
                connection, null, sql, parameters);
        }

        private static Dictionary<string, object> QueryOne(
            ReignDbConnection connection,
            ReignDbTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    ReignPostgreSqlDialect.Normalize(connection, sql);
                foreach ((string name, object value) in parameters)
                    command.Parameters.AddWithValue(
                        name, value ?? DBNull.Value);
                using (ReignDbDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new Dictionary<string, object>();
                    return new Dictionary<string, object>
                    {
                        ["ordinal"] = reader.GetInt32(0)
                    };
                }
            }
        }

        private static bool ReadBool(
            Dictionary<string, object> value,
            string key,
            bool fallback)
        {
            if (value == null || !value.TryGetValue(key, out object raw))
                return fallback;
            try { return Convert.ToBoolean(raw); }
            catch { return fallback; }
        }

        private static string ReadString(
            Dictionary<string, object> value,
            string key,
            string fallback)
        {
            return value != null && value.TryGetValue(key, out object raw)
                ? Convert.ToString(raw) ?? fallback
                : fallback;
        }
    }
}
