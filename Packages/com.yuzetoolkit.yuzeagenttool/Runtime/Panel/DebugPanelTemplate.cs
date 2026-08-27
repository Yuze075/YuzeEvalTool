#nullable enable
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit.Agent
{
    public static class DebugPanelTemplate
    {
        public static TemplateContainer Clone(VisualTreeAsset asset, string label)
        {
            if (asset == null)
                throw new MissingReferenceException($"UnityAgentTool UXML template reference is missing: {label}.");

            return asset.CloneTree();
        }

        public static T QueryRequired<T>(VisualElement root, string name) where T : VisualElement
        {
            var element = root.Q<T>(name);
            if (element == null)
                throw new MissingReferenceException($"UnityAgentTool template element '{name}' of type '{typeof(T).Name}' was not found.");
            return element;
        }
    }
}
