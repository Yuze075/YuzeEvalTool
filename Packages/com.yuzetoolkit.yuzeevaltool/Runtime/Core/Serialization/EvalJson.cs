#nullable enable
using System.Collections.Generic;
using YuzeToolkit.Json;

namespace YuzeToolkit.Eval
{
    public static class EvalJson
    {
        public static object? Parse(string json)
        {
            try
            {
                return ConvertJsonData(YuzeJson.Parse(json));
            }
            catch (JsonException exception)
            {
                throw new System.FormatException("Invalid JSON.", exception);
            }
        }

        public static string Stringify(object? value)
        {
            try
            {
                return YuzeJson.Serialize(value);
            }
            catch (JsonException exception)
            {
                throw new System.FormatException("Unable to serialize JSON.", exception);
            }
        }

        private static object? ConvertJsonData(JsonData data)
        {
            if (data.IsNull) return null;

            if (data.IsObject)
            {
                var result = new Dictionary<string, object?>(System.StringComparer.Ordinal);
                foreach (var property in data.Properties)
                    result[property.Key] = ConvertJsonData(property.Value);
                return result;
            }

            if (data.IsArray)
            {
                var result = new List<object?>(data.Count);
                for (var index = 0; index < data.Count; index++)
                    result.Add(ConvertJsonData(data[index]));
                return result;
            }

            if (data.IsString) return data.AsString();
            if (data.IsBoolean) return data.AsBoolean();
            if (!data.IsNumber) throw new System.FormatException($"Unsupported JSON node type {data.Type}.");

            var number = data.AsNumber();
            if (number.Kind == JsonNumberKind.Int64 && number.TryGetInt64(out var signed))
                return signed is >= int.MinValue and <= int.MaxValue ? (object)(int)signed : signed;
            if (number.Kind == JsonNumberKind.UInt64 && number.TryGetUInt64(out var unsigned)) return unsigned;
            if (number.Kind is JsonNumberKind.Decimal or JsonNumberKind.Double &&
                number.TryGetDouble(out var floating)) return floating;
            throw new System.FormatException("JSON number cannot be represented by a CLR primitive.");
        }
    }
}
