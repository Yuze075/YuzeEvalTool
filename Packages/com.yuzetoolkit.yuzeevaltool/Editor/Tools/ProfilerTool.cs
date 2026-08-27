#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    [EvalTool("Profiler", "Global Unity Profiler metric discovery and bounded CPU sampling in PlayMode through ProfilerRecorder.")]
    public sealed partial class ProfilerTool
    {
        [EvalFunction(
            "List globally available Profiler metrics. Discovery includes GPU and worker-thread metrics; start samples CPU data with an explicit thread scope. category is exact; nameContains is ordinal case-insensitive.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> listAvailable(
            [EvalParameter("Optional exact Profiler category name, at most 512 characters.")] string category = "",
            [EvalParameter("Optional case-insensitive metric-name substring, at most 512 characters.")] string nameContains = "",
            [EvalParameter("Maximum returned metrics, clamped to 1..512.")] int limit = 256)
        {
            return ProfilerSamplingStore.ListAvailable(category, nameContains, limit);
        }

        [EvalFunction(
            "Start a bounded CPU ProfilerRecorder session in PlayMode. metrics is an array of exact {category,name} pairs. threadScope is 'main-thread' or 'all-threads'. Sampling begins after warmup and discards one guard frame at each edge when Unity reports a complete frame window.",
            Safety = EvalToolSafety.MutatesRuntimeState | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> start(
            [EvalParameter("Array of 1..16 exact {category,name} Profiler metric pairs; each string is at most 512 characters.")] object metrics,
            [EvalParameter("Player frames to ignore before recorders start; 0..36000.")] int warmupFrames = 120,
            [EvalParameter("Player frames retained for statistics; 1..10000.")] int sampleFrames = 300,
            [EvalParameter("Optional diagnostic label, truncated to 128 characters.")] string label = "",
            [EvalParameter("CPU sample scope: 'main-thread' (default) or 'all-threads'.")] string threadScope = "main-thread")
        {
            return ProfilerSamplingStore.Start(metrics, warmupFrames, sampleFrames, label, threadScope);
        }

        [EvalFunction(
            "Get one CPU Profiler sampling session. Statistics use raw ProfilerRecorder values; optional samples are paged independently for every metric.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> get(
            [EvalParameter("Profiler sampling session id returned by start.")] string id,
            [EvalParameter("Include raw {index,value,count} samples for every metric.")] bool includeSamples = false,
            [EvalParameter("Zero-based raw sample offset in range 0..10000.")] int offset = 0,
            [EvalParameter("Raw samples per metric, clamped to 1..500.")] int limit = 200)
        {
            return ProfilerSamplingStore.Get(id, includeSamples, offset, limit);
        }

        [EvalFunction("Stop a running Profiler sampling session and retain captured data.", Safety = EvalToolSafety.MutatesRuntimeState)]
        public Dictionary<string, object?> cancel(
            [EvalParameter("Profiler sampling session id returned by start.")] string id)
        {
            return ProfilerSamplingStore.Cancel(id);
        }

        [EvalFunction("Release one Profiler sampling session and all retained samples.", Safety = EvalToolSafety.MutatesRuntimeState)]
        public Dictionary<string, object?> release(
            [EvalParameter("Profiler sampling session id whose data should be released.")] string id)
        {
            return ProfilerSamplingStore.Release(id);
        }
    }

    [InitializeOnLoad]
    internal static class ProfilerSamplingStore
    {
        private const string MainThreadScope = "main-thread";
        private const string AllThreadsScope = "all-threads";
        private const string MainThreadSamplingScope = "main-thread-cpu";
        private const string AllThreadsSamplingScope = "all-threads-cpu";
        private const string MainThreadTimeAggregation = "single-thread-accumulated-time";
        private const string AllThreadsTimeAggregation = "accumulated-concurrent-thread-time-not-wall-clock";
        private const string SampleAggregation = "sum-all-samples-per-player-frame";
        private const string PercentileScope = "sample-frame";
        private const string MeanPerInvocationScope = "marker-invocation";
        private const string CounterSampleScope = "sample-frame";
        private const string CounterMultipleWritesSemantics =
            "value-counters-may-report-last-written-value-per-player-frame";
        private const int MaxActiveSessions = 4;
        private const int MaxRetainedSessions = 16;
        private const int MaxMetricCount = 16;
        private const int MaxWarmupFrames = 36000;
        private const int MaxSampleFrames = 10000;
        private const int GuardFrameCount = 1;
        private const int MaxPageSize = 500;
        private const int MaxLabelLength = 128;
        private const int MaxErrorLength = 1024;
        private const int MaxMetricTextLength = 512;
        private const int MaxSessionIdLength = 64;

        private static readonly Dictionary<string, ProfilerSamplingSession> Sessions =
            new(StringComparer.Ordinal);

        private static bool _updateRegistered;

        static ProfilerSamplingStore()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            EditorApplication.quitting += DisposeAll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static Dictionary<string, object?> ListAvailable(string category, string nameContains, int limit)
        {
            category = category?.Trim() ?? string.Empty;
            nameContains = nameContains?.Trim() ?? string.Empty;
            ValidateTextLength(category, MaxMetricTextLength, "category");
            ValidateTextLength(nameContains, MaxMetricTextLength, "nameContains");
            limit = Clamp(limit, 1, 512);

            var metrics = GetAvailableMetrics()
                .Where(metric => category.Length == 0 ||
                                 string.Equals(metric.Category, category, StringComparison.Ordinal))
                .Where(metric => nameContains.Length == 0 ||
                                 metric.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(metric => metric.Category, StringComparer.Ordinal)
                .ThenBy(metric => metric.Name, StringComparer.Ordinal)
                .ToList();

            return EvalData.Obj(
                ("discoveryScope", "global-profiler-registry"),
                ("samplingScope", MainThreadSamplingScope),
                ("defaultThreadScope", MainThreadScope),
                ("supportedThreadScopes", new List<object?> { MainThreadScope, AllThreadsScope }),
                ("scopeNotice", "The global registry includes GPU and worker-thread metrics. start records CPU samples from the selected main-thread or all-threads scope; discovery does not guarantee that a metric emits samples in either scope."),
                ("count", Math.Min(metrics.Count, limit)),
                ("matchedCount", metrics.Count),
                ("truncated", metrics.Count > limit),
                ("metrics", metrics.Take(limit).Select(metric => (object?)metric.ToObject()).ToList()));
        }

        public static Dictionary<string, object?> Start(object metricsValue, int warmupFrames, int sampleFrames,
            string label, string threadScope)
        {
            if (!Application.isPlaying || EditorStatusProvider.IsChangingPlayMode)
                throw new InvalidOperationException("Profiler sampling requires stable PlayMode.");
            if (EditorApplication.isPaused)
                throw new InvalidOperationException("Profiler sampling cannot start while PlayMode is paused.");

            ValidateRange(warmupFrames, 0, MaxWarmupFrames, "warmupFrames");
            ValidateRange(sampleFrames, 1, MaxSampleFrames, "sampleFrames");
            var requestedMetrics = ParseRequestedMetrics(metricsValue);
            label = Truncate(label?.Trim() ?? string.Empty, MaxLabelLength);
            threadScope = ValidateThreadScope(threadScope);

            if (Sessions.Values.Count(session => session.IsRunning) >= MaxActiveSessions)
                throw new InvalidOperationException(
                    $"At most {MaxActiveSessions} Profiler sessions may run at once. Cancel or wait for one to finish.");
            MakeRoomForSession();

            var session = new ProfilerSamplingSession(
                Guid.NewGuid().ToString("N"),
                label,
                requestedMetrics,
                warmupFrames,
                sampleFrames,
                threadScope,
                Time.frameCount);
            Sessions.Add(session.Id, session);
            try
            {
                if (warmupFrames == 0)
                    BeginSampling(session, Time.frameCount);
                EnsureUpdate();
                return ToResult(session, false, 0, 20);
            }
            catch
            {
                Sessions.Remove(session.Id);
                DisposeRecorders(session);
                StopUpdateWhenIdle();
                throw;
            }
        }

        public static Dictionary<string, object?> Get(string id, bool includeSamples, int offset, int limit)
        {
            var session = RequireSession(id);
            ValidateRange(offset, 0, MaxSampleFrames, "offset");
            return ToResult(session, includeSamples, offset, Clamp(limit, 1, MaxPageSize));
        }

        public static Dictionary<string, object?> Cancel(string id)
        {
            var session = RequireSession(id);
            if (session.IsRunning)
            {
                try
                {
                    StopAndCapture(session, false);
                    session.Status = "cancelled";
                    session.CompletionReason = "cancelled";
                    session.SamplingCompletedAtFrame = Time.frameCount;
                    session.CompletedAtUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    Fault(session, ex);
                }
            }

            StopUpdateWhenIdle();
            return ToResult(session, false, 0, 20);
        }

        public static Dictionary<string, object?> Release(string id)
        {
            var session = RequireSession(id);
            Sessions.Remove(session.Id);
            DisposeRecorders(session);
            StopUpdateWhenIdle();
            return EvalData.Obj(
                ("released", true),
                ("id", session.Id),
                ("metricCount", session.Metrics.Count),
                ("retainedSampleCount", session.Metrics.Sum(metric => metric.Samples.Count)));
        }

        private static List<AvailableProfilerMetric> GetAvailableMetrics()
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var metrics = new List<AvailableProfilerMetric>(handles.Count);
            foreach (var handle in handles)
            {
                if (!handle.Valid)
                    continue;
                var description = ProfilerRecorderHandle.GetDescription(handle);
                metrics.Add(new AvailableProfilerMetric(
                    description.Category.Name,
                    description.Name,
                    description.UnitType.ToString(),
                    description.DataType.ToString(),
                    handle));
            }

            return metrics;
        }

        private static List<ProfilerMetricCapture> ParseRequestedMetrics(object metricsValue)
        {
            var values = EvalData.AsArray(metricsValue);
            if (values == null || values.Count == 0)
                throw new InvalidOperationException("Argument 'metrics' must be a non-empty array.");
            if (values.Count > MaxMetricCount)
                throw new InvalidOperationException($"A Profiler session supports at most {MaxMetricCount} metrics.");

            var metrics = new List<ProfilerMetricCapture>(values.Count);
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < values.Count; i++)
            {
                var item = EvalData.AsObject(values[i]);
                if (item == null)
                    throw new InvalidOperationException($"metrics[{i}] must be an object.");
                var category = (EvalData.GetString(item, "category") ?? string.Empty).Trim();
                var name = (EvalData.GetString(item, "name") ?? string.Empty).Trim();
                if (category.Length == 0 || name.Length == 0)
                    throw new InvalidOperationException($"metrics[{i}] requires non-empty 'category' and 'name'.");
                ValidateTextLength(category, MaxMetricTextLength, $"metrics[{i}].category");
                ValidateTextLength(name, MaxMetricTextLength, $"metrics[{i}].name");
                if (!pairs.Add(category + "\0" + name))
                    throw new InvalidOperationException($"Profiler metric '{category}/{name}' is duplicated.");
                metrics.Add(new ProfilerMetricCapture(category, name));
            }

            return metrics;
        }

        private static void EnsureUpdate()
        {
            if (_updateRegistered)
                return;
            EditorApplication.update += Tick;
            _updateRegistered = true;
        }

        private static void Tick()
        {
            if (!Application.isPlaying)
            {
                CancelRunning("play-mode-exit");
                return;
            }
            if (EditorApplication.isPaused)
            {
                CancelRunning("play-mode-paused");
                return;
            }

            var frame = Time.frameCount;
            foreach (var session in Sessions.Values.Where(candidate => candidate.IsRunning).ToArray())
            {
                if (frame == session.LastUnityFrame)
                    continue;
                session.LastUnityFrame = frame;
                try
                {
                    Advance(session, frame);
                }
                catch (Exception ex)
                {
                    Fault(session, ex);
                }
            }

            StopUpdateWhenIdle();
        }

        private static void Advance(ProfilerSamplingSession session, int frame)
        {
            if (session.Status == "warming")
            {
                if (session.WarmedUpFrames < session.WarmupFrames)
                    session.WarmedUpFrames++;
                if (session.WarmedUpFrames >= session.WarmupFrames)
                    BeginSampling(session, frame);
                return;
            }

            session.ObservedSamplingFrames++;
            if (session.ObservedSamplingFrames < session.SampleFrames + GuardFrameCount * 2)
                return;

            StopAndCapture(session, true);
            session.Status = "completed";
            session.CompletionReason = "frame-limit";
            session.SamplingCompletedAtFrame = frame;
            session.CompletedAtUtc = DateTime.UtcNow;
        }

        private static void BeginSampling(ProfilerSamplingSession session, int frame)
        {
            StartRecorders(session);
            session.Status = "sampling";
            session.SamplingStartedAtFrame = frame;
        }

        private static void StartRecorders(ProfilerSamplingSession session)
        {
            var available = GetAvailableMetrics();
            foreach (var metric in session.Metrics)
            {
                var matches = available.Where(candidate =>
                        string.Equals(candidate.Category, metric.Category, StringComparison.Ordinal) &&
                        string.Equals(candidate.Name, metric.Name, StringComparison.Ordinal))
                    .ToList();
                if (matches.Count == 0)
                    throw new InvalidOperationException(
                        $"Profiler metric '{metric.Category}/{metric.Name}' is not available after warmup.");
                if (matches.Count > 1)
                    throw new InvalidOperationException(
                        $"Profiler metric '{metric.Category}/{metric.Name}' is ambiguous ({matches.Count} handles).");

                var availableMetric = matches[0];
                metric.Unit = availableMetric.Unit;
                metric.DataType = availableMetric.DataType;
                var options = ProfilerRecorderOptions.StartImmediately |
                              ProfilerRecorderOptions.SumAllSamplesInFrame;
                if (session.ThreadScope == MainThreadScope)
                    options |= ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
                metric.Recorder = new ProfilerRecorder(
                    availableMetric.Handle,
                    session.SampleFrames + GuardFrameCount * 2,
                    options);
                metric.HasRecorder = true;
                if (!metric.Recorder.Valid)
                    throw new InvalidOperationException(
                        $"ProfilerRecorder for '{metric.Category}/{metric.Name}' is invalid.");
                metric.WasValid = true;
            }
        }

        private static void StopAndCapture(ProfilerSamplingSession session, bool trimGuardFrames)
        {
            Exception? firstError = null;
            foreach (var metric in session.Metrics)
            {
                if (!metric.HasRecorder)
                    continue;
                try
                {
                    if (metric.Recorder.IsRunning)
                        metric.Recorder.Stop();
                    var samples = metric.Recorder.ToArray();
                    var completeWindow = trimGuardFrames &&
                                         samples.Length >= session.SampleFrames + GuardFrameCount * 2;
                    var start = completeWindow ? GuardFrameCount : 0;
                    var count = completeWindow ? session.SampleFrames : samples.Length;
                    metric.CompleteFrameWindow = completeWindow;
                    metric.DiscardedGuardSamples = completeWindow ? GuardFrameCount * 2 : 0;
                    metric.Samples.Clear();
                    for (var i = 0; i < count; i++)
                    {
                        var sample = samples[start + i];
                        metric.Samples.Add(new RawProfilerSample(sample.Value, sample.Count));
                    }
                    metric.CaptureCompleted = true;
                }
                catch (Exception ex)
                {
                    firstError ??= ex;
                }
                finally
                {
                    DisposeRecorder(metric);
                }
            }

            if (firstError != null)
                throw firstError;
        }

        private static void Fault(ProfilerSamplingSession session, Exception ex)
        {
            DisposeRecorders(session);
            session.Status = "faulted";
            session.CompletionReason = "internal-error";
            session.Error = Truncate($"{ex.GetType().Name}: {ex.Message}", MaxErrorLength);
            session.CompletedAtUtc = DateTime.UtcNow;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
                CancelRunning("play-mode-exit");
        }

        private static void CancelRunning(string reason)
        {
            foreach (var session in Sessions.Values.Where(candidate => candidate.IsRunning).ToArray())
            {
                try
                {
                    StopAndCapture(session, false);
                    session.Status = "cancelled";
                    session.CompletionReason = reason;
                    session.SamplingCompletedAtFrame = Time.frameCount;
                    session.CompletedAtUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    Fault(session, ex);
                }
            }

            StopUpdateWhenIdle();
        }

        private static void DisposeAll()
        {
            EditorApplication.update -= Tick;
            _updateRegistered = false;
            foreach (var session in Sessions.Values)
                DisposeRecorders(session);
            Sessions.Clear();
        }

        private static void DisposeRecorders(ProfilerSamplingSession session)
        {
            foreach (var metric in session.Metrics)
                DisposeRecorder(metric);
        }

        private static void DisposeRecorder(ProfilerMetricCapture metric)
        {
            if (!metric.HasRecorder)
                return;
            metric.Recorder.Dispose();
            metric.Recorder = default;
            metric.HasRecorder = false;
        }

        private static void StopUpdateWhenIdle()
        {
            if (!_updateRegistered || Sessions.Values.Any(session => session.IsRunning))
                return;
            EditorApplication.update -= Tick;
            _updateRegistered = false;
        }

        private static void MakeRoomForSession()
        {
            while (Sessions.Count >= MaxRetainedSessions)
            {
                var oldest = Sessions.Values
                    .Where(session => !session.IsRunning)
                    .OrderBy(session => session.CreatedAtUtc)
                    .FirstOrDefault();
                if (oldest == null)
                    throw new InvalidOperationException(
                        $"All {MaxRetainedSessions} retained Profiler sessions are active. Cancel or release one first.");
                Sessions.Remove(oldest.Id);
                DisposeRecorders(oldest);
            }
        }

        private static ProfilerSamplingSession RequireSession(string id)
        {
            var normalizedId = id?.Trim() ?? string.Empty;
            ValidateTextLength(normalizedId, MaxSessionIdLength, "id");
            if (normalizedId.Length == 0 || !Sessions.TryGetValue(normalizedId, out var session))
                throw new InvalidOperationException($"Profiler session '{id}' was not found.");
            return session;
        }

        private static Dictionary<string, object?> ToResult(ProfilerSamplingSession session, bool includeSamples,
            int offset, int limit)
        {
            var allThreads = session.ThreadScope == AllThreadsScope;
            var metrics = session.Metrics.Select(metric =>
                    (object?)ToMetricResult(metric, includeSamples, offset, limit))
                .ToList();
            return EvalData.Obj(
                ("id", session.Id),
                ("label", session.Label),
                ("status", session.Status),
                ("completionReason", session.CompletionReason),
                ("error", session.Error),
                ("threadScope", session.ThreadScope),
                ("samplingScope", allThreads ? AllThreadsSamplingScope : MainThreadSamplingScope),
                ("sampleAggregation", SampleAggregation),
                ("timeAggregationSemantics", allThreads
                    ? AllThreadsTimeAggregation
                    : MainThreadTimeAggregation),
                ("canExceedWallClockFrameTime", allThreads),
                ("scopeWarning", allThreads
                    ? "All-thread time values sum samples across concurrent threads per Player frame and can exceed wall-clock frame time. Semaphore.WaitForSignal measures waiting spans, not CPU work."
                    : "Main-thread scope excludes worker-thread samples."),
                ("percentileScope", PercentileScope),
                ("meanPerInvocationScope", MeanPerInvocationScope),
                ("counterSampleScope", CounterSampleScope),
                ("counterMultipleWritesSemantics", CounterMultipleWritesSemantics),
                ("createdAtUtc", session.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("completedAtUtc", session.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture)),
                ("warmupFrames", session.WarmupFrames),
                ("warmedUpFrames", session.WarmedUpFrames),
                ("sampleFrames", session.SampleFrames),
                ("observedSamplingFrames", session.ObservedSamplingFrames),
                ("samplingStartedAtFrame", session.SamplingStartedAtFrame),
                ("samplingCompletedAtFrame", session.SamplingCompletedAtFrame),
                ("metricCount", metrics.Count),
                ("samplePage", EvalData.Obj(
                    ("included", includeSamples),
                    ("offset", offset),
                    ("limit", limit))),
                ("metrics", metrics));
        }

        private static Dictionary<string, object?> ToMetricResult(ProfilerMetricCapture metric,
            bool includeSamples, int offset, int limit)
        {
            var values = metric.Samples.Select(sample => sample.Value).ToList();
            var sorted = values.OrderBy(value => value).ToList();
            long? total = null;
            if (values.Count > 0)
            {
                long sum = 0;
                checked
                {
                    foreach (var value in values)
                        sum += value;
                }
                total = sum;
            }

            long invocationCount = 0;
            checked
            {
                foreach (var sample in metric.Samples)
                    invocationCount += sample.Count;
            }

            var result = EvalData.Obj(
                ("category", metric.Category),
                ("name", metric.Name),
                ("unit", metric.Unit),
                ("dataType", metric.DataType),
                ("valueEncoding", "raw-int64"),
                ("valid", metric.WasValid && metric.CaptureCompleted),
                ("sampleCount", metric.Samples.Count),
                ("invocationCount", invocationCount),
                ("completeFrameWindow", metric.CompleteFrameWindow),
                ("discardedGuardSamples", metric.DiscardedGuardSamples),
                ("total", total),
                ("min", sorted.Count > 0 ? sorted[0] : null),
                ("mean", total.HasValue ? (double)total.Value / values.Count : null),
                ("meanPerInvocation", total.HasValue && invocationCount > 0
                    ? (double)total.Value / invocationCount
                    : null),
                ("p50", Percentile(sorted, 0.50)),
                ("p95", Percentile(sorted, 0.95)),
                ("max", sorted.Count > 0 ? sorted[^1] : null));

            if (string.Equals(metric.Unit, ProfilerMarkerDataUnit.TimeNanoseconds.ToString(),
                    StringComparison.Ordinal))
            {
                result["totalMs"] = total.HasValue ? total.Value / 1_000_000d : null;
                result["minMs"] = sorted.Count > 0 ? sorted[0] / 1_000_000d : null;
                result["meanFrameMs"] = total.HasValue ? total.Value / (double)values.Count / 1_000_000d : null;
                result["meanPerInvocationMs"] = total.HasValue && invocationCount > 0
                    ? total.Value / (double)invocationCount / 1_000_000d
                    : null;
                result["p50Ms"] = ToMilliseconds(Percentile(sorted, 0.50));
                result["p95Ms"] = ToMilliseconds(Percentile(sorted, 0.95));
                result["maxMs"] = sorted.Count > 0 ? sorted[^1] / 1_000_000d : null;
            }

            if (!includeSamples)
                return result;

            var page = metric.Samples
                .Skip(offset)
                .Take(limit)
                .Select((sample, pageIndex) => (object?)EvalData.Obj(
                    ("index", offset + pageIndex),
                    ("value", sample.Value),
                    ("count", sample.Count)))
                .ToList();
            result["samples"] = EvalData.Obj(
                ("offset", offset),
                ("limit", limit),
                ("returnedCount", page.Count),
                ("hasMore", offset + page.Count < metric.Samples.Count),
                ("items", page));
            return result;
        }

        private static long? Percentile(IReadOnlyList<long> sorted, double percentile)
        {
            if (sorted.Count == 0)
                return null;
            var index = Math.Max(0, (int)Math.Ceiling(percentile * sorted.Count) - 1);
            return sorted[index];
        }

        private static double? ToMilliseconds(long? nanoseconds) =>
            nanoseconds.HasValue ? nanoseconds.Value / 1_000_000d : null;

        private static void ValidateRange(int value, int min, int max, string name)
        {
            if (value < min || value > max)
                throw new InvalidOperationException($"Argument '{name}' must be in range {min}..{max}.");
        }

        private static void ValidateTextLength(string value, int maxLength, string name)
        {
            if (value.Length > maxLength)
                throw new InvalidOperationException(
                    $"Argument '{name}' must contain at most {maxLength} characters.");
        }

        private static string ValidateThreadScope(string value)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (normalized == MainThreadScope || normalized == AllThreadsScope)
                return normalized;
            throw new InvalidOperationException(
                "Argument 'threadScope' must be exactly 'main-thread' or 'all-threads'.");
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }

    internal sealed class ProfilerSamplingSession
    {
        public ProfilerSamplingSession(string id, string label, List<ProfilerMetricCapture> metrics,
            int warmupFrames, int sampleFrames, string threadScope, int initialFrame)
        {
            Id = id;
            Label = label;
            Metrics = metrics;
            WarmupFrames = warmupFrames;
            SampleFrames = sampleFrames;
            ThreadScope = threadScope;
            LastUnityFrame = initialFrame;
            CreatedAtUtc = DateTime.UtcNow;
        }

        public string Id { get; }
        public string Label { get; }
        public List<ProfilerMetricCapture> Metrics { get; }
        public int WarmupFrames { get; }
        public int SampleFrames { get; }
        public string ThreadScope { get; }
        public DateTime CreatedAtUtc { get; }
        public DateTime? CompletedAtUtc { get; set; }
        public string Status { get; set; } = "warming";
        public string CompletionReason { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public int LastUnityFrame { get; set; }
        public int WarmedUpFrames { get; set; }
        public int ObservedSamplingFrames { get; set; }
        public int SamplingStartedAtFrame { get; set; } = -1;
        public int SamplingCompletedAtFrame { get; set; } = -1;
        public bool IsRunning => Status is "warming" or "sampling";
    }

    internal sealed class ProfilerMetricCapture
    {
        public ProfilerMetricCapture(string category, string name)
        {
            Category = category;
            Name = name;
        }

        public string Category { get; }
        public string Name { get; }
        public string Unit { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public ProfilerRecorder Recorder;
        public bool HasRecorder { get; set; }
        public bool WasValid { get; set; }
        public bool CaptureCompleted { get; set; }
        public bool CompleteFrameWindow { get; set; }
        public int DiscardedGuardSamples { get; set; }
        public List<RawProfilerSample> Samples { get; } = new();
    }

    internal readonly struct RawProfilerSample
    {
        public RawProfilerSample(long value, long count)
        {
            Value = value;
            Count = count;
        }

        public long Value { get; }
        public long Count { get; }
    }

    internal readonly struct AvailableProfilerMetric
    {
        public AvailableProfilerMetric(string category, string name, string unit, string dataType,
            ProfilerRecorderHandle handle)
        {
            Category = category;
            Name = name;
            Unit = unit;
            DataType = dataType;
            Handle = handle;
        }

        public string Category { get; }
        public string Name { get; }
        public string Unit { get; }
        public string DataType { get; }
        public ProfilerRecorderHandle Handle { get; }

        public Dictionary<string, object?> ToObject() => EvalData.Obj(
            ("category", Category),
            ("name", Name),
            ("unit", Unit),
            ("dataType", DataType));
    }
}
