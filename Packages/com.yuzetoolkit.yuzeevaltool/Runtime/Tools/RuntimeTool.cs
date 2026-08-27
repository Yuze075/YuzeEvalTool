#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YuzeToolkit
{
    [UnityEngine.Scripting.Preserve]
    [EvalTool("Runtime", "Environment state and Unity log buffer access.")]
    [EvalSubTool(typeof(ObjectsTool))]
    [EvalSubTool(typeof(ComponentsTool))]
    [EvalSubTool(typeof(DiagnosticsTool))]
    [EvalSubTool(typeof(ReflectionTool))]
    [EvalSubTool(typeof(InspectTool))]
    [EvalSubTool(typeof(ObserveFramesTool))]
    public sealed partial class RuntimeTool
    {
        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Return environment, Unity version, platform, play state, paths, active scene, and registered tools.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getState()
        {
            var scene = SceneManager.GetActiveScene();
            var registeredTools = EvalToolRegistry.ListSummaries();
            return EvalData.Obj(
                ("environment", ToolUtilities.GetEnvironmentObject()),
                ("unityVersion", Application.unityVersion),
                ("platform", Application.platform.ToString()),
                ("isEditor", Application.isEditor),
                ("isRuntime", !Application.isEditor),
                ("isPlaying", Application.isPlaying),
                ("dataPath", Application.dataPath),
                ("persistentDataPath", Application.persistentDataPath),
                ("activeScene", EvalData.Obj(
                    ("name", scene.name),
                    ("path", scene.path),
                    ("isLoaded", scene.isLoaded),
                    ("rootCount", scene.rootCount)
                )),
                ("registeredToolCount", registeredTools.Count),
                ("registeredTools", registeredTools)
            );
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Return the newest captured Unity logs, optionally limited by count and log type.", Safety = EvalToolSafety.ReadOnly)]
        public List<object?> getRecentLogs(int count = 50, string type = "all")
        {
            return UnityLogBuffer.GetRecent(count, type);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Clear only the eval tool log buffer.", Safety = EvalToolSafety.MutatesRuntimeState)]
        public string clearLogs()
        {
            UnityLogBuffer.Clear();
            return "Unity eval tool log buffer cleared.";
        }
    }
}
