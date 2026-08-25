#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YuzeToolkit
{
    [EvalTool("Editor", "Editor state, compilation, selection, menu commands, play mode, and screenshots.")]
    [EvalSubTool(typeof(AssetsTool))]
    [EvalSubTool(typeof(ImportersTool))]
    [EvalSubTool(typeof(ScenesTool))]
    [EvalSubTool(typeof(PrefabsTool))]
    [EvalSubTool(typeof(SerializedTool))]
    [EvalSubTool(typeof(ProjectTool))]
    [EvalSubTool(typeof(ProfilerTool))]
    [EvalSubTool(typeof(PipelineTool))]
    [EvalSubTool(typeof(TestsTool))]
    [EvalSubTool(typeof(CodeUsagesTool))]
    [EvalSubTool(typeof(ValidationTool))]
    public sealed partial class EditorTool
    {
        [EvalFunction("Return Editor state.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getState()
        {
            var scene = SceneManager.GetActiveScene();
            var selection = new List<object?>();
            foreach (var obj in Selection.objects)
            {
                if (obj == null) continue;
                selection.Add(EvalData.Obj(
                    ("name", obj.name),
                    ("type", obj.GetType().FullName ?? obj.GetType().Name),
                    ("instanceId", obj.GetInstanceID())
                ));
            }

            return EvalData.Obj(
                ("environment", ToolUtilities.GetEnvironmentObject()),
                ("isPlaying", EditorApplication.isPlaying),
                ("isPaused", EditorApplication.isPaused),
                ("isCompiling", EditorApplication.isCompiling),
                ("isUpdating", EditorApplication.isUpdating),
                ("isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode),
                ("isChangingPlayMode", EditorStatusProvider.IsChangingPlayMode),
                ("evalBusyReason", EditorStatusProvider.GetEvalBusyReason() ?? string.Empty),
                ("applicationPath", Application.dataPath + "/.."),
                ("dataPath", Application.dataPath),
                ("unityVersion", Application.unityVersion),
                ("activeScene", EvalData.Obj(
                    ("name", scene.name),
                    ("path", scene.path),
                    ("isDirty", scene.isDirty),
                    ("isLoaded", scene.isLoaded),
                    ("rootCount", scene.rootCount)
                )),
                ("selection", EvalData.Obj(
                    ("count", selection.Count),
                    ("activeInstanceId", Selection.activeObject != null ? Selection.activeObject.GetInstanceID() : 0),
                    ("activeObjectName", Selection.activeObject != null ? Selection.activeObject.name : string.Empty),
                    ("items", selection)
                ))
            );
        }

        [EvalFunction("Return compilation/import state and the last compilation or refresh request.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getCompilationState() => EditorCompilationMonitor.GetStateObject();

        [EvalFunction("Request script compilation and return the observable request state. If Unity is playing or changing play mode, the request exits PlayMode first and runs after stable EditMode.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.TriggersReload)]
        public Dictionary<string, object?> requestScriptCompilation()
        {
            return EditorCompilationMonitor.RequestScriptCompilation();
        }

        [EvalFunction("Request AssetDatabase refresh for script changes and return the observable request state. If Unity is playing or changing play mode, the request exits PlayMode first and runs after stable EditMode.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.TriggersReload)]
        public Dictionary<string, object?> scheduleAssetRefresh()
        {
            return EditorCompilationMonitor.RefreshAssetDatabaseNow();
        }

        [EvalFunction("Read compiler messages.", Safety = EvalToolSafety.ReadOnly)]
        public List<object?> getCompilerMessages(int count = 50) => UnityLogBuffer.GetCompilerLikeMessages(count);

        [EvalFunction("Enter or exit play mode.", Safety = EvalToolSafety.MutatesEditorState)]
        public Dictionary<string, object?> setPlayMode(bool isPlaying)
        {
            EditorApplication.isPlaying = isPlaying;
            return EditorStatusProvider.GetStateObject();
        }

        [EvalFunction("Set pause state.", Safety = EvalToolSafety.MutatesEditorState)]
        public Dictionary<string, object?> setPause(bool isPaused)
        {
            EditorApplication.isPaused = isPaused;
            return EditorStatusProvider.GetStateObject();
        }

        [EvalFunction("Execute an Editor menu item.", Safety = EvalToolSafety.MutatesEditorState | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> executeMenuItem(string path, bool confirm = false)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Argument 'path' is required.");
            if (!path.StartsWith("UnityEvalTool/", StringComparison.Ordinal) && !confirm)
                throw new InvalidOperationException("Executing arbitrary menu items requires confirm: true.");
            var ok = EditorApplication.ExecuteMenuItem(path);
            return EvalData.Obj(("path", path), ("executed", ok));
        }

        [EvalFunction("Read current selection.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getSelection()
        {
            var items = Selection.objects
                .Where(obj => obj != null)
                .Select(obj => (object?)EvalData.Obj(
                    ("name", obj.name),
                    ("type", obj.GetType().FullName ?? obj.GetType().Name),
                    ("instanceId", obj.GetInstanceID()),
                    ("assetPath", AssetDatabase.GetAssetPath(obj))))
                .ToList();
            return EvalData.Obj(
                ("count", items.Count),
                ("activeInstanceId", Selection.activeObject != null ? Selection.activeObject.GetInstanceID() : 0),
                ("items", items));
        }

        [EvalFunction("Set selection.", Safety = EvalToolSafety.MutatesEditorState)]
        public Dictionary<string, object?> setSelection(object items)
        {
            var objects = new List<UnityEngine.Object>();
            foreach (var item in EvalData.AsArray(items) ?? new List<object?>())
            {
                UnityEngine.Object? obj = null;
                if (item is int id) obj = EditorUtility.InstanceIDToObject(id);
                if (item is long longId) obj = EditorUtility.InstanceIDToObject(checked((int)longId));
                if (item is string path) obj = AssetDatabase.LoadMainAssetAtPath(path) ?? ToolUtilities.ResolveGameObject(path);
                if (EvalData.AsObject(item) is { } selector)
                {
                    var assetPath = EvalData.GetString(selector, "assetPath");
                    obj = !string.IsNullOrWhiteSpace(assetPath) ? AssetDatabase.LoadMainAssetAtPath(assetPath) : ToolUtilities.ResolveGameObject(selector);
                }
                if (obj != null) objects.Add(obj);
            }
            Selection.objects = objects.ToArray();
            return getSelection();
        }

        [EvalFunction("Capture Game View screenshot.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> screenshotGameView(string path = "Temp/UnityEvalTool-GameView.png")
        {
            if (!ToolUtilities.TryResolveProjectPath(path, out var fullPath, out var projectPath, out var error))
                throw new InvalidOperationException(error);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            ScreenCapture.CaptureScreenshot(fullPath);
            return EvalData.Obj(("path", projectPath), ("fullPath", fullPath), ("message", "Screenshot capture was requested. The file may be written after the current frame."));
        }

        [EvalFunction("Synchronously capture a PNG from the Game View, Scene View, or a visible Editor window. Hard limits are 8,388,608 source pixels for Editor windows, 16,777,216 output pixels, and 33,554,432 encoded PNG bytes.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> captureViewport(
            [EvalParameter("Capture source: game, scene, or editor_window.")]
            string target = "game",
            [EvalParameter("Zero preserves source dimensions; 1..8192 proportionally downsizes the longest edge.")]
            int maxLongEdge = 0,
            [EvalParameter("For editor_window, optional title, short C# type, or full C# type query. The matched tab must be visible.")]
            string windowQuery = "")
        {
            return EditorViewportCapture.Capture(target, maxLongEdge, windowQuery);
        }
    }
}
