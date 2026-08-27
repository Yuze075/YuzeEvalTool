#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    [UnityEngine.Scripting.Preserve]
    [EvalTool("Components", "Component query, edit, and instance method operations for live Unity objects.")]
    public sealed partial class ComponentsTool
    {
        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "List components on one resolved GameObject, including component index values used by get/remove/set calls.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> list(object target)
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            var components = go.GetComponents<Component>()
                .Select((component, index) => (object?)ToolUtilities.SummarizeComponent(component, index)).ToList();
            return EvalData.Obj(("object", ToolUtilities.SummarizeGameObject(go, false)), ("components", components));
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Get one component summary and readable public fields/properties. Select by component type name and optional index, or by component index when type is empty.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> get(object target, string type = "", int index = -1, bool includeValues = false)
        {
            var component = ResolveComponent(target, type, index, out var error);
            if (component == null) throw new InvalidOperationException(error);
            return EvalData.Obj(
                ("summary", ToolUtilities.SummarizeComponent(component)),
                ("members", includeValues ? GetReadableMembers(component) : GetReadableMemberDefinitions(component))
            );
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Find live Components by C# type name using Unity object lookup. Returns component summaries and lightweight GameObject selectors.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> find(string typeName, int limit = 20, bool includeInactive = false)
        {
            var type = ToolUtilities.FindType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                throw new InvalidOperationException($"Component type '{typeName}' was not found.");

            var components = ToolUtilities.FindComponents(type, includeInactive, Math.Max(1, limit))
                .Select(component => (object?)EvalData.Obj(
                    ("component", ToolUtilities.SummarizeComponent(component)),
                    ("gameObject", ToolUtilities.SummarizeGameObject(component.gameObject, false))))
                .ToList();

            return EvalData.Obj(
                ("type", type.FullName ?? type.Name),
                ("includeInactive", includeInactive),
                ("count", components.Count),
                ("components", components));
        }

        internal static Component? ResolveComponent(object target, string typeName, int index, out string error)
        {
            error = string.Empty;
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null)
            {
                error = "Target GameObject was not found or is ambiguous.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(typeName))
            {
                var components = go.GetComponents<Component>();
                if (index >= 0 && index < components.Length)
                    return components[index];
                error = "Argument 'type' or valid 'index' is required.";
                return null;
            }

            var type = ToolUtilities.FindType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                error = $"Component type '{typeName}' was not found.";
                return null;
            }

            var all = go.GetComponents(type);
            var requestedIndex = index < 0 ? 0 : index;
            if (requestedIndex >= 0 && requestedIndex < all.Length)
                return (Component)all[requestedIndex];

            error = $"Component '{typeName}' index {requestedIndex} was not found on '{go.name}'.";
            return null;
        }

        private static List<object?> GetReadableMembers(Component component)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var type = component.GetType();
            var result = new List<object?>();

            foreach (var field in type.GetFields(flags).Where(f => !f.IsStatic))
            {
                object? value;
                try
                {
                    value = field.GetValue(component);
                }
                catch
                {
                    continue;
                }

                result.Add(EvalData.Obj(("kind", "field"), ("name", field.Name),
                    ("type", field.FieldType.FullName ?? field.FieldType.Name),
                    ("value", ToolUtilities.ToJsonFriendly(value))));
            }

            foreach (var property in type.GetProperties(flags)
                         .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                object? value;
                try
                {
                    value = property.GetValue(component);
                }
                catch
                {
                    continue;
                }

                result.Add(EvalData.Obj(("kind", "property"), ("name", property.Name),
                    ("type", property.PropertyType.FullName ?? property.PropertyType.Name),
                    ("canWrite", property.CanWrite), ("value", ToolUtilities.ToJsonFriendly(value))));
            }

            return result;
        }

        private static List<object?> GetReadableMemberDefinitions(Component component)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var type = component.GetType();
            var result = new List<object?>();

            foreach (var field in type.GetFields(flags).Where(f => !f.IsStatic))
                result.Add(EvalData.Obj(("kind", "field"), ("name", field.Name),
                    ("type", field.FieldType.FullName ?? field.FieldType.Name), ("canWrite", !field.IsInitOnly)));

            foreach (var property in type.GetProperties(flags)
                         .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                result.Add(EvalData.Obj(("kind", "property"), ("name", property.Name),
                    ("type", property.PropertyType.FullName ?? property.PropertyType.Name), ("canWrite", property.CanWrite)));

            return result;
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Add a component by C# type name to one resolved GameObject and return the new component summary.",
            Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> add(object target, string type)
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            var componentType = ToolUtilities.FindType(type);
            if (componentType == null || !typeof(Component).IsAssignableFrom(componentType))
                throw new InvalidOperationException($"Component type '{type}' was not found.");
#if UNITY_EDITOR
            var component = !Application.isPlaying
                ? UnityEditor.Undo.AddComponent(go, componentType)
                : go.AddComponent(componentType);
#else
            var component = go.AddComponent(componentType);
#endif
            ToolUtilities.MarkDirty(go);
            return ToolUtilities.SummarizeComponent(component);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Remove one component selected by type/index. Requires confirm: true because this is destructive.",
            Safety = EvalToolSafety.MutatesScene | EvalToolSafety.Destructive | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> remove(object target, string type = "", int index = -1, bool confirm = false)
        {
            if (!confirm)
                throw new InvalidOperationException("Component removal requires confirm: true.");
            var component = ResolveComponent(target, type, index, out var error);
            if (component == null) throw new InvalidOperationException(error);
            var summary = ToolUtilities.SummarizeComponent(component);
            ToolUtilities.DestroyObject(component);
            return EvalData.Obj(("removed", summary));
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Set one component property or field by member name. Non-public or static member writes require includeNonPublic/includeStatic plus confirmDangerous: true.",
            Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> setProperty(object target, string type, string member, object? value,
            int index = 0, bool includeNonPublic = false, bool includeStatic = false, bool confirmDangerous = false)
        {
            var component = ResolveComponent(target, type, index, out var error);
            if (component == null) throw new InvalidOperationException(error);
            if (string.IsNullOrWhiteSpace(member))
                throw new InvalidOperationException("Argument 'member' is required.");
            if ((includeNonPublic || includeStatic) && !confirmDangerous)
                throw new InvalidOperationException(
                    "Setting non-public or static members requires confirmDangerous: true.");
            ToolUtilities.RecordUndo(component, "MCP Set Component Property");
            if (!ToolUtilities.TrySetMember(component, member, value, out error, includeNonPublic, includeStatic))
                throw new InvalidOperationException(error);
            ToolUtilities.MarkDirty(component);
            return EvalData.Obj(("component", ToolUtilities.SummarizeComponent(component)), ("member", member),
                ("value", ToolUtilities.ToJsonFriendly(value)));
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Set multiple component properties or fields from an object map of memberName -> value. Non-public or static writes require confirmDangerous: true.",
            Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> setProperties(object target, string type, object values, int index = 0,
            bool includeNonPublic = false, bool includeStatic = false, bool confirmDangerous = false)
        {
            var component = ResolveComponent(target, type, index, out var error);
            if (component == null) throw new InvalidOperationException(error);
            if ((includeNonPublic || includeStatic) && !confirmDangerous)
                throw new InvalidOperationException(
                    "Setting non-public or static members requires confirmDangerous: true.");

            var valueMap = EvalData.AsObject(values);
            if (valueMap == null || valueMap.Count == 0)
                throw new InvalidOperationException("Argument 'values' must contain at least one member update.");

            ToolUtilities.RecordUndo(component, "MCP Set Component Properties");
            var results = new List<object?>();
            foreach (var pair in valueMap)
            {
                if (!ToolUtilities.TrySetMember(component, pair.Key, pair.Value, out error, includeNonPublic,
                        includeStatic))
                    throw new InvalidOperationException(error);
                results.Add(EvalData.Obj(
                    ("member", pair.Key),
                    ("value", ToolUtilities.ToJsonFriendly(pair.Value))
                ));
            }

            ToolUtilities.MarkDirty(component);
            return EvalData.Obj(
                ("component", ToolUtilities.SummarizeComponent(component)),
                ("count", results.Count),
                ("changes", results)
            );
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Call a component instance method selected by type, method name, component index, and positional args array. Public method effects are method-dependent; non-public methods require includeNonPublic and confirmDangerous: true.",
            Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> callMethod(object target, string type, string method, object? args = null,
            int index = 0, bool includeNonPublic = false, bool confirmDangerous = false)
        {
            var component = ResolveComponent(target, type, index, out var error);
            if (component == null) throw new InvalidOperationException(error);
            if (string.IsNullOrWhiteSpace(method))
                throw new InvalidOperationException("Argument 'method' is required.");
            if (includeNonPublic && !confirmDangerous)
                throw new InvalidOperationException(
                    "Calling non-public instance methods requires confirmDangerous: true.");

            var values = EvalData.AsArray(args) ?? new List<object?>();
            var flags = BindingFlags.Public | BindingFlags.Instance;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            var candidates = component.GetType()
                .GetMethods(flags)
                .Where(candidate => candidate.Name == method && !candidate.IsSpecialName &&
                                    candidate.GetParameters().Length == values.Count)
                .ToList();
            Exception? lastError = null;

            foreach (var candidate in candidates)
            {
                try
                {
                    var parameters = candidate.GetParameters();
                    var converted = new object?[parameters.Length];
                    for (var i = 0; i < parameters.Length; i++)
                        converted[i] = ToolUtilities.ConvertToType(values[i], parameters[i].ParameterType);

                    var result = candidate.Invoke(component, converted);
                    ToolUtilities.MarkDirty(component);
                    return EvalData.Obj(
                        ("component", ToolUtilities.SummarizeComponent(component)),
                        ("method", ReflectionTool.FormatMethod(candidate)),
                        ("result", ToolUtilities.ToJsonFriendly(result))
                    );
                }
                catch (Exception ex)
                {
                    lastError = ex.InnerException ?? ex;
                    // Try the next overload.
                }
            }

            var detail = lastError == null
                ? string.Empty
                : $" Last failure: {lastError.GetType().Name}: {lastError.Message}";
            throw new InvalidOperationException(
                $"No callable instance overload '{method}' with {values.Count} argument(s) was found on '{component.GetType().FullName}'.{detail}");
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "List available non-abstract Component types. Query filters by full type name; use returned fullName with add/get/set/call methods.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> listTypes(string query = "", int limit = 200)
        {
            limit = Math.Max(1, limit);
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .SelectMany(ToolUtilities.GetTypesSafe)
                .Where(type => type is { IsAbstract: false } && typeof(Component).IsAssignableFrom(type))
                .Where(type =>
                    string.IsNullOrWhiteSpace(query) ||
                    (type.FullName ?? type.Name).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(type => type.FullName ?? type.Name, StringComparer.Ordinal)
                .Take(limit)
                .Select(type => (object?)EvalData.Obj(
                    ("fullName", type.FullName ?? type.Name),
                    ("name", type.Name),
                    ("assembly", type.Assembly.GetName().Name ?? string.Empty)))
                .ToList();
            return EvalData.Obj(("count", types.Count), ("types", types));
        }
    }
}
