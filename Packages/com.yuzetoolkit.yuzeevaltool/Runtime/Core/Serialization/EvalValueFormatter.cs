#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YuzeToolkit.Eval
{
    [UnityEngine.Scripting.Preserve]
    public static class EvalValueFormatter
    {
        [UnityEngine.Scripting.Preserve]
        public static object? Format(object? value, string mode = "default", int depth = 4)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "default" : mode;
            return mode switch
            {
                "name" => GetName(value),
                "path" => GetPath(value),
                "text" => ToText(value, depth),
                "json" => ToJsonFriendly(value, depth),
                "yaml" => ToYaml(ToJsonFriendly(value, depth)),
                "summary" => ToJsonFriendly(value, depth),
                _ => ToJsonFriendly(value, depth)
            };
        }

        [UnityEngine.Scripting.Preserve]
        public static string ToJson(object? value, string mode = "default", int depth = 4) =>
            EvalJson.Stringify(Format(value, mode, depth));

        [UnityEngine.Scripting.Preserve]
        public static string ToEvalText(object? value, int depth = 4) =>
            EvalJson.Stringify(ToJsonFriendly(value, depth));

        [UnityEngine.Scripting.Preserve]
        public static Dictionary<string, object?> Describe(object? value, int depth = 4)
        {
            var formatted = ToJsonFriendly(value, depth);
            return formatted as Dictionary<string, object?> ?? EvalData.Obj(("value", formatted));
        }

        [UnityEngine.Scripting.Preserve]
        public static object? FromJson(string json) =>
            EvalJson.Parse(string.IsNullOrWhiteSpace(json) ? "null" : json);

        private static object? ToJsonFriendly(object? value, int depth)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return ToJsonFriendly(value, depth, visited);
        }

        private static object? ToJsonFriendly(object? value, int depth, HashSet<object> visited)
        {
            if (value == null) return null;
            if (value is string or bool or byte or short or int or long or float or double or decimal) return value;
            if (value is DateTime dateTime) return dateTime.ToString("O");
            if (value is Guid guid) return guid.ToString("D");
            if (value is Enum e) return e.ToString();
            if (value is Type type) return type.FullName ?? type.Name;
            if (value is Vector2 v2) return ToolUtilities.Vector2ToObject(v2);
            if (value is Vector3 v3) return ToolUtilities.Vector3ToObject(v3);
            if (value is Color color) return ToolUtilities.ColorToObject(color);
            if (value is GameObject go) return ToolUtilities.SummarizeGameObject(go);
            if (value is Component component)
                return EvalData.Obj(
                    ("component", ToolUtilities.SummarizeComponent(component)),
                    ("gameObject", ToolUtilities.SummarizeGameObject(component.gameObject, false)));
            if (value is IDictionary dictionary)
            {
                if (depth < 0)
                    return TryFormatScalarDictionary(dictionary, out var scalarDictionary)
                        ? scalarDictionary
                        : FormatDepthLimit(value, dictionary.Count);
                if (!TryEnterReference(value, visited, out var cycle)) return cycle;

                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                    result[Convert.ToString(entry.Key) ?? string.Empty] =
                        ToJsonFriendly(entry.Value, depth - 1, visited);
                return result;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                if (depth < 0) return FormatDepthLimit(value, value is ICollection collection ? collection.Count : -1);
                if (!TryEnterReference(value, visited, out var cycle)) return cycle;

                var result = new List<object?>();
                foreach (var item in enumerable)
                    result.Add(ToJsonFriendly(item, depth - 1, visited));
                return result;
            }

            if (value is UnityEngine.Object unityObject) return SummarizeUnityObject(unityObject);
            if (depth <= 0) return ToText(value, 0);
            if (!TryEnterReference(value, visited, out var customObjectCycle)) return customObjectCycle;

            return SummarizeCustomObject(value, depth, visited);
        }

        private static bool TryEnterReference(object value, HashSet<object> visited, out string cycle)
        {
            cycle = string.Empty;
            if (value.GetType().IsValueType) return true;
            if (visited.Add(value)) return true;

            cycle = $"<cycle:{value.GetType().Name}>";
            return false;
        }

        private static string FormatDepthLimit(object value, int count)
        {
            var countText = count >= 0 ? $", count={count}" : string.Empty;
            return $"<max-depth:{value.GetType().Name}{countText}>";
        }

        private static bool TryFormatScalarDictionary(IDictionary dictionary, out Dictionary<string, object?> result)
        {
            result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!TryFormatScalarValue(entry.Value, out var formatted))
                {
                    result.Clear();
                    return false;
                }

                result[Convert.ToString(entry.Key) ?? string.Empty] = formatted;
            }

            return true;
        }

        private static bool TryFormatScalarValue(object? value, out object? formatted)
        {
            formatted = value;
            switch (value)
            {
                case null:
                    return true;
                case string or bool or byte or short or int or long or float or double or decimal:
                    return true;
                case DateTime dateTime:
                    formatted = dateTime.ToString("O");
                    return true;
                case Guid guid:
                    formatted = guid.ToString("D");
                    return true;
                case Enum e:
                    formatted = e.ToString();
                    return true;
                case Type type:
                    formatted = type.FullName ?? type.Name;
                    return true;
                default:
                    formatted = null;
                    return false;
            }
        }

        private static Dictionary<string, object?> SummarizeUnityObject(UnityEngine.Object obj)
        {
            var result = EvalData.Obj(
                ("name", obj.name),
                ("type", obj.GetType().FullName ?? obj.GetType().Name),
                ("instanceId", obj.GetInstanceID()));
#if UNITY_EDITOR
            var assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                result["assetPath"] = assetPath;
                result["guid"] = AssetDatabase.AssetPathToGUID(assetPath);
            }
