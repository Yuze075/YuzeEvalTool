#nullable enable
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit
{
    public sealed class DebugPanelContext
    {
        internal DebugPanelContext(VisualElement root)
        {
            Root = root;
        }

        public VisualElement Root { get; }

        public void AddStyleSheet(StyleSheet styleSheet)
        {
            AddStyleSheet(Root, styleSheet);
        }

        public VisualElement CreateLayer(string name)
        {
            var layer = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            Root.Add(layer);
            return layer;
        }

        internal static void AddStyleSheet(VisualElement root, StyleSheet styleSheet)
        {
            if (styleSheet == null)
                throw new MissingReferenceException("UnityAgentTool stylesheet reference is missing.");

            if (!root.styleSheets.Contains(styleSheet))
                root.styleSheets.Add(styleSheet);
        }
    }
}
