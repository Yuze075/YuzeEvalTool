#nullable enable
using UnityEngine;

namespace YuzeToolkit
{
    internal static class PerformanceHudConstants
    {
        public const int SampleCapacity = 1024;
        public const int GraphSamples = 150;
        public const int SpectrumSamples = 128;
        public const float PanelWidth = 200f;
    }

    internal readonly struct PerformanceUpdate
    {
        public PerformanceUpdate(PerformanceMetricsSnapshot? metrics)
        {
            Metrics = metrics;
        }

        public PerformanceMetricsSnapshot? Metrics { get; }
    }

    internal readonly struct PerformanceMetricsSnapshot
    {
        public PerformanceMetricsSnapshot(FpsSnapshot fps, RamSnapshot ram, AudioSnapshot audio)
        {
            Fps = fps;
            Ram = ram;
            Audio = audio;
        }

        public FpsSnapshot Fps { get; }

        public RamSnapshot Ram { get; }

        public AudioSnapshot Audio { get; }
    }

    internal readonly struct FpsSnapshot
    {
        public FpsSnapshot(float fps, float deltaMs, float average, float onePercent, float zeroOnePercent,
            float[] samples, int sampleCount)
        {
            Fps = fps;
            DeltaMs = deltaMs;
            Average = average;
            OnePercent = onePercent;
            ZeroOnePercent = zeroOnePercent;
            Samples = samples;
            SampleCount = sampleCount;
        }

        public float Fps { get; }

        public float DeltaMs { get; }

        public float Average { get; }

        public float OnePercent { get; }

        public float ZeroOnePercent { get; }

        public float[] Samples { get; }

        public int SampleCount { get; }

    }

    internal readonly struct RamSnapshot
    {
        public RamSnapshot(float reserved, float allocated, float mono, float[] reservedSamples,
            float[] allocatedSamples, float[] monoSamples, int sampleCount)
        {
            Reserved = reserved;
            Allocated = allocated;
            Mono = mono;
            ReservedSamples = reservedSamples;
            AllocatedSamples = allocatedSamples;
            MonoSamples = monoSamples;
            SampleCount = sampleCount;
        }

        public float Reserved { get; }

        public float Allocated { get; }

        public float Mono { get; }

        public float[] ReservedSamples { get; }

        public float[] AllocatedSamples { get; }

        public float[] MonoSamples { get; }

        public int SampleCount { get; }
    }

    internal readonly struct AudioSnapshot
    {
        public AudioSnapshot(float? decibels, float[] samples, int sampleCount)
        {
            Decibels = decibels;
            Samples = samples;
            SampleCount = sampleCount;
        }

        public float? Decibels { get; }

        public float[] Samples { get; }

        public int SampleCount { get; }
    }

    internal readonly struct GraphSeries
    {
        public GraphSeries(float[] values, int count, Color color)
        {
            Values = values;
            Count = Mathf.Clamp(count, 0, values.Length);
            Color = color;
        }

        public float[] Values { get; }

        public int Count { get; }

        public Color Color { get; }
    }

    internal enum GraphKind
    {
        Fps,
        Ram,
        Audio
    }
}
