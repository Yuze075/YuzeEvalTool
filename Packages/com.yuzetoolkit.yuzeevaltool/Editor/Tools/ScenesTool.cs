#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace YuzeToolkit
{
    [EvalTool("Scenes", "Scene file and open scene hierarchy operations.")]
    public sealed partial class ScenesTool
    {
        [EvalFunction("List open scenes.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> listOpenScenes()
        {
            var scenes = new List<object?>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
                scenes.Add(SummarizeScene(SceneManager.GetSceneAt(i), false));
            return EvalData.Obj(("count", scenes.Count), ("scenes", scenes));
        }

        internal static Dictionary<string, object?> SummarizeScene(Scene scene, bool includeRoots)
        {
            return EvalData.Obj(
                ("name", scene.name),
                ("path", scene.path),
                ("handle", scene.handle),
                ("isLoaded", scene.isLoaded),
                ("isDirty", scene.isDirty),
                ("isValid", scene.IsValid()),
                ("rootCount", scene.IsValid() && scene.isLoaded ? scene.rootCount : 0),
                ("roots", includeRoots ? ToolUtilities.GetRootSummaries(scene) : new List<object?>()));
        }

        [EvalFunction("Get scene hierarchy.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getSceneHierarchy(int depth = 2, bool includeComponents = false, int limit = 200)
        {
            var scene = SceneManager.GetActiveScene();
            depth = Math.Max(0, depth);
            limit = Math.Max(0, limit);
            return EvalData.Obj(
                ("name", scene.name),
                ("path", scene.path),
                ("handle", scene.handle),
                ("isLoaded", scene.isLoaded),
                ("isDirty", scene.isDirty),
                ("isValid", scene.IsValid()),
                ("rootCount", scene.IsValid() && scene.isLoaded ? scene.rootCount : 0),
                ("depth", depth),
                ("includeComponents", includeComponents),
                ("roots", ToolUtilities.GetHierarchySummaries(scene, depth, includeComponents, limit))
            );
        }

        [EvalFunction("Open a scene.", Safety = EvalToolSafety.MutatesEditorState | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> openScene(string path, string mode = "Single", bool confirm = false, bool saveDirtyScenes = false)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Argument 'path' is required.");
            var openMode = Enum.TryParse<OpenSceneMode>(mode, true, out var parsed) ? parsed : OpenSceneMode.Single;
            EnsureSceneReplacementAllowed(openMode == OpenSceneMode.Single, confirm, saveDirtyScenes);
            var scene = EditorSceneManager.OpenScene(path, openMode);
            return SummarizeScene(scene, true);
        }

        [EvalFunction("Create a scene.", Safety = EvalToolSafety.MutatesEditorState | EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> createScene(string path = "", string setup = "DefaultGameObjects", string mode = "Single", bool confirm = false, bool saveDirtyScenes = false)
        {
            var sceneSetup = Enum.TryParse<NewSceneSetup>(setup, true, out var parsedSetup) ? parsedSetup : NewSceneSetup.DefaultGameObjects;
            var sceneMode = Enum.TryParse<NewSceneMode>(mode, true, out var parsedMode) ? parsedMode : NewSceneMode.Single;
            EnsureSceneReplacementAllowed(sceneMode == NewSceneMode.Single, confirm, saveDirtyScenes);
            var scene = EditorSceneManager.NewScene(sceneSetup, sceneMode);
            if (!string.IsNullOrWhiteSpace(path))
                EditorSceneManager.SaveScene(scene, path);
            return SummarizeScene(scene, true);
        }

        [EvalFunction("Save active scene.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> saveScene()
        {
            var scene = SceneManager.GetActiveScene();
            var ok = EditorSceneManager.SaveScene(scene);
            if (!ok) throw new InvalidOperationException("Failed to save active scene.");
            return SummarizeScene(scene, false);
        }

        [EvalFunction("Save scene as path.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> saveSceneAs(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Argument 'path' is required.");
            var scene = SceneManager.GetActiveScene();
            var ok = EditorSceneManager.SaveScene(scene, path);
            if (!ok) throw new InvalidOperationException($"Failed to save scene as '{path}'.");
            return SummarizeScene(scene, false);
        }

        [EvalFunction("Set active scene.", Safety = EvalToolSafety.MutatesEditorState)]
        public Dictionary<string, object?> setActiveScene(string path)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.path != path && scene.name != path) continue;
                SceneManager.SetActiveScene(scene);
                return SummarizeScene(scene, false);
            }
            throw new InvalidOperationException($"Open scene '{path}' was not found.");
        }

        private static void EnsureSceneReplacementAllowed(bool replacesOpenScenes, bool confirm, bool saveDirtyScenes)
        {
            if (!replacesOpenScenes) return;
            if (!confirm)
                throw new InvalidOperationException("Replacing open scenes requires confirm: true.");
            if (!HasDirtyOpenScenes()) return;
            if (!saveDirtyScenes)
                throw new InvalidOperationException("Open scenes contain unsaved changes. Pass saveDirtyScenes: true to save them before replacing.");
            if (!EditorSceneManager.SaveOpenScenes())
                throw new InvalidOperationException("Failed to save dirty open scenes.");
        }

        private static bool HasDirtyOpenScenes()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty) return true;
            }
            return false;
        }
    }
}
