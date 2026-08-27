#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit
{
    internal sealed class PerformanceMonitorView
    {
        private readonly VisualTreeAsset _templateAsset;
        private readonly Dictionary<string, Label> _fpsLabels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Label> _ramLabels = new(StringComparer.Ordinal);
        private TemplateContainer? _template;
        private VisualElement? _graphHud;
        private DebugGraphElement? _fpsGraph;
        private DebugGraphElement? _ramGraph;
        private DebugGraphElement? _audioGraph;
        private Label? _audioValue;
        private readonly GraphSeries[] _fpsSeries = new GraphSeries[1];
        private readonly GraphSeries[] _ramSeries = new GraphSeries[3];
        private readonly GraphSeries[] _audioSeries = new GraphSeries[1];

        public PerformanceMonitorView(VisualTreeAsset templateAsset)
        {
            _templateAsset = templateAsset;
        }

        public void AttachTo(VisualElement root)
        {
            _template = DebugPanelTemplate.Clone(_templateAsset, nameof(PerformanceMonitorView));
            _graphHud = DebugPanelTemplate.QueryRequired<VisualElement>(_template, "unity-debug-tool-graphy-hud");
            _graphHud.RemoveFromHierarchy();
            root.Add(_graphHud);

            _fpsLabels["fps"] = QueryLabel("fps-value");
            _fpsLabels["ms"] = QueryLabel("fps-ms");
            _fpsLabels["average"] = QueryLabel("fps-average");
            _fpsLabels["onePercent"] = QueryLabel("fps-one-percent");
            _fpsLabels["zeroOnePercent"] = QueryLabel("fps-zero-one-percent");

            _ramLabels["reserved"] = QueryLabel("ram-reserved");
            _ramLabels["allocated"] = QueryLabel("ram-allocated");
            _ramLabels["mono"] = QueryLabel("ram-mono");
            _audioValue = QueryLabel("audio-db");

            _fpsGraph = AddGraph("fps-graph-slot", GraphKind.Fps);
            _ramGraph = AddGraph("ram-graph-slot", GraphKind.Ram);
            _audioGraph = AddGraph("audio-graph-slot", GraphKind.Audio);
        }

        public void Detach()
        {
            _graphHud?.RemoveFromHierarchy();
            _template?.RemoveFromHierarchy();
            _template = null;
            _graphHud = null;
            _fpsLabels.Clear();
            _ramLabels.Clear();
            _fpsGraph = null;
            _ramGraph = null;
            _audioGraph = null;
            _audioValue = null;
        }

        public void SetEmbeddedLayout()
        {
            if (_graphHud == null) return;
            _graphHud.style.position = Position.Relative;
            _graphHud.style.left = StyleKeyword.Auto;
            _graphHud.style.right = StyleKeyword.Auto;
            _graphHud.style.top = StyleKeyword.Auto;
            _graphHud.style.bottom = StyleKeyword.Auto;
            _graphHud.style.alignSelf = Align.FlexStart;
        }

        public void ApplyMetrics(PerformanceMetricsSnapshot snapshot)
        {
            ApplyFps(snapshot.Fps);
            ApplyRam(snapshot.Ram);
            ApplyAudio(snapshot.Audio);
        }

        private DebugGraphElement AddGraph(string slotName, GraphKind kind)
        {
            if (_graphHud == null) throw new InvalidOperationException("Performance template is not attached.");

            var slot = DebugPanelTemplate.QueryRequired<VisualElement>(_graphHud, slotName);
            var graph = new DebugGraphElement(kind);
            graph.AddToClassList(PerformanceMonitorUss.HudGraphClass);
            slot.Add(graph);
            return graph;
        }

        private Label QueryLabel(string name)
        {
            if (_graphHud == null) throw new InvalidOperationException("Performance template is not attached.");
            return DebugPanelTemplate.QueryRequired<Label>(_graphHud, name);
        }

        private void ApplyFps(FpsSnapshot snapshot)
        {
            if (_fpsLabels.Count == 0) return;

            var fpsColor = PerformanceMonitorUss.GetFpsColor(snapshot.Fps);
            SetTextAndColor(_fpsLabels["fps"], $"{Mathf.RoundToInt(snapshot.Fps)}", fpsColor);
            SetTextAndColor(_fpsLabels["ms"], $"{snapshot.DeltaMs:0.0}", fpsColor);
            SetTextAndColor(_fpsLabels["average"], $"{snapshot.Average:0}", PerformanceMonitorUss.GetFpsColor(snapshot.Average));
            SetTextAndColor(_fpsLabels["onePercent"], $"{snapshot.OnePercent:0}", PerformanceMonitorUss.GetFpsColor(snapshot.OnePercent));
            SetTextAndColor(_fpsLabels["zeroOnePercent"], $"{snapshot.ZeroOnePercent:0}", PerformanceMonitorUss.GetFpsColor(snapshot.ZeroOnePercent));

            _fpsSeries[0] = new GraphSeries(snapshot.Samples, snapshot.SampleCount, fpsColor);
            _fpsGraph?.SetSeries(_fpsSeries, snapshot.Average, 60f, 30f);
        }

        private void ApplyRam(RamSnapshot snapshot)
        {
            if (_ramLabels.Count == 0) return;

            _ramLabels["reserved"].text = $"{snapshot.Reserved:0}";
            _ramLabels["allocated"].text = $"{snapshot.Allocated:0}";
            _ramLabels["mono"].text = $"{snapshot.Mono:0}";

            _ramSeries[0] = new GraphSeries(snapshot.ReservedSamples, snapshot.SampleCount, PerformanceMonitorUss.RamReservedColor);
            _ramSeries[1] = new GraphSeries(snapshot.AllocatedSamples, snapshot.SampleCount, PerformanceMonitorUss.RamAllocatedColor);
            _ramSeries[2] = new GraphSeries(snapshot.MonoSamples, snapshot.SampleCount, PerformanceMonitorUss.RamMonoColor);
            _ramGraph?.SetSeries(_ramSeries);
        }

        private void ApplyAudio(AudioSnapshot snapshot)
        {
            if (_audioValue != null)
            {
                if (snapshot.Decibels == null)
                    SetTextAndColor(_audioValue, "-- dB", PerformanceMonitorUss.MutedTextColor);
                else
                    SetTextAndColor(_audioValue, $"{snapshot.Decibels.Value:0} dB", PerformanceMonitorUss.TextColor);
            }

            _audioSeries[0] = new GraphSeries(snapshot.Samples, snapshot.SampleCount, new Color(1f, 1f, 1f, 0.65f));
            _audioGraph?.SetSeries(_audioSeries);
        }

        private static void SetTextAndColor(Label label, string text, Color color)
        {
            label.text = text;
            label.style.color = color;
        }

    }
}
