#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace YuzeToolkit
{
    [EvalTool("Project", "Project settings, profiler, and editor tool diagnostics.")]
    public sealed partial class ProjectTool
    {
        [EvalFunction("Read project settings.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getProjectSettings()
        {
            return EvalData.Obj(
                ("productName", PlayerSettings.productName),
                ("companyName", PlayerSettings.companyName),
                ("applicationIdentifier", PlayerSettings.applicationIdentifier),
                ("tags", UnityEditorInternal.InternalEditorUtility.tags.Cast<object?>().ToList()),
                ("layers", UnityEditorInternal.InternalEditorUtility.layers.Cast<object?>().ToList()));
        }

        [EvalFunction("Read profiler state.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getProfilerState()
        {
            var driver = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.ProfilerDriver");
            object? isEnabled = null;
            object? isRecording = null;
            if (driver != null)
            {
                isEnabled = driver.GetProperty("enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                isRecording = driver.GetProperty("profileEditor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            }

            return EvalData.Obj(
                ("profilerDriverAvailable", driver != null),
                ("enabled", isEnabled),
                ("profileEditor", isRecording));
        }

        [EvalFunction("Read MCP/editor tool state.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getToolState()
        {
            var toolManager = typeof(Editor).Assembly.GetType("UnityEditor.EditorTools.ToolManager");
            var activeToolType = toolManager?.GetProperty("activeToolType", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Type;
            return EvalData.Obj(
                ("activeToolType", activeToolType != null ? activeToolType.FullName ?? activeToolType.Name : string.Empty),
                ("pivotMode", Tools.pivotMode.ToString()),
                ("pivotRotation", Tools.pivotRotation.ToString()),
                ("current", Tools.current.ToString()));
        }
    }
}
