#nullable enable
using UnityEngine;
#if YUZE_USE_UNITY_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.Profiling;
using UnityEngine.UIElements;
using YuzeToolkit.UnityAgent;

namespace YuzeToolkit
{
    [DisallowMultipleComponent]
    public sealed class PerformanceMonitorModule : MonoBehaviour, IDebugPanelModule
    {
        [SerializeField, Tooltip("Required UXML template for the performance monitor. Initialization fails when it is missing.")]
        private VisualTreeAsset? template;

        [SerializeField, Tooltip("Required USS for the performance monitor. Initialization fails when it is missing.")]
        private StyleSheet? styleSheet;

#if YUZE_USE_UNITY_INPUT_SYSTEM
        [SerializeField, Tooltip("Keyboard key used with the DebugPanel modifiers to show or hide the performance monitor module.")]
        private Key toggleKey = Key.F10;
#endif

        private readonly PerformanceSampler _sampler = new();
        private PerformanceMonitorView? _view;
        private VisualElement? _layer;
        private System.IDisposable? _workspaceRegistration;

        public int SortOrder => 0;

#if YUZE_USE_UNITY_INPUT_SYSTEM
        public Key ToggleKey => toggleKey;
#endif

        public void Initialize(DebugPanelContext context)
        {
            if (template == null || styleSheet == null)
                throw new MissingReferenceException(
                    $"{nameof(PerformanceMonitorModule)} requires both UXML and USS references.");

            context.AddStyleSheet(styleSheet);
            try
            {
                _layer = context.CreateLayer("unity-agent-performance-layer");
                PerformanceMonitorUss.ApplyLayer(_layer);
                _view = new PerformanceMonitorView(template);
                _view.AttachTo(_layer);
                _workspaceRegistration = UnityAgentWorkspaceRegistry.RegisterSystemInfoSection(
                    "unity-agent-performance", 0, () => CreateWorkspaceSection(template, styleSheet));
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
        }

        public void Tick()
        {
            if (_view == null) return;

            var update = _sampler.Tick(Time.unscaledDeltaTime);
            if (update.Metrics != null)
                _view.ApplyMetrics(update.Metrics.Value);
        }

        public void Shutdown()
        {
            _view?.Detach();
            _view = null;
            _workspaceRegistration?.Dispose();
            _workspaceRegistration = null;
            _layer?.RemoveFromHierarchy();
            _layer = null;
            _sampler.Reset();
        }

        public static IUnityAgentWorkspaceSection CreateWorkspaceSection(
            VisualTreeAsset templateAsset, StyleSheet styleSheetAsset)
        {
            if (templateAsset == null) throw new System.ArgumentNullException(nameof(templateAsset));
            if (styleSheetAsset == null) throw new System.ArgumentNullException(nameof(styleSheetAsset));
            return new PerformanceWorkspaceSection(templateAsset, styleSheetAsset);
        }

        private sealed class PerformanceWorkspaceSection : IUnityAgentWorkspaceSection
        {
            private readonly PerformanceSampler _sampler = new();
            private readonly Label _fps;
            private readonly Label _fpsAverage;
            private readonly Label _fpsOnePercent;
            private readonly Label _fpsZeroOnePercent;
            private readonly Label _ramReserved;
            private readonly Label _ramAllocated;
            private readonly Label _ramMono;
            private readonly Label _audio;
            private readonly DebugGraphElement _fpsGraph;
            private readonly DebugGraphElement _ramGraph;
            private readonly DebugGraphElement _audioGraph;
            private readonly GraphSeries[] _fpsSeries = new GraphSeries[1];
            private readonly GraphSeries[] _ramSeries = new GraphSeries[3];
            private readonly GraphSeries[] _audioSeries = new GraphSeries[1];

            public PerformanceWorkspaceSection(VisualTreeAsset templateAsset, StyleSheet styleSheetAsset)
            {
                Root = new VisualElement { name = "unity-agent-performance-section" };
                ApplyWorkspaceRootLayout(Root);
                Root.style.flexShrink = 0;
                Root.Add(CreateSectionHeading("PERFORMANCE", "Live frame, memory and audio metrics"));

                var cards = CreateCardGrid();
                Root.Add(cards);

                var fpsCard = CreateMetricCard("Frame rate");
                _fps = CreateHeadline("Waiting for samples");
                fpsCard.Add(_fps);
                _fpsAverage = AddMetric(fpsCard, "Average", "—");
                _fpsOnePercent = AddMetric(fpsCard, "1% low", "—");
                _fpsZeroOnePercent = AddMetric(fpsCard, "0.1% low", "—");
                _fpsGraph = new DebugGraphElement(GraphKind.Fps);
                _fpsGraph.UseFlexibleWidth();
                fpsCard.Add(_fpsGraph);
                cards.Add(fpsCard);

                var ramCard = CreateMetricCard("Memory");
                _ramReserved = AddMetric(ramCard, "Reserved", "—");
                _ramAllocated = AddMetric(ramCard, "Allocated", "—");
                _ramMono = AddMetric(ramCard, "Managed", "—");
                _ramGraph = new DebugGraphElement(GraphKind.Ram);
                _ramGraph.UseFlexibleWidth();
                ramCard.Add(_ramGraph);
                cards.Add(ramCard);

                var audioCard = CreateMetricCard("Audio spectrum");
                _audio = CreateHeadline("No signal");
                audioCard.Add(_audio);
                var audioHint = new Label("Peak output level");
                AgentUi.ApplyTypography(audioHint, AgentTypography.Caption);
                audioHint.style.color = AgentUi.TextCaption;
                audioCard.Add(audioHint);
                _audioGraph = new DebugGraphElement(GraphKind.Audio);
                _audioGraph.UseFlexibleWidth();
                audioCard.Add(_audioGraph);
                cards.Add(audioCard);

                if (!Application.isPlaying) ApplyEditorState();
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
                if (!Application.isPlaying)
                {
                    ApplyEditorState();
                    return;
                }
                var update = _sampler.Tick(Time.unscaledDeltaTime);
                if (update.Metrics != null) ApplyMetrics(update.Metrics.Value);
            }

            public void Dispose()
            {
                _sampler.Reset();
                Root.RemoveFromHierarchy();
            }

            private void ApplyMetrics(PerformanceMetricsSnapshot snapshot)
            {
                var fpsColor = snapshot.Fps.Fps >= 60f
                    ? AgentUi.Success
                    : snapshot.Fps.Fps >= 30f
                        ? AgentUi.Warning
                        : AgentUi.Error;
                _fps.text = $"{Mathf.RoundToInt(snapshot.Fps.Fps)} FPS  ·  {snapshot.Fps.DeltaMs:0.0} ms";
                _fps.style.color = fpsColor;
                _fpsAverage.text = $"{snapshot.Fps.Average:0} FPS";
                _fpsOnePercent.text = $"{snapshot.Fps.OnePercent:0} FPS";
                _fpsZeroOnePercent.text = $"{snapshot.Fps.ZeroOnePercent:0} FPS";
                _fpsSeries[0] = new GraphSeries(snapshot.Fps.Samples, snapshot.Fps.SampleCount, fpsColor);
                _fpsGraph.SetSeries(_fpsSeries, snapshot.Fps.Average, 60f, 30f);

                _ramReserved.text = $"{snapshot.Ram.Reserved:0} MB";
                _ramAllocated.text = $"{snapshot.Ram.Allocated:0} MB";
                _ramMono.text = $"{snapshot.Ram.Mono:0} MB";
                _ramSeries[0] = new GraphSeries(snapshot.Ram.ReservedSamples, snapshot.Ram.SampleCount,
                    AgentUi.TextTertiary);
                _ramSeries[1] = new GraphSeries(snapshot.Ram.AllocatedSamples, snapshot.Ram.SampleCount,
                    AgentUi.Accent);
                _ramSeries[2] = new GraphSeries(snapshot.Ram.MonoSamples, snapshot.Ram.SampleCount,
                    AgentUi.Warning);
                _ramGraph.SetSeries(_ramSeries);

                _audio.text = snapshot.Audio.Decibels == null ? "No signal" : $"{snapshot.Audio.Decibels.Value:0} dB";
                _audio.style.color = snapshot.Audio.Decibels == null ? AgentUi.TextTertiary : AgentUi.Text;
                _audioSeries[0] = new GraphSeries(snapshot.Audio.Samples, snapshot.Audio.SampleCount,
                    AgentUi.TextSecondary);
                _audioGraph.SetSeries(_audioSeries);
            }

            private void ApplyEditorState()
            {
                _fps.text = "Play Mode required";
                _fps.style.color = AgentUi.TextTertiary;
                _fpsAverage.text = "—";
                _fpsOnePercent.text = "—";
                _fpsZeroOnePercent.text = "—";
                _ramReserved.text = $"{Profiler.GetTotalReservedMemoryLong() / 1048576f:0} MB";
                _ramAllocated.text = $"{Profiler.GetTotalAllocatedMemoryLong() / 1048576f:0} MB";
                _ramMono.text = $"{Profiler.GetMonoUsedSizeLong() / 1048576f:0} MB";
                _audio.text = "Play Mode required";
                _audio.style.color = AgentUi.TextTertiary;
            }

            private static VisualElement CreateSectionHeading(string title, string subtitle)
            {
                var root = new VisualElement();
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

            private static VisualElement CreateCardGrid()
            {
                var grid = new VisualElement();
                grid.style.width = new Length(100, LengthUnit.Percent);
                grid.style.minWidth = 0;
                grid.style.flexDirection = FlexDirection.Row;
                grid.style.flexWrap = Wrap.Wrap;
                grid.style.alignItems = Align.Stretch;
                return grid;
            }

            private static VisualElement CreateMetricCard(string title)
            {
                var card = AgentUi.RoundedPanel(14);
                card.style.flexGrow = 1;
                card.style.flexShrink = 1;
                card.style.flexBasis = 250;
                card.style.minWidth = 220;
                card.style.minHeight = 170;
                card.style.marginRight = 10;
                card.style.marginBottom = 10;
                card.style.paddingLeft = 14;
                card.style.paddingRight = 14;
                card.style.paddingTop = 12;
                card.style.paddingBottom = 12;
                card.style.backgroundColor = AgentUi.Surface1;
                AgentUi.SetBorder(card, AgentUi.Border1, 1);
                var heading = new Label(title);
                AgentUi.ApplyTypography(heading, AgentTypography.BodyStrong);
                heading.style.color = AgentUi.TextSecondary;
                heading.style.marginBottom = 5;
                card.Add(heading);
                return card;
            }

            private static Label CreateHeadline(string text)
            {
                var label = new Label(text);
                AgentUi.ApplyTypography(label, AgentTypography.PageTitle);
                label.style.marginBottom = 5;
                return label;
            }

            private static Label AddMetric(VisualElement parent, string key, string value)
            {
                var row = new VisualElement();
                row.style.minWidth = 0;
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                var keyLabel = new Label(key);
                AgentUi.ApplyTypography(keyLabel, AgentTypography.Caption);
                keyLabel.style.flexGrow = 1;
                keyLabel.style.minWidth = 0;
                keyLabel.style.color = AgentUi.TextCaption;
                row.Add(keyLabel);
                var valueLabel = new Label(value);
                AgentUi.ApplyTypography(valueLabel, AgentTypography.Caption);
                valueLabel.style.flexShrink = 0;
                valueLabel.style.color = AgentUi.TextSecondary;
                row.Add(valueLabel);
                parent.Add(row);
                return valueLabel;
            }
        }
    }
}
