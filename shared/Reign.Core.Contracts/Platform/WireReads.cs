using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Reign.Core.Contracts.Platform
{
    /// <summary>
    /// Field reads with exactly the fallback rules of the server's ReadString/ReadInt/ReadLong/ReadBool/
    /// ReadDouble/ReadDictionaryList helpers, so typed request contracts parse a payload the same way
    /// the hand-written handlers did.
    /// </summary>
    internal static class WireReads
    {
        internal static string ReadString(IDictionary<string, object>? source, string key, string fallback)
        {
            if (source == null || !source.TryGetValue(key, out object? value) || value == null) return fallback;
            return Convert.ToString(value) ?? fallback;
        }

        internal static int ReadInt(IDictionary<string, object>? source, string key, int fallback)
        {
            if (source == null || !source.TryGetValue(key, out object? value) || value == null) return fallback;
            string? text = Convert.ToString(value);
            if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int parsed))
                return parsed;
            if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out parsed))
                return parsed;
            string digits = new string((text ?? string.Empty).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out parsed) ? parsed : fallback;
        }

        internal static long ReadLong(IDictionary<string, object>? source, string key, long fallback)
        {
            if (source == null || !source.TryGetValue(key, out object? value) || value == null) return fallback;
            return long.TryParse(Convert.ToString(value), out long parsed) ? parsed : fallback;
        }

        internal static bool ReadBool(IDictionary<string, object>? source, string key, bool fallback)
        {
            if (source == null || !source.TryGetValue(key, out object? value) || value == null) return fallback;
            if (value is bool flag) return flag;
            return bool.TryParse(Convert.ToString(value), out bool parsed) ? parsed : fallback;
        }

        internal static double ReadDouble(IDictionary<string, object>? source, string key, double fallback)
        {
            if (source == null || !source.TryGetValue(key, out object? value) || value == null) return fallback;
            return double.TryParse(Convert.ToString(value), out double parsed) ? parsed : fallback;
        }

        internal static List<Dictionary<string, object>> ReadDictionaryList(IDictionary<string, object>? source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object? value) || value == null)
                return new List<Dictionary<string, object>>();
            if (value is ArrayList array) return array.Cast<object>().OfType<Dictionary<string, object>>().ToList();
            if (value is IEnumerable<object> enumerable) return enumerable.OfType<Dictionary<string, object>>().ToList();
            return value is Dictionary<string, object> single
                ? new List<Dictionary<string, object>> { single }
                : new List<Dictionary<string, object>>();
        }

        internal static List<Dictionary<string, object>> CopyRows(IEnumerable<Dictionary<string, object>>? rows)
        {
            return rows == null ? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>>(rows);
        }
    }
}
