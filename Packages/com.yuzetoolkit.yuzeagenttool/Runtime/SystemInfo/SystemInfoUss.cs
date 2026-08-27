#nullable enable
using UnityEngine.UIElements;

namespace YuzeToolkit
{
    internal static class SystemInfoUss
    {
        public const string LayerClass = "yuzu-debug-system-info-layer";
        public const string LabelClass = "yuzu-debug-system-info-label";
        public const string MutedLabelClass = "yuzu-debug-system-info-label-muted";
        public const string RowClass = "yuzu-debug-system-info-row";
        public const string KeyClass = "yuzu-debug-system-info-key";
        public const string ValueClass = "yuzu-debug-system-info-value";

        public static void ApplyLayer(VisualElement layer)
        {
            layer.AddToClassList(LayerClass);
            layer.pickingMode = PickingMode.Ignore;
        }
    }
}
