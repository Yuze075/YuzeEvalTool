#nullable enable
using System.Collections.Generic;

namespace YuzeToolkit.Eval
{
    [UnityEngine.Scripting.Preserve]
    [EvalTool("UnityEval", "Yuze Eval Tool catalog, tool management, and authoring guidance.")]
    public sealed partial class ToolManagerTool
    {
        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Return a prompt that explains how to author a valid loader-backed JavaScript tool.", Safety = EvalToolSafety.ReadOnly)]
        public string getJsToolAuthoringPrompt() => EvalToolRegistry.GetJsToolAuthoringPrompt();

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("List all registered tools. C# and loader-backed JavaScript tools can be enabled or disabled.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> listTools(bool refresh = false) => EvalToolRegistry.GetIndex(refresh);

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Return full metadata for one tool.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getToolDetails(string name, bool refresh = false) =>
            EvalToolRegistry.GetToolDetails(name, refresh);

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Enable or disable a C# or JS tool. In the Editor this enabled state is persisted by tool path.",
            Safety = EvalToolSafety.MutatesEditorState)]
        public Dictionary<string, object?> setToolEnabled(string name, bool enabled) =>
            EvalToolRegistry.SetToolEnabled(name, enabled);
    }
}
