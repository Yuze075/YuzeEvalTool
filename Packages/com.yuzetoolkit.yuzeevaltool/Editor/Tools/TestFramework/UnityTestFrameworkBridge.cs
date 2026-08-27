#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.TestTools;

namespace YuzeToolkit.Eval
{
    [InitializeOnLoad]
    internal static class UnityTestFrameworkBridgeBootstrap
    {
        static UnityTestFrameworkBridgeBootstrap()
        {
            UnityTestToolState.instance.InterruptRunningLists(
                "Test discovery was interrupted by an assembly reload; start a new list request.");
            UnityTestFrameworkBridgeRegistry.Register(new UnityTestFrameworkBridge());
        }
    }

    internal sealed class UnityTestFrameworkBridge : IUnityTestFrameworkBridge
    {
        private readonly TestRunnerApi api;
        private IUnityTestRunCallbacks? callbacks;
        private string? ownedRunId;
        private bool executeInProgress;
        private bool isolationFailureReported;

        public UnityTestFrameworkBridge()
        {
            api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.hideFlags = HideFlags.HideAndDontSave;
            api.RegisterCallbacks(new TestRunnerCallbacks(this));
        }

        public void SetCallbacks(IUnityTestRunCallbacks value)
        {
            callbacks = value ?? throw new ArgumentNullException(nameof(value));
            ownedRunId = UnityTestToolState.instance.GetActiveRunId();
            isolationFailureReported = false;
        }

        public void RetrieveTests(UnityTestFilterSpec filter, int limit, Action<UnityTestDiscoveryData> completed,
            Action<string> failed)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            if (failed == null) throw new ArgumentNullException(nameof(failed));
            api.RetrieveTestList(ParseMode(filter.Mode), root =>
            {
                try
                {
                    completed(UnityTestDiscoveryUtility.Create(root, filter, limit));
                }
                catch (Exception exception)
                {
                    failed($"Test discovery failed: {exception.Message}");
                }
            });
        }

        public string Run(UnityTestFilterSpec filter)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            if (!string.IsNullOrWhiteSpace(ownedRunId))
                throw new InvalidOperationException($"Test run '{ownedRunId}' is already owned by UnityEvalTool.");
            var activeBeforeStart = UnityTestFrameworkRunRegistry.GetActiveRunIds();
            if (activeBeforeStart.Count != 0)
                throw new InvalidOperationException(
                    "Unity Test Framework already has an active run. Yuze Eval Tool waits for exclusive ownership so global framework callbacks cannot be attributed to the wrong run.");
            var testFilter = new Filter
            {
                testMode = ParseMode(filter.Mode),
                assemblyNames = NullIfEmpty(filter.Assemblies),
                testNames = NullIfEmpty(filter.Tests),
                groupNames = NullIfEmpty(filter.Groups),
                categoryNames = NullIfEmpty(filter.Categories)
            };
            executeInProgress = true;
            string runId;
            try
            {
                runId = api.Execute(new ExecutionSettings(testFilter));
            }
            finally
            {
                executeInProgress = false;
            }
            if (string.IsNullOrWhiteSpace(runId))
                throw new InvalidOperationException("Unity Test Framework returned an empty test run id.");
            if (string.IsNullOrWhiteSpace(ownedRunId)) ownedRunId = runId;
            else if (!string.Equals(ownedRunId, runId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Unity Test Framework callback ownership changed from '{ownedRunId}' to returned run '{runId}'.");
            isolationFailureReported = false;
            return runId;
        }

        public bool Cancel(string runId) => TestRunnerApi.CancelTestRun(runId);

        public bool IsRunActive(string runId)
        {
            var active = !string.IsNullOrWhiteSpace(runId) &&
                         UnityTestFrameworkRunRegistry.GetActiveRunIds().Contains(runId);
            if (!active && string.Equals(ownedRunId, runId, StringComparison.Ordinal)) ownedRunId = null;
            return active;
        }

        private static TestMode ParseMode(string mode) =>
            mode == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode;

        private static string[]? NullIfEmpty(List<string> values) =>
            values.Count == 0 ? null : values.ToArray();

        private bool TryGetOwnedRunForCallback(out string runId)
        {
            runId = ownedRunId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(runId) && executeInProgress)
            {
                var activeDuringExecute = UnityTestFrameworkRunRegistry.GetActiveRunIds();
                if (activeDuringExecute.Count == 1)
                {
                    runId = activeDuringExecute.First();
                    ownedRunId = runId;
                    UnityTestToolState.instance.BindRunIdFromCallback(runId);
                }
            }
            if (string.IsNullOrWhiteSpace(runId)) return false;

            IReadOnlyCollection<string> activeRunIds;
            try
            {
                activeRunIds = UnityTestFrameworkRunRegistry.GetActiveRunIds();
            }
            catch (Exception exception)
            {
                ReportIsolationFailure(runId,
                    $"Unable to verify Unity Test Framework callback ownership: {exception.Message}");
                return false;
            }

            if (activeRunIds.Count == 1 && activeRunIds.Contains(runId)) return true;
            if (!activeRunIds.Contains(runId))
            {
                ReportIsolationFailure(runId,
                    $"Unity Test Framework no longer reports owned run '{runId}' as active; its global callback was ignored.");
                return false;
            }

            ReportIsolationFailure(runId,
                "Another Unity Test Framework run became active while UnityEvalTool's run was executing. " +
                "Because framework callbacks do not contain a run id, the owned run was canceled and ambiguous callbacks were ignored.");
            return false;
        }

