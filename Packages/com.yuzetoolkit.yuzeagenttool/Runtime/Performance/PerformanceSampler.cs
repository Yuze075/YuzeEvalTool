#nullable enable
using System;
using UnityEngine;
using UnityEngine.Profiling;

namespace YuzeToolkit.Agent
{
    internal sealed class PerformanceSampler
    {
        private readonly FixedSampleBuffer _fpsSamples = new(PerformanceHudConstants.SampleCapacity);
        private readonly FixedSampleBuffer _reservedSamples = new(PerformanceHudConstants.SampleCapacity);
        private readonly FixedSampleBuffer _allocatedSamples = new(PerformanceHudConstants.SampleCapacity);
        private readonly FixedSampleBuffer _monoSamples = new(PerformanceHudConstants.SampleCapacity);
        private readonly float[] _spectrum = new float[PerformanceHudConstants.SpectrumSamples];
        private readonly float[] _sortedFpsScratch = new float[PerformanceHudConstants.SampleCapacity];
        private readonly float[] _fpsGraphSamples = new float[PerformanceHudConstants.GraphSamples];
        private readonly float[] _reservedGraphSamples = new float[PerformanceHudConstants.GraphSamples];
        private readonly float[] _allocatedGraphSamples = new float[PerformanceHudConstants.GraphSamples];
        private readonly float[] _monoGraphSamples = new float[PerformanceHudConstants.GraphSamples];
        private readonly float[] _audioGraphSamples = new float[PerformanceHudConstants.GraphSamples];
        private float _metricsTimer;

        public PerformanceUpdate Tick(float unscaledDeltaTime)
        {
            if (float.IsNaN(unscaledDeltaTime) || float.IsInfinity(unscaledDeltaTime) ||
                unscaledDeltaTime <= 0.000001f)
                return new PerformanceUpdate(null);

            var delta = unscaledDeltaTime;
            var fps = 1f / delta;
            _fpsSamples.Add(fps);

            _metricsTimer += unscaledDeltaTime;

            PerformanceMetricsSnapshot? metrics = null;

            if (_metricsTimer >= 0.25f)
            {
                _metricsTimer = 0f;
                metrics = new PerformanceMetricsSnapshot(
                    CaptureFps(delta, fps),
                    CaptureRam(),
                    CaptureAudio());
            }

            return new PerformanceUpdate(metrics);
        }

        public void Reset()
        {
            _fpsSamples.Clear();
            _reservedSamples.Clear();
            _allocatedSamples.Clear();
            _monoSamples.Clear();
            _metricsTimer = 0f;
            Array.Clear(_spectrum, 0, _spectrum.Length);
            Array.Clear(_sortedFpsScratch, 0, _sortedFpsScratch.Length);
            Array.Clear(_fpsGraphSamples, 0, _fpsGraphSamples.Length);
            Array.Clear(_reservedGraphSamples, 0, _reservedGraphSamples.Length);
            Array.Clear(_allocatedGraphSamples, 0, _allocatedGraphSamples.Length);
            Array.Clear(_monoGraphSamples, 0, _monoGraphSamples.Length);
            Array.Clear(_audioGraphSamples, 0, _audioGraphSamples.Length);
        }

        private FpsSnapshot CaptureFps(float delta, float fps)
        {
            var sampleCount = _fpsSamples.Count;
            _fpsSamples.CopyTo(_sortedFpsScratch);
            Array.Sort(_sortedFpsScratch, 0, sampleCount);
            var graphSampleCount = _fpsSamples.CopyLatestTo(_fpsGraphSamples);

            var average = 0f;
            for (var i = 0; i < sampleCount; i++)
                average += _sortedFpsScratch[i];
            if (sampleCount > 0)
                average /= sampleCount;

            return new FpsSnapshot(
                fps,
                delta * 1000f,
                average,
                LowAverage(_sortedFpsScratch, sampleCount, 0.01f),
                LowAverage(_sortedFpsScratch, sampleCount, 0.001f),
                _fpsGraphSamples,
                graphSampleCount);
        }

        private RamSnapshot CaptureRam()
        {
            var allocated = Profiler.GetTotalAllocatedMemoryLong() / 1048576f;
            var reserved = Profiler.GetTotalReservedMemoryLong() / 1048576f;
            var mono = Profiler.GetMonoUsedSizeLong() / 1048576f;

            _allocatedSamples.Add(allocated);
            _reservedSamples.Add(reserved);
            _monoSamples.Add(mono);

            var sampleCount = _reservedSamples.CopyLatestTo(_reservedGraphSamples);
            _allocatedSamples.CopyLatestTo(_allocatedGraphSamples);
            _monoSamples.CopyLatestTo(_monoGraphSamples);

            return new RamSnapshot(
                reserved,
                allocated,
                mono,
                _reservedGraphSamples,
                _allocatedGraphSamples,
                _monoGraphSamples,
                sampleCount);
        }

        private AudioSnapshot CaptureAudio()
        {
            AudioListener.GetSpectrumData(_spectrum, 0, FFTWindow.Blackman);

            var highest = 0f;
            for (var i = 0; i < _spectrum.Length; i++)
                highest = Mathf.Max(highest, _spectrum[i]);

            float? decibels = null;
            if (highest > 0.000001f)
                decibels = Mathf.Clamp(20f * Mathf.Log10(highest), -80f, 0f);

            for (var i = 0; i < _audioGraphSamples.Length; i++)
            {
                var index = Mathf.Clamp(
                    Mathf.RoundToInt(i / (float)(_audioGraphSamples.Length - 1) * (_spectrum.Length - 1)),
                    0,
                    _spectrum.Length - 1);
                _audioGraphSamples[i] = Mathf.Sqrt(Mathf.Clamp01(_spectrum[index] * 80f));
            }

            return new AudioSnapshot(decibels, _audioGraphSamples, _audioGraphSamples.Length);
        }

        private static float LowAverage(float[] sortedValues, int valueCount, float ratio)
        {
            if (valueCount == 0) return 0f;
            var count = Mathf.Clamp(Mathf.CeilToInt(valueCount * ratio), 1, valueCount);
            var total = 0f;
            for (var i = 0; i < count; i++)
                total += sortedValues[i];
            return total / count;
        }

        private sealed class FixedSampleBuffer
        {
            private readonly float[] _values;
            private int _start;

            public FixedSampleBuffer(int capacity)
            {
                _values = new float[capacity];
            }

            public int Count { get; private set; }

            public void Add(float value)
            {
                if (Count < _values.Length)
                {
                    _values[(_start + Count) % _values.Length] = value;
                    Count++;
                    return;
                }

                _values[_start] = value;
                _start = (_start + 1) % _values.Length;
            }

            public void Clear()
            {
                Array.Clear(_values, 0, _values.Length);
                _start = 0;
                Count = 0;
            }

            public int CopyTo(float[] destination)
            {
                var count = Mathf.Min(Count, destination.Length);
                CopyRange(0, count, destination);
                return count;
            }

            public int CopyLatestTo(float[] destination)
            {
                var count = Mathf.Min(Count, destination.Length);
                CopyRange(Count - count, count, destination);
                return count;
            }

            private void CopyRange(int sourceOffset, int count, float[] destination)
            {
                for (var i = 0; i < count; i++)
                    destination[i] = _values[(_start + sourceOffset + i) % _values.Length];
            }
        }
    }
}
