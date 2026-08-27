#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace YuzeToolkit
{
    [EvalTool("Prefabs", "Prefab instance, asset, stage, override, and unpack operations.")]
    public sealed partial class PrefabsTool
    {
        [EvalFunction("Instantiate prefab.", Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> instantiate(string path, object? parent = null, object? position = null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new InvalidOperationException($"Prefab '{path}' was not found.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            ToolUtilities.RegisterCreatedObjectUndo(instance, "MCP Instantiate Prefab");
            if (parent != null)
            {
                var parentObject = ToolUtilities.ResolveGameObject(parent);
                if (parentObject == null)
                {
                    ToolUtilities.DestroyObject(instance);
                    throw new InvalidOperationException("Parent GameObject was not found or is ambiguous.");
                }

                ToolUtilities.RecordUndo(instance.transform, "MCP Set Prefab Parent");
                instance.transform.SetParent(parentObject.transform, false);
            }
            if (position != null)
            {
                ToolUtilities.RecordUndo(instance.transform, "MCP Set Prefab Position");
                instance.transform.position = ToolUtilities.ToVector3(position, instance.transform.position);
            }

            ToolUtilities.MarkDirty(instance);
            return ToolUtilities.SummarizeGameObject(instance);
        }

        [EvalFunction("Create prefab from object.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> createFromObject(object target, string path, bool confirmOverwrite = false)
        {
            if (AssetDatabase.LoadMainAssetAtPath(path) != null && !confirmOverwrite)
                throw new InvalidOperationException("Creating over an existing prefab requires confirmOverwrite: true.");
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Argument 'path' is required.");
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            if (prefab == null) throw new InvalidOperationException($"Failed to create prefab '{path}'.");
            return AssetsTool.SummarizeAsset(path);
        }

        [EvalFunction("Create prefab variant.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> createVariant(string basePath, string path, bool confirmOverwrite = false)
        {
            if (AssetDatabase.LoadMainAssetAtPath(path) != null && !confirmOverwrite)
                throw new InvalidOperationException("Creating over an existing prefab variant requires confirmOverwrite: true.");
            if (string.IsNullOrWhiteSpace(basePath)) throw new InvalidOperationException("Argument 'basePath' is required.");
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Argument 'path' is required.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(basePath);
            if (prefab == null) throw new InvalidOperationException($"Prefab '{basePath}' was not found.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var saved = PrefabUtility.SaveAsPrefabAsset(instance, path);
                if (saved == null) throw new InvalidOperationException($"Failed to create prefab variant '{path}'.");
                return AssetsTool.SummarizeAsset(path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [EvalFunction("Open Prefab Stage.", Safety = EvalToolSafety.MutatesEditorState)]
        public Dictionary<string, object?> openStage(string path)
        {
            var stage = PrefabStageUtility.OpenPrefab(path);
            return EvalData.Obj(("assetPath", stage.assetPath), ("scenePath", stage.scene.path));
        }

        [EvalFunction("Close Prefab Stage.", Safety = EvalToolSafety.MutatesEditorState)]
        public string closeStage()
        {
            StageUtility.GoBackToPreviousStage();
            return "Prefab stage close requested.";
        }

        [EvalFunction("Save Prefab Stage.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> saveStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null) throw new InvalidOperationException("No prefab stage is open.");
            var savePrefab = stage.GetType().GetMethod("SavePrefab", BindingFlags.Instance | BindingFlags.NonPublic);
            if (savePrefab == null) throw new InvalidOperationException("PrefabStage.SavePrefab was not found in this Unity version.");
            try
            {
                savePrefab.Invoke(stage, Array.Empty<object>());
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(ex.InnerException?.Message ?? ex.Message);
            }
            AssetDatabase.SaveAssets();
            return EvalData.Obj(("assetPath", stage.assetPath), ("saved", true));
        }

        [EvalFunction("Get prefab overrides.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getOverrides(object target)
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            var overrides = PrefabUtility.GetObjectOverrides(go)
                .Select(ov => (object?)EvalData.Obj(
                    ("instanceObject", ov.instanceObject != null ? ov.instanceObject.name : string.Empty),
                    ("instanceId", ov.instanceObject != null ? ov.instanceObject.GetInstanceID() : 0)))
                .ToList();
            var modifications = PrefabUtility.GetPropertyModifications(go)?
                .Select(mod => (object?)EvalData.Obj(
                    ("target", mod.target != null ? mod.target.name : string.Empty),
                    ("targetInstanceId", mod.target != null ? mod.target.GetInstanceID() : 0),
                    ("propertyPath", mod.propertyPath),
                    ("value", mod.value),
                    ("objectReference", mod.objectReference != null ? SerializedTool.SummarizeObject(mod.objectReference) : null)))
                .ToList() ?? new List<object?>();
            return EvalData.Obj(
                ("count", overrides.Count),
                ("overrides", overrides),
                ("propertyModificationCount", modifications.Count),
                ("propertyModifications", modifications)
            );
        }

        [EvalFunction("Apply prefab overrides.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> applyOverrides(object target, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Applying prefab overrides requires confirm: true.");
            var root = ResolvePrefabRoot(target);
            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.AutomatedAction);
            return ToolUtilities.SummarizeGameObject(root, false);
        }

        [EvalFunction("Revert prefab overrides.", Safety = EvalToolSafety.MutatesScene | EvalToolSafety.Destructive | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> revertOverrides(object target, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Reverting prefab overrides requires confirm: true.");
            var root = ResolvePrefabRoot(target);
            PrefabUtility.RevertPrefabInstance(root, InteractionMode.AutomatedAction);
            return ToolUtilities.SummarizeGameObject(root, false);
        }

        [EvalFunction("Unpack prefab instance.", Safety = EvalToolSafety.MutatesScene | EvalToolSafety.Destructive | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> unpack(object target, string mode = "OutermostRoot", bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Unpacking a prefab instance requires confirm: true.");
            var root = ResolvePrefabRoot(target);
            var unpackMode = Enum.TryParse<PrefabUnpackMode>(mode, true, out var parsed) ? parsed : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(root, unpackMode, InteractionMode.AutomatedAction);
            return ToolUtilities.SummarizeGameObject(root, false);
        }

        private static GameObject ResolvePrefabRoot(object target)
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            if (root == null) throw new InvalidOperationException("Target is not part of a prefab instance.");
            return root;
        }
    }
}
