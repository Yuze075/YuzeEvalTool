#nullable enable
using System;
using System.Collections.Generic;

namespace YuzeToolkit.Eval
{
    [UnityEngine.Scripting.Preserve]
    [EvalTool("Inspect", "Format C#/Unity object references into AI-readable data.")]
    public sealed partial class InspectTool
    {
        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Return a default summary DTO.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> describe(object? value, int depth = 4) =>
            EvalValueFormatter.Describe(ResolveInspectable(value), depth);

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Format a value with mode: default, summary, name, path, text, json, yaml.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> format(object? value, string mode = "default", int depth = 4) =>
            EvalValueFormatter.Describe(EvalValueFormatter.Format(ResolveInspectable(value), mode, depth), depth);

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Return a Unity/C# object's name.", Safety = EvalToolSafety.ReadOnly)]
        public string toName(object? value) => EvalValueFormatter.Format(ResolveInspectable(value), "name") as string ?? string.Empty;

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Return a scene hierarchy path or asset path.", Safety = EvalToolSafety.ReadOnly)]
        public string toPath(object? value) => EvalValueFormatter.Format(ResolveInspectable(value), "path") as string ?? string.Empty;

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Return a JSON string for a formatted value.", Safety = EvalToolSafety.ReadOnly)]
        public string toJson(object? value, string mode = "json", int depth = 4) =>
            EvalValueFormatter.ToJson(ResolveInspectable(value), mode, depth);

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Return a YAML string for a formatted value.", Safety = EvalToolSafety.ReadOnly)]
        public string toYaml(object? value, int depth = 4) =>
            EvalValueFormatter.Format(ResolveInspectable(value), "yaml", depth) as string ?? string.Empty;

        private static object? ResolveInspectable(object? value)
        {
            if (value is UnityEngine.Object) return value;
            return ToolUtilities.ResolveGameObject(value) ?? value;
        }
    }
}