        private void ReportIsolationFailure(string runId, string message)
        {
            if (isolationFailureReported) return;
            isolationFailureReported = true;
            callbacks?.RunError(runId, message, "OwnershipConflict");
            try
            {
                TestRunnerApi.CancelTestRun(runId);
            }
            finally
            {
                ownedRunId = null;
            }
        }

        private void CompleteOwnership(string runId)
        {
            if (string.Equals(ownedRunId, runId, StringComparison.Ordinal)) ownedRunId = null;
        }

        private sealed class TestRunnerCallbacks : IErrorCallbacks
        {
            private readonly UnityTestFrameworkBridge owner;

            public TestRunnerCallbacks(UnityTestFrameworkBridge owner)
            {
                this.owner = owner;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                if (owner.TryGetOwnedRunForCallback(out var runId))
                    owner.callbacks?.RunStarted(runId, testsToRun?.TestCaseCount ?? 0);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                if (!owner.TryGetOwnedRunForTerminalCallback(out var runId)) return;
                owner.callbacks?.RunFinished(runId, UnityTestResultUtility.CreateRunResult(result));
                owner.CompleteOwnership(runId);
            }

            public void TestStarted(ITestAdaptor test)
            {
                if (test != null && !test.IsSuite)
                {
                    if (owner.TryGetOwnedRunForCallback(out var runId))
                        owner.callbacks?.TestStarted(runId, test.FullName ?? string.Empty);
                }
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result?.Test != null && !result.Test.IsSuite)
                {
                    if (owner.TryGetOwnedRunForCallback(out var runId))
                        owner.callbacks?.TestFinished(runId);
                }
            }

