#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

namespace YuzeToolkit.Eval
{
    [UnityEngine.Scripting.Preserve]
    [EvalTool("Diagnostics", "Read-only cameras, physics, graphics, UI, and loaded texture diagnostics.")]
    public sealed partial class DiagnosticsTool
    {
        [UnityEngine.Scripting.Preserve]
        [EvalFunction("List cameras.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> listCameras()
        {
            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(camera => camera != null && ToolUtilities.IsUsableSceneObject(camera.gameObject, true))
                .Select(camera => (object?)Summarize(camera))
                .ToList();
            return EvalData.Obj(("count", cameras.Count), ("cameras", cameras));
        }

        internal static object Summarize(Camera camera) =>
            EvalData.Obj(
                ("name", camera.name),
                ("instanceId", camera.GetInstanceID()),
                ("gameObject", ToolUtilities.SummarizeGameObject(camera.gameObject, false)),
                ("enabled", camera.enabled),
                ("tag", camera.tag),
                ("clearFlags", camera.clearFlags.ToString()),
                ("orthographic", camera.orthographic),
                ("orthographicSize", camera.orthographicSize),
                ("fieldOfView", camera.fieldOfView),
                ("nearClipPlane", camera.nearClipPlane),
                ("farClipPlane", camera.farClipPlane),
                ("depth", camera.depth));

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Read 2D/3D physics settings.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getPhysicsState()
        {
            var colliders2D = UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(collider => collider != null && ToolUtilities.IsUsableSceneObject(collider.gameObject, true))
                .Select(collider => (object?)EvalData.Obj(
                    ("type", collider.GetType().FullName ?? collider.GetType().Name),
                    ("enabled", collider.enabled),
                    ("isTrigger", collider.isTrigger),
                    ("gameObject", ToolUtilities.SummarizeGameObject(collider.gameObject, false))))
                .ToList();

            var colliders3D = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(collider => collider != null && ToolUtilities.IsUsableSceneObject(collider.gameObject, true))
                .Select(collider => (object?)EvalData.Obj(
                    ("type", collider.GetType().FullName ?? collider.GetType().Name),
                    ("enabled", collider.enabled),
                    ("isTrigger", collider.isTrigger),
                    ("attachedRigidbody", collider.attachedRigidbody != null
                        ? EvalData.Obj(("name", collider.attachedRigidbody.name), ("instanceId", collider.attachedRigidbody.GetInstanceID()))
                        : null),
                    ("gameObject", ToolUtilities.SummarizeGameObject(collider.gameObject, false))))
                .ToList();

            var rigidbodies3D = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(body => body != null && ToolUtilities.IsUsableSceneObject(body.gameObject, true))
                .Select(body => (object?)EvalData.Obj(
                    ("name", body.name),
                    ("instanceId", body.GetInstanceID()),
                    ("isKinematic", body.isKinematic),
                    ("useGravity", body.useGravity),
                    ("mass", body.mass),
                    ("gameObject", ToolUtilities.SummarizeGameObject(body.gameObject, false))))
                .ToList();

            var rigidbodies2D = UnityEngine.Object.FindObjectsByType<Rigidbody2D>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(body => body != null && ToolUtilities.IsUsableSceneObject(body.gameObject, true))
                .Select(body => (object?)EvalData.Obj(
                    ("name", body.name),
                    ("instanceId", body.GetInstanceID()),
                    ("bodyType", body.bodyType.ToString()),
                    ("simulated", body.simulated),
                    ("gravityScale", body.gravityScale),
                    ("gameObject", ToolUtilities.SummarizeGameObject(body.gameObject, false))))
                .ToList();

            return EvalData.Obj(
                ("gravity3D", ToolUtilities.Vector3ToObject(Physics.gravity)),
                ("queriesHitTriggers3D", Physics.queriesHitTriggers),
                ("collider3DCount", colliders3D.Count),
                ("colliders3D", colliders3D),
                ("rigidbody3DCount", rigidbodies3D.Count),
                ("rigidbodies3D", rigidbodies3D),
                ("gravity2D", ToolUtilities.Vector2ToObject(Physics2D.gravity)),
                ("queriesHitTriggers", Physics2D.queriesHitTriggers),
                ("collider2DCount", colliders2D.Count),
                ("colliders2D", colliders2D),
                ("rigidbody2DCount", rigidbodies2D.Count),
                ("rigidbodies2D", rigidbodies2D));
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("Read render pipeline and quality state.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getGraphicsState()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            return EvalData.Obj(
                ("renderPipeline",
                    pipeline != null ? pipeline.GetType().FullName ?? pipeline.GetType().Name : "Built-in"),
                ("renderPipelineAsset", pipeline != null ? pipeline.name : string.Empty),
                ("activeColorSpace", QualitySettings.activeColorSpace.ToString()),
                ("qualityLevel", QualitySettings.GetQualityLevel()),
                ("qualityName",
                    QualitySettings.names.Length > QualitySettings.GetQualityLevel()
                        ? QualitySettings.names[QualitySettings.GetQualityLevel()]
                        : string.Empty));
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("List UI canvases and EventSystems.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> listCanvases()
        {
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(canvas => canvas != null && ToolUtilities.IsUsableSceneObject(canvas.gameObject, true))
                .Select(canvas => (object?)EvalData.Obj(
                    ("name", canvas.name),
                    ("instanceId", canvas.GetInstanceID()),
                    ("renderMode", canvas.renderMode.ToString()),
                    ("sortingOrder", canvas.sortingOrder),
                    ("gameObject", ToolUtilities.SummarizeGameObject(canvas.gameObject, false))))
                .ToList();
            var eventSystems = UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(eventSystem => eventSystem != null && ToolUtilities.IsUsableSceneObject(eventSystem.gameObject, true))
                .Select(eventSystem => (object?)EvalData.Obj(
                    ("name", eventSystem.name),
                    ("instanceId", eventSystem.GetInstanceID()),
                    ("enabled", eventSystem.enabled),
                    ("currentSelectedGameObject", eventSystem.currentSelectedGameObject != null
                        ? ToolUtilities.SummarizeGameObject(eventSystem.currentSelectedGameObject, false)
                        : null),
                    ("gameObject", ToolUtilities.SummarizeGameObject(eventSystem.gameObject, false))))
                .ToList();
            return EvalData.Obj(
                ("canvasCount", canvases.Count),
                ("canvases", canvases),
                ("eventSystemCount", eventSystems.Count),
                ("eventSystems", eventSystems));
        }

        [UnityEngine.Scripting.Preserve]
        [EvalFunction("List loaded textures.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> listLoadedTextures(int limit = 100)
        {
            limit = Math.Max(1, limit);
            var textures = Resources.FindObjectsOfTypeAll<Texture>()
                .Where(texture => texture != null)
                .Take(limit)
                .Select(texture => (object?)EvalData.Obj(
                    ("name", texture.name),
                    ("type", texture.GetType().FullName ?? texture.GetType().Name),
                    ("instanceId", texture.GetInstanceID()),
                    ("width", texture.width),
                    ("height", texture.height)))
                .ToList();
            return EvalData.Obj(("count", textures.Count), ("textures", textures));
        }
    }
}
