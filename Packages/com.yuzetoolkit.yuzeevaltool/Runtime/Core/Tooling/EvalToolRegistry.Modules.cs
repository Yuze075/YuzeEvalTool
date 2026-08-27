#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace YuzeToolkit
{
    public static partial class EvalToolRegistry
    {
        private static readonly object ModuleSourceCacheSyncRoot = new();
        private static readonly Dictionary<string, string> ModuleSourceCache = new(StringComparer.Ordinal);

        public static bool ToolModuleExists(string toolPath)
        {
            var normalized = NormalizePath(toolPath);
            if (string.IsNullOrEmpty(normalized)) return true;
            if (TryResolveCSharp(normalized, out var csharpTool))
                return true;
            return JsToolPathMayExist(normalized);
        }

        public static bool TryGetModuleSource(string toolPath, out string source)
        {
            source = string.Empty;
            var normalized = NormalizePath(toolPath);
            lock (ModuleSourceCacheSyncRoot)
            {
                if (ModuleSourceCache.TryGetValue(normalized, out source))
                    return true;
            }

            if (TryResolveCSharp(toolPath, out var tool))
            {
                source = GenerateCSharpToolModule(ToDescriptor(tool));
                CacheModuleSource(normalized, source);
                return true;
            }

            if (!TryGetJsModuleSource(toolPath, out source))
                return false;

            CacheModuleSource(normalized, source);
            return true;
        }

        public static string GenerateIndexModuleSource()
        {
            var json = EvalJson.Stringify(GetIndex(false));
            return $@"let catalog = {json};

function readCatalog(refresh) {{
  const parsed = JSON.parse(CS.YuzeToolkit.EvalToolRegistry.GetToolCatalogJson(!!refresh));
  if (parsed && parsed.success === false) throw new Error(parsed.error || ""Eval tool catalog failed."");
  return parsed;
}}

function readToolDetails(path, refresh) {{
  const parsed = JSON.parse(CS.YuzeToolkit.EvalToolRegistry.GetToolDetailsJson(String(path || ''), !!refresh));
  if (parsed && parsed.success === false) throw new Error(parsed.error || ""Eval tool details failed."");
  return parsed;
}}

export let description = catalog.description;
export let tools = catalog.tools;

export function listTools() {{
  return tools;
}}

export function listRootTools() {{
  return tools;
}}

export function refreshTools() {{
  catalog = readCatalog(true);
  description = catalog.description;
  tools = catalog.tools;
  return {{ tools, description }};
}}

export function getToolDetails(path, refresh = false) {{
  return readToolDetails(path, refresh);
}}

export function describeTool(path, refresh = false) {{
  const details = readToolDetails(path, refresh);
  return {{
    name: details.name,
    path: details.path,
    description: details.description,
    editorOnly: details.editorOnly,
    enabled: details.enabled,
    source: details.source,
    functions: details.functions
  }};
}}
	";
        }

        private static void CacheModuleSource(string toolPath, string source)
        {
            lock (ModuleSourceCacheSyncRoot)
                ModuleSourceCache[toolPath] = source;
        }

        private static void ClearGeneratedModuleCache()
        {
            lock (ModuleSourceCacheSyncRoot)
                ModuleSourceCache.Clear();
            ClearJsDescriptorCache();
        }

        private static string GenerateCSharpToolModule(EvalToolDescriptor descriptor)
        {
            var json = EvalJson.Stringify(ToJson(descriptor));
            var builder = new StringBuilder();
            builder.AppendLine($"const descriptor = {json};");
            builder.AppendLine(@"
export const name = descriptor.name;
export const path = descriptor.path;
export const description = descriptor.description;
export const editorOnly = descriptor.editorOnly;
export const functions = descriptor.functions || [];

export function isEnabled() {
  return CS.YuzeToolkit.EvalToolRegistry.IsEnabled(descriptor.path);
}

function getInstance() {
  return CS.YuzeToolkit.EvalToolRegistry.GetRequiredInstance(descriptor.path);
}

function toSerializable(value) {
  return JSON.parse(CS.YuzeToolkit.EvalValueFormatter.ToJson(value));
}

function toToolArgument(value) {
  if (value === null || value === undefined) return value;
  const type = typeof value;
  if (type !== ""object"") return value;
  return CS.YuzeToolkit.EvalValueFormatter.FromJson(JSON.stringify(value));
}
");

            if (descriptor.SubTools.Count > 0)
            {
                builder.AppendLine(@"
export const subTools = descriptor.subTools || [];

export async function getSubTool(name) {
  const child = subTools.find(function(tool) { return tool.name === String(name) || tool.path === String(name); });
  if (!child) throw new Error('Sub tool ' + name + ' was not found under ' + descriptor.path + '.');
  return await import('tools://' + child.path);
}
");
            }

            foreach (var function in descriptor.Functions)
            {
                if (!IsValidJavaScriptIdentifier(function.MethodName)) continue;
                var escapedMethodName = function.MethodName;
                var functionJson = EvalJson.Stringify(ToFunctionJson(function));
                builder.AppendLine($"export function {escapedMethodName}(...args) {{");
                builder.AppendLine($"  return toSerializable(getInstance().{escapedMethodName}(...args.map(toToolArgument)));");
                builder.AppendLine("}");
                builder.AppendLine($"{escapedMethodName}.description = {functionJson}.description;");
                builder.AppendLine($"{escapedMethodName}.parameters = {functionJson}.parameters;");
            }

            return builder.ToString();
        }

        private static string EscapeJavaScriptString(string value)
        {
            var escaped = new StringBuilder(value.Length + 8);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '\'': escaped.Append("\\'"); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    case '\u2028': escaped.Append("\\u2028"); break;
                    case '\u2029': escaped.Append("\\u2029"); break;
                    default:
                        if (character < ' ')
                            escaped.Append("\\u").Append(((int)character).ToString("x4"));
                        else
                            escaped.Append(character);
                        break;
                }
            }

            return escaped.ToString();
        }
    }
}
