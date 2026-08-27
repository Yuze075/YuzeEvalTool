#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace YuzeToolkit
{
    public sealed partial class EvalCliCommandService
    {
        private enum Missing
        {
            Value
        }

        private sealed class CliCommandException : Exception
        {
            public CliCommandException(string message) : base(message)
            {
            }
        }

        private sealed class CliTool
        {
            private CliTool(
                string requestedName,
                string name,
                string path,
                string description,
                bool enabled,
                bool isAlias,
                IReadOnlyList<CliFunction> functions,
                IReadOnlyList<string> subToolPaths)
            {
                CommandName = requestedName;
                DisplayName = name;
                Path = path;
                Description = description;
                Enabled = enabled;
                IsAlias = isAlias;
                Functions = functions;
                SubToolPaths = subToolPaths;
            }

            public string CommandName { get; }

            public string DisplayName { get; }

            public string Path { get; }

            public string Description { get; }

            public bool Enabled { get; }

            public bool IsAlias { get; }

            public IReadOnlyList<CliFunction> Functions { get; }

            public IReadOnlyList<string> SubToolPaths { get; }

            public CliFunction? FindFunction(string methodName) =>
                Functions.FirstOrDefault(function =>
                    string.Equals(function.MethodName, methodName, StringComparison.OrdinalIgnoreCase));

            public CliTool WithAlias(string alias) =>
                new(alias, DisplayName, Path, Description, Enabled, true, Functions, SubToolPaths);

            public static CliTool FromDetails(string requestedName, Dictionary<string, object?> data)
            {
                var path = EvalData.GetString(data, "path") ?? requestedName;
                var functions = new List<CliFunction>();
                foreach (var rawFunction in EvalData.AsArray(data.TryGetValue("functions", out var f) ? f : null) ??
                                            new List<object?>())
                {
                    var functionObj = EvalData.AsObject(rawFunction);
                    if (functionObj == null) continue;
                    functions.Add(CliFunction.FromJson(functionObj));
                }

                var subToolPaths = new List<string>();
                foreach (var rawSubTool in EvalData.AsArray(data.TryGetValue("subTools", out var s) ? s : null) ??
                                           new List<object?>())
                {
                    var subToolObj = EvalData.AsObject(rawSubTool);
                    var subToolPath = subToolObj == null
                        ? string.Empty
                        : EvalData.GetString(subToolObj, "path") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(subToolPath))
                        subToolPaths.Add(subToolPath);
                }

                return new CliTool(
                    requestedName,
                    EvalData.GetString(data, "name") ?? path,
                    path,
                    EvalData.GetString(data, "description") ?? string.Empty,
                    EvalData.GetBool(data, "enabled", true),
                    !string.Equals(requestedName, path, StringComparison.Ordinal),
                    functions,
                    subToolPaths);
            }
        }

        private sealed class CliFunction
        {
            private CliFunction(string methodName, string description, IReadOnlyList<CliParameter> parameters)
            {
                MethodName = methodName;
                Description = description;
                Parameters = parameters;
            }

            public string MethodName { get; }

            public string Description { get; }

            public IReadOnlyList<CliParameter> Parameters { get; }

            public static CliFunction FromJson(Dictionary<string, object?> data)
            {
                var parameterObjects = new List<Dictionary<string, object?>>();
                foreach (var rawParameter in EvalData.AsArray(data.TryGetValue("parameters", out var p) ? p : null) ??
                                             new List<object?>())
                {
                    var parameterObj = EvalData.AsObject(rawParameter);
                    if (parameterObj == null) continue;
                    parameterObjects.Add(parameterObj);
                }

                var parameterNames = parameterObjects
                    .Select(parameter => EvalData.GetString(parameter, "name") ?? string.Empty)
                    .ToList();
                var parameters = parameterObjects
                    .Select(parameter => CliParameter.FromJson(parameter, parameterNames))
                    .ToList();

                return new CliFunction(
                    EvalData.GetString(data, "methodName") ?? EvalData.GetString(data, "name") ?? string.Empty,
                    EvalData.GetString(data, "description") ?? string.Empty,
                    parameters);
            }
        }

        private sealed class CliParameter
        {
            private CliParameter(
                string name,
                string type,
                bool optional,
                object? defaultValue,
                string description,
                IReadOnlyList<string> flags)
            {
                Name = name;
                Type = type;
                Optional = optional;
                DefaultValue = defaultValue;
                Description = description;
                Flags = flags;
            }

            public string Name { get; }

            public string Type { get; }

            public bool Optional { get; }

            public object? DefaultValue { get; }

            public string Description { get; }

            public IReadOnlyList<string> Flags { get; }

            public static CliParameter FromJson(
                Dictionary<string, object?> data,
                IReadOnlyList<string> allParameterNames)
            {
                var name = EvalData.GetString(data, "name") ?? string.Empty;
                var type = EvalData.GetString(data, "type") ?? "string";
                return new CliParameter(
                    name,
                    type,
                    EvalData.GetBool(data, "optional", false),
                    data.TryGetValue("defaultValue", out var defaultValue) ? defaultValue : null,
                    EvalData.GetString(data, "description") ?? string.Empty,
                    BuildFlags(name, allParameterNames));
            }

            private static IReadOnlyList<string> BuildFlags(string name, IReadOnlyList<string> allParameterNames)
            {
                var result = new List<string>();
                if (string.IsNullOrWhiteSpace(name)) return result;

                AddUnique(result, "--" + ToKebabCase(name));
                AddUnique(result, "--" + name);

                var shortFlag = "-" + char.ToLowerInvariant(name[0]);
                var shortFlagIsUnique = allParameterNames.Count(parameterName =>
                    !string.IsNullOrWhiteSpace(parameterName) &&
                    char.ToLowerInvariant(parameterName[0]) == char.ToLowerInvariant(name[0])) == 1;
                if (shortFlagIsUnique && !string.Equals(shortFlag, "-h", StringComparison.OrdinalIgnoreCase))
                    AddUnique(result, shortFlag);

                return result;
            }

            private static void AddUnique(List<string> values, string value)
            {
                if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    values.Add(value);
            }
        }
    }
}
