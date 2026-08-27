#nullable enable
using System.Collections.Generic;
using YuzeToolkit.LitJson;

namespace YuzeToolkit.Eval
{
    public static class EvalJson
    {
        public static object? Parse(string json)
        {
            try
            {
                return ConvertJsonData(JsonMapper.ToObject(json));
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
                return JsonMapper.ToJson(value);
            }
            catch (JsonException exception)
            {
                throw new System.FormatException("Unable to serialize JSON.", exception);
            }
        }

        private static object? ConvertJsonData(JsonData? data)
        {
            if (data == null) return null;

            if (data.IsObject)
            {
                var result = new Dictionary<string, object?>(System.StringComparer.Ordinal);
                foreach (var key in data.Keys)
                    result[key] = ConvertJsonData(data[key]);
                return result;
            }

            if (data.IsArray)
            {
                var result = new List<object?>(data.Count);
                for (var index = 0; index < data.Count; index++)
                    result.Add(ConvertJsonData(data[index]));
                return result;
            }

            var wrapper = (IJsonWrapper)data;
            if (data.IsString) return wrapper.GetString();
            if (data.IsBoolean) return wrapper.GetBoolean();
            if (data.IsInt) return wrapper.GetInt();
            if (data.IsLong) return wrapper.GetLong();
            if (data.IsDouble) return wrapper.GetDouble();
            return null;
        }
    }
}
