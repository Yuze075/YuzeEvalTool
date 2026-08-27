#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YuzeToolkit.Eval;

namespace YuzeToolkit.Agent
{
    internal static class AgentJson
    {
        public static Dictionary<string, object?> Object(params (string Key, object? Value)[] entries) =>
            EvalData.Obj(entries);

        public static List<object?> Array(params object?[] entries) => EvalData.Arr(entries);

        public static Dictionary<string, object?> ParseObject(string json)
            => EvalData.AsObject(EvalJson.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json))
               ?? throw new FormatException("Expected a JSON object.");

        public static Dictionary<string, object?>? GetObject(Dictionary<string, object?> value, string key)
        {
            return value.TryGetValue(key, out var raw) ? EvalData.AsObject(raw) : null;
        }

        public static Dictionary<string, object?>? GetOptionalObject(
            Dictionary<string, object?> value,
            string key)
        {
            if (!value.TryGetValue(key, out var raw) || raw == null) return null;
            return EvalData.AsObject(raw) ??
                   throw new FormatException($"JSON property '{key}' must be an object or null.");
        }

        public static List<object?>? GetArray(Dictionary<string, object?> value, string key)
        {
            return value.TryGetValue(key, out var raw) ? EvalData.AsArray(raw) : null;
        }

        public static string GetString(Dictionary<string, object?> value, string key, string fallback = "") =>
            EvalData.GetString(value, key) ?? fallback;

        public static long GetLong(Dictionary<string, object?> value, string key, long fallback = 0)
        {
            if (!value.TryGetValue(key, out var raw) || raw == null) return fallback;
            return raw switch
            {
                long longValue => longValue,
                int intValue => intValue,
                double doubleValue => checked((long)doubleValue),
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => fallback
            };
        }

        public static int GetSchemaVersion(Dictionary<string, object?> value, int fallback = 1)
        {
            if (!value.TryGetValue("schemaVersion", out var raw)) return fallback;
            var version = raw switch
            {
                int intValue => intValue,
                long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
                double doubleValue when !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue) &&
                                        doubleValue >= int.MinValue && doubleValue <= int.MaxValue &&
                                        Math.Truncate(doubleValue) == doubleValue => (int)doubleValue,
                _ => throw new FormatException("JSON property 'schemaVersion' must be a positive integer.")
            };
            if (version < 1)
                throw new FormatException("JSON property 'schemaVersion' must be a positive integer.");
            return version;
        }

        public static DateTime GetDateTime(Dictionary<string, object?> value, string key, DateTime fallback)
        {
            var text = GetString(value, key);
            return DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : fallback;
        }

        public static TEnum GetEnum<TEnum>(Dictionary<string, object?> value, string key, TEnum fallback)
            where TEnum : struct, Enum
        {
            if (!value.TryGetValue(key, out var raw) || raw == null) return fallback;
            if (raw is string text && Enum.TryParse<TEnum>(text, true, out var parsed) &&
                Enum.IsDefined(typeof(TEnum), parsed)) return parsed;
            throw new FormatException($"JSON property '{key}' is not a defined {typeof(TEnum).Name} value.");
        }

        public static List<Dictionary<string, object?>> Objects(List<object?>? values)
        {
            return values == null
                ? new List<Dictionary<string, object?>>()
                : values.Select(EvalData.AsObject).Where(value => value != null).Select(value => value!).ToList();
        }

        /// <summary>
        /// Reads a persisted array whose elements are required to be JSON objects. Unlike
        /// <see cref="Objects"/>, this never drops malformed elements from user-owned documents.
        /// A missing property is represented by an empty list so schema migrations may still
        /// distinguish presence through Dictionary.ContainsKey.
        /// </summary>
        public static List<Dictionary<string, object?>> GetObjectArray(
            Dictionary<string, object?> value,
            string key)
        {
            if (!value.TryGetValue(key, out var raw))
                return new List<Dictionary<string, object?>>();
            var values = EvalData.AsArray(raw) ??
                         throw new FormatException($"JSON property '{key}' must be an array.");
            var result = new List<Dictionary<string, object?>>(values.Count);
            for (var index = 0; index < values.Count; index++)
            {
                var item = EvalData.AsObject(values[index]);
                if (item == null)
                    throw new FormatException($"JSON property '{key}[{index}]' must be an object.");
                result.Add(item);
            }
            return result;
        }

        public static string Stringify(object? value) => EvalJson.Stringify(value);

        public static string Utc(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
