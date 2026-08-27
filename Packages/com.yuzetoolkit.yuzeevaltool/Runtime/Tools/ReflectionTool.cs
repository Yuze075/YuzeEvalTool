#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YuzeToolkit
{
    [UnityEngine.Scripting.Preserve]
    [EvalTool("Reflection", "Inspect C# types and call static methods when helper modules do not cover a custom API.")]
    public sealed partial class ReflectionTool
    {
        [UnityEngine.Scripting.Preserve]
        [EvalFunction("List public C# namespaces currently loaded in the AppDomain.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getNamespaces()
        {
            var namespaces = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .SelectMany(ToolUtilities.GetTypesSafe)
                .Where(type => type is { IsPublic: true })
                .Select(type => type.Namespace ?? "(global)")
                .Distinct()
                .OrderBy(ns => ns, StringComparer.Ordinal)
                .Cast<object?>()
                .ToList();

            return EvalData.Obj(("count", namespaces.Count), ("namespaces", namespaces));
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("List public C# types in the exact namespaceName returned by getNamespaces.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getTypes(string namespaceName)
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
                throw new InvalidOperationException("Argument 'namespaceName' is required.");

            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .SelectMany(ToolUtilities.GetTypesSafe)
                .Where(type => type is { IsPublic: true } && (type.Namespace ?? "(global)") == namespaceName)
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .Select(type => (object?)EvalData.Obj(
                    ("fullName", type.FullName),
                    ("name", type.Name),
                    ("kind", GetKind(type))
                ))
                .ToList();

            return EvalData.Obj(("namespace", namespaceName), ("count", types.Count), ("types", types));
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Get public C# type details by full type name, including declared public methods, fields, and properties.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getTypeDetails(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                throw new InvalidOperationException("Argument 'fullName' is required.");

            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(t => t != null);

            if (type == null)
                throw new InvalidOperationException($"Type '{fullName}' was not found.");

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                          BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Select(method => (object?)FormatMethod(method))
                .ToList();

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                        BindingFlags.DeclaredOnly)
                .Select(field => (object?)$"{FormatType(field.FieldType)} {field.Name}")
                .ToList();

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly)
                .Select(prop => (object?)$"{FormatType(prop.PropertyType)} {prop.Name}")
                .ToList();

            return EvalData.Obj(
                ("fullName", type.FullName),
                ("name", type.Name),
                ("namespace", type.Namespace ?? "(global)"),
                ("kind", GetKind(type)),
                ("baseType", type.BaseType?.FullName ?? string.Empty),
                ("methods", methods),
                ("fields", fields),
                ("properties", properties)
            );
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Find C# methods by method/type text query, or list methods on a specific type. Non-public search requires includeNonPublic and confirmDangerous: true.",
            Safety = EvalToolSafety.ReflectionDangerous | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> findMethods(string query = "", string type = "",
            bool includeNonPublic = false, bool confirmDangerous = false, int limit = 100)
        {
            if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(type))
                throw new InvalidOperationException("Argument 'query' or 'type' is required to avoid scanning every loaded method.");
            if (includeNonPublic && !confirmDangerous)
                throw new InvalidOperationException("Searching non-public methods requires confirmDangerous: true.");
            limit = Math.Max(1, limit);
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            var types = string.IsNullOrWhiteSpace(type)
                ? AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)
                    .SelectMany(ToolUtilities.GetTypesSafe)
                : new[] { ToolUtilities.FindType(type) }.Where(t => t != null)!;

            var methods = types
                .SelectMany(t => t!.GetMethods(flags).Select(method => (Type: t!, Method: method)))
                .Where(item => !item.Method.IsSpecialName)
                .Where(item => string.IsNullOrWhiteSpace(query) ||
                               item.Method.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               (item.Type.FullName ?? item.Type.Name).IndexOf(query,
                                   StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(item => item.Type.FullName ?? item.Type.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Method.Name, StringComparer.Ordinal)
                .Take(limit)
                .Select(item => (object?)EvalData.Obj(
                    ("declaringType", item.Type.FullName),
                    ("name", item.Method.Name),
                    ("isStatic", item.Method.IsStatic),
                    ("signature", FormatMethod(item.Method))))
                .ToList();

            return EvalData.Obj(("count", methods.Count), ("methods", methods));
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Call a static C# method by type name, method name, and positional args array. Non-public static calls require includeNonPublic and confirmDangerous: true.",
            Safety = EvalToolSafety.ReflectionDangerous | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> callStaticMethod(string type, string method, object? args = null,
            bool includeNonPublic = false, bool confirmDangerous = false)
        {
            var targetType = ToolUtilities.FindType(type);
            if (targetType == null) throw new InvalidOperationException($"Type '{type}' was not found.");
            var values = EvalData.AsArray(args) ?? new List<object?>();
            if (includeNonPublic && !confirmDangerous)
                throw new InvalidOperationException(
                    "Calling non-public static methods requires confirmDangerous: true.");
            var flags = BindingFlags.Public | BindingFlags.Static;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;
            var candidates = targetType.GetMethods(flags)
                .Where(candidate => candidate.Name == method && candidate.GetParameters().Length == values.Count)
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
                    var result = candidate.Invoke(null, converted);
                    return EvalData.Obj(("method", FormatMethod(candidate)),
                        ("result", ToolUtilities.ToJsonFriendly(result)));
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
                $"No callable static overload '{method}' with {values.Count} argument(s) was found on '{type}'.{detail}");
        }

        internal static string GetKind(Type type)
        {
            if (type.IsEnum) return "enum";
            if (type.IsInterface) return "interface";
            if (typeof(Delegate).IsAssignableFrom(type)) return "delegate";
            if (type.IsValueType) return "struct";
            return "class";
        }

        internal static string FormatType(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(bool)) return "bool";
            if (type.IsArray) return FormatType(type.GetElementType()!) + "[]";
            return type.FullName ?? type.Name;
        }

        internal static string FormatMethod(MethodInfo method)
        {
            var parameters = method.GetParameters()
                .Select(parameter => $"{FormatType(parameter.ParameterType)} {parameter.Name}");
            return $"{FormatType(method.ReturnType)} {method.Name}({string.Join(", ", parameters)})";
        }
    }
}
