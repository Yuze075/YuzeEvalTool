#nullable enable
using System.Collections.Generic;
using System;
using System.Linq;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    [UnityEngine.Scripting.Preserve]
    [EvalTool("Objects", "Scene GameObject, hierarchy, and Transform query/edit operations.")]
    public sealed partial class ObjectsTool
    {
        private const int DefaultFindLimit = 20;

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Find active scene GameObjects by exact name. Use findByPath for hierarchy paths, findByTag for tags, and Runtime/Components.find for component types.",
            Safety = EvalToolSafety.ReadOnly)]
        public List<Dictionary<string, object?>> find(string name, int limit = DefaultFindLimit)
        {
            return ToolUtilities.FindActiveGameObjectsByName(name, Math.Max(1, limit))
                .Select(go => ToolUtilities.SummarizeGameObject(go, false))
                .ToList();
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Find the first active scene GameObject by exact name and return a lightweight selector summary.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?>? findOne(string name)
        {
            var go = ToolUtilities.FindActiveGameObjectsByName(name, 1).FirstOrDefault();
            return go == null ? null : ToolUtilities.SummarizeGameObject(go, false);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Find one scene GameObject by exact hierarchy path. This explicit path query may include inactive objects.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?>? findByPath(string path, bool includeInactive = true)
        {
            var go = ToolUtilities.FindGameObjectByPath(path, includeInactive);
            return go == null ? null : ToolUtilities.SummarizeGameObject(go, false);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Find active scene GameObjects by tag using Unity GameObject.FindGameObjectsWithTag. Inactive tagged objects are intentionally unsupported.",
            Safety = EvalToolSafety.ReadOnly)]
        public List<Dictionary<string, object?>> findByTag(string tag, int limit = DefaultFindLimit)
        {
            return ToolUtilities.FindActiveGameObjectsByTag(tag, Math.Max(1, limit))
                .Select(go => ToolUtilities.SummarizeGameObject(go, false))
                .ToList();
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Resolve one GameObject and return its summary. Target may be instance id, exact name/path string, GameObject, Component, or selector object with instanceId/path/name.",
            Safety = EvalToolSafety.ReadOnly)]
        public GameObject get(object target)
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            return go;
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Create a GameObject, optionally as a Unity primitive and optionally parented. Position values accept {x,y,z} objects or arrays.",
            Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> create(string name = "GameObject", string primitive = "",
            object? parent = null, object? localPosition = null, object? position = null, object? localScale = null)
        {
            GameObject go;
            if (string.IsNullOrWhiteSpace(primitive) || primitive.Equals("empty", StringComparison.OrdinalIgnoreCase))
                go = new GameObject(name);
            else if (Enum.TryParse<PrimitiveType>(primitive, true, out var primitiveType))
            {
                go = GameObject.CreatePrimitive(primitiveType);
                go.name = name;
            }
            else
                throw new InvalidOperationException($"Unknown primitive type '{primitive}'.");

            ToolUtilities.RegisterCreatedObjectUndo(go, "MCP Create GameObject");

            if (parent != null)
            {
                var parentObject = ToolUtilities.ResolveGameObject(parent);
                if (parentObject == null)
                {
                    ToolUtilities.DestroyObject(go);
                    throw new InvalidOperationException("Parent GameObject was not found or is ambiguous.");
                }

                ToolUtilities.RecordUndo(go.transform, "MCP Set Parent");
                go.transform.SetParent(parentObject.transform, false);
            }

            ToolUtilities.RecordUndo(go.transform, "MCP Set Transform");
            if (localPosition != null)
                go.transform.localPosition = ToolUtilities.ToVector3(localPosition, go.transform.localPosition);
            if (position != null) go.transform.position = ToolUtilities.ToVector3(position, go.transform.position);
            if (localScale != null)
                go.transform.localScale = ToolUtilities.ToVector3(localScale, go.transform.localScale);
            ToolUtilities.MarkDirty(go);
            return ToolUtilities.SummarizeGameObject(go);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Destroy one resolved GameObject. Requires confirm: true because this is destructive.",
            Safety = EvalToolSafety.MutatesScene | EvalToolSafety.Destructive | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> destroy(object target, bool confirm = false)
        {
            if (!confirm)
                throw new InvalidOperationException("Destroy requires confirm: true.");

            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            var summary = ToolUtilities.SummarizeGameObject(go, false);
            ToolUtilities.DestroyObject(go);
            return EvalData.Obj(("destroyed", summary));
        }

        [EvalFunction(
            "Duplicate one resolved GameObject under the same parent and optionally assign a new name.",
            Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> duplicate(object target, string name = "")
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            var clone = UnityEngine.Object.Instantiate(go, go.transform.parent);
            clone.name = string.IsNullOrWhiteSpace(name) ? go.name + " Copy" : name;
            ToolUtilities.RegisterCreatedObjectUndo(clone, "MCP Duplicate GameObject");
            ToolUtilities.MarkDirty(clone);
            return ToolUtilities.SummarizeGameObject(clone);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Change a GameObject parent. Pass parent null to unparent; worldPositionStays controls whether world transform is preserved.",
            Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> setParent(object target, object? parent = null,
            bool worldPositionStays = true)
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            Transform? parentTransform = null;
            if (parent != null)
            {
                var parentObject = ToolUtilities.ResolveGameObject(parent);
                if (parentObject == null)
                    throw new InvalidOperationException("Parent GameObject was not found or is ambiguous.");
                parentTransform = parentObject.transform;
            }

            ToolUtilities.RecordUndo(go.transform, "MCP Set Parent");
            go.transform.SetParent(parentTransform, worldPositionStays);
            ToolUtilities.MarkDirty(go);
            return ToolUtilities.SummarizeGameObject(go);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Set position, localPosition, rotationEuler, localRotationEuler, or localScale on one GameObject. Vector values accept {x,y,z} objects or arrays.",
            Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> setTransform(object target, object? position = null,
            object? localPosition = null, object? rotationEuler = null, object? localRotationEuler = null,
            object? localScale = null)
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            ToolUtilities.RecordUndo(go.transform, "MCP Set Transform");
            if (position != null) go.transform.position = ToolUtilities.ToVector3(position, go.transform.position);
            if (localPosition != null)
                go.transform.localPosition = ToolUtilities.ToVector3(localPosition, go.transform.localPosition);
            if (rotationEuler != null)
                go.transform.eulerAngles = ToolUtilities.ToVector3(rotationEuler, go.transform.eulerAngles);
            if (localRotationEuler != null)
                go.transform.localEulerAngles =
                    ToolUtilities.ToVector3(localRotationEuler, go.transform.localEulerAngles);
            if (localScale != null)
                go.transform.localScale = ToolUtilities.ToVector3(localScale, go.transform.localScale);
            ToolUtilities.MarkDirty(go);
            return ToolUtilities.SummarizeGameObject(go);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Set one GameObject active or inactive.", Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> setActive(object target, bool active)
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            ToolUtilities.RecordUndo(go, "MCP Set Active");
            go.SetActive(active);
            ToolUtilities.MarkDirty(go);
            return ToolUtilities.SummarizeGameObject(go);
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction(
            "Set one GameObject's name, layer, and/or tag. Empty name or tag leaves that field unchanged; layer int.MinValue leaves layer unchanged.",
            Safety = EvalToolSafety.MutatesScene)]
        public Dictionary<string, object?> setNameLayerTag(object target, string name = "", int layer = int.MinValue,
            string tag = "")
        {
            var go = ToolUtilities.ResolveGameObject(target);
            if (go == null) throw new InvalidOperationException("Target GameObject was not found or is ambiguous.");
            ToolUtilities.RecordUndo(go, "MCP Set Name Layer Tag");
            if (!string.IsNullOrEmpty(name)) go.name = name;
            if (!string.IsNullOrEmpty(tag)) go.tag = tag;
            if (layer != int.MinValue) go.layer = layer;
            ToolUtilities.MarkDirty(go);
            return ToolUtilities.SummarizeGameObject(go);
        }
    }
}
