#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YuzeToolkit
{
    [EvalTool("Serialized", "SerializedObject and Inspector property reads/writes.")]
    public sealed partial class SerializedTool
    {
        [EvalFunction("Read serialized properties.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> get(object target, string propertyPath = "", int limit = 200)
        {
            var obj = ResolveUnityObject(target);
            if (obj == null) throw new InvalidOperationException("Target UnityEngine.Object was not found.");
            var serialized = new SerializedObject(obj);
            if (string.IsNullOrWhiteSpace(propertyPath))
            {
                limit = Math.Max(1, limit);
                var props = new List<object?>();
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                while (props.Count < limit && iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    props.Add(SummarizeProperty(iterator));
                }
                return EvalData.Obj(("target", SummarizeObject(obj)), ("count", props.Count), ("limit", limit), ("properties", props));
            }

            var prop = serialized.FindProperty(propertyPath);
            if (prop == null) throw new InvalidOperationException($"Serialized property '{propertyPath}' was not found.");
            return SummarizeProperty(prop);
        }

        internal static UnityEngine.Object? ResolveUnityObject(object? target)
        {
            if (target is UnityEngine.Object unityObject) return unityObject;
            if (target is string path) return AssetDatabase.LoadMainAssetAtPath(path) ?? ToolUtilities.ResolveGameObject(path);
            if (target is int id) return EditorUtility.InstanceIDToObject(id);
            if (target is long longId) return EditorUtility.InstanceIDToObject(checked((int)longId));
            if (EvalData.AsObject(target) is { } selector)
            {
                var assetPath = EvalData.GetString(selector, "assetPath");
                if (!string.IsNullOrWhiteSpace(assetPath)) return AssetDatabase.LoadMainAssetAtPath(assetPath);
                var guid = EvalData.GetString(selector, "guid");
                if (!string.IsNullOrWhiteSpace(guid)) return AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                var instanceId = EvalData.GetInt(selector, "instanceId", 0);
                if (instanceId != 0) return EditorUtility.InstanceIDToObject(instanceId);
                return ToolUtilities.ResolveGameObject(selector);
            }
            return ToolUtilities.ResolveGameObject(target);
        }

        internal static Dictionary<string, object?> SummarizeObject(UnityEngine.Object obj) =>
            EvalData.Obj(("name", obj.name), ("type", obj.GetType().FullName ?? obj.GetType().Name), ("instanceId", obj.GetInstanceID()), ("assetPath", AssetDatabase.GetAssetPath(obj)));

        internal static Dictionary<string, object?> SummarizeProperty(SerializedProperty property)
        {
            return EvalData.Obj(
                ("propertyPath", property.propertyPath),
                ("displayName", property.displayName),
                ("type", property.propertyType.ToString()),
                ("isArray", property.isArray),
                ("value", GetPropertyValue(property)));
        }

        private static object? GetPropertyValue(SerializedProperty property)
        {
            return property.propertyType switch
            {
                SerializedPropertyType.Integer => property.intValue,
                SerializedPropertyType.Boolean => property.boolValue,
                SerializedPropertyType.Float => property.floatValue,
                SerializedPropertyType.String => property.stringValue,
                SerializedPropertyType.Color => ToolUtilities.ColorToObject(property.colorValue),
                SerializedPropertyType.ObjectReference => property.objectReferenceValue != null ? SummarizeObject(property.objectReferenceValue) : null,
                SerializedPropertyType.Enum => property.enumDisplayNames.Length > property.enumValueIndex ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex,
                SerializedPropertyType.Vector2 => ToolUtilities.Vector2ToObject(property.vector2Value),
                SerializedPropertyType.Vector3 => ToolUtilities.Vector3ToObject(property.vector3Value),
                SerializedPropertyType.Vector4 => ToolUtilities.Vector4ToObject(property.vector4Value),
                SerializedPropertyType.Quaternion => ToolUtilities.QuaternionToObject(property.quaternionValue),
                SerializedPropertyType.Vector2Int => EvalData.Obj(("x", property.vector2IntValue.x), ("y", property.vector2IntValue.y)),
                SerializedPropertyType.Vector3Int => EvalData.Obj(("x", property.vector3IntValue.x), ("y", property.vector3IntValue.y), ("z", property.vector3IntValue.z)),
                SerializedPropertyType.Rect => ToolUtilities.RectToObject(property.rectValue),
                SerializedPropertyType.RectInt => ToolUtilities.RectIntToObject(property.rectIntValue),
                SerializedPropertyType.Bounds => ToolUtilities.BoundsToObject(property.boundsValue),
                SerializedPropertyType.BoundsInt => ToolUtilities.BoundsIntToObject(property.boundsIntValue),
                SerializedPropertyType.AnimationCurve => SummarizeAnimationCurve(property.animationCurveValue),
                _ => property.hasVisibleChildren ? "(children)" : property.stringValue
            };
        }

        private static Dictionary<string, object?> SummarizeAnimationCurve(AnimationCurve curve)
        {
            var keys = new List<object?>();
            foreach (var key in curve.keys)
            {
                keys.Add(EvalData.Obj(
                    ("time", key.time),
                    ("value", key.value),
                    ("inTangent", key.inTangent),
                    ("outTangent", key.outTangent),
                    ("inWeight", key.inWeight),
                    ("outWeight", key.outWeight),
                    ("weightedMode", key.weightedMode.ToString())));
            }

            return EvalData.Obj(
                ("preWrapMode", curve.preWrapMode.ToString()),
                ("postWrapMode", curve.postWrapMode.ToString()),
                ("keys", keys));
        }

        [EvalFunction("Set one serialized property.", Safety = EvalToolSafety.MutatesScene | EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> set(object target, string propertyPath, object? value, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Serialized property writes require confirm: true.");
            var obj = ResolveUnityObject(target);
            if (obj == null) throw new InvalidOperationException("Target UnityEngine.Object was not found.");
            var serialized = new SerializedObject(obj);
            var prop = serialized.FindProperty(propertyPath);
            if (prop == null) throw new InvalidOperationException($"Serialized property '{propertyPath}' was not found.");
            ToolUtilities.RecordUndo(obj, "MCP Set Serialized Property");
            SetPropertyValue(prop, value);
            serialized.ApplyModifiedProperties();
            ToolUtilities.MarkDirty(obj);
            return SummarizeProperty(prop);
        }

        internal static void SetPropertyValue(SerializedProperty property, object? value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    property.intValue = value is int i ? i : Convert.ToInt32(value);
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = value is bool b ? b : Convert.ToBoolean(value);
                    break;
                case SerializedPropertyType.Float:
                    property.floatValue = ToolUtilities.ToFloat(value, property.floatValue);
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = Convert.ToString(value) ?? string.Empty;
                    break;
                case SerializedPropertyType.Color:
                    if (EvalData.AsObject(value) is { } color)
                    {
                        property.colorValue = new Color(
                            EvalData.GetFloat(color, "r", property.colorValue.r),
                            EvalData.GetFloat(color, "g", property.colorValue.g),
                            EvalData.GetFloat(color, "b", property.colorValue.b),
                            EvalData.GetFloat(color, "a", property.colorValue.a));
                    }
                    break;
                case SerializedPropertyType.Vector2:
                    var vector = ToolUtilities.ToVector3(value, property.vector2Value);
                    property.vector2Value = new Vector2(vector.x, vector.y);
                    break;
                case SerializedPropertyType.Vector3:
                    property.vector3Value = ToolUtilities.ToVector3(value, property.vector3Value);
                    break;
                case SerializedPropertyType.Vector4:
                    property.vector4Value = ToolUtilities.ToVector4(value, property.vector4Value);
                    break;
                case SerializedPropertyType.Quaternion:
                    property.quaternionValue = ToolUtilities.ToQuaternion(value, property.quaternionValue);
                    break;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = ResolveObjectReference(value);
                    break;
                case SerializedPropertyType.Enum:
                    property.enumValueIndex = ResolveEnumIndex(property, value);
                    break;
                case SerializedPropertyType.Vector2Int:
                    var vector2Int = ToolUtilities.ToVector3(value, new Vector3(property.vector2IntValue.x, property.vector2IntValue.y, 0));
                    property.vector2IntValue = new Vector2Int(Mathf.RoundToInt(vector2Int.x), Mathf.RoundToInt(vector2Int.y));
                    break;
                case SerializedPropertyType.Vector3Int:
                    var vector3Int = ToolUtilities.ToVector3(value, new Vector3(property.vector3IntValue.x, property.vector3IntValue.y, property.vector3IntValue.z));
                    property.vector3IntValue = new Vector3Int(Mathf.RoundToInt(vector3Int.x), Mathf.RoundToInt(vector3Int.y), Mathf.RoundToInt(vector3Int.z));
                    break;
                case SerializedPropertyType.Rect:
                    property.rectValue = ToRect(value, property.rectValue);
                    break;
                case SerializedPropertyType.RectInt:
                    var rect = ToRect(value, new Rect(property.rectIntValue.x, property.rectIntValue.y, property.rectIntValue.width, property.rectIntValue.height));
                    property.rectIntValue = new RectInt(Mathf.RoundToInt(rect.x), Mathf.RoundToInt(rect.y), Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height));
                    break;
                case SerializedPropertyType.Bounds:
                    if (EvalData.AsObject(value) is { } bounds)
                        property.boundsValue = new Bounds(
                            ToolUtilities.ToVector3(bounds.TryGetValue("center", out var center) ? center : null, property.boundsValue.center),
                            ToolUtilities.ToVector3(bounds.TryGetValue("size", out var size) ? size : null, property.boundsValue.size));
                    break;
                case SerializedPropertyType.BoundsInt:
                    if (EvalData.AsObject(value) is { } boundsInt)
                    {
                        var position = ToolUtilities.ToVector3(
                            boundsInt.TryGetValue("position", out var positionValue) ? positionValue : null,
                            property.boundsIntValue.position);
                        var size = ToolUtilities.ToVector3(
                            boundsInt.TryGetValue("size", out var sizeValue) ? sizeValue : null,
                            property.boundsIntValue.size);
                        property.boundsIntValue = new BoundsInt(
                            Mathf.RoundToInt(position.x),
                            Mathf.RoundToInt(position.y),
                            Mathf.RoundToInt(position.z),
                            Mathf.RoundToInt(size.x),
                            Mathf.RoundToInt(size.y),
                            Mathf.RoundToInt(size.z));
                    }
                    break;
                case SerializedPropertyType.AnimationCurve:
                    property.animationCurveValue = ToAnimationCurve(value, property.animationCurveValue);
                    break;
                default:
                    throw new InvalidOperationException($"SerializedPropertyType '{property.propertyType}' is not supported by serialized.set yet.");
            }
        }

        private static int ResolveEnumIndex(SerializedProperty property, object? value)
        {
            if (value is int intIndex)
                return ValidateEnumIndex(property, intIndex);
            if (value is long longIndex)
                return ValidateEnumIndex(property, checked((int)longIndex));

            var text = Convert.ToString(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var displayIndex = FindEnumIndex(property.enumDisplayNames, text!);
                if (displayIndex >= 0) return displayIndex;

                var nameIndex = FindEnumIndex(property.enumNames, text!);
                if (nameIndex >= 0) return nameIndex;

                if (int.TryParse(text, out var parsedIndex))
                    return ValidateEnumIndex(property, parsedIndex);
            }

            throw new InvalidOperationException(
                $"Enum value '{text}' was not found for serialized property '{property.propertyPath}'.");
        }

        private static int FindEnumIndex(string[] values, string text)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], text, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int ValidateEnumIndex(SerializedProperty property, int index)
        {
            if (index < 0 || index >= property.enumNames.Length)
                throw new InvalidOperationException(
                    $"Enum index {index} is outside serialized property '{property.propertyPath}'.");
            return index;
        }

        private static Rect ToRect(object? value, Rect defaultValue)
        {
            if (EvalData.AsObject(value) is { } rect)
                return new Rect(
                    EvalData.GetFloat(rect, "x", defaultValue.x),
                    EvalData.GetFloat(rect, "y", defaultValue.y),
                    EvalData.GetFloat(rect, "width", defaultValue.width),
                    EvalData.GetFloat(rect, "height", defaultValue.height));
            return defaultValue;
        }

        private static AnimationCurve ToAnimationCurve(object? value, AnimationCurve defaultValue)
        {
            if (value is AnimationCurve curve) return curve;
            if (EvalData.AsObject(value) is not { } curveData) return defaultValue;

            var keys = new List<Keyframe>();
            if (EvalData.AsArray(curveData.TryGetValue("keys", out var keysValue) ? keysValue : null) is { } keyArray)
            {
                foreach (var keyValue in keyArray)
                {
                    if (EvalData.AsObject(keyValue) is not { } keyData) continue;
                    var key = new Keyframe(
                        EvalData.GetFloat(keyData, "time"),
                        EvalData.GetFloat(keyData, "value"),
                        EvalData.GetFloat(keyData, "inTangent"),
                        EvalData.GetFloat(keyData, "outTangent"))
                    {
                        inWeight = EvalData.GetFloat(keyData, "inWeight"),
                        outWeight = EvalData.GetFloat(keyData, "outWeight")
                    };
                    var weightedMode = EvalData.GetString(keyData, "weightedMode");
                    if (!string.IsNullOrWhiteSpace(weightedMode) &&
                        Enum.TryParse<WeightedMode>(weightedMode, true, out var parsedWeightedMode))
                        key.weightedMode = parsedWeightedMode;
                    keys.Add(key);
                }
            }

            var result = new AnimationCurve(keys.ToArray());
            var preWrapMode = EvalData.GetString(curveData, "preWrapMode");
            if (!string.IsNullOrWhiteSpace(preWrapMode) &&
                Enum.TryParse<WrapMode>(preWrapMode, true, out var parsedPreWrapMode))
                result.preWrapMode = parsedPreWrapMode;
            var postWrapMode = EvalData.GetString(curveData, "postWrapMode");
            if (!string.IsNullOrWhiteSpace(postWrapMode) &&
                Enum.TryParse<WrapMode>(postWrapMode, true, out var parsedPostWrapMode))
                result.postWrapMode = parsedPostWrapMode;
            return result;
        }

        private static UnityEngine.Object? ResolveObjectReference(object? value)
        {
            if (value == null) return null;
            if (value is UnityEngine.Object obj) return obj;
            if (value is int id) return EditorUtility.InstanceIDToObject(id);
            if (value is long longId) return EditorUtility.InstanceIDToObject(checked((int)longId));
            if (value is string path) return AssetDatabase.LoadMainAssetAtPath(path) ?? ToolUtilities.ResolveGameObject(path);
            return ResolveUnityObject(value);
        }

        [EvalFunction("Set multiple serialized properties from an array of {propertyPath,value} or an object map of propertyPath -> value.", Safety = EvalToolSafety.MutatesScene | EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> setMany(object target, object changes, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Serialized property writes require confirm: true.");
            var obj = ResolveUnityObject(target);
            if (obj == null) throw new InvalidOperationException("Target UnityEngine.Object was not found.");
            var changeList = ToolUtilities.GetPropertyChanges(changes);
            if (changeList.Count == 0) throw new InvalidOperationException("Argument 'changes' must contain at least one property update.");

            var serialized = new SerializedObject(obj);
            ToolUtilities.RecordUndo(obj, "MCP Set Serialized Properties");
            var results = new List<object?>();
            foreach (var change in changeList)
            {
                var propertyPath = ToolUtilities.GetString(change, "propertyPath");
                var prop = serialized.FindProperty(propertyPath);
                if (prop == null) throw new InvalidOperationException($"Serialized property '{propertyPath}' was not found.");
                change.TryGetValue("value", out var value);
                SetPropertyValue(prop, value);
                results.Add(SummarizeProperty(prop));
            }

            serialized.ApplyModifiedProperties();
            ToolUtilities.MarkDirty(obj);
            return EvalData.Obj(("target", SummarizeObject(obj)), ("count", results.Count), ("properties", results));
        }

        [EvalFunction("Resize array property.", Safety = EvalToolSafety.MutatesScene | EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> resizeArray(object target, string propertyPath, int size, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Serialized array writes require confirm: true.");
            if (size < 0) throw new InvalidOperationException("Argument 'size' must be zero or greater.");
            var prop = ResolveArrayProperty(target, propertyPath, out var serialized, out var obj);
            ToolUtilities.RecordUndo(obj, "MCP Resize Serialized Array");
            prop.arraySize = size;
            serialized.ApplyModifiedProperties();
            ToolUtilities.MarkDirty(obj);
            return SummarizeProperty(prop);
        }

        [EvalFunction("Insert array element.", Safety = EvalToolSafety.MutatesScene | EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> insertArrayElement(object target, string propertyPath, int index = -1, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Serialized array writes require confirm: true.");
            var prop = ResolveArrayProperty(target, propertyPath, out var serialized, out var obj);
            if (index < 0 || index > prop.arraySize) index = prop.arraySize;
            ToolUtilities.RecordUndo(obj, "MCP Insert Serialized Array Element");
            prop.InsertArrayElementAtIndex(index);
            serialized.ApplyModifiedProperties();
            ToolUtilities.MarkDirty(obj);
            return SummarizeProperty(prop);
        }

        [EvalFunction("Delete array element.", Safety = EvalToolSafety.MutatesScene | EvalToolSafety.MutatesProject | EvalToolSafety.Destructive | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> deleteArrayElement(object target, string propertyPath, int index, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Serialized array writes require confirm: true.");
            var prop = ResolveArrayProperty(target, propertyPath, out var serialized, out var obj);
            if (index < 0 || index >= prop.arraySize) throw new InvalidOperationException($"Array index {index} is outside '{propertyPath}'.");
            ToolUtilities.RecordUndo(obj, "MCP Delete Serialized Array Element");
            var beforeSize = prop.arraySize;
            prop.DeleteArrayElementAtIndex(index);
            if (prop.arraySize == beforeSize && index < prop.arraySize)
                prop.DeleteArrayElementAtIndex(index);
            serialized.ApplyModifiedProperties();
            ToolUtilities.MarkDirty(obj);
            var summary = SummarizeProperty(prop);
            summary["beforeSize"] = beforeSize;
            summary["afterSize"] = prop.arraySize;
            return summary;
        }

        private static SerializedProperty ResolveArrayProperty(object target, string propertyPath, out SerializedObject serialized, out UnityEngine.Object obj)
        {
            obj = ResolveUnityObject(target) ?? throw new InvalidOperationException("Target UnityEngine.Object was not found.");
            serialized = new SerializedObject(obj);
            var prop = serialized.FindProperty(propertyPath);
            if (prop == null || !prop.isArray) throw new InvalidOperationException($"Serialized array property '{propertyPath}' was not found.");
            return prop;
        }
    }
}
