#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace YuzeToolkit
{
    public static class EvalData
    {
        public static Dictionary<string, object?> Obj(params (string Key, object? Value)[] values)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in values)
                result[key] = value;
            return result;
        }

        public static List<object?> Arr(params object?[] values) => new(values);

        public static Dictionary<string, object?>? AsObject(object? value) => value as Dictionary<string, object?>;

        public static List<object?>? AsArray(object? value) => value as List<object?>;

        public static string? GetString(Dictionary<string, object?> obj, string key)
        {
            if (!obj.TryGetValue(key, out var value) || value == null) return null;
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static int GetInt(Dictionary<string, object?> obj, string key, int defaultValue = 0)
        {
            if (!obj.TryGetValue(key, out var value) || value == null) return defaultValue;
            return value switch
            {
                int v => v,
                long v => checked((int)v),
                double v => checked((int)v),
                float v => checked((int)v),
                string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => defaultValue
            };
        }

        public static float GetFloat(Dictionary<string, object?> obj, string key, float defaultValue = 0f)
        {
            if (!obj.TryGetValue(key, out var value) || value == null) return defaultValue;
            return value switch
            {
                float v => v,
                double v => (float)v,
                int v => v,
                long v => v,
                string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => defaultValue
            };
        }

        public static bool GetBool(Dictionary<string, object?> obj, string key, bool defaultValue = false)
        {
            if (!obj.TryGetValue(key, out var value) || value == null) return defaultValue;
            return value switch
            {
                bool v => v,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => defaultValue
            };
        }
    }
}
