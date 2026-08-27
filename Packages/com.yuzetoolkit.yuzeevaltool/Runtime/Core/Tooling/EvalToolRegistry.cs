#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YuzeToolkit.Eval
{
    [UnityEngine.Scripting.Preserve]
    public static partial class EvalToolRegistry
    {
        private static readonly HashSet<string> JavaScriptReservedIdentifiers = new(StringComparer.Ordinal)
        {
            "arguments", "await", "break", "case", "catch", "class", "const", "continue",
            "debugger", "default", "delete", "do", "else", "enum", "eval", "export", "extends",
            "false", "finally", "for", "function", "if", "implements", "import", "in",
            "instanceof", "interface", "let", "new", "null", "package", "private", "protected",
            "public", "return", "static", "super", "switch", "this", "throw", "true", "try",
            "typeof", "var", "void", "while", "with", "yield"
        };

        private const string EditorPrefPrefix = nameof(YuzeToolkit) + ".McpTool.Enabled.";
        private static readonly Dictionary<string, IEvalTool> CSharpRoots = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, JsToolRegistration> JsRoots = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> EnabledOverrides = new(StringComparer.Ordinal);
        private static readonly object SyncRoot = new();

        [UnityEngine.Scripting.Preserve] public static event Action? Changed;

        [UnityEngine.Scripting.Preserve]
        public static void RegisterRoot(IEvalTool tool)
        {
            if (!TryRegisterRoot(tool))
                throw new InvalidOperationException($"Eval root tool '{tool?.Name}' is already registered.");
        }

        /// <summary>
        /// Registers a root Tool and returns an independent lifetime handle. Disposing the handle only unregisters
        /// this exact Tool instance and has no relationship to DebugPanel or any other UI.
        /// </summary>
        [UnityEngine.Scripting.Preserve]
        public static IDisposable RegisterRootScoped(IEvalTool tool)
        {
            RegisterRoot(tool);
            return new ScopedRootRegistration(tool);
        }

        [UnityEngine.Scripting.Preserve]
        public static bool TryRegisterRoot(IEvalTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            ValidateToolTree(tool, tool.GetType(), tool.Name);

            lock (SyncRoot)
            {
                if (CSharpRoots.ContainsKey(tool.Name) || JsRoots.ContainsKey(tool.Name)) return false;
                CSharpRoots.Add(tool.Name, tool);
                EnsureKnownNoLock(tool.Name, tool);
            }

            ClearGeneratedModuleCache();
            Changed?.Invoke();
            return true;
        }

        [UnityEngine.Scripting.Preserve]
        public static bool UnregisterRoot(string name)
        {
            var normalized = NormalizePath(name);
            bool removed;
            lock (SyncRoot)
                removed = CSharpRoots.Remove(normalized) || JsRoots.Remove(normalized);

            if (!removed) return false;
            ClearGeneratedModuleCache();
            Changed?.Invoke();
            return true;
        }

        [UnityEngine.Scripting.Preserve]
        public static bool TryUnregisterRoot(IEvalTool expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            var normalized = NormalizePath(expected.Name);
            var removed = false;
            lock (SyncRoot)
            {
                if (CSharpRoots.TryGetValue(normalized, out var registered) &&
                    ReferenceEquals(registered, expected))
                    removed = CSharpRoots.Remove(normalized);
            }

            if (!removed) return false;
            ClearGeneratedModuleCache();
            Changed?.Invoke();
            return true;
        }

        [UnityEngine.Scripting.Preserve]
        public static bool TryGet(string path, out IEvalTool tool)
        {
            if (!TryResolveCSharp(path, out var resolved) || !IsEnabled(resolved.Path))
            {
                tool = null!;
                return false;
            }

            tool = resolved.Instance;
            return true;
        }

        [UnityEngine.Scripting.Preserve]
        public static object GetRequiredInstance(string path)
        {
            if (!TryResolveCSharp(path, out var tool) || !IsEnabled(tool.Path))
                throw new InvalidOperationException($"Eval tool '{path}' is unknown, disabled, or no longer available.");
            return tool.Instance;
        }

        [UnityEngine.Scripting.Preserve]
        public static bool TryResolve(string path, out string resolvedPath, out IEvalTool tool)
        {
            if (TryResolveCSharp(path, out var resolved))
            {
                resolvedPath = resolved.Path;
                tool = resolved.Instance;
                return true;
            }

            resolvedPath = string.Empty;
            tool = null!;
            return false;
        }

        [UnityEngine.Scripting.Preserve]
        public static bool IsEnabled(string path)
        {
            var normalized = NormalizePath(path);
            if (string.IsNullOrEmpty(normalized)) return true;
            lock (SyncRoot)
            {
                var current = string.Empty;
                foreach (var segment in normalized.Split('/'))
                {
                    current = current.Length == 0 ? segment : current + "/" + segment;
                    if (EnabledOverrides.TryGetValue(current, out var enabled) && !enabled)
                        return false;
                }

                return !EnabledOverrides.TryGetValue(normalized, out var exactEnabled) || exactEnabled;
            }
        }

        [UnityEngine.Scripting.Preserve]
        public static void SetEnabled(string path, bool enabled)
        {
            var normalized = NormalizePath(path);
            lock (SyncRoot)
                EnabledOverrides[normalized] = enabled;
            ClearGeneratedModuleCache();
            Changed?.Invoke();
        }

        [UnityEngine.Scripting.Preserve]
        public static Dictionary<string, object?> SetToolEnabled(string path, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Failure("Tool path is required.");

            var normalized = NormalizePath(path);
            if (!ToolExists(normalized))
                return Failure($"Tool '{path}' was not found.");

            SetEnabled(normalized, enabled);
#if UNITY_EDITOR
            EditorPrefs.SetBool(EditorPrefPrefix + normalized, enabled);
#endif
            return Success(("path", normalized), ("enabled", enabled));
        }

        public static void ValidateToolSegment(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Eval tool name cannot be empty.", nameof(name));
            if (name.Equals("index", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Eval tool name 'index' is reserved.", nameof(name));
            if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                throw new ArgumentException($"Eval tool name '{name}' contains invalid path characters.", nameof(name));
        }

        [UnityEngine.Scripting.Preserve]
        public static List<object?> ListSummaries()
        {
            var result = new List<object?>();
            foreach (var tool in ListCSharpRoots())
            {
                result.Add(EvalData.Obj(
                    ("name", tool.Name),
                    ("path", tool.Path),
                    ("enabled", IsEnabled(tool.Path))
                ));
            }

            return result;
        }

        [UnityEngine.Scripting.Preserve]
        public static string GetToolCatalogJson(bool refresh)
        {
            try
            {
                return EvalJson.Stringify(GetIndex(refresh));
            }
            catch (Exception ex)
            {
                return EvalJson.Stringify(Failure(ex.Message));
            }
        }

        [UnityEngine.Scripting.Preserve]
        public static string GetToolDetailsJson(string path, bool refresh)
        {
            try
            {
                return EvalJson.Stringify(GetToolDetails(path, refresh));
            }
            catch (Exception ex)
            {
                return EvalJson.Stringify(Failure(ex.Message));
            }
        }

        private static bool TryResolveCSharp(string path, out ResolvedTool tool)
        {
            lock (SyncRoot)
                return TryResolveCSharpNoLock(path, out tool);
        }

        private static bool TryResolveCSharpNoLock(string path, out ResolvedTool tool)
        {
            tool = default;
            var normalizedPath = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath)) return false;

            var segments = normalizedPath.Split('/');
            if (segments.Length == 0 || !CSharpRoots.TryGetValue(segments[0], out var current))
                return false;

            var resolved = current.Name;
            for (var i = 1; i < segments.Length; i++)
            {
                current = FindSubTool(current, segments[i]);
                if (current == null) return false;
                resolved += "/" + current.Name;
            }

            tool = new ResolvedTool(current.Name, resolved, current.Description, current);
            return true;
        }

        private static IReadOnlyList<ResolvedTool> ListCSharpRoots()
        {
            lock (SyncRoot)
                return CSharpRoots.Values
                    .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                    .Select(tool => new ResolvedTool(tool.Name, tool.Name, tool.Description, tool))
                    .ToList();
        }

        private static IReadOnlyList<JsToolRegistration> ListJsRoots()
        {
            lock (SyncRoot)
                return JsRoots.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
        }

        private static bool ToolExists(string path)
        {
            if (TryResolveCSharp(path, out _)) return true;
            return TryGetJsDescriptor(path, out _);
        }

        private static IEvalTool? FindSubTool(IEvalTool parent, string name)
        {
            foreach (var subTool in parent.SubTools)
            {
                if (string.Equals(subTool.Name, name, StringComparison.Ordinal))
                    return subTool;
            }

            return null;
        }

        private static void ValidateToolTree(IEvalTool tool, Type ownerType, string path)
        {
            ValidateToolSegment(tool.Name);
            if (string.IsNullOrWhiteSpace(tool.Description))
                throw new InvalidOperationException(
                    $"Eval tool type '{ownerType.FullName}' must define a non-empty Description.");

            ValidateFunctions(ownerType, tool.Functions);

            var subToolNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var subTool in tool.SubTools)
            {
                if (subTool == null)
                    throw new InvalidOperationException($"Eval tool '{path}' contains a null sub tool.");
                ValidateToolSegment(subTool.Name);
                if (!subToolNames.Add(subTool.Name))
                    throw new InvalidOperationException($"Eval tool '{path}' has duplicate sub tool name '{subTool.Name}'.");
                ValidateToolTree(subTool, subTool.GetType(), path + "/" + subTool.Name);
            }
        }

        private static void ValidateFunctions(Type toolType, IReadOnlyList<EvalToolFunctionDescriptor> functions)
        {
            if (functions == null)
                throw new InvalidOperationException($"Eval tool type '{toolType.FullName}' has null Functions.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var function in functions)
            {
                if (function == null)
                    throw new InvalidOperationException($"Eval tool type '{toolType.FullName}' contains a null function descriptor.");
                if (string.IsNullOrWhiteSpace(function.MethodName))
                    throw new InvalidOperationException($"Eval tool type '{toolType.FullName}' contains an empty function name.");
                if (!IsValidJavaScriptIdentifier(function.MethodName))
                    throw new InvalidOperationException(
                        $"Eval tool type '{toolType.FullName}' has invalid function name '{function.MethodName}'.");
                if (!names.Add(function.MethodName))
                    throw new InvalidOperationException(
                        $"Eval tool type '{toolType.FullName}' has duplicate function name '{function.MethodName}'.");
                if (string.IsNullOrWhiteSpace(function.Description))
                    throw new InvalidOperationException(
                        $"Eval tool type '{toolType.FullName}' function '{function.MethodName}' must define a non-empty Description.");
                EvalToolSafetyUtility.ValidateDeclared(function.Safety,
                    $"Eval tool type '{toolType.FullName}' function '{function.MethodName}'");

                foreach (var parameter in function.Parameters)
                {
                    if (parameter == null)
                        throw new InvalidOperationException(
                            $"Eval tool type '{toolType.FullName}' function '{function.MethodName}' contains a null parameter descriptor.");
                    if (string.IsNullOrWhiteSpace(parameter.Name))
                        throw new InvalidOperationException(
                            $"Eval tool type '{toolType.FullName}' function '{function.MethodName}' contains an empty parameter name.");
                    if (string.IsNullOrWhiteSpace(parameter.Type))
                        throw new InvalidOperationException(
                            $"Eval tool type '{toolType.FullName}' function '{function.MethodName}' parameter '{parameter.Name}' must define a non-empty type.");
                }
            }
        }

        private static void EnsureKnownNoLock(string path)
        {
            if (!EnabledOverrides.ContainsKey(path))
                EnabledOverrides[path] = true;
        }

        private static void EnsureKnownNoLock(string rootPath, IEvalTool rootTool)
        {
            EnsureKnownNoLock(rootPath);
            foreach (var subTool in rootTool.SubTools)
                EnsureKnownNoLock(rootPath + "/" + subTool.Name, subTool);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var normalized = path.Replace('\\', '/').Trim('/');
            while (normalized.Contains("//"))
                normalized = normalized.Replace("//", "/");
            return normalized;
        }

        private static string GetRootName(string path)
        {
            var normalized = NormalizePath(path);
            var separator = normalized.IndexOf('/');
            return separator < 0 ? normalized : normalized[..separator];
        }

        private static bool IsValidJavaScriptIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            // Generated Tool modules declare one ES-module export per function. Keywords and
            // strict-mode binding names can be valid property keys but are not valid declarations.
            if (JavaScriptReservedIdentifiers.Contains(value)) return false;
            if (!IsIdentifierStart(value[0])) return false;
            for (var i = 1; i < value.Length; i++)
            {
                if (!IsIdentifierPart(value[i])) return false;
            }

            return true;
        }

        private static bool IsIdentifierStart(char value) =>
            value == '_' || value == '$' || char.IsLetter(value);

        private static bool IsIdentifierPart(char value) =>
            IsIdentifierStart(value) || char.IsDigit(value);

        private static Dictionary<string, object?> Success(params (string Key, object? Value)[] values)
        {
            var result = EvalData.Obj(("success", true));
            foreach (var (key, value) in values)
                result[key] = value;
            return result;
        }

        private static Dictionary<string, object?> Failure(string error, params (string Key, object? Value)[] values)
        {
            var result = EvalData.Obj(("success", false), ("error", error));
            foreach (var (key, value) in values)
                result[key] = value;
            return result;
        }

        private readonly struct ResolvedTool
        {
            public ResolvedTool(string name, string path, string description, IEvalTool instance)
            {
                Name = name;
                Path = path;
                Description = description;
                Instance = instance;
            }

            public string Name { get; }
            public string Path { get; }
            public string Description { get; }
            public IEvalTool Instance { get; }
            public Type ToolType => Instance.GetType();
            public IReadOnlyList<EvalToolFunctionDescriptor> Functions => Instance.Functions;
        }

        private sealed class ScopedRootRegistration : IDisposable
        {
            private IEvalTool? _tool;

            public ScopedRootRegistration(IEvalTool tool) => _tool = tool;

            public void Dispose()
            {
                if (_tool == null) return;
                TryUnregisterRoot(_tool);
                _tool = null;
            }
        }

        private readonly struct JsToolRegistration
        {
            public JsToolRegistration(string modulePath, string name, string description)
            {
                ModulePath = modulePath;
                Name = name;
                Description = description;
            }

            public string ModulePath { get; }
            public string Name { get; }
            public string Description { get; }
        }
    }
}
