#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit.UnityAgent
{
    /// <summary>
    /// Read-only path and embedded-content summary used by Editor settings surfaces. Effective
    /// package/project defaults seed the machine settings layer when it is missing or invalid;
    /// user-owned Provider profiles are stored in a separate machine document.
    /// </summary>
    internal sealed class AgentBuildContentView : VisualElement
    {
        private readonly VisualElement _rootList;

        public AgentBuildContentView(UnityAgentHost host)
        {
            _ = host ?? throw new ArgumentNullException(nameof(host));
            style.flexShrink = 0;
            style.paddingLeft = 12;
            style.paddingRight = 12;
            style.paddingTop = 7;
            style.paddingBottom = 9;
            style.borderTopWidth = 1;
            style.borderTopColor = new Color(0.18f, 0.21f, 0.25f);
            style.backgroundColor = new Color(0.055f, 0.068f, 0.085f);

            var title = new Label("Instruction path availability and embedded content");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            Add(title);
            var help = new Label(
                "Availability controls direct Editor/Player path discovery. Embed copies a build-time " +
                "snapshot into Player independently of availability.");
            help.style.whiteSpace = WhiteSpace.Normal;
            help.style.color = new Color(0.52f, 0.56f, 0.62f);
            Add(help);
            _rootList = new VisualElement();
            _rootList.style.marginTop = 4;
            Add(_rootList);
        }

        public void Refresh(AgentSettingsDocument settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _rootList.Clear();
            AddRoots("AGENTS.md", settings.AgentsRoots, isSkillRoot: false);
            AddRoots("Skills", settings.SkillRoots, isSkillRoot: true);
        }

        private void AddRoots(
            string heading,
            IReadOnlyList<AgentPathLocation> roots,
            bool isSkillRoot)
        {
            var label = new Label(heading);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 4;
            _rootList.Add(label);
            if (roots.Count == 0)
            {
                var empty = new Label("No roots configured.");
                empty.style.color = new Color(0.52f, 0.56f, 0.62f);
                _rootList.Add(empty);
                return;
            }

            for (var index = 0; index < roots.Count; index++)
            {
                var root = roots[index];
                var relative = string.IsNullOrEmpty(root.RelativePath) ? "." : root.RelativePath;
                var buildState = root.EmbedInPlayerBuild ? "embedded" : "not embedded";
                var fixedPath = isSkillRoot
                    ? root.UseUnityAgentToolDirectory
                        ? $"{AgentPaths.SettingsDirectoryName} / {AgentPaths.SkillDirectoryName}"
                        : AgentPaths.SkillDirectoryName
                    : root.UseUnityAgentToolDirectory
                        ? AgentPaths.SettingsDirectoryName
                        : ".";
                var item = new Label(
                    $"{index + 1}. {root.BasePath} / {fixedPath} / {relative}  ·  " +
                    $"{GetScopeLabel(root.Scope)}  ·  {buildState}");
                AgentTooltip.Attach(item, isSkillRoot ? AgentPaths.ResolveSkill(root) : AgentPaths.Resolve(root));
                item.style.color = new Color(0.72f, 0.76f, 0.82f);
                _rootList.Add(item);
            }
        }

        private static string GetScopeLabel(AgentPathScope scope) => scope switch
        {
            AgentPathScope.None => "None",
            AgentPathScope.EditorOnly => "Editor only",
            AgentPathScope.PlayerOnly => "Player only",
            AgentPathScope.All => "Editor and Player",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Agent path scope.")
        };
    }
}
