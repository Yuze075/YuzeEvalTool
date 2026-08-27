#nullable enable
using System;
using System.Globalization;
using UnityEngine;

namespace YuzeToolkit.Agent
{
    internal static class DebugToolUtility
    {
        public static string FormatValue(object? value)
        {
            return value switch
            {
                null => "null",
                float f => f.ToString("0.###", CultureInfo.InvariantCulture),
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                Vector2 v => $"({v.x:0.###}, {v.y:0.###})",
                Vector3 v => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})",
                Vector4 v => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###}, {v.w:0.###})",
                Color c => $"RGBA({c.r:0.###}, {c.g:0.###}, {c.b:0.###}, {c.a:0.###})",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };
        }

        public static string FormatNumber<TValue>(string format, TValue value)
        {
            if (string.IsNullOrWhiteSpace(format))
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

            try
            {
                if (format.IndexOf("{0", StringComparison.Ordinal) >= 0)
                    return string.Format(CultureInfo.InvariantCulture, format, value);

                return value is IFormattable formattable
                    ? formattable.ToString(format, CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch (FormatException)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }
}
