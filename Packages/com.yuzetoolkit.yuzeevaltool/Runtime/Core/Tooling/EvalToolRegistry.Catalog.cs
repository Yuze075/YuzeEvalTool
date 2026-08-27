#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YuzeToolkit
{
    public static partial class EvalToolRegistry
    {
        public static Dictionary<string, object?> GetIndex(bool refresh)
        {
            if (refresh) RefreshToolMetadataCaches();
            var tools = ListTools(false);
            return EvalData.Obj(
                ("toolImportPrefix", "tools://"),
                ("tools", tools.Select(ToSummaryJson).Cast<object?>().ToList()),
                ("description", BuildDescription(tools))
            );
        }

        public static Dictionary<string, object?> GetToolDetails(string path, bool refresh)
        {
            if (refresh) RefreshToolMetadataCaches();
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Tool path is required.");
            if (TryResolveCSharp(path, out var csharpTool))
                return ToJson(ToDescriptor(csharpTool));
            if (TryGetJsDescriptor(path, out var jsTool))
                return ToJson(jsTool);
            throw new InvalidOperationException($"Tool '{path}' was not found or is no longer available.");
        }

        public static Dictionary<string, object?> GetCliCatalog(bool refresh)
        {
            if (refresh) RefreshToolMetadataCaches();
            var tools = FlattenTools(ListTools(false));
            return EvalData.Obj(
                ("version", "2.0"),
                ("tools", tools.Select(ToCliJson).Cast<object?>().ToList()),
                ("commands", tools.SelectMany(ToCliCommands).Cast<object?>().ToList())
            );
        }

        public static IReadOnlyList<EvalToolDescriptor> ListTools(bool refresh = false)
        {
            if (refresh) RefreshToolMetadataCaches();
            return ListCSharpRoots()
                .Select(ToDescriptor)
                .Concat(ListJsRoots().Select(GetRequiredJsRootDescriptor))
                .OrderBy(tool => tool.Path, StringComparer.Ordinal)
                .ToList();
        }

        public static bool TryGetFunctionDescriptor(string toolPath, string functionName, out EvalToolFunctionDescriptor descriptor)
        {
            descriptor = null!;
            if (!TryResolveCSharp(toolPath, out var tool))
                return false;

            descriptor = tool.Functions.FirstOrDefault(function =>
                string.Equals(function.MethodName, functionName, StringComparison.Ordinal))!;
            return descriptor != null;
        }

        private static IReadOnlyList<EvalToolDescriptor> FlattenTools(IReadOnlyList<EvalToolDescriptor> roots)
        {
            var result = new List<EvalToolDescriptor>();
            var queue = new Queue<string>(roots.Select(root => root.Path));
            var seen = new HashSet<string>(StringComparer.Ordinal);

            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                if (!seen.Add(path)) continue;
                if (!TryGetDescriptor(path, out var descriptor))
                    throw new InvalidOperationException(
                        $"Tool metadata for '{path}' could not be loaded or failed validation.");
                result.Add(descriptor);
                foreach (var subTool in descriptor.SubTools)
                    queue.Enqueue(subTool.Path);
            }

            return result;
        }

        private static bool TryGetDescriptor(string path, out EvalToolDescriptor descriptor)
        {
            if (TryResolveCSharp(path, out var csharpTool))
            {
                descriptor = ToDescriptor(csharpTool);
                return true;
            }

            return TryGetJsDescriptor(path, out descriptor);
        }

        private static EvalToolDescriptor ToDescriptor(ResolvedTool tool)
        {
            return new EvalToolDescriptor(
                tool.Name,
                tool.Path,
                tool.Description,
                IsEditorOnlyAssembly(tool.ToolType),
                IsEnabled(tool.Path),
                "csharp",
                tool.Functions.Select(function => EvalToolSafetyUtility.Apply(tool.Path, function)).ToList(),
                tool.Instance.SubTools.Select(subTool => ToSummaryDescriptor(subTool, tool.Path + "/" + subTool.Name)).ToList()
            );
        }

        private static EvalToolDescriptor GetRequiredJsRootDescriptor(JsToolRegistration tool)
        {
            if (TryGetJsDescriptor(tool.Name, out var descriptor)) return descriptor;
            throw new InvalidOperationException(
                $"Registered JS tool '{tool.Name}' from module '{tool.ModulePath}' no longer provides valid metadata.");
        }

        private static EvalToolSummaryDescriptor ToSummaryDescriptor(IEvalTool tool, string path)
        {
            return new EvalToolSummaryDescriptor(
                tool.Name,
                path,
                tool.Description,
                IsEditorOnlyAssembly(tool.GetType()),
                IsEnabled(path),
                "csharp",
                tool.Functions.Count);
        }

        private static bool IsEditorOnlyAssembly(Type toolType) =>
            toolType.Assembly.GetName().Name?.IndexOf(".Editor", StringComparison.OrdinalIgnoreCase) >= 0;

        private static Dictionary<string, object?> ToJson(EvalToolDescriptor descriptor)
        {
            var result = EvalData.Obj(
                ("name", descriptor.Name),
                ("path", descriptor.Path),
                ("importPath", "tools://" + descriptor.Path),
                ("description", descriptor.Description),
                ("editorOnly", descriptor.EditorOnly),
                ("enabled", descriptor.Enabled),
                ("source", descriptor.Source),
                ("functions", descriptor.Functions.Select(ToFunctionJson).Cast<object?>().ToList())
            );
            if (descriptor.SubTools.Count > 0)
                result["subTools"] = descriptor.SubTools.Select(ToSummaryJson).Cast<object?>().ToList();
            return result;
        }

        private static Dictionary<string, object?> ToFunctionJson(EvalToolFunctionDescriptor function)
        {
            var result = EvalData.Obj(
                ("name", function.MethodName),
                ("methodName", function.MethodName),
                ("description", function.Description),
                ("safety", EvalToolSafetyUtility.ToJson(function.Safety)),
                ("riskLevel", function.RiskLevel),
                ("requiresConfirmation", function.RequiresConfirmation),
                ("parameters", function.Parameters.Select(parameter => ToParameterJson(function, parameter)).Cast<object?>().ToList())
            );
            AddConditionalSafetyMetadata(result, function);
            return result;
        }

        private static Dictionary<string, object?> ToParameterJson(EvalToolFunctionDescriptor function, EvalToolParameterDescriptor parameter) =>
            EvalData.Obj(
                ("name", parameter.Name),
                ("type", parameter.Type),
                ("optional", parameter.Optional),
                ("defaultValue", parameter.DefaultValue),
                ("description", GetParameterDescription(function, parameter))
            );

        private static Dictionary<string, object?> ToSummaryJson(EvalToolDescriptor descriptor) =>
            EvalData.Obj(
                ("name", descriptor.Name),
                ("path", descriptor.Path),
                ("importPath", "tools://" + descriptor.Path),
                ("description", descriptor.Description),
                ("editorOnly", descriptor.EditorOnly),
                ("enabled", descriptor.Enabled),
                ("source", descriptor.Source),
                ("functionCount", descriptor.Functions.Count)
            );

        private static Dictionary<string, object?> ToSummaryJson(EvalToolSummaryDescriptor descriptor) =>
            EvalData.Obj(
                ("name", descriptor.Name),
                ("path", descriptor.Path),
                ("importPath", "tools://" + descriptor.Path),
                ("description", descriptor.Description),
                ("editorOnly", descriptor.EditorOnly),
                ("enabled", descriptor.Enabled),
                ("source", descriptor.Source),
                ("functionCount", descriptor.FunctionCount)
            );

        private static Dictionary<string, object?> ToCliJson(EvalToolDescriptor descriptor) =>
            EvalData.Obj(
                ("name", descriptor.Name),
                ("path", descriptor.Path),
                ("importPath", "tools://" + descriptor.Path),
                ("description", descriptor.Description),
                ("editorOnly", descriptor.EditorOnly),
                ("enabled", descriptor.Enabled),
                ("source", descriptor.Source),
                ("functions", descriptor.Functions.Select(function => (object?)ToCliFunctionJson(descriptor, function)).ToList())
            );

        private static IEnumerable<object?> ToCliCommands(EvalToolDescriptor descriptor)
        {
            foreach (var function in descriptor.Functions)
                yield return ToCliFunctionJson(descriptor, function);
        }

        private static Dictionary<string, object?> ToCliFunctionJson(EvalToolDescriptor descriptor, EvalToolFunctionDescriptor function)
        {
            var result = EvalData.Obj(
                ("toolName", descriptor.Name),
                ("toolPath", descriptor.Path),
                ("name", function.MethodName),
                ("methodName", function.MethodName),
                ("command", descriptor.Path),
                ("description", function.Description),
                ("usage", BuildCliUsage(descriptor, function)),
                ("importPath", "tools://" + descriptor.Path),
                ("editorOnly", descriptor.EditorOnly),
                ("enabled", descriptor.Enabled),
                ("source", descriptor.Source),
                ("safety", EvalToolSafetyUtility.ToJson(function.Safety)),
                ("riskLevel", function.RiskLevel),
                ("requiresConfirmation", function.RequiresConfirmation),
                ("parameters", function.Parameters.Select(parameter => (object?)EvalData.Obj(
                    ("name", parameter.Name),
                    ("type", parameter.Type),
                    ("optional", parameter.Optional),
                    ("defaultValue", parameter.DefaultValue),
                    ("flags", BuildParameterFlags(parameter, function.Parameters)),
                    ("description", GetParameterDescription(function, parameter))
                )).ToList())
            );
            AddConditionalSafetyMetadata(result, function);
            return result;
        }

        private static void AddConditionalSafetyMetadata(Dictionary<string, object?> result, EvalToolFunctionDescriptor function)
        {
            var parameterNames = function.Parameters.Select(parameter => parameter.Name).ToList();
            var hasConfirmDangerous = parameterNames.Any(name =>
                string.Equals(name, "confirmDangerous", StringComparison.OrdinalIgnoreCase));
            var hasConfirmOverwrite = parameterNames.Any(name =>
                string.Equals(name, "confirmOverwrite", StringComparison.OrdinalIgnoreCase));
            if (hasConfirmDangerous)
            {
                result["conditionalRequiresConfirmation"] = true;
                result["conditionalReflectionDangerous"] = true;
                result["conditionalSafetyNote"] =
                    "Non-public, static, or reflection-heavy branches require confirmDangerous: true.";
            }
            else if (hasConfirmOverwrite)
            {
                result["conditionalRequiresConfirmation"] = true;
                result["conditionalDestructive"] = true;
                result["conditionalSafetyNote"] =
                    "Replacing an existing asset or file requires confirmOverwrite: true.";
            }
        }

        private static string GetParameterDescription(EvalToolFunctionDescriptor function, EvalToolParameterDescriptor parameter)
        {
            if (!string.IsNullOrWhiteSpace(parameter.Description))
                return parameter.Description;

            if (parameter.Name == "refresh" &&
                (function.MethodName == "listTools" || function.MethodName == "getToolDetails"))
                return "Whether to rebuild the tool catalog before returning metadata.";
            if (parameter.Name == "name" &&
                (function.MethodName == "getToolDetails" || function.MethodName == "setToolEnabled"))
                return "Tool path or tool name, such as Runtime/Objects.";

            return parameter.Name switch
            {
                "target" => "GameObject, Component, Unity object, instance id, exact name/path, or selector object.",
                "path" => "Project-relative asset path, scene path, menu path, or output path depending on the command.",
                "from" => "Source project-relative asset path.",
                "to" => "Destination project-relative asset path.",
                "filter" => "Unity AssetDatabase search filter.",
                "folders" => "Optional folder path or array of folder paths that limits the search scope.",
                "limit" => "Maximum number of results to return. Values <= 0 mean no explicit limit where supported.",
                "count" => "Maximum number of entries to return.",
                "type" => "C# type name, component type name, log type, or mode depending on the command.",
                "index" => "Zero-based component or array index. -1 means default selection where supported.",
                "value" => "Value to assign or format.",
                "values" => "Object map of memberName -> value.",
                "changes" => "Array of {propertyPath,value} entries or object map of propertyPath -> value.",
                "propertyPath" => "Unity serialized property path.",
                "propertyPathKey" => "Unity serialized property path.",
                "member" => "Public field or property name on the selected component.",
                "method" => "Method name to invoke.",
                "args" => "Positional argument array for the method call.",
                "confirm" => "Must be true for operations with destructive or broad side effects.",
                "confirmOverwrite" => "Must be true when replacing an existing asset or file.",
                "confirmDangerous" => "Must be true for non-public, static, or reflection-heavy operations.",
                "includeInactive" => "Whether inactive scene objects are included.",
                "includeComponents" => "Whether GameObject summaries include component summaries.",
                "includeProperties" => "Whether importer summaries include serialized importer properties.",
                "includeNonPublic" => "Whether non-public members or methods are included.",
                "includeStatic" => "Whether static members are included.",
                "recursive" => "Whether dependency lookup includes nested dependencies.",
                "refresh" => "Whether to refresh AssetDatabase after the file edit.",
                "saveAndReimport" => "Whether to call SaveAndReimport after importer edits.",
                "isPlaying" => "Desired Editor play mode state.",
                "isPaused" => "Desired Editor pause state.",
                "active" => "Desired GameObject active state.",
                "name" => "Name to assign or lookup.",
                "by" => "Lookup mode: name, path, tag, or component.",
                "parent" => "Parent GameObject selector. Null means no parent where supported.",
                "worldPositionStays" => "Whether world transform is preserved when changing parent.",
                "position" => "World position as {x,y,z} or [x,y,z].",
                "localPosition" => "Local position as {x,y,z} or [x,y,z].",
                "rotationEuler" => "World Euler rotation as {x,y,z} or [x,y,z].",
                "localRotationEuler" => "Local Euler rotation as {x,y,z} or [x,y,z].",
                "localScale" => "Local scale as {x,y,z} or [x,y,z].",
                "primitive" => "Unity PrimitiveType name, or empty for a plain GameObject.",
                "layer" => "Unity layer integer. int.MinValue leaves it unchanged where supported.",
                "tag" => "Unity tag string. Empty leaves it unchanged where supported.",
                "mode" => "Command-specific mode string.",
                "className" => "C# class name to generate. Empty uses the file name.",
                "namespaceName" => "Optional C# namespace for generated scripts.",
                "shaderName" => "Shader name used when creating a material.",
                "properties" => "Object map of material/importer/serialized properties depending on the command.",
                "packageId" => "Package Manager package id or git URL to add.",
                "packageName" => "Package name to remove or search.",
                "testName" => "Optional test filter name.",
                "locationPathName" => "Build output path passed to BuildPipeline.",
                "host" => "Host/IP address to bind or connect.",
                "port" => "Port number. 0 means choose an available port where supported.",
                "token" => "Bearer token for authenticated local connections.",
                "requireToken" => "Legacy Broker-global flag; always false because each Unity connection authorizes independently.",
                "enabled" => "Desired enabled state.",
                "depth" => "Maximum object formatting or hierarchy traversal depth.",
                "size" => "New serialized array size.",
                "fullName" => "Full C# type name.",
                "query" => "Text query used to filter results.",
                _ => $"Parameter '{parameter.Name}' of type {parameter.Type}."
            };
        }

        private static string BuildCliUsage(EvalToolDescriptor descriptor, EvalToolFunctionDescriptor function)
        {
            var builder = new StringBuilder();
            builder.Append(descriptor.Path);
            builder.Append(' ');
            builder.Append(function.MethodName);
            foreach (var parameter in function.Parameters)
            {
                builder.Append(' ');
                builder.Append(parameter.Optional ? '[' : '<');
                builder.Append("--");
                builder.Append(ToKebabCase(parameter.Name));
                if (!IsBoolType(parameter.Type))
                {
                    builder.Append(' ');
                    builder.Append(parameter.Type);
                }
                builder.Append(parameter.Optional ? ']' : '>');
            }
            return builder.ToString();
        }

        private static bool IsBoolType(string type) =>
            string.Equals(type.TrimEnd('?'), "bool", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type.TrimEnd('?'), "boolean", StringComparison.OrdinalIgnoreCase);

        private static List<object?> BuildParameterFlags(
            EvalToolParameterDescriptor parameter,
            IReadOnlyList<EvalToolParameterDescriptor> allParameters)
        {
            var flags = new List<object?>();
            var parameterName = parameter.Name;
            if (!string.IsNullOrWhiteSpace(parameterName))
            {
                AddUnique(flags, "--" + ToKebabCase(parameterName));
                AddUnique(flags, "--" + parameterName);
                var shortFlag = "-" + char.ToLowerInvariant(parameterName[0]);
                var shortFlagIsUnique = allParameters.Count(other =>
                    !string.IsNullOrWhiteSpace(other.Name) &&
                    char.ToLowerInvariant(other.Name[0]) == char.ToLowerInvariant(parameterName[0])) == 1;
                if (shortFlagIsUnique && !IsReservedCliShortFlag(shortFlag))
                    AddUnique(flags, shortFlag);
            }
            return flags;
        }

        private static bool IsReservedCliShortFlag(string flag) =>
            string.Equals(flag, "-h", StringComparison.OrdinalIgnoreCase);

        private static void AddUnique(List<object?> flags, string flag)
        {
            if (!flags.Contains(flag))
                flags.Add(flag);
        }

        private static string ToKebabCase(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var builder = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) builder.Append('-');
                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c == '_' ? '-' : c);
                }
            }
            return builder.ToString();
        }

        private static string BuildDescription(IReadOnlyList<EvalToolDescriptor> tools)
        {
            var lines = tools.Select(tool =>
            {
                var tags = new List<string> { tool.Source };
                if (tool.EditorOnly) tags.Add("Editor-only");
                if (!tool.Enabled) tags.Add("disabled");
                return $"- {tool.Path}: `tools://{tool.Path}` [{string.Join(", ", tags)}] - {tool.Description}";
            });

            return @$"Yuze Eval Tool module index.

Discovery:
- `listTools()` returns the root summaries below.
- `getToolDetails('Tool/Path')` returns that tool's functions, positional parameter order, defaults, safety flags, and direct `subTools` when present.
- Import only the tool you need with `await import('tools://Tool/Path')`; the imported module also exports its `functions` metadata.
- If `subTools` is absent, do not invent child paths. Plain imports use the configured JavaScript loader and are unrelated to Tool lookup.

Available root tools:
{string.Join("\n", lines)}

Direct C# fallback:
- Prefer helper modules. Use full names under `CS.*` only when no module covers the API.
- Use `puer.$typeof` for `System.Type`, `puer.$generic` with CLR arity names for runtime generics, `get_Item`/`set_Item` for C# indexers, `puer.$ref`/`puer.$unref` for ref/out, and `await puer.$promise(task)` for C# Task.
".Trim();
        }
    }
}
