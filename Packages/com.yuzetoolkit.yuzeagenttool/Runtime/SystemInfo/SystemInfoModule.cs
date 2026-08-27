#nullable enable
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if YUZE_USE_UNITY_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UIElements;
using YuzeToolkit.UnityAgent;

namespace YuzeToolkit
{
    [DisallowMultipleComponent]
    public sealed class SystemInfoModule : MonoBehaviour, IDebugPanelModule
    {
        [SerializeField, Tooltip("Required UXML template for system information. Initialization fails when it is missing.")]
        private VisualTreeAsset? template;

        [SerializeField, Tooltip("Required USS for system information. Initialization fails when it is missing.")]
        private StyleSheet? styleSheet;

#if YUZE_USE_UNITY_INPUT_SYSTEM
        [SerializeField, Tooltip("Keyboard key used with the DebugPanel modifiers to show or hide the system information module.")]
        private Key toggleKey = Key.F10;
#endif

        private const float RefreshInterval = 1f;
        private SystemInfoView? _view;
        private VisualElement? _layer;
        private float _refreshTimer;
        private System.IDisposable? _workspaceRegistration;

        public int SortOrder => 1;

#if YUZE_USE_UNITY_INPUT_SYSTEM
        public Key ToggleKey => toggleKey;
#endif

        public void Initialize(DebugPanelContext context)
        {
            if (template == null || styleSheet == null)
                throw new MissingReferenceException(
                    $"{nameof(SystemInfoModule)} requires both UXML and USS references.");

            try
            {
                context.AddStyleSheet(styleSheet);
                _layer = context.CreateLayer("unity-agent-system-info-layer");
                SystemInfoUss.ApplyLayer(_layer);
                _view = new SystemInfoView(template);
                _view.AttachTo(_layer);
                _workspaceRegistration = UnityAgentWorkspaceRegistry.RegisterSystemInfoSection(
                    "unity-agent-system-info", 10, () => CreateWorkspaceSection(template, styleSheet));
                Refresh();
            }
            catch
            {
                Shutdown();
                throw;
            }
        }

        public void SetVisible(bool visible)
        {
            if (_layer != null)
                _layer.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

            if (visible)
                Refresh();
        }

        public void Tick()
        {
            if (_view == null) return;

            _refreshTimer += Time.unscaledDeltaTime;
            if (_refreshTimer < RefreshInterval) return;

            Refresh();
        }

        public void Shutdown()
        {
            _view?.Detach();
            _view = null;
            _workspaceRegistration?.Dispose();
            _workspaceRegistration = null;
            _layer?.RemoveFromHierarchy();
            _layer = null;
            _refreshTimer = 0f;
        }

        public static IUnityAgentWorkspaceSection CreateWorkspaceSection(
            VisualTreeAsset templateAsset, StyleSheet styleSheetAsset)
        {
            if (templateAsset == null) throw new System.ArgumentNullException(nameof(templateAsset));
            if (styleSheetAsset == null) throw new System.ArgumentNullException(nameof(styleSheetAsset));
            return new SystemInfoWorkspaceSection(templateAsset, styleSheetAsset);
        }

        private sealed class SystemInfoWorkspaceSection : IUnityAgentWorkspaceSection
        {
            private readonly VisualElement _cards;
            private readonly Dictionary<string, Label> _values = new(System.StringComparer.Ordinal);
            private string _signature = string.Empty;
            private float _timer;

            public SystemInfoWorkspaceSection(VisualTreeAsset templateAsset, StyleSheet styleSheetAsset)
            {
                Root = new VisualElement { name = "unity-agent-system-details-section" };
                ApplyWorkspaceRootLayout(Root);
                Root.style.flexShrink = 0;
                Root.Add(CreateSectionHeading("SYSTEM DETAILS",
                    "Display, graphics, hardware and registered runtime information"));
                _cards = new VisualElement();
                _cards.style.width = new Length(100, LengthUnit.Percent);
                _cards.style.minWidth = 0;
                _cards.style.flexDirection = FlexDirection.Row;
                _cards.style.flexWrap = Wrap.Wrap;
                _cards.style.alignItems = Align.FlexStart;
                Root.Add(_cards);
                ApplySnapshot(SystemInfoRegistry.CaptureSnapshot());
            }

            private static void ApplyWorkspaceRootLayout(VisualElement root)
            {
                root.style.position = Position.Relative;
                root.style.left = StyleKeyword.Auto;
                root.style.right = StyleKeyword.Auto;
                root.style.top = StyleKeyword.Auto;
                root.style.bottom = StyleKeyword.Auto;
                root.style.width = new Length(100, LengthUnit.Percent);
                root.style.minWidth = 0;
            }

