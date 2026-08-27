#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace YuzeToolkit.Eval
{
    public static class ToolUtilities
    {
        private static readonly Dictionary<string, Type?> TypeLookupCache = new(StringComparer.Ordinal);

        public static Dictionary<string, object?> ParseArgs(string json) =>
            EvalData.AsObject(EvalJson.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json)) ?? new Dictionary<string, object?>();

        public static string GetString(Dictionary<string, object?> args, string key, string defaultValue = "") =>
            EvalData.GetString(args, key) ?? defaultValue;

        public static List<object?> GetArray(Dictionary<string, object?> args, string key)
        {
            return args.TryGetValue(key, out var value) ? EvalData.AsArray(value) ?? new List<object?>() : new List<object?>();
        }

        public static List<Dictionary<string, object?>> GetPropertyChanges(object changes)
        {
            if (EvalData.AsArray(changes) is { } array)
            {
                return array
                    .Select(EvalData.AsObject)
                    .Where(change => change != null)
                    .Select(change => change!)
                    .ToList();
            }

            if (EvalData.AsObject(changes) is { } map)
            {
                if (map.ContainsKey("propertyPath") || map.ContainsKey("value"))
                    return new List<Dictionary<string, object?>> { map };

                return map
                    .Select(pair => EvalData.Obj(("propertyPath", pair.Key), ("value", pair.Value)))
                    .ToList();
            }

            return new List<Dictionary<string, object?>>();
        }

        public static string GetEnvironmentName() => Application.isEditor ? "Editor" : "Runtime";

        public static object GetEnvironmentObject() =>
            EvalData.Obj(
                ("name", GetEnvironmentName()),
                ("isEditor", Application.isEditor),
                ("isRuntime", !Application.isEditor));

        public static string GetProjectRoot()
        {
            return TrimTrailingSeparators(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
        }

        public static bool TryResolveProjectPath(string path, out string fullPath, out string projectRelativePath, out string error)
        {
            fullPath = string.Empty;
            projectRelativePath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Path is required.";
                return false;
            }

            var root = GetProjectRoot();
            var candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));
            candidate = TrimTrailingSeparators(candidate);

            var rootWithSeparator = EnsureTrailingSeparator(root);
            if (!candidate.Equals(root, PathComparison) &&
                !candidate.StartsWith(rootWithSeparator, PathComparison))
            {
                error = "Path must stay inside the Unity project.";
                return false;
            }

            fullPath = candidate;
            projectRelativePath = candidate.Equals(root, PathComparison)
                ? string.Empty
                : candidate.Substring(rootWithSeparator.Length)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
            return true;
        }

        public static void RecordUndo(UnityEngine.Object? obj, string name)
        {
#if UNITY_EDITOR
            if (obj == null || Application.isPlaying) return;
            Undo.RecordObject(obj, name);
#endif
        }

        public static void RegisterCreatedObjectUndo(GameObject? go, string name)
        {
#if UNITY_EDITOR
            if (go == null || Application.isPlaying) return;
            Undo.RegisterCreatedObjectUndo(go, name);
#endif
        }

        public static void MarkDirty(UnityEngine.Object? obj)
        {
#if UNITY_EDITOR
            if (obj == null || Application.isPlaying) return;
            EditorUtility.SetDirty(obj);
            if (obj is GameObject go && go.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(go.scene);
            else if (obj is Component component && component.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
#endif
        }

        public static void DestroyObject(UnityEngine.Object obj)
        {
#if UNITY_EDITOR
            if (Application.isEditor && !Application.isPlaying)
            {
                Undo.DestroyObjectImmediate(obj);
                return;
            }
#endif
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                return path;
            return path + Path.DirectorySeparatorChar;
        }

        private static string TrimTrailingSeparators(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static Type? FindType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            var normalized = typeName.Trim();
            if (TypeLookupCache.TryGetValue(normalized, out var cachedType))
                return cachedType;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            {
                var exact = assembly.GetType(normalized, false);
                if (exact != null)
                {
                    TypeLookupCache[normalized] = exact;
                    return exact;
                }

                foreach (var type in GetTypesSafe(assembly))
                {
                    if (type.FullName == normalized || type.Name == normalized)
                    {
                        TypeLookupCache[normalized] = type;
                        return type;
                    }
                }
            }

            TypeLookupCache[normalized] = null;
            return null;
        }

        public static IEnumerable<Type> GetTypesSafe(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes().Where(type => type != null)!;
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null)!;
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        public static GameObject? ResolveGameObject(Dictionary<string, object?> args, bool defaultIncludeInactive = true)
        {
            if (args.TryGetValue("target", out var target))
                return ResolveGameObject(target, defaultIncludeInactive);

            return ResolveGameObjectSelector(args, defaultIncludeInactive);
        }

        public static GameObject? ResolveGameObject(object? selectorValue, bool defaultIncludeInactive = true)
        {
            if (selectorValue is GameObject go)
                return go;
            if (selectorValue is Component component)
                return component.gameObject;
            if (selectorValue is UnityEngine.Object unityObject)
                return FindGameObjectByInstanceId(unityObject.GetInstanceID(), defaultIncludeInactive);
            if (selectorValue is string text)
                return ResolveGameObject(text, defaultIncludeInactive);
            if (selectorValue is int id)
                return FindGameObjectByInstanceId(id, defaultIncludeInactive);
            if (selectorValue is long longId)
                return FindGameObjectByInstanceId(checked((int)longId), defaultIncludeInactive);
            if (EvalData.AsObject(selectorValue) is { } obj)
                return ResolveGameObjectSelector(obj, defaultIncludeInactive);
            return null;
        }

        public static GameObject? ResolveGameObject(string nameOrPath, bool includeInactive)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
            var matches = nameOrPath.Contains('/')
                ? FindGameObjects("path", nameOrPath, includeInactive, 2)
                : FindGameObjects("name", nameOrPath, includeInactive, 2);
            return matches.Count == 1 ? matches[0] : null;
        }

        private static GameObject? ResolveGameObjectSelector(Dictionary<string, object?> selector, bool defaultIncludeInactive = true)
        {
            var includeInactive = EvalData.GetBool(selector, "includeInactive", defaultIncludeInactive);
            var instanceId = EvalData.GetInt(selector, "instanceId", 0);
            if (instanceId != 0)
                return FindGameObjectByInstanceId(instanceId, includeInactive);

            var path = EvalData.GetString(selector, "path");
            if (!string.IsNullOrWhiteSpace(path))
                return FindGameObjectByPath(path!, includeInactive);

            var name = EvalData.GetString(selector, "name");
            if (!string.IsNullOrWhiteSpace(name))
                return FindGameObjects("name", name!, includeInactive, 2).SingleOrDefault();

            return null;
        }

        public static List<GameObject> FindGameObjects(string by, string value, bool includeInactive, int limit = 100)
        {
            limit = Math.Max(1, limit);
            return by switch
            {
                "path" => FindGameObjectByPath(value, includeInactive) is { } go
                    ? new List<GameObject> { go }
                    : new List<GameObject>(),
                "tag" when !includeInactive => FindActiveGameObjectsByTag(value, limit),
                "tag" => throw new InvalidOperationException(
                    "Inactive tag search is not supported. Use active-only Objects.findByTag or narrow the query by path."),
                "component" => throw new InvalidOperationException(
                    "Component search moved to Runtime/Components.find(typeName, limit, includeInactive)."),
                _ => EnumerateLoadedSceneGameObjects(includeInactive)
                    .Where(go => go.name == value)
                    .Take(limit)
                    .ToList()
            };
        }

        public static List<GameObject> FindActiveGameObjectsByName(string name, int limit)
        {
            if (string.IsNullOrWhiteSpace(name)) return new List<GameObject>();
            limit = Math.Max(1, limit);
            return EnumerateLoadedSceneGameObjects(false)
                .Where(go => IsUsableSceneObject(go, false) && go.name == name)
                .Take(limit)
                .ToList();
        }

        public static GameObject? FindGameObjectByPath(string path, bool includeInactive)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return EnumerateLoadedSceneGameObjects(includeInactive)
                .FirstOrDefault(go => GetPath(go) == path);
        }

        public static List<GameObject> FindActiveGameObjectsByTag(string tag, int limit)
        {
            if (string.IsNullOrWhiteSpace(tag)) return new List<GameObject>();
            limit = Math.Max(1, limit);
            try
            {
                return GameObject.FindGameObjectsWithTag(tag)
                    .Where(go => IsUsableSceneObject(go, false))
                    .Take(limit)
                    .ToList();
            }
            catch (UnityException ex)
            {
                throw new InvalidOperationException($"Tag '{tag}' is not defined in Unity TagManager.", ex);
            }
        }

        public static List<Component> FindComponents(Type componentType, bool includeInactive, int limit)
        {
            if (!typeof(Component).IsAssignableFrom(componentType))
                throw new InvalidOperationException($"Type '{componentType.FullName}' is not a Unity Component.");

            limit = Math.Max(1, limit);
            var results = new List<Component>(limit);
            foreach (var go in EnumerateLoadedSceneGameObjects(includeInactive))
            {
                foreach (var component in go.GetComponents(componentType).OfType<Component>())
                {
                    if (component == null) continue;
                    results.Add(component);
                    if (results.Count >= limit) break;
                }

                if (results.Count >= limit) break;
            }

            return results;
        }

        private static IEnumerable<GameObject> EnumerateLoadedSceneGameObjects(bool includeInactive)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var go in EnumerateHierarchy(root, includeInactive))
                        yield return go;
                }
            }
        }

        private static IEnumerable<GameObject> EnumerateHierarchy(GameObject root, bool includeInactive)
        {
            if (root == null) yield break;

            var stack = new Stack<GameObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var go = stack.Pop();
                if (!IsUsableSceneObject(go, includeInactive))
                {
                    if (!includeInactive) continue;
                }
                else
                {
                    yield return go;
                }

                var transform = go.transform;
                for (var i = transform.childCount - 1; i >= 0; i--)
                    stack.Push(transform.GetChild(i).gameObject);
            }
        }

        public static bool IsUsableSceneObject(GameObject go, bool includeInactive)
        {
            if (go == null) return false;
            var scene = go.scene;
            if (!scene.IsValid() || !scene.isLoaded) return false;
            if ((go.hideFlags & HideFlags.HideAndDontSave) != 0) return false;
            return includeInactive || go.activeInHierarchy;
        }

        public static GameObject? FindGameObjectByInstanceId(int instanceId, bool includeInactive)
        {
#if UNITY_EDITOR
            if (EditorUtility.InstanceIDToObject(instanceId) is GameObject editorGo &&
                IsUsableSceneObject(editorGo, includeInactive))
                return editorGo;
            if (EditorUtility.InstanceIDToObject(instanceId) is Component editorComponent &&
                IsUsableSceneObject(editorComponent.gameObject, includeInactive))
                return editorComponent.gameObject;
#endif
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go.GetInstanceID() == instanceId && IsUsableSceneObject(go, includeInactive))
                    return go;
            }

            foreach (var component in Resources.FindObjectsOfTypeAll<Component>())
            {
                if (component == null || component.GetInstanceID() != instanceId) continue;
                var go = component.gameObject;
                if (IsUsableSceneObject(go, includeInactive))
                    return go;
            }

            return null;
        }

        public static string GetPath(GameObject go)
        {
            var names = new Stack<string>();
            var current = go.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        public static Dictionary<string, object?> SummarizeGameObject(GameObject go, bool includeComponents = true)
        {
            return EvalData.Obj(
                ("name", go.name),
                ("instanceId", go.GetInstanceID()),
                ("path", GetPath(go)),
                ("tag", go.tag),
                ("layer", go.layer),
                ("activeSelf", go.activeSelf),
                ("activeInHierarchy", go.activeInHierarchy),
                ("scene", EvalData.Obj(
                    ("name", go.scene.name),
                    ("path", go.scene.path),
                    ("handle", go.scene.handle)
                )),
                ("transform", EvalData.Obj(
                    ("position", Vector3ToObject(go.transform.position)),
                    ("localPosition", Vector3ToObject(go.transform.localPosition)),
                    ("rotationEuler", Vector3ToObject(go.transform.eulerAngles)),
                    ("localRotationEuler", Vector3ToObject(go.transform.localEulerAngles)),
                    ("localScale", Vector3ToObject(go.transform.localScale))
                )),
                ("components", includeComponents
                    ? go.GetComponents<Component>().Select((component, index) => (object?)SummarizeComponent(component, index)).ToList()
                    : new List<object?>())
            );
        }

        public static Dictionary<string, object?> SummarizeComponent(Component? component, int index = -1)
        {
            if (component == null)
                return EvalData.Obj(("missing", true), ("index", index));

            var type = component.GetType();
            return EvalData.Obj(
                ("type", type.FullName ?? type.Name),
                ("name", type.Name),
                ("instanceId", component.GetInstanceID()),
                ("index", index),
                ("enabled", component is Behaviour behaviour ? behaviour.enabled : null)
            );
        }

        public static Dictionary<string, object?> Vector3ToObject(Vector3 value) =>
            EvalData.Obj(("x", value.x), ("y", value.y), ("z", value.z));

        public static Dictionary<string, object?> Vector4ToObject(Vector4 value) =>
            EvalData.Obj(("x", value.x), ("y", value.y), ("z", value.z), ("w", value.w));

        public static Dictionary<string, object?> Vector2ToObject(Vector2 value) =>
            EvalData.Obj(("x", value.x), ("y", value.y));

        public static Dictionary<string, object?> QuaternionToObject(Quaternion value) =>
            EvalData.Obj(("x", value.x), ("y", value.y), ("z", value.z), ("w", value.w),
                ("eulerAngles", Vector3ToObject(value.eulerAngles)));

        public static Dictionary<string, object?> ColorToObject(Color value) =>
            EvalData.Obj(("r", value.r), ("g", value.g), ("b", value.b), ("a", value.a));

        public static Dictionary<string, object?> RectToObject(Rect value) =>
            EvalData.Obj(("x", value.x), ("y", value.y), ("width", value.width), ("height", value.height));

        public static Dictionary<string, object?> RectIntToObject(RectInt value) =>
            EvalData.Obj(("x", value.x), ("y", value.y), ("width", value.width), ("height", value.height));

        public static Dictionary<string, object?> BoundsToObject(Bounds value) =>
            EvalData.Obj(("center", Vector3ToObject(value.center)), ("size", Vector3ToObject(value.size)));

        public static Dictionary<string, object?> BoundsIntToObject(BoundsInt value) =>
            EvalData.Obj(
                ("position", EvalData.Obj(("x", value.position.x), ("y", value.position.y), ("z", value.position.z))),
                ("size", EvalData.Obj(("x", value.size.x), ("y", value.size.y), ("z", value.size.z))));

        public static Vector3 GetVector3(Dictionary<string, object?> args, string key, Vector3 defaultValue)
        {
            if (!args.TryGetValue(key, out var value)) return defaultValue;
            return ToVector3(value, defaultValue);
        }

        public static Vector3 ToVector3(object? value, Vector3 defaultValue)
        {
            if (EvalData.AsObject(value) is { } obj)
                return new Vector3(
                    EvalData.GetFloat(obj, "x", defaultValue.x),
                    EvalData.GetFloat(obj, "y", defaultValue.y),
                    EvalData.GetFloat(obj, "z", defaultValue.z));

            if (EvalData.AsArray(value) is { Count: >= 2 } arr)
                return new Vector3(ToFloat(arr[0], defaultValue.x), ToFloat(arr[1], defaultValue.y), arr.Count > 2 ? ToFloat(arr[2], defaultValue.z) : defaultValue.z);

            return defaultValue;
        }

        public static Vector4 ToVector4(object? value, Vector4 defaultValue)
        {
            if (EvalData.AsObject(value) is { } obj)
                return new Vector4(
                    EvalData.GetFloat(obj, "x", defaultValue.x),
                    EvalData.GetFloat(obj, "y", defaultValue.y),
                    EvalData.GetFloat(obj, "z", defaultValue.z),
                    EvalData.GetFloat(obj, "w", defaultValue.w));

            if (EvalData.AsArray(value) is { Count: >= 2 } arr)
                return new Vector4(
                    ToFloat(arr[0], defaultValue.x),
                    ToFloat(arr[1], defaultValue.y),
                    arr.Count > 2 ? ToFloat(arr[2], defaultValue.z) : defaultValue.z,
                    arr.Count > 3 ? ToFloat(arr[3], defaultValue.w) : defaultValue.w);

            return defaultValue;
        }

        public static Quaternion ToQuaternion(object? value, Quaternion defaultValue)
        {
            if (EvalData.AsObject(value) is { } obj)
            {
                if (obj.TryGetValue("eulerAngles", out var eulerValue))
                    return Quaternion.Euler(ToVector3(eulerValue, defaultValue.eulerAngles));
                return new Quaternion(
                    EvalData.GetFloat(obj, "x", defaultValue.x),
                    EvalData.GetFloat(obj, "y", defaultValue.y),
                    EvalData.GetFloat(obj, "z", defaultValue.z),
                    EvalData.GetFloat(obj, "w", defaultValue.w));
            }

            if (EvalData.AsArray(value) is { Count: >= 3 } arr)
            {
                if (arr.Count == 3)
                    return Quaternion.Euler(ToVector3(value, defaultValue.eulerAngles));
                return new Quaternion(
                    ToFloat(arr[0], defaultValue.x),
                    ToFloat(arr[1], defaultValue.y),
                    ToFloat(arr[2], defaultValue.z),
                    ToFloat(arr[3], defaultValue.w));
            }

            return defaultValue;
        }

        public static object? ConvertToType(object? value, Type targetType)
        {
            if (value == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            var nullable = Nullable.GetUnderlyingType(targetType);
            if (nullable != null)
                return ConvertToType(value, nullable);

            if (targetType == typeof(string))
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool))
                return value is bool b ? b : bool.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "false");
            if (targetType == typeof(int))
                return checked((int)ToDouble(value, 0));
            if (targetType == typeof(long))
                return checked((long)ToDouble(value, 0));
            if (targetType == typeof(float))
                return ToFloat(value, 0f);
            if (targetType == typeof(double))
                return ToDouble(value, 0d);
            if (targetType.IsEnum)
                return value is string s ? Enum.Parse(targetType, s, true) : Enum.ToObject(targetType, checked((int)ToDouble(value, 0)));
            if (targetType == typeof(Vector2))
            {
                var vector = ToVector3(value, Vector3.zero);
                return new Vector2(vector.x, vector.y);
            }
            if (targetType == typeof(Vector3))
                return ToVector3(value, Vector3.zero);
            if (targetType == typeof(Vector4))
                return ToVector4(value, Vector4.zero);
            if (targetType == typeof(Quaternion))
                return ToQuaternion(value, Quaternion.identity);
            if (targetType == typeof(Color) && EvalData.AsObject(value) is { } color)
                return new Color(
                    EvalData.GetFloat(color, "r", 1f),
                    EvalData.GetFloat(color, "g", 1f),
                    EvalData.GetFloat(color, "b", 1f),
                    EvalData.GetFloat(color, "a", 1f));
            if (targetType == typeof(Rect) && EvalData.AsObject(value) is { } rect)
                return new Rect(
                    EvalData.GetFloat(rect, "x"),
                    EvalData.GetFloat(rect, "y"),
                    EvalData.GetFloat(rect, "width"),
                    EvalData.GetFloat(rect, "height"));
            if (targetType == typeof(Bounds) && EvalData.AsObject(value) is { } bounds)
                return new Bounds(
                    ToVector3(bounds.TryGetValue("center", out var center) ? center : null, Vector3.zero),
                    ToVector3(bounds.TryGetValue("size", out var size) ? size : null, Vector3.zero));

            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                if (value is int id)
                {
#if UNITY_EDITOR
                    var editorObject = EditorUtility.InstanceIDToObject(id);
                    if (editorObject == null) return null;
                    if (targetType.IsInstanceOfType(editorObject)) return editorObject;
                    if (editorObject is GameObject editorGo)
                        return targetType == typeof(GameObject) ? editorGo : editorGo.GetComponent(targetType);
                    if (editorObject is Component editorComponent)
                        return targetType == typeof(GameObject) ? editorComponent.gameObject : editorComponent.GetComponent(targetType);
                    return null;
#else
                    return Resources.FindObjectsOfTypeAll(targetType).Cast<UnityEngine.Object>().FirstOrDefault(obj => obj.GetInstanceID() == id);
#endif
                }
                if (EvalData.AsObject(value) is { } selector)
                {
                    var go = ResolveGameObject(selector);
                    if (go == null) return null;
                    return targetType == typeof(GameObject) ? go : go.GetComponent(targetType);
                }
            }

            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        public static object? ToJsonFriendly(object? value, int depth = 0)
        {
            return EvalValueFormatter.Format(value, "json", Math.Max(0, 4 - depth));
        }

        public static float ToFloat(object? value, float defaultValue)
        {
            return value switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                long l => l,
                string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => defaultValue
            };
        }

        private static double ToDouble(object? value, double defaultValue)
        {
            return value switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => defaultValue
            };
        }

        public static MemberInfo? FindMember(Type type, string name, bool includeNonPublic = false, bool includeStatic = false)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;
            if (includeStatic) flags |= BindingFlags.Static;
            return (MemberInfo?)type.GetField(name, flags) ?? type.GetProperty(name, flags);
        }

        public static bool TrySetMember(object target, string memberName, object? value, out string error, bool includeNonPublic = false, bool includeStatic = false)
        {
            error = string.Empty;
            var type = target.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;
            if (includeStatic) flags |= BindingFlags.Static;

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                field.SetValue(target, ConvertToType(value, field.FieldType));
                return true;
            }

            var property = type.GetProperty(memberName, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, ConvertToType(value, property.PropertyType));
                return true;
            }

            error = $"Writable field or property '{memberName}' was not found on {type.FullName}.";
            return false;
        }

        public static List<object?> GetRootSummaries(Scene scene)
        {
            var roots = new List<object?>();
            if (!scene.IsValid() || !scene.isLoaded) return roots;
            foreach (var root in scene.GetRootGameObjects())
                roots.Add(SummarizeGameObject(root, false));
            return roots;
        }

        public static List<object?> GetHierarchySummaries(Scene scene, int depth, bool includeComponents, int limit)
        {
            var roots = new List<object?>();
            if (!scene.IsValid() || !scene.isLoaded) return roots;

            var remaining = limit <= 0 ? int.MaxValue : limit;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (remaining <= 0) break;
                roots.Add(SummarizeHierarchy(root, Math.Max(0, depth), includeComponents, ref remaining));
            }

            return roots;
        }

        private static object SummarizeHierarchy(GameObject go, int depth, bool includeComponents, ref int remaining)
        {
            remaining--;
            var children = new List<object?>();
            if (depth > 0)
            {
                for (var i = 0; i < go.transform.childCount && remaining > 0; i++)
                    children.Add(SummarizeHierarchy(go.transform.GetChild(i).gameObject, depth - 1, includeComponents, ref remaining));
            }

            return EvalData.Obj(
                ("name", go.name),
                ("instanceId", go.GetInstanceID()),
                ("path", GetPath(go)),
                ("tag", go.tag),
                ("layer", go.layer),
                ("activeSelf", go.activeSelf),
                ("activeInHierarchy", go.activeInHierarchy),
                ("transform", EvalData.Obj(
                    ("position", Vector3ToObject(go.transform.position)),
                    ("localPosition", Vector3ToObject(go.transform.localPosition)),
                    ("rotationEuler", Vector3ToObject(go.transform.eulerAngles)),
                    ("localRotationEuler", Vector3ToObject(go.transform.localEulerAngles)),
                    ("localScale", Vector3ToObject(go.transform.localScale))
                )),
                ("components", includeComponents
                    ? go.GetComponents<Component>().Select((component, index) => (object?)SummarizeComponent(component, index)).ToList()
                    : new List<object?>()),
                ("children", children)
            );
        }
    }
}
