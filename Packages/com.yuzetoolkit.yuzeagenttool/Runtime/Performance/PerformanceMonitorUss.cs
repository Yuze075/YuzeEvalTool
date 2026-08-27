#nullable enable
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit.Agent
{
    internal static class PerformanceMonitorUss
    {
        public const string LayerClass = "yuzu-debug-performance-layer";
        public const string LabelClass = "yuzu-debug-label";
        public const string HudGraphClass = "yuzu-debug-hud-graph";

        public static readonly Color TextColor = new(1f, 1f, 1f, 0.86f);
        public static readonly Color MutedTextColor = new(1f, 1f, 1f, 0.55f);
        public static readonly Color GoodColor = new(0.208f, 0.679f, 0.622f, 1f);
        public static readonly Color CautionColor = new(0.914f, 0.769f, 0.416f, 1f);
        public static readonly Color CriticalColor = new(0.906f, 0.435f, 0.318f, 1f);
        public static readonly Color RamReservedColor = new(0.996f, 0.894f, 0.251f, 1f);
        public static readonly Color RamAllocatedColor = new(0.945f, 0.357f, 0.710f, 1f);
        public static readonly Color RamMonoColor = new(0f, 0.733f, 0.976f, 1f);

        public static void ApplyLayer(VisualElement layer)
        {
            layer.AddToClassList(LayerClass);
            layer.pickingMode = PickingMode.Ignore;
        }

        public static Color GetFpsColor(float fps)
        {
            return fps >= 60f
                ? GoodColor
                : fps >= 30f
                    ? CautionColor
                    : CriticalColor;
        }
    }
}
