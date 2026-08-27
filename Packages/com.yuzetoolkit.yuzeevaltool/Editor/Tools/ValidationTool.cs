#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace YuzeToolkit
{
    [EvalTool("Validation", "Project health checks for missing scripts, loaded-scene references, and conventions.")]
    public sealed partial class ValidationTool
    {
        [EvalFunction("Run all validations.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> run(object? folders = null, int limit = 0, bool includeProjectWideChecks = false)
        {
            var effectiveLimit = limit <= 0 ? 200 : limit;
            var result = EvalData.Obj(
                ("missingReferences", GetMissingReferencesResult(effectiveLimit))
            );
            if (includeProjectWideChecks)
            {
                result["missingScripts"] = GetMissingScriptsResult(folders, effectiveLimit);
                result["serializedFieldTooltips"] = GetSerializedFieldTooltipsResult(folders, effectiveLimit);
            }
            else
            {
                result["skipped"] = EvalData.Arr("missingScripts", "serializedFieldTooltips");
                result["note"] = "Project-wide checks require includeProjectWideChecks: true plus folders/limit.";
            }

            return result;
        }

        [EvalFunction("Find missing scripts.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> missingScripts(object? folders = null, int limit = 100) =>
            GetMissingScriptsResult(folders, limit);

        private static Dictionary<string, object?> GetMissingScriptsResult(object? foldersValue, int limit)
        {
            limit = Math.Max(1, limit <= 0 ? 100 : limit);
            var folders = ToStringList(foldersValue);
            var issues = new List<object?>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>().Where(go => go != null && ToolUtilities.IsUsableSceneObject(go, true)))
            {
                var count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (count <= 0) continue;
                issues.Add(EvalData.Obj(("scope", "scene"), ("count", count), ("gameObject", ToolUtilities.SummarizeGameObject(go, false))));
                if (issues.Count >= limit) return EvalData.Obj(("count", issues.Count), ("issues", issues));
            }

            if (folders.Count == 0)
                return EvalData.Obj(("count", issues.Count), ("issues", issues),
                    ("note", "Prefab scanning skipped because folders was not provided."));

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", folders.ToArray()))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    issues.Add(EvalData.Obj(("scope", "prefab"), ("path", path), ("error", "Prefab asset could not be loaded.")));
                    continue;
                }
                var count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab);
                if (count > 0)
                    issues.Add(EvalData.Obj(("scope", "prefab"), ("path", path), ("count", count)));
                if (issues.Count >= limit) break;
            }

            return EvalData.Obj(("count", issues.Count), ("issues", issues));
        }

        [EvalFunction("Find broken serialized object references in loaded scenes.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> missingReferences(int limit = 200) => GetMissingReferencesResult(limit);

        private static Dictionary<string, object?> GetMissingReferencesResult(int limit)
        {
            limit = Math.Max(1, limit);
            var issues = new List<object?>();

            foreach (var go in EnumerateLoadedSceneGameObjects(true))
            {
                CollectMissingReferences(go, ToolUtilities.GetPath(go), issues, limit);
                if (issues.Count >= limit) break;
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component == null) continue;
                    CollectMissingReferences(component, ToolUtilities.GetPath(go) + "/" + component.GetType().Name, issues, limit);
                    if (issues.Count >= limit) break;
                }
                if (issues.Count >= limit) break;
            }

            return EvalData.Obj(("count", issues.Count), ("issues", issues));
        }

        private static IEnumerable<GameObject> EnumerateLoadedSceneGameObjects(bool includeInactive)
        {
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
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
            var stack = new Stack<GameObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var go = stack.Pop();
                if (ToolUtilities.IsUsableSceneObject(go, includeInactive))
                    yield return go;
                if (!includeInactive && !go.activeInHierarchy) continue;
                var transform = go.transform;
                for (var i = transform.childCount - 1; i >= 0; i--)
                    stack.Push(transform.GetChild(i).gameObject);
            }
        }

        private static void CollectMissingReferences(UnityEngine.Object obj, string owner, List<object?> issues, int limit)
        {
            try
            {
                var serialized = new SerializedObject(obj);
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                        iterator.objectReferenceValue == null &&
                        iterator.objectReferenceInstanceIDValue != 0)
                    {
                        issues.Add(EvalData.Obj(
                            ("owner", owner),
                            ("type", obj.GetType().FullName ?? obj.GetType().Name),
                            ("propertyPath", iterator.propertyPath)
                        ));
                        if (issues.Count >= limit) return;
                    }
                }
            }
            catch
            {
                // Some editor-only objects cannot be serialized safely; skip them.
            }
        }

        [EvalFunction("Check SerializeField Tooltip convention.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> serializedFieldTooltips(object? folders = null, int limit = 0) => GetSerializedFieldTooltipsResult(folders, limit);

        private static Dictionary<string, object?> GetSerializedFieldTooltipsResult(object? foldersValue, int limit)
        {
            var folders = ToStringList(foldersValue);
            if (folders.Count == 0)
            {
                return EvalData.Obj(("count", 0), ("issues", EvalData.Arr()),
                    ("note", "Tooltip scan requires folders to avoid recursively reading the whole project."));
            }

            var root = ToolUtilities.GetProjectRoot();
            var issues = new List<object?>();
            foreach (var folder in folders)
            {
                if (!ToolUtilities.TryResolveProjectPath(folder, out var fullFolder, out _, out _)) continue;
                if (!Directory.Exists(fullFolder)) continue;
                foreach (var file in Directory.GetFiles(fullFolder, "*.cs", SearchOption.AllDirectories))
                {
                    ScanFile(root, file, issues);
                    if (limit > 0 && issues.Count >= limit)
                        return EvalData.Obj(("count", issues.Count), ("issues", issues));
                }
            }

            return EvalData.Obj(("count", issues.Count), ("issues", issues));
        }

        private static void ScanFile(string root, string file, List<object?> issues)
        {
            var lines = File.ReadAllLines(file);
            var attributeLines = new List<string>();
            var attributeStartLine = 0;
            var typeStack = new Stack<(int BodyDepth, bool SerializesPublicFields)>();
            bool? pendingTypeSerializesPublicFields = null;
            var braceDepth = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = StripLineComment(lines[i]).Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    attributeLines.Clear();
                    attributeStartLine = 0;
                    continue;
                }

                if (TrySplitAttributeLine(trimmed, out var attributeText, out var codeAfterAttribute))
                {
                    if (attributeLines.Count == 0)
                        attributeStartLine = i + 1;
                    attributeLines.Add(attributeText);
                    if (string.IsNullOrWhiteSpace(codeAfterAttribute))
                        continue;
                    trimmed = codeAfterAttribute.Trim();
                }

                var attributes = string.Join(" ", attributeLines);
                if (TryGetTypePublicSerialization(trimmed, attributes, out var typeSerializesPublicFields))
                {
                    pendingTypeSerializesPublicFields = typeSerializesPublicFields;
                }
                else if (pendingTypeSerializesPublicFields != null && trimmed.Contains("{", StringComparison.Ordinal))
                {
                    // The previous line declared a serializable type and this line opens its body.
                }
                else if (IsSerializedFieldDeclaration(trimmed, attributes, typeStack.Count > 0 && typeStack.Peek().SerializesPublicFields) && !HasTooltipAttribute(attributes))
                {
                    issues.Add(EvalData.Obj(
                        ("path", file.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/')),
                        ("line", attributeStartLine > 0 ? attributeStartLine : i + 1),
                        ("text", trimmed)
                    ));
                }

                var (openBraces, closeBraces) = CountBraces(trimmed);
                if (pendingTypeSerializesPublicFields != null && openBraces > 0)
                {
                    typeStack.Push((braceDepth + openBraces, pendingTypeSerializesPublicFields.Value));
                    pendingTypeSerializesPublicFields = null;
                }

                braceDepth += openBraces - closeBraces;
                while (typeStack.Count > 0 && braceDepth < typeStack.Peek().BodyDepth)
                    typeStack.Pop();

                attributeLines.Clear();
                attributeStartLine = 0;
            }
        }

        private static bool TrySplitAttributeLine(string trimmed, out string attributeText, out string codeAfterAttribute)
        {
            attributeText = string.Empty;
            codeAfterAttribute = string.Empty;
            if (!trimmed.StartsWith("[", StringComparison.Ordinal)) return false;

            var closeIndex = trimmed.LastIndexOf(']');
            if (closeIndex < 0)
            {
                attributeText = trimmed;
                return true;
            }

            attributeText = trimmed.Substring(0, closeIndex + 1);
            codeAfterAttribute = trimmed.Substring(closeIndex + 1);
            return true;
        }

        private static bool IsSerializedFieldDeclaration(string code, string attributes, bool currentTypeSerializesPublicFields)
        {
            if (string.IsNullOrWhiteSpace(code) || !code.TrimEnd().EndsWith(";", StringComparison.Ordinal))
                return false;
            if (code.Contains("=>", StringComparison.Ordinal) || code.Contains("{", StringComparison.Ordinal))
                return false;

            var declarationPrefix = code;
            var equalsIndex = declarationPrefix.IndexOf('=');
            if (equalsIndex >= 0)
                declarationPrefix = declarationPrefix.Substring(0, equalsIndex);
            if (declarationPrefix.Contains("(", StringComparison.Ordinal) || declarationPrefix.Contains(")", StringComparison.Ordinal))
                return false;

            var accessMatch = Regex.Match(code, @"^\s*(public|private|protected|internal)(?:\s+(?:protected|internal))?\s+");
            if (!accessMatch.Success) return false;
            if (Regex.IsMatch(code, @"\b(static|const|readonly)\b")) return false;

            var remainder = code.Substring(accessMatch.Length).TrimStart();
            if (Regex.IsMatch(remainder, @"^(class|struct|interface|enum|delegate|event)\b"))
                return false;

            if (ContainsAttribute(attributes, "NonSerialized"))
                return false;

            if (ContainsAttribute(attributes, "SerializeField") || ContainsAttribute(attributes, "SerializeReference"))
                return true;

            return currentTypeSerializesPublicFields && string.Equals(accessMatch.Groups[1].Value, "public", StringComparison.Ordinal);
        }

        private static bool TryGetTypePublicSerialization(string code, string attributes, out bool serializesPublicFields)
        {
            serializesPublicFields = false;
            var match = Regex.Match(code, @"\b(class|struct)\s+\w+(?:\s*:\s*([^{]+))?");
            if (!match.Success) return false;

            var inheritance = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
            serializesPublicFields = ContainsAttribute(attributes, "Serializable") ||
                                     Regex.IsMatch(inheritance, @"\b(MonoBehaviour|ScriptableObject|Component|Behaviour)\b");
            return true;
        }

        private static bool HasTooltipAttribute(string attributes) => ContainsAttribute(attributes, "Tooltip");

        private static bool ContainsAttribute(string attributes, string attributeName) =>
            Regex.IsMatch(attributes, $@"(?:^|[\s,\[:]){Regex.Escape(attributeName)}(?:Attribute)?\b");

        private static (int Open, int Close) CountBraces(string line)
        {
            var open = 0;
            var close = 0;
            var inString = false;
            var escaped = false;

            foreach (var c in line)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString) continue;
                if (c == '{') open++;
                else if (c == '}') close++;
            }

            return (open, close);
        }

        private static string StripLineComment(string line)
        {
            var inString = false;
            var escaped = false;
            for (var i = 0; i < line.Length - 1; i++)
            {
                var c = line[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                    inString = !inString;
                if (!inString && c == '/' && line[i + 1] == '/')
                    return line.Substring(0, i);
            }

            return line;
        }

        private static List<string> ToStringList(object? value)
        {
            if (value == null) return new List<string>();
            if (value is string single) return string.IsNullOrWhiteSpace(single) ? new List<string>() : new List<string> { single };
            return (EvalData.AsArray(value) ?? new List<object?>())
                .Select(Convert.ToString)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!)
                .ToList();
        }
    }
}