#endif
            return result;
        }

        private static Dictionary<string, object?> SummarizeCustomObject(object value, int depth,
            HashSet<object> visited)
        {
            var type = value.GetType();
            var result = EvalData.Obj(
                ("type", type.FullName ?? type.Name),
                ("string", value.ToString() ?? string.Empty)
            );

            var members = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance |
                                                        System.Reflection.BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                try
                {
                    members[property.Name] = ToJsonFriendly(property.GetValue(value), depth - 1, visited);
                }
                catch
                {
                    members[property.Name] = "<unreadable>";
                }
            }

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
                                                 System.Reflection.BindingFlags.Public))
            {
                try
                {
                    members[field.Name] = ToJsonFriendly(field.GetValue(value), depth - 1, visited);
                }
                catch
                {
                    members[field.Name] = "<unreadable>";
                }
            }

            result["members"] = members;
            return result;
        }

        private static string GetName(object? value)
        {
            if (value == null) return string.Empty;
            if (EvalData.AsObject(value) is { } obj)
            {
                var name = EvalData.GetString(obj, "name");
                if (!string.IsNullOrWhiteSpace(name)) return name!;
                var type = EvalData.GetString(obj, "type");
                if (!string.IsNullOrWhiteSpace(type)) return type!;
            }

            return value switch
            {
                UnityEngine.Object unityObj => unityObj.name,
                Type type => type.FullName ?? type.Name,
                _ => value.ToString() ?? string.Empty
            };
        }

        private static string GetPath(object? value)
        {
            if (value == null) return string.Empty;
            if (EvalData.AsObject(value) is { } selector)
            {
                var path = EvalData.GetString(selector, "path");
                if (!string.IsNullOrWhiteSpace(path)) return path!;
                var assetPath = EvalData.GetString(selector, "assetPath");
                if (!string.IsNullOrWhiteSpace(assetPath)) return assetPath!;
            }

            if (value is GameObject go) return ToolUtilities.GetPath(go);
            if (value is Component component) return ToolUtilities.GetPath(component.gameObject);
#if UNITY_EDITOR
            if (value is UnityEngine.Object obj)
            {
                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrWhiteSpace(assetPath)) return assetPath;
            }
#endif
            return value.ToString() ?? string.Empty;
        }

        private static string ToText(object? value, int depth)
        {
            if (value == null) return "null";
            if (value is string text) return text;
            if (value is UnityEngine.Object) return EvalJson.Stringify(ToJsonFriendly(value, depth));
            return value.ToString() ?? string.Empty;
        }

        private static string ToYaml(object? value)
        {
            var lines = new List<string>();
            AppendYaml(lines, value, 0, "-");
            return string.Join("\n", lines);
        }

        private static void AppendYaml(List<string> lines, object? value, int indent, string listPrefix)
        {
            var pad = new string(' ', indent);
            switch (value)
            {
                case null:
                    lines.Add(pad + "null");
                    break;
                case IDictionary<string, object?> dictionary:
                    foreach (var pair in dictionary)
                    {
                        if (IsScalar(pair.Value))
                            lines.Add($"{pad}{pair.Key}: {ScalarToString(pair.Value)}");
                        else
                        {
                            lines.Add($"{pad}{pair.Key}:");
                            AppendYaml(lines, pair.Value, indent + 2, "-");
                        }
                    }

                    break;
                case IEnumerable enumerable when value is not string:
                    foreach (var item in enumerable)
                    {
                        if (IsScalar(item))
                            lines.Add($"{pad}{listPrefix} {ScalarToString(item)}");
                        else
                        {
                            lines.Add($"{pad}{listPrefix}");
                            AppendYaml(lines, item, indent + 2, "-");
                        }
                    }

                    break;
                default:
                    lines.Add(pad + ScalarToString(value));
                    break;
            }
        }

        private static bool IsScalar(object? value) =>
            value == null || value is string or bool or int or long or float or double or decimal;

        private static string ScalarToString(object? value) =>
            value == null
                ? "null"
                : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