            public VisualElement Root { get; }

            public void Tick()
            {
                _timer += Time.unscaledDeltaTime;
                if (_timer < RefreshInterval) return;
                _timer = 0f;
                ApplySnapshot(SystemInfoRegistry.CaptureSnapshot());
            }

            public void Dispose()
            {
                Root.RemoveFromHierarchy();
            }

            private void ApplySnapshot(SystemInfoSnapshot snapshot)
            {
                var signature = string.Join("\n", snapshot.Lines.Select(value => value.Key));
                if (!string.Equals(_signature, signature, System.StringComparison.Ordinal))
                {
                    _signature = signature;
                    Rebuild(snapshot);
                    return;
                }

                foreach (var line in snapshot.Lines)
                    if (_values.TryGetValue(line.Key, out var label)) label.text = line.Value;
            }

            private void Rebuild(SystemInfoSnapshot snapshot)
            {
                _cards.Clear();
                _values.Clear();
                var groups = snapshot.Lines.GroupBy(value => Category(value.Key))
                    .OrderBy(value => CategoryOrder(value.Key));
                foreach (var group in groups)
                {
                    var card = CreateInfoCard(group.Key);
                    var lines = group.ToList();
                    for (var index = 0; index < lines.Count; index++)
                        AddInfoRow(card, lines[index], index < lines.Count - 1);
                    _cards.Add(card);
                }
            }

            private void AddInfoRow(VisualElement parent, SystemInfoLine line, bool divider)
            {
                var row = new VisualElement();
                row.style.minWidth = 0;
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.FlexStart;
                row.style.paddingTop = 7;
                row.style.paddingBottom = 7;
                if (divider)
                {
                    row.style.borderBottomWidth = 1;
                    row.style.borderBottomColor = AgentUi.Border1;
                }

                var key = new Label(line.Key);
                AgentUi.ApplyTypography(key, AgentTypography.Caption);
                key.style.width = 112;
                key.style.flexShrink = 0;
                key.style.color = AgentUi.TextCaption;
                row.Add(key);

                var value = new Label(line.Value);
                AgentUi.ApplyTypography(value, AgentTypography.Body, false);
                value.style.flexGrow = 1;
                value.style.flexShrink = 1;
                value.style.minWidth = 0;
                value.style.whiteSpace = WhiteSpace.Normal;
                value.style.color = AgentUi.TextSecondary;
                row.Add(value);
                _values[line.Key] = value;
                parent.Add(row);
            }

            private static VisualElement CreateSectionHeading(string title, string subtitle)
            {
                var root = new VisualElement();
                root.style.marginTop = 6;
                root.style.marginBottom = 10;
                var heading = new Label(title);
                AgentUi.ApplyTypography(heading, AgentTypography.Caption);
                heading.style.unityFontStyleAndWeight = FontStyle.Bold;
                heading.style.color = AgentUi.TextSecondary;
                root.Add(heading);
                var help = new Label(subtitle);
                AgentUi.ApplyTypography(help, AgentTypography.Caption, false);
                help.style.color = AgentUi.TextCaption;
                help.style.marginTop = 2;
                root.Add(help);
                return root;
            }

            private static VisualElement CreateInfoCard(string title)
            {
                var card = AgentUi.RoundedPanel(14);
                card.style.flexGrow = 1;
                card.style.flexShrink = 1;
                card.style.flexBasis = 360;
                card.style.minWidth = 280;
                card.style.marginRight = 10;
                card.style.marginBottom = 10;
                card.style.paddingLeft = 14;
                card.style.paddingRight = 14;
                card.style.paddingTop = 12;
                card.style.paddingBottom = 8;
                card.style.backgroundColor = AgentUi.Surface1;
                AgentUi.SetBorder(card, AgentUi.Border1, 1);
                var heading = new Label(title);
                AgentUi.ApplyTypography(heading, AgentTypography.BodyStrong);
                heading.style.color = AgentUi.TextSecondary;
                heading.style.marginBottom = 3;
                card.Add(heading);
                return card;
            }

            private static string Category(string key) => key switch
            {
                "Screen" or "Window" => "Display",
                "Graphics API" or "GPU" or "VRAM" or "Max Texture Size" or "Shader Level" => "Graphics",
                "CPU" or "RAM" or "OS" => "Hardware & platform",
                _ => "Runtime registrations"
            };

            private static int CategoryOrder(string category) => category switch
            {
                "Display" => 0,
                "Graphics" => 1,
                "Hardware & platform" => 2,
                _ => 3
            };
        }

        private void Refresh()
        {
            _refreshTimer = 0f;
            _view?.ApplySnapshot(SystemInfoRegistry.CaptureSnapshot());
        }
    }
}
