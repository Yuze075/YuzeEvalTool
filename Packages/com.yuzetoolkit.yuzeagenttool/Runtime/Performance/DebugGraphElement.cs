#nullable enable
using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit.Agent
{
    internal sealed class DebugGraphElement : VisualElement
    {
        private const float FpsHeight = 36f;
        private const float RamHeight = 34f;
        private const float AudioHeight = 24f;
        private readonly GraphKind _kind;
        private GraphSeries[] _series = Array.Empty<GraphSeries>();
        private float _average;
        private float _goodThreshold;
        private float _cautionThreshold;

        public DebugGraphElement(GraphKind kind)
        {
            _kind = kind;
            style.minWidth = PerformanceHudConstants.PanelWidth - 12f;
            style.maxWidth = PerformanceHudConstants.PanelWidth - 12f;
            style.minHeight = kind switch
            {
                GraphKind.Fps => FpsHeight,
                GraphKind.Ram => RamHeight,
                _ => AudioHeight
            };
            style.marginTop = 2;
            style.marginBottom = 1;
            generateVisualContent += Draw;
        }

        public void UseFlexibleWidth()
        {
            style.width = new Length(100, LengthUnit.Percent);
            style.minWidth = 0;
            style.maxWidth = StyleKeyword.None;
        }

        public void SetSeries(
            GraphSeries[] series,
            float average = 0f,
            float goodThreshold = 0f,
            float cautionThreshold = 0f)
        {
            _series = series;
            _average = average;
            _goodThreshold = goodThreshold;
            _cautionThreshold = cautionThreshold;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width <= 1f || rect.height <= 1f)
                return;

            DrawRect(context, rect, new Color(0f, 0f, 0f, 0.015f));

            switch (_kind)
            {
                case GraphKind.Fps:
                    DrawFps(context, rect);
                    break;
                case GraphKind.Ram:
                    DrawRam(context, rect);
                    break;
                case GraphKind.Audio:
                    DrawAudio(context, rect);
                    break;
            }
        }

        private void DrawFps(MeshGenerationContext context, Rect rect)
        {
            if (_series.Length == 0 || _series[0].Count == 0)
            {
                DrawGraphLine(context, rect, 0.08f, PerformanceMonitorUss.CautionColor, 0.95f);
                return;
            }

            var values = _series[0].Values;
            var valueCount = _series[0].Count;
            var max = Mathf.Max(FindMax(values, valueCount), _goodThreshold, 1f);
            var averageNormalized = Mathf.Clamp01(_average / max);
            var goodNormalized = Mathf.Clamp01(_goodThreshold / max);
            var cautionNormalized = Mathf.Clamp01(_cautionThreshold / max);

            DrawGraphLine(context, rect, goodNormalized, PerformanceMonitorUss.GoodColor, 0.75f);
            DrawGraphLine(context, rect, cautionNormalized, PerformanceMonitorUss.CautionColor, 0.95f);
            DrawGraphLine(context, rect, averageNormalized, new Color(1f, 1f, 1f, 0.72f), 0.85f);

            var start = Mathf.Max(0, valueCount - PerformanceHudConstants.GraphSamples);
            var count = valueCount - start;
            var step = rect.width / Mathf.Max(1, PerformanceHudConstants.GraphSamples - 1);
            for (var i = 0; i < count; i++)
            {
                var normalized = Mathf.Clamp01(values[start + i] / max);
                var x = rect.xMax - (count - 1 - i) * step;
                var y = Mathf.Lerp(rect.yMax, rect.yMin, normalized);
                var color = normalized >= goodNormalized
                    ? PerformanceMonitorUss.GoodColor
                    : normalized >= cautionNormalized
                        ? PerformanceMonitorUss.CautionColor
                        : PerformanceMonitorUss.CriticalColor;
                color.a = 0.42f;
                DrawRect(context, new Rect(x, y, Mathf.Max(1f, step * 0.45f), rect.yMax - y), color);
            }
        }

        private void DrawRam(MeshGenerationContext context, Rect rect)
        {
            if (_series.Length < 3)
                return;

            var max = Mathf.Max(
                FindMax(_series[0].Values, _series[0].Count),
                FindMax(_series[1].Values, _series[1].Count),
                FindMax(_series[2].Values, _series[2].Count),
                1f);
            var count = 0;
            for (var i = 0; i < _series.Length; i++)
                count = Mathf.Max(count, _series[i].Count);

            var start = Mathf.Max(0, count - PerformanceHudConstants.GraphSamples);
            var visibleCount = count - start;
            var step = rect.width / Mathf.Max(1, PerformanceHudConstants.GraphSamples - 1);

            for (var i = 0; i < visibleCount; i++)
            {
                var sampleIndex = start + i;
                var x = rect.xMax - (visibleCount - 1 - i) * step;
                for (var seriesIndex = 0; seriesIndex < _series.Length; seriesIndex++)
                {
                    var values = _series[seriesIndex].Values;
                    if (sampleIndex >= _series[seriesIndex].Count) continue;

                    var normalized = Mathf.Clamp01(values[sampleIndex] / max);
                    var y = Mathf.Lerp(rect.yMax - 2f, rect.yMin, normalized);
                    var color = _series[seriesIndex].Color;
                    color.a = seriesIndex == 0 ? 0.24f : 0.38f;
                    DrawRect(context, new Rect(x, y, Mathf.Max(1f, step * 0.55f), rect.yMax - 2f - y), color);
                }
            }

            DrawRect(context, new Rect(rect.xMin, rect.yMax - 2f, rect.width, 1f), new Color(1f, 1f, 1f, 0.22f));
        }

        private void DrawAudio(MeshGenerationContext context, Rect rect)
        {
            DrawRect(context, new Rect(rect.xMin, rect.yMax - 2f, rect.width, 1f), new Color(1f, 1f, 1f, 0.50f));

            if (_series.Length == 0 || _series[0].Count == 0)
                return;

            var values = _series[0].Values;
            var valueCount = _series[0].Count;
            var start = Mathf.Max(0, valueCount - PerformanceHudConstants.GraphSamples);
            var count = valueCount - start;
            var step = rect.width / Mathf.Max(1, count);
            for (var i = 0; i < count; i++)
            {
                var normalized = Mathf.Clamp01(values[start + i]);
                var height = normalized * (rect.height - 3f);
                if (height <= 0.5f) continue;

                var x = rect.xMin + i * step;
                DrawRect(
                    context,
                    new Rect(x, rect.yMax - 2f - height, Mathf.Max(1f, step * 0.55f), height),
                    new Color(1f, 1f, 1f, 0.35f));
            }
        }

        private static void DrawGraphLine(MeshGenerationContext context, Rect rect, float normalized, Color color, float alpha)
        {
            var lineColor = color;
            lineColor.a = alpha;
            var y = Mathf.Lerp(rect.yMax, rect.yMin, Mathf.Clamp01(normalized));
            DrawRect(context, new Rect(rect.xMin, y, rect.width, 1.2f), lineColor);
        }

        private static void DrawRect(MeshGenerationContext context, Rect rect, Color color)
        {
            if (rect.width <= 0f || rect.height <= 0f || color.a <= 0f) return;

            var mesh = context.Allocate(4, 6);
            mesh.SetNextVertex(new Vertex
            {
                position = new Vector3(rect.xMin, rect.yMin, Vertex.nearZ),
                tint = color,
                uv = Vector2.zero
            });
            mesh.SetNextVertex(new Vertex
            {
                position = new Vector3(rect.xMax, rect.yMin, Vertex.nearZ),
                tint = color,
                uv = Vector2.right
            });
            mesh.SetNextVertex(new Vertex
            {
                position = new Vector3(rect.xMax, rect.yMax, Vertex.nearZ),
                tint = color,
                uv = Vector2.one
            });
            mesh.SetNextVertex(new Vertex
            {
                position = new Vector3(rect.xMin, rect.yMax, Vertex.nearZ),
                tint = color,
                uv = Vector2.up
            });
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(1);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(3);
            mesh.SetNextIndex(0);
        }

        private static float FindMax(float[] values, int count)
        {
            var max = 0f;
            for (var i = 0; i < count; i++)
                max = Mathf.Max(max, values[i]);
            return max;
        }
    }
}
