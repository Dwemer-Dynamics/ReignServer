using System;
using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace ReignBetaServer
{
    internal static class ReignDatabaseParameterExtensions
    {
        public static DbParameter AddWithValue(
            this System.Data.Common.DbParameterCollection parameters,
            string name,
            object value)
        {
            DbParameter parameter = new NpgsqlParameter(
                NormalizePostgreSqlName(name), value ?? DBNull.Value);
            parameters.Add(parameter);
            return parameter;
        }

        public static DbParameter Add(
            this System.Data.Common.DbParameterCollection parameters,
            string name,
            NpgsqlDbType databaseType)
        {
            DbParameter parameter = new NpgsqlParameter(
                NormalizePostgreSqlName(name), databaseType);
            parameters.Add(parameter);
            return parameter;
        }

        private static string NormalizePostgreSqlName(string name)
        {
            return StripPrefix(name);
        }

        private static string StripPrefix(string name)
        {
            string normalized = (name ?? string.Empty).Trim();
            while (normalized.StartsWith("$", StringComparison.Ordinal)
                   || normalized.StartsWith("@", StringComparison.Ordinal)
                   || normalized.StartsWith(":", StringComparison.Ordinal))
                normalized = normalized.Substring(1);
            return normalized;
        }
    }
}
