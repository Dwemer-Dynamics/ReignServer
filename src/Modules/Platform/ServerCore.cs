global using static ReignBetaServer.ServerCore;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    // Shared stateless helpers. Available to every type in the assembly through
    // the global using static above, so call sites stay unqualified.
    // Only pure helpers belong here: no Program state, campaign caches or settings.
    internal static class ServerCore
    {
        internal static Dictionary<string, object> TestDict(params object[] pairs)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>();
            AddPairs(dict, pairs);
            return dict;
        }

        internal static void AddPairs(Dictionary<string, object> dict, params object[] pairs)
        {
            if (dict == null || pairs == null)
            {
                return;
            }

            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                string key = Convert.ToString(pairs[i], CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    dict[key] = pairs[i + 1];
                }
            }
        }

        internal static ArrayList TestList(params object[] items)
        {
            ArrayList list = new ArrayList();
            if (items != null)
            {
                foreach (object item in items)
                {
                    list.Add(item);
                }
            }

            return list;
        }

        internal static string ReadString(Dictionary<string, object> source, string key, string fallback)
        {
            return source != null && source.TryGetValue(key, out object value) && value != null ? Convert.ToString(value) : fallback;
        }

        internal static int ReadInt(Dictionary<string, object> source, string key, int fallback)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return fallback;
            }

            string text = Convert.ToString(value);
            if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }

            if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out parsed))
            {
                return parsed;
            }

            string digits = new string((text ?? string.Empty).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out parsed) ? parsed : fallback;
        }

        internal static long ReadLong(Dictionary<string, object> source, string key, long fallback)
        {
            return source != null && source.TryGetValue(key, out object value) && value != null && long.TryParse(Convert.ToString(value), out long parsed) ? parsed : fallback;
        }

        internal static bool ReadBool(Dictionary<string, object> source, string key, bool fallback)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return fallback;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            return bool.TryParse(Convert.ToString(value), out bool parsed) ? parsed : fallback;
        }

        internal static double ReadDouble(Dictionary<string, object> source, string key, double fallback)
        {
            return source != null && source.TryGetValue(key, out object value) && value != null && double.TryParse(Convert.ToString(value), out double parsed) ? parsed : fallback;
        }

        internal static string ReadFirstString(Dictionary<string, object> source, params string[] keys)
        {
            if (source == null || keys == null)
            {
                return string.Empty;
            }

            foreach (string key in keys)
            {
                string value = ReadString(source, key, "");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        internal static List<Dictionary<string, object>> ReadDictionaryList(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return new List<Dictionary<string, object>>();
            }

            if (value is ArrayList array)
            {
                return array.Cast<object>().OfType<Dictionary<string, object>>().ToList();
            }

            if (value is IEnumerable<object> enumerable)
            {
                return enumerable.OfType<Dictionary<string, object>>().ToList();
            }

            Dictionary<string, object> single = value as Dictionary<string, object>;
            return single == null ? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>> { single };
        }

        internal static List<string> ReadStringList(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return new List<string>();
            }

            if (value is ArrayList array)
            {
                return array.Cast<object>().Select(Convert.ToString).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }

            if (value is IEnumerable<object> enumerable)
            {
                return enumerable.Select(Convert.ToString).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }

            string text = Convert.ToString(value);
            return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text };
        }

        internal static Dictionary<string, object> ReadDictionary(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value))
            {
                return null;
            }

            return value as Dictionary<string, object>;
        }

        internal static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return "";
        }
    }
}