            public void OnError(string message)
            {
                if (!owner.TryGetOwnedRunForTerminalCallback(out var runId)) return;
                owner.callbacks?.RunError(runId, message ?? "Unity Test Framework reported an unspecified run error.",
                    "Error");
                owner.CompleteOwnership(runId);
            }
        }

        private bool TryGetOwnedRunForTerminalCallback(out string runId)
        {
            runId = ownedRunId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(runId)) return false;
            IReadOnlyCollection<string> activeRunIds;
            try
            {
                activeRunIds = UnityTestFrameworkRunRegistry.GetActiveRunIds();
            }
            catch (Exception exception)
            {
                ReportIsolationFailure(runId,
                    $"Unable to verify Unity Test Framework terminal callback ownership: {exception.Message}");
                return false;
            }
            if (activeRunIds.Count == 1 && activeRunIds.Contains(runId)) return true;
            if (activeRunIds.Count == 0) return true;
            ReportIsolationFailure(runId,
                "Another Unity Test Framework run was active while a terminal callback for UnityEvalTool's run was delivered. " +
                "The ambiguous global callback was ignored and the owned run was terminated as an ownership conflict.");
            return false;
        }
    }

    internal static class UnityTestFrameworkRunRegistry
    {
        private const string HolderTypeName =
            "UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder";

        public static IReadOnlyCollection<string> GetActiveRunIds()
        {
            var assembly = typeof(TestRunnerApi).Assembly;
            var holderType = assembly.GetType(HolderTypeName, throwOnError: true)!;
            var instanceProperty = holderType.GetProperty("instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            var holder = instanceProperty?.GetValue(null) ??
                         throw new InvalidOperationException($"{HolderTypeName}.instance is unavailable.");
            var runsField = holderType.GetField("TestRuns",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var runs = runsField?.GetValue(holder) as IEnumerable ??
                       throw new InvalidOperationException($"{HolderTypeName}.TestRuns is unavailable.");
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var run in runs)
            {
                if (run == null) continue;
                var runType = run.GetType();
                var isRunningValue = runType.GetField("isRunning",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(run);
                if (isRunningValue is not bool isRunning || !isRunning) continue;
                var guid = runType.GetField("guid",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(run) as string;
                if (!string.IsNullOrWhiteSpace(guid)) result.Add(guid!);
            }
            return result;
        }
    }

    internal static class UnityTestDiscoveryUtility
    {
        private const int MaxVisitedNodeCount = 50000;
        private const int MaxStoredCharacterCount = 2 * 1024 * 1024;
        private const int MaxFieldCharacterCount = 32768;
        private const string TruncationMarker = "<truncated>";

        public static UnityTestDiscoveryData Create(ITestAdaptor root, UnityTestFilterSpec filter, int limit)
        {
            var matcher = new TestFilterMatcher(filter);
            var stored = new List<UnityTestCaseData>(Math.Min(limit, UnityTestToolUtility.MaxDiscoveryLimit));
            var scan = new DiscoveryScanState();
            var budget = new DiscoveryTextBudget(MaxStoredCharacterCount);
            if (root != null)
                Flatten(root, string.Empty, Array.Empty<string>(), matcher, stored, limit, scan, budget);
            return new UnityTestDiscoveryData
            {
                TotalAvailable = scan.TotalAvailable,
                ScannedTestCount = scan.ScannedTestCount,
                TotalMatched = scan.TotalMatched,
                ScanTruncated = scan.ScanTruncated,
                StorageTruncated = scan.TotalMatched > stored.Count,
                StoredCharacterCount = budget.Used,
                CharacterLimit = MaxStoredCharacterCount,
                TextTruncated = budget.Truncated,
                Tests = stored
            };
        }

        private static void Flatten(
            ITestAdaptor test,
            string inheritedAssembly,
            IReadOnlyCollection<string> inheritedCategories,
            TestFilterMatcher matcher,
            ICollection<UnityTestCaseData> destination,
            int storageLimit,
            DiscoveryScanState scan,
            DiscoveryTextBudget budget)
        {
            if (scan.ScanTruncated) return;
            if (scan.VisitedNodeCount >= MaxVisitedNodeCount)
            {
                scan.ScanTruncated = true;
                return;
            }
            scan.VisitedNodeCount++;
            var assembly = ResolveAssembly(test, inheritedAssembly);
            var categories = inheritedCategories
                .Concat(test.Categories ?? Array.Empty<string>())
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (!test.IsSuite)
            {
                scan.TotalAvailable++;
                scan.ScannedTestCount++;
                var fullName = test.FullName ?? string.Empty;
                if (matcher.Matches(assembly, fullName, categories))
                {
                    scan.TotalMatched++;
                    if (destination.Count < storageLimit && budget.Remaining > 0)
                    {
                        var arguments = (test.Arguments ?? Array.Empty<object>()).Take(129).ToList();
                        if (categories.Length > 128 || arguments.Count > 128) budget.MarkTruncated();
                        destination.Add(new UnityTestCaseData
                        {
                            Id = budget.Store(test.Id ?? string.Empty, MaxFieldCharacterCount),
                            UniqueName = budget.Store(test.UniqueName ?? string.Empty, MaxFieldCharacterCount),
                            Name = budget.Store(test.Name ?? string.Empty, MaxFieldCharacterCount),
                            FullName = budget.Store(fullName, MaxFieldCharacterCount),
                            ParentFullName = budget.Store(test.ParentFullName ?? string.Empty, MaxFieldCharacterCount),
                            Assembly = budget.Store(assembly, MaxFieldCharacterCount),
                            Mode = budget.Store(test.TestMode.ToString(), MaxFieldCharacterCount),
                            RunState = budget.Store(test.RunState.ToString(), MaxFieldCharacterCount),
                            Description = budget.Store(test.Description ?? string.Empty, MaxFieldCharacterCount),
                            SkipReason = budget.Store(test.SkipReason ?? string.Empty, MaxFieldCharacterCount),
                            Categories = categories.Take(128)
                                .Select(category => budget.Store(category, MaxFieldCharacterCount)).ToList(),
                            Arguments = arguments.Take(128)
                                .Select(argument => budget.Store(FormatArgument(argument), MaxFieldCharacterCount))
                                .ToList()
                        });
                    }
                }
            }

            if (test.Children == null) return;
            foreach (var child in test.Children)
            {
                if (child != null) Flatten(child, assembly, categories, matcher, destination, storageLimit, scan, budget);
                if (scan.ScanTruncated) break;
            }
        }

        private sealed class DiscoveryScanState
        {
            public int TotalAvailable;
            public int VisitedNodeCount;
            public int ScannedTestCount;
            public int TotalMatched;
            public bool ScanTruncated;
        }

        private sealed class DiscoveryTextBudget
        {
            private readonly int limit;

            public DiscoveryTextBudget(int limit)
            {
                this.limit = limit;
            }

            public int Used { get; private set; }
            public int Remaining => Math.Max(0, limit - Used);
            public bool Truncated { get; private set; }

            public void MarkTruncated()
            {
                Truncated = true;
            }

            public string Store(string value, int fieldLimit)
            {
                value ??= string.Empty;
                var allowed = Math.Min(fieldLimit, Math.Max(0, limit - Used));
                if (value.Length <= allowed)
                {
                    Used += value.Length;
                    return value;
                }
                Truncated = true;
                if (allowed == 0) return string.Empty;
                if (allowed <= TruncationMarker.Length)
                {
                    Used += allowed;
                    return TruncationMarker.Substring(0, allowed);
                }
                Used += allowed;
                return value.Substring(0, allowed - TruncationMarker.Length) + TruncationMarker;
            }
        }

        private static string ResolveAssembly(ITestAdaptor test, string inheritedAssembly)
        {
            var fullName = test.TypeInfo?.Assembly?.FullName;
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                var normalizedFullName = fullName!;
                var comma = normalizedFullName.IndexOf(',');
                return (comma >= 0 ? normalizedFullName.Substring(0, comma) : normalizedFullName).Trim();
            }

            if (test.IsTestAssembly && !string.IsNullOrWhiteSpace(test.Name))
                return Path.GetFileNameWithoutExtension(test.Name);
            return inheritedAssembly;
        }

        private static string FormatArgument(object? argument)
        {
            if (argument == null) return "null";
            if (argument is string text) return text;
            if (argument is char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal)
                return Convert.ToString(argument, CultureInfo.InvariantCulture) ?? string.Empty;
            if (argument is Enum enumValue) return enumValue.ToString();
            if (argument is Type typeValue) return typeValue.FullName ?? typeValue.Name;
            return $"<{argument.GetType().FullName ?? argument.GetType().Name}>";
        }

        private sealed class TestFilterMatcher
        {
            private readonly UnityTestFilterSpec filter;
            private readonly List<RegexValue> groups;
            private readonly List<RegexValue> categories;

            public TestFilterMatcher(UnityTestFilterSpec filter)
            {
                this.filter = filter;
                groups = filter.Groups.Select(RegexValue.Create).ToList();
                categories = filter.Categories.Select(RegexValue.Create).ToList();
            }

            public bool Matches(string assembly, string fullName, IReadOnlyCollection<string> testCategories) =>
                MatchesValues(filter.Assemblies, value => string.Equals(assembly, value, StringComparison.OrdinalIgnoreCase)) &&
                MatchesValues(filter.Tests, value => string.Equals(fullName, value, StringComparison.Ordinal)) &&
                MatchesRegex(groups, regex => regex.IsMatch(fullName)) &&
                MatchesRegex(categories, regex => testCategories.Any(regex.IsMatch));

            private static bool MatchesValues(IReadOnlyCollection<string> values, Func<string, bool> match)
            {
                if (values.Count == 0) return true;
                var included = values.Where(value => !value.StartsWith("!", StringComparison.Ordinal)).ToList();
                var excluded = values.Where(value => value.StartsWith("!", StringComparison.Ordinal)).Select(value => value.Substring(1));
                return (included.Count == 0 || included.Any(match)) && excluded.All(value => !match(value));
            }

            private static bool MatchesRegex(IReadOnlyCollection<RegexValue> values, Func<Regex, bool> match)
            {
                if (values.Count == 0) return true;
                var included = values.Where(value => !value.Excluded).ToList();
                var excluded = values.Where(value => value.Excluded);
                return (included.Count == 0 || included.Any(value => match(value.Regex))) &&
                       excluded.All(value => !match(value.Regex));
            }

            private sealed class RegexValue
            {
                public bool Excluded { get; private set; }
                public Regex Regex { get; private set; } = null!;

                public static RegexValue Create(string value)
                {
                    var excluded = value.StartsWith("!", StringComparison.Ordinal);
                    return new RegexValue
                    {
                        Excluded = excluded,
                        Regex = new Regex(excluded ? value.Substring(1) : value, RegexOptions.CultureInvariant,
                            UnityTestToolUtility.RegexTimeout)
                    };
                }
            }
        }
    }

    internal static class UnityTestResultUtility
    {
        private const int MaxStoredResultCount = 10000;
        private const int MaxResultTextLength = 32768;
        private const int MaxStoredCharacterCount = 2 * 1024 * 1024;
        private const string TruncationMarker = "\n<truncated>";

        public static UnityTestRunResultData CreateRunResult(ITestResultAdaptor root)
        {
            var results = new List<UnityTestResultData>();
            var totalResultCount = 0;
            var budget = new ResultTextBudget(MaxStoredCharacterCount);
            var summary = Create(root, budget);
            if (root != null) FlattenLeaves(root, results, ref totalResultCount, budget);
            return new UnityTestRunResultData
            {
                Summary = summary,
                Results = results,
                TotalResultCount = totalResultCount,
                ResultsTruncated = totalResultCount > results.Count || budget.Truncated,
                StoredCharacterCount = budget.Used,
                CharacterLimit = MaxStoredCharacterCount,
                TextTruncated = budget.Truncated
            };
        }

        private static UnityTestResultData Create(ITestResultAdaptor? result, ResultTextBudget budget)
        {
            if (result == null) return new UnityTestResultData();
            return new UnityTestResultData
            {
                Name = budget.Store(result.Name ?? string.Empty, MaxResultTextLength),
                FullName = budget.Store(result.FullName ?? string.Empty, MaxResultTextLength),
                TestStatus = budget.Store(result.TestStatus.ToString(), MaxResultTextLength),
                ResultState = budget.Store(result.ResultState ?? string.Empty, MaxResultTextLength),
                Duration = result.Duration,
                AssertCount = result.AssertCount,
                PassCount = result.PassCount,
                FailCount = result.FailCount,
                SkipCount = result.SkipCount,
                InconclusiveCount = result.InconclusiveCount,
                Message = budget.Store(result.Message ?? string.Empty, MaxResultTextLength),
                StackTrace = budget.Store(result.StackTrace ?? string.Empty, MaxResultTextLength),
                Output = budget.Store(result.Output ?? string.Empty, MaxResultTextLength)
            };
        }

        private static void FlattenLeaves(ITestResultAdaptor result, ICollection<UnityTestResultData> destination,
            ref int totalResultCount, ResultTextBudget budget)
        {
            if (result.Test != null && !result.Test.IsSuite)
            {
                totalResultCount++;
                if (destination.Count < MaxStoredResultCount && budget.Remaining > 0)
                    destination.Add(Create(result, budget));
                else
                    budget.MarkTruncated();
            }
            if (result.Children == null) return;
            foreach (var child in result.Children)
            {
                if (child != null) FlattenLeaves(child, destination, ref totalResultCount, budget);
            }
        }

        private sealed class ResultTextBudget
        {
            private readonly int limit;

            public ResultTextBudget(int limit)
            {
                this.limit = limit;
            }

            public int Used { get; private set; }
            public int Remaining => Math.Max(0, limit - Used);
            public bool Truncated { get; private set; }

            public string Store(string value, int fieldLimit)
            {
                value ??= string.Empty;
                var allowed = Math.Min(fieldLimit, Remaining);
                if (value.Length <= allowed)
                {
                    Used += value.Length;
                    return value;
                }

                Truncated = true;
                if (allowed <= 0) return string.Empty;
                if (allowed <= TruncationMarker.Length)
                {
                    Used += allowed;
                    return TruncationMarker.Substring(0, allowed);
                }

                var prefixLength = allowed - TruncationMarker.Length;
                Used += allowed;
                return value.Substring(0, prefixLength) + TruncationMarker;
            }

            public void MarkTruncated()
            {
                Truncated = true;
            }
        }
    }
}
