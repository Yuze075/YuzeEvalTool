#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    [EvalTool("Tests", "Discover, run, inspect, and cancel Unity Test Framework EditMode and PlayMode tests without blocking the Editor.")]
    public sealed partial class TestsTool
    {
        [EvalFunction("Start bounded asynchronous test discovery. Returns a listId; call getList(listId) to read the completed result. groups/categories accept only the documented bounded safe-regex subset.", Safety = EvalToolSafety.ReadOnly | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> list(
            [EvalParameter("EditMode or PlayMode.")]
            string mode = "EditMode",
            [EvalParameter("Optional exact assembly name or array; prefix an entry with ! to exclude it.")]
            object? assemblies = null,
            [EvalParameter("Optional exact full test name or array; prefix an entry with ! to exclude it.")]
            object? tests = null,
            [EvalParameter("Optional bounded safe-regex group/full-name filter or array; prefix an entry with ! to exclude it.")]
            object? groups = null,
            [EvalParameter("Optional bounded safe-regex category filter or array; prefix an entry with ! to exclude it.")]
            object? categories = null,
            [EvalParameter("Maximum retained matching tests; 1..5000.")]
            int limit = 500)
        {
            UnityTestToolUtility.ValidateDiscoveryLimit(limit);
            var filter = UnityTestToolUtility.CreateFilter(mode, assemblies, tests, groups, categories);
            var bridge = UnityTestFrameworkBridgeRegistry.Require();
            var listId = UnityTestToolState.instance.BeginList(filter, limit);
            try
            {
                bridge.RetrieveTests(
                    filter,
                    limit,
                    result => UnityTestToolState.instance.CompleteList(listId, result),
                    error => UnityTestToolState.instance.FailList(listId, error));
            }
            catch (Exception exception)
            {
                UnityTestToolState.instance.FailList(listId, exception.Message);
                throw;
            }

            return UnityTestToolState.instance.GetList(listId, 0, UnityTestToolUtility.DefaultPageSize);
        }

        [EvalFunction("Read a bounded page from an asynchronous test discovery request returned by list.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getList(
            [EvalParameter("Discovery id returned by list.")] string listId,
            [EvalParameter("Zero-based stored-test offset.")] int offset = 0,
            [EvalParameter("Page size; 1..500.")] int limit = 100)
        {
            if (string.IsNullOrWhiteSpace(listId)) throw new InvalidOperationException("Argument 'listId' is required.");
            return UnityTestToolState.instance.GetList(listId, offset, limit);
        }

        [EvalFunction("Start an exclusively owned filtered Unity test run and return its runId. Ambiguous global TestRunner callbacks fail explicitly instead of being attributed to this run.",
            Safety = EvalToolSafety.MutatesEditorState | EvalToolSafety.TriggersReload | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> run(
            [EvalParameter("EditMode or PlayMode.")]
            string mode = "EditMode",
            [EvalParameter("Optional exact assembly name or array; prefix an entry with ! to exclude it.")]
            object? assemblies = null,
            [EvalParameter("Optional exact full test name or array; prefix an entry with ! to exclude it.")]
            object? tests = null,
            [EvalParameter("Optional bounded safe-regex group/full-name filter or array; prefix an entry with ! to exclude it.")]
            object? groups = null,
            [EvalParameter("Optional bounded safe-regex category filter or array; prefix an entry with ! to exclude it.")]
            object? categories = null)
        {
            var filter = UnityTestToolUtility.CreateFilter(mode, assemblies, tests, groups, categories);
            var state = UnityTestToolState.instance;
            var bridge = UnityTestFrameworkBridgeRegistry.Require();
            state.ReconcileActiveRun(bridge);
            state.EnsureCanStartRun();
            var pendingId = state.PrepareRun(filter);
            string runId;
            try
            {
                runId = bridge.Run(filter);
                if (string.IsNullOrWhiteSpace(runId))
                    throw new InvalidOperationException("Unity Test Framework returned an empty test run id.");
                state.BindRunId(pendingId, runId);
            }
            catch (Exception exception)
            {
                state.FailRunStart(pendingId, exception.Message);
                throw;
            }
            return state.GetRun(runId, "summary", 0, UnityTestToolUtility.DefaultPageSize);
        }

        [EvalFunction("Read a bounded page from a test run. detail must be summary, failures, or all.",
            Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> get(
            [EvalParameter("Official run id returned by run.")] string runId,
            [EvalParameter("summary, failures, or all.")] string detail = "summary",
            [EvalParameter("Zero-based stored-result offset.")] int offset = 0,
            [EvalParameter("Page size; 1..500.")] int limit = 100)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new InvalidOperationException("Argument 'runId' is required.");
            var state = UnityTestToolState.instance;
            if (UnityTestFrameworkBridgeRegistry.TryGet(out var bridge)) state.ReconcileActiveRun(bridge);
            return state.GetRun(runId, UnityTestToolUtility.NormalizeDetail(detail), offset, limit);
        }

        [EvalFunction("Request cancellation of a running test run. Completion remains callback-driven.", Safety = EvalToolSafety.MutatesEditorState)]
        public Dictionary<string, object?> cancel(
            [EvalParameter("Official run id returned by run.")] string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new InvalidOperationException("Argument 'runId' is required.");
            var state = UnityTestToolState.instance;
            var bridge = UnityTestFrameworkBridgeRegistry.Require();
            state.ReconcileActiveRun(bridge);
            if (!state.TryGetRun(runId, out var run))
                return EvalData.Obj(("found", false), ("runId", runId), ("cancelAccepted", false));
            if (!run.IsActive)
            {
                var completed = state.GetRun(runId, "summary", 0, UnityTestToolUtility.DefaultPageSize);
                completed["cancelAccepted"] = false;
                return completed;
            }

            var accepted = bridge.Cancel(runId);
            if (accepted) state.MarkCancelRequested(runId);
            else if (!bridge.IsRunActive(runId))
                state.FailRun(runId,
                    "Unity Test Framework no longer has this run registered, so cancellation could not be delivered.",
                    "Lost");
            var result = state.GetRun(runId, "summary", 0, UnityTestToolUtility.DefaultPageSize);
            result["cancelAccepted"] = accepted;
            return result;
        }
    }

    public sealed class UnityTestFilterSpec
    {
        public string Mode { get; set; } = "EditMode";
        public List<string> Assemblies { get; set; } = new();
        public List<string> Tests { get; set; } = new();
        public List<string> Groups { get; set; } = new();
        public List<string> Categories { get; set; } = new();
    }

    public sealed class UnityTestCaseData
    {
        public string Id { get; set; } = string.Empty;
        public string UniqueName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string ParentFullName { get; set; } = string.Empty;
        public string Assembly { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string RunState { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SkipReason { get; set; } = string.Empty;
        public List<string> Categories { get; set; } = new();
        public List<string> Arguments { get; set; } = new();
    }

    public sealed class UnityTestDiscoveryData
    {
        public int TotalAvailable { get; set; }
        public int ScannedTestCount { get; set; }
        public int TotalMatched { get; set; }
        public bool ScanTruncated { get; set; }
        public bool StorageTruncated { get; set; }
        public int StoredCharacterCount { get; set; }
        public int CharacterLimit { get; set; }
        public bool TextTruncated { get; set; }
        public List<UnityTestCaseData> Tests { get; set; } = new();
    }

    public sealed class UnityTestResultData
    {
        public string Name { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string TestStatus { get; set; } = string.Empty;
        public string ResultState { get; set; } = string.Empty;
        public double Duration { get; set; }
        public int AssertCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public int SkipCount { get; set; }
        public int InconclusiveCount { get; set; }
        public string Message { get; set; } = string.Empty;
        public string StackTrace { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
    }

    public sealed class UnityTestRunResultData
    {
        public UnityTestResultData Summary { get; set; } = new();
        public List<UnityTestResultData> Results { get; set; } = new();
        public int TotalResultCount { get; set; }
        public bool ResultsTruncated { get; set; }
        public int StoredCharacterCount { get; set; }
        public int CharacterLimit { get; set; }
        public bool TextTruncated { get; set; }
    }

    public interface IUnityTestRunCallbacks
    {
        void RunStarted(string runId, int testCaseCount);
        void TestStarted(string runId, string fullName);
        void TestFinished(string runId);
        void RunFinished(string runId, UnityTestRunResultData result);
        void RunError(string runId, string message, string status);
    }

    public interface IUnityTestFrameworkBridge
    {
        void SetCallbacks(IUnityTestRunCallbacks callbacks);
        void RetrieveTests(UnityTestFilterSpec filter, int limit, Action<UnityTestDiscoveryData> completed,
            Action<string> failed);
        string Run(UnityTestFilterSpec filter);
        bool Cancel(string runId);
        bool IsRunActive(string runId);
    }

    public static class UnityTestFrameworkBridgeRegistry
    {
        public const string UnavailableMessage =
            "Unity Test Framework support is unavailable. Install com.unity.test-framework 1.4.0 or newer and let Unity recompile the project.";

        private static IUnityTestFrameworkBridge? _bridge;
        private static readonly IUnityTestRunCallbacks Callbacks = new UnityTestRunCallbackReceiver();

        public static void Register(IUnityTestFrameworkBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            bridge.SetCallbacks(Callbacks);
        }

        internal static IUnityTestFrameworkBridge Require() =>
            _bridge ?? throw new InvalidOperationException(UnavailableMessage);

        internal static bool TryGet(out IUnityTestFrameworkBridge bridge)
        {
            bridge = _bridge!;
            return bridge != null;
        }

        private sealed class UnityTestRunCallbackReceiver : IUnityTestRunCallbacks
        {
            public void RunStarted(string runId, int testCaseCount) =>
                UnityTestToolState.instance.MarkRunStarted(runId, testCaseCount);

            public void TestStarted(string runId, string fullName) =>
                UnityTestToolState.instance.MarkTestStarted(runId, fullName);

            public void TestFinished(string runId) => UnityTestToolState.instance.MarkTestFinished(runId);

            public void RunFinished(string runId, UnityTestRunResultData result) =>
                UnityTestToolState.instance.CompleteRun(runId, result);

            public void RunError(string runId, string message, string status) =>
                UnityTestToolState.instance.FailRun(runId, message, status);
        }
    }

    public static class UnityTestToolUtility
    {
        public const int DefaultPageSize = 100;
        public const int MaxPageSize = 500;
        public const int MaxDiscoveryLimit = 5000;
        private const int MaxExactFilterCount = 256;
        private const int MaxExactFilterLength = 1024;
        private const int MaxRegexFilterCount = 32;
        private const int MaxRegexFilterLength = 256;
        private const int MaxRegexQuantifierCount = 4;
        public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

        public static UnityTestFilterSpec CreateFilter(
            string mode,
            object? assemblies,
            object? tests,
            object? groups,
            object? categories)
        {
            var filter = new UnityTestFilterSpec
            {
                Mode = NormalizeMode(mode),
                Assemblies = ToStrings(assemblies, "assemblies", MaxExactFilterCount, MaxExactFilterLength),
                Tests = ToStrings(tests, "tests", MaxExactFilterCount, MaxExactFilterLength),
                Groups = ToStrings(groups, "groups", MaxRegexFilterCount, MaxRegexFilterLength),
                Categories = ToStrings(categories, "categories", MaxRegexFilterCount, MaxRegexFilterLength)
            };
            ValidateRegexFilters(filter.Groups, "groups");
            ValidateRegexFilters(filter.Categories, "categories");
            return filter;
        }

        public static string NormalizeDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail) || detail.Equals("summary", StringComparison.OrdinalIgnoreCase))
                return "summary";
            if (detail.Equals("failures", StringComparison.OrdinalIgnoreCase)) return "failures";
            if (detail.Equals("all", StringComparison.OrdinalIgnoreCase)) return "all";
            throw new InvalidOperationException("Argument 'detail' must be summary, failures, or all.");
        }

        public static void ValidateDiscoveryLimit(int limit)
        {
            if (limit < 1 || limit > MaxDiscoveryLimit)
                throw new InvalidOperationException(
                    $"Argument 'limit' must be between 1 and {MaxDiscoveryLimit}.");
        }

        public static (int Offset, int Limit) NormalizePage(int offset, int limit)
        {
            if (offset < 0)
                throw new InvalidOperationException("Argument 'offset' cannot be negative.");
            if (limit < 1 || limit > MaxPageSize)
                throw new InvalidOperationException(
                    $"Argument 'limit' must be between 1 and {MaxPageSize}.");
            return (offset, limit);
        }

        private static string NormalizeMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode) || mode.Equals("EditMode", StringComparison.OrdinalIgnoreCase))
                return "EditMode";
            if (mode.Equals("PlayMode", StringComparison.OrdinalIgnoreCase)) return "PlayMode";
            throw new InvalidOperationException("Argument 'mode' must be EditMode or PlayMode.");
        }

        private static List<string> ToStrings(object? value, string argumentName, int maxCount, int maxLength)
        {
            if (value == null) return new List<string>();
            IEnumerable values = value is string ? new[] { value } : value as IEnumerable ?? new[] { value };
            var result = new List<string>();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in values)
            {
                var text = Convert.ToString(item)?.Trim();
                if (string.IsNullOrWhiteSpace(text) || !distinct.Add(text!)) continue;
                if (text!.Length > maxLength)
                    throw new InvalidOperationException(
                        $"Argument '{argumentName}' entries cannot exceed {maxLength} characters.");
                result.Add(text);
                if (result.Count > maxCount)
                    throw new InvalidOperationException(
                        $"Argument '{argumentName}' cannot contain more than {maxCount} distinct entries.");
            }
            return result;
        }

        private static void ValidateRegexFilters(IEnumerable<string> values, string argumentName)
        {
            foreach (var value in values)
            {
                var expression = value.StartsWith("!", StringComparison.Ordinal) ? value.Substring(1) : value;
                if (expression.Length == 0)
                    throw new InvalidOperationException($"Argument '{argumentName}' contains an empty exclusion filter.");
                try
                {
                    _ = new Regex(expression, RegexOptions.CultureInvariant, RegexTimeout);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidOperationException($"Argument '{argumentName}' contains invalid regex '{value}': {exception.Message}", exception);
                }
                ValidateSafeRegexSyntax(expression, value, argumentName);
            }
        }

        private static void ValidateSafeRegexSyntax(string expression, string original, string argumentName)
        {
            var inCharacterClass = false;
            var escaped = false;
            var quantifierCount = 0;
            for (var index = 0; index < expression.Length; index++)
            {
                var character = expression[index];
                if (escaped)
                {
                    if (!inCharacterClass && (char.IsDigit(character) || character == 'k'))
                        throw UnsafeRegex(argumentName, original,
                            "backreferences are not supported");
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '[' && !inCharacterClass)
                {
                    inCharacterClass = true;
                    continue;
                }
                if (character == ']' && inCharacterClass)
                {
                    inCharacterClass = false;
                    continue;
                }
                if (inCharacterClass) continue;
                if (character == '(' || character == ')' || character == '{' || character == '}')
                    throw UnsafeRegex(argumentName, original,
                        "groups, lookarounds, and counted repetitions are not supported");
                if (character == '*' || character == '+' || character == '?')
                {
                    quantifierCount++;
                    if (index > 0 && expression[index - 1] == '.')
                        throw UnsafeRegex(argumentName, original,
                            "unbounded wildcard repetition (.* or .+) is not supported; use a bounded character class");
                    if (index == 0 || expression[index - 1] == '^' || expression[index - 1] == '|' ||
                        expression[index - 1] == '*' || expression[index - 1] == '+' || expression[index - 1] == '?')
                        throw UnsafeRegex(argumentName, original, "a quantifier has no safe preceding atom");
                }
            }

            if (escaped || inCharacterClass)
                return; // Regex construction below reports the precise syntax error.
            if (quantifierCount > MaxRegexQuantifierCount)
                throw UnsafeRegex(argumentName, original,
                    $"at most {MaxRegexQuantifierCount} repetition operators are allowed");
        }

        private static InvalidOperationException UnsafeRegex(string argumentName, string original, string reason) =>
            new($"Argument '{argumentName}' contains unsafe regex '{original}': {reason}. " +
                "Use a bounded expression made from literals, anchors, character classes, alternation, and simple *, +, or ? repetition.");
    }

    [FilePath("Library/UnityEvalToolTestToolState.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class UnityTestToolState : ScriptableSingleton<UnityTestToolState>
    {
        private const int RetainedRunLimit = 32;
        private const int RetainedListLimit = 16;
        private const int MaxStateDiagnosticLength = 32768;

        [SerializeField] private List<UnityTestRunRecord> runs = new();
        [SerializeField] private List<UnityTestListRecord> lists = new();

        public void EnsureCanStartRun()
        {
            var active = runs.FirstOrDefault(run => run.IsActive);
            if (active != null)
                throw new InvalidOperationException($"Test run '{active.RunId}' is still {active.Status}. Wait for its callback-driven completion or cancel it before starting another run.");
        }

        public void ReconcileActiveRun(IUnityTestFrameworkBridge bridge)
        {
            if (bridge == null) throw new ArgumentNullException(nameof(bridge));
            var active = GetActiveRun();
            if (active == null) return;
            if (active.RunId.StartsWith("pending-", StringComparison.Ordinal))
            {
                active.Status = "Interrupted";
                active.Error = "The Editor reloaded before Unity Test Framework returned and bound an official run id. " +
                               "The pending record was terminated instead of guessing ownership of a global callback.";
                active.CurrentTest = string.Empty;
                active.FinishedAtUtc = DateTime.UtcNow.ToString("O");
                Save(true);
                return;
            }
            bool frameworkRunIsActive;
            try
            {
                frameworkRunIsActive = bridge.IsRunActive(active.RunId);
            }
            catch (Exception exception)
            {
                active.Status = "OwnershipCheckFailed";
                active.Error = LimitDiagnostic(
                    "Unity Test Framework run ownership could not be verified, so the persisted run was terminated: " +
                    exception.Message);
                active.CurrentTest = string.Empty;
                active.FinishedAtUtc = DateTime.UtcNow.ToString("O");
                Save(true);
                throw new InvalidOperationException(active.Error, exception);
            }
            if (frameworkRunIsActive) return;
            active.Status = "Lost";
            active.Error = "Unity Test Framework no longer has this persisted run registered. " +
                           "It may have been interrupted by an Editor restart or an unrecoverable framework cleanup.";
            active.CurrentTest = string.Empty;
            active.FinishedAtUtc = DateTime.UtcNow.ToString("O");
            Save(true);
        }

        public string PrepareRun(UnityTestFilterSpec filter)
        {
            TrimRunsForNewRecord();
            var pendingId = "pending-" + Guid.NewGuid().ToString("N");
            runs.Add(UnityTestRunRecord.Create(pendingId, filter));
            Save(true);
            return pendingId;
        }

        public void BindRunId(string pendingId, string runId)
        {
            var run = runs.FirstOrDefault(item => string.Equals(item.RunId, pendingId, StringComparison.Ordinal));
            if (run == null)
            {
                if (runs.Any(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))) return;
                throw new InvalidOperationException($"Pending test run '{pendingId}' is not tracked.");
            }
            if (runs.Any(item => !ReferenceEquals(item, run) && string.Equals(item.RunId, runId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Test run '{runId}' is already tracked.");
            run.RunId = runId;
            if (run.Status == "Starting") run.Status = "Queued";
            Save(true);
        }

        public void BindRunIdFromCallback(string runId)
        {
            var pending = runs.FirstOrDefault(item => item.IsActive &&
                                                      item.RunId.StartsWith("pending-", StringComparison.Ordinal));
            if (pending == null || runs.Any(item => !ReferenceEquals(item, pending) &&
                                                    string.Equals(item.RunId, runId, StringComparison.Ordinal)))
                return;
            pending.RunId = runId;
            if (pending.Status == "Starting") pending.Status = "Queued";
            Save(true);
        }

        public void FailRunStart(string pendingId, string error)
        {
            var run = runs.FirstOrDefault(item => string.Equals(item.RunId, pendingId, StringComparison.Ordinal));
            if (run == null) run = runs.FirstOrDefault(item => item.IsActive);
            if (run == null || !run.IsActive) return;
            run.Status = "StartFailed";
            run.Error = LimitDiagnostic(error);
            run.FinishedAtUtc = DateTime.UtcNow.ToString("O");
            Save(true);
        }

        internal bool TryGetRun(string runId, out UnityTestRunRecord run)
        {
            run = runs.FirstOrDefault(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))!;
            return run != null;
        }

        public Dictionary<string, object?> GetRun(string runId, string detail, int offset, int limit)
        {
            var page = UnityTestToolUtility.NormalizePage(offset, limit);
            return TryGetRun(runId, out var run)
                ? run.ToObject(detail, page.Offset, page.Limit)
                : EvalData.Obj(("found", false), ("runId", runId));
        }

        public string? GetActiveRunId()
        {
            var run = GetActiveRun();
            return run != null && !run.RunId.StartsWith("pending-", StringComparison.Ordinal)
                ? run.RunId
                : null;
        }

        public void MarkRunStarted(string runId, int testCaseCount)
        {
            var run = GetActiveRun(runId);
            if (run == null) return;
            run.Status = "Running";
            run.LoadedTestCount = testCaseCount;
            Save(true);
        }

        public void MarkTestStarted(string runId, string fullName)
        {
            var run = GetActiveRun(runId);
            if (run == null) return;
            run.CurrentTest = LimitDiagnostic(fullName);
        }

        public void MarkTestFinished(string runId)
        {
            var run = GetActiveRun(runId);
            if (run == null) return;
            run.CompletedTestCount++;
            run.CurrentTest = string.Empty;
        }

        public void CompleteRun(string runId, UnityTestRunResultData result)
        {
            var run = GetActiveRun(runId);
            if (run == null) return;
            run.Complete(result);
            Save(true);
        }

        public void FailRun(string runId, string error, string status)
        {
            var run = GetActiveRun(runId);
            if (run == null) return;
            run.Status = string.IsNullOrWhiteSpace(status) ? "Error" : status;
            run.Error = LimitDiagnostic(error);
            run.CurrentTest = string.Empty;
            run.FinishedAtUtc = DateTime.UtcNow.ToString("O");
            Save(true);
        }

        public void MarkCancelRequested(string runId)
        {
            if (!TryGetRun(runId, out var run) || !run.IsActive) return;
            run.Status = "CancelRequested";
            Save(true);
        }

        public string BeginList(UnityTestFilterSpec filter, int limit)
        {
            TrimListsForNewRecord();
            var id = Guid.NewGuid().ToString("N");
            lists.Add(UnityTestListRecord.Create(id, filter, limit));
            Save(true);
            return id;
        }

        public Dictionary<string, object?> GetList(string listId, int offset, int limit)
        {
            var page = UnityTestToolUtility.NormalizePage(offset, limit);
            var record = lists.FirstOrDefault(item => string.Equals(item.ListId, listId, StringComparison.Ordinal));
            return record != null
                ? record.ToObject(page.Offset, page.Limit)
                : EvalData.Obj(("found", false), ("listId", listId));
        }

        public void CompleteList(string listId, UnityTestDiscoveryData result)
        {
            var record = lists.FirstOrDefault(item => string.Equals(item.ListId, listId, StringComparison.Ordinal));
            if (record == null || record.Status != "Running") return;
            record.Complete(result);
            Save(true);
        }

        public void FailList(string listId, string error)
        {
            var record = lists.FirstOrDefault(item => string.Equals(item.ListId, listId, StringComparison.Ordinal));
            if (record == null || record.Status != "Running") return;
            record.Fail(LimitDiagnostic(error));
            Save(true);
        }

        public void InterruptRunningLists(string reason)
        {
            var changed = false;
            foreach (var record in lists.Where(item => item.Status == "Running"))
            {
                record.Interrupt(LimitDiagnostic(reason));
                changed = true;
            }
            if (changed) Save(true);
        }

        private UnityTestRunRecord? GetActiveRun() => runs.FirstOrDefault(run => run.IsActive);

        private UnityTestRunRecord? GetActiveRun(string runId) =>
            runs.FirstOrDefault(run => run.IsActive && string.Equals(run.RunId, runId, StringComparison.Ordinal));

        private static string LimitDiagnostic(string? value)
        {
            value ??= string.Empty;
            return value.Length <= MaxStateDiagnosticLength
                ? value
                : value.Substring(0, MaxStateDiagnosticLength - 12) + "<truncated>";
        }

        private void TrimRunsForNewRecord()
        {
            while (runs.Count >= RetainedRunLimit)
            {
                var removable = runs.FirstOrDefault(run => !run.IsActive);
                if (removable == null)
                    throw new InvalidOperationException($"The retained test run limit ({RetainedRunLimit}) is full of active records.");
                runs.Remove(removable);
            }
        }

        private void TrimListsForNewRecord()
        {
            while (lists.Count >= RetainedListLimit)
            {
                var removable = lists.FirstOrDefault(list => list.Status != "Running");
                if (removable == null)
                    throw new InvalidOperationException($"The retained discovery limit ({RetainedListLimit}) is full of pending requests.");
                lists.Remove(removable);
            }
        }
    }

    [Serializable]
    internal sealed class UnityTestRunRecord
    {
        public string RunId = string.Empty;
        public string Mode = string.Empty;
        public string Status = string.Empty;
        public string StartedAtUtc = string.Empty;
        public string FinishedAtUtc = string.Empty;
        public string CurrentTest = string.Empty;
        public string Error = string.Empty;
        public int LoadedTestCount;
        public int SelectedTestCount;
        public int CompletedTestCount;
        public int TotalResultCount;
        public bool ResultsTruncated;
        public int StoredResultCharacterCount;
        public int ResultCharacterLimit;
        public bool ResultTextTruncated;
        public List<string> Assemblies = new();
        public List<string> Tests = new();
        public List<string> Groups = new();
        public List<string> Categories = new();
        public UnityTestResultRecord? Summary;
        public List<UnityTestResultRecord> Results = new();

        public bool IsActive => Status == "Starting" || Status == "Queued" || Status == "Running" || Status == "CancelRequested";

        public static UnityTestRunRecord Create(string runId, UnityTestFilterSpec filter) => new()
        {
            RunId = runId,
            Mode = filter.Mode,
            Status = "Starting",
            StartedAtUtc = DateTime.UtcNow.ToString("O"),
            Assemblies = new List<string>(filter.Assemblies),
            Tests = new List<string>(filter.Tests),
            Groups = new List<string>(filter.Groups),
            Categories = new List<string>(filter.Categories)
        };

        public void Complete(UnityTestRunResultData result)
        {
            Summary = UnityTestResultRecord.Create(result.Summary);
            Results = result.Results.Select(UnityTestResultRecord.Create).ToList();
            TotalResultCount = Math.Max(result.TotalResultCount, Results.Count);
            ResultsTruncated = result.ResultsTruncated || TotalResultCount > Results.Count;
            StoredResultCharacterCount = result.StoredCharacterCount;
            ResultCharacterLimit = result.CharacterLimit;
            ResultTextTruncated = result.TextTruncated;
            CompletedTestCount = Math.Max(CompletedTestCount, Results.Count);
            SelectedTestCount = Results.Count;
            CurrentTest = string.Empty;
            FinishedAtUtc = DateTime.UtcNow.ToString("O");
            Status = Summary.ResultState.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Canceled"
                : string.IsNullOrWhiteSpace(Summary.TestStatus) ? "Finished" : Summary.TestStatus;
        }

        public Dictionary<string, object?> ToObject(string detail, int offset, int limit)
        {
            var selectedResults = detail == "all"
                ? Results
                : detail == "failures"
                    ? Results.Where(result => result.IsFailure).ToList()
                    : new List<UnityTestResultRecord>();
            offset = Math.Min(offset, selectedResults.Count);
            var page = selectedResults.Skip(offset).Take(limit).ToList();
            var nextOffset = offset + page.Count;
            var effectiveTotalResultCount = Math.Max(TotalResultCount, Results.Count);
            return EvalData.Obj(
                ("found", true),
                ("runId", RunId),
                ("mode", Mode),
                ("status", Status),
                ("isCompleted", !IsActive),
                ("startedAtUtc", StartedAtUtc),
                ("finishedAtUtc", FinishedAtUtc),
                ("loadedTestCount", LoadedTestCount),
                ("selectedTestCount", SelectedTestCount),
                ("completedTestCount", CompletedTestCount),
                ("currentTest", CurrentTest),
                ("error", Error),
                ("filters", UnityTestRecordUtility.FilterObject(Assemblies, Tests, Groups, Categories)),
                ("summary", Summary?.ToObject()),
                ("detail", detail),
                ("storedResultCount", Results.Count),
                ("totalResultCount", effectiveTotalResultCount),
                ("resultsTruncated", ResultsTruncated || effectiveTotalResultCount > Results.Count),
                ("storedResultCharacterCount", StoredResultCharacterCount),
                ("resultCharacterLimit", ResultCharacterLimit),
                ("resultTextTruncated", ResultTextTruncated),
                ("matchedResultCount", selectedResults.Count),
                ("offset", offset),
                ("resultCount", page.Count),
                ("nextOffset", nextOffset),
                ("hasMore", nextOffset < selectedResults.Count),
                ("results", page.Select(result => (object?)result.ToObject()).ToList()));
        }
    }

    [Serializable]
    internal sealed class UnityTestResultRecord
    {
        public string Name = string.Empty;
        public string FullName = string.Empty;
        public string TestStatus = string.Empty;
        public string ResultState = string.Empty;
        public double Duration;
        public int AssertCount;
        public int PassCount;
        public int FailCount;
        public int SkipCount;
        public int InconclusiveCount;
        public string Message = string.Empty;
        public string StackTrace = string.Empty;
        public string Output = string.Empty;

        public bool IsFailure =>
            string.Equals(TestStatus, "Failed", StringComparison.OrdinalIgnoreCase) ||
            ResultState.StartsWith("Failed", StringComparison.OrdinalIgnoreCase);

        public static UnityTestResultRecord Create(UnityTestResultData result) => new()
        {
            Name = result.Name,
            FullName = result.FullName,
            TestStatus = result.TestStatus,
            ResultState = result.ResultState,
            Duration = result.Duration,
            AssertCount = result.AssertCount,
            PassCount = result.PassCount,
            FailCount = result.FailCount,
            SkipCount = result.SkipCount,
            InconclusiveCount = result.InconclusiveCount,
            Message = result.Message,
            StackTrace = result.StackTrace,
            Output = result.Output
        };

        public Dictionary<string, object?> ToObject() => EvalData.Obj(
            ("name", Name),
            ("fullName", FullName),
            ("testStatus", TestStatus),
            ("resultState", ResultState),
            ("duration", Duration),
            ("assertCount", AssertCount),
            ("passCount", PassCount),
            ("failCount", FailCount),
            ("skipCount", SkipCount),
            ("inconclusiveCount", InconclusiveCount),
            ("message", Message),
            ("stackTrace", StackTrace),
            ("output", Output));
    }

    [Serializable]
    internal sealed class UnityTestListRecord
    {
        public string ListId = string.Empty;
        public string Mode = string.Empty;
        public string Status = string.Empty;
        public string StartedAtUtc = string.Empty;
        public string FinishedAtUtc = string.Empty;
        public string Error = string.Empty;
        public int Limit;
        public int TotalAvailable;
        public int ScannedTestCount;
        public int TotalMatched;
        public bool ScanTruncated;
        public bool StorageTruncated;
        public int StoredCharacterCount;
        public int CharacterLimit;
        public bool TextTruncated;
        public List<string> Assemblies = new();
        public List<string> TestsFilter = new();
        public List<string> Groups = new();
        public List<string> Categories = new();
        public List<UnityTestCaseRecord> Tests = new();

        public static UnityTestListRecord Create(string id, UnityTestFilterSpec filter, int limit) => new()
        {
            ListId = id,
            Mode = filter.Mode,
            Status = "Running",
            StartedAtUtc = DateTime.UtcNow.ToString("O"),
            Limit = limit,
            Assemblies = new List<string>(filter.Assemblies),
            TestsFilter = new List<string>(filter.Tests),
            Groups = new List<string>(filter.Groups),
            Categories = new List<string>(filter.Categories)
        };

        public void Complete(UnityTestDiscoveryData result)
        {
            Status = "Completed";
            FinishedAtUtc = DateTime.UtcNow.ToString("O");
            TotalAvailable = result.TotalAvailable;
            ScannedTestCount = result.ScannedTestCount;
            TotalMatched = result.TotalMatched;
            ScanTruncated = result.ScanTruncated;
            StorageTruncated = result.StorageTruncated;
            StoredCharacterCount = result.StoredCharacterCount;
            CharacterLimit = result.CharacterLimit;
            TextTruncated = result.TextTruncated;
            Tests = result.Tests.Select(UnityTestCaseRecord.Create).ToList();
        }

        public void Fail(string error)
        {
            Status = "Failed";
            FinishedAtUtc = DateTime.UtcNow.ToString("O");
            Error = error ?? string.Empty;
        }

        public void Interrupt(string reason)
        {
            Status = "Interrupted";
            FinishedAtUtc = DateTime.UtcNow.ToString("O");
            Error = reason ?? string.Empty;
        }

        public Dictionary<string, object?> ToObject(int offset, int limit)
        {
            offset = Math.Min(offset, Tests.Count);
            var page = Tests.Skip(offset).Take(limit).ToList();
            var nextOffset = offset + page.Count;
            return EvalData.Obj(
                ("found", true),
                ("listId", ListId),
                ("mode", Mode),
                ("status", Status),
                ("isCompleted", Status != "Running"),
                ("startedAtUtc", StartedAtUtc),
                ("finishedAtUtc", FinishedAtUtc),
                ("error", Error),
                ("limit", Limit),
                ("totalAvailable", TotalAvailable),
                ("scannedTestCount", ScannedTestCount),
                ("totalMatched", TotalMatched),
                ("storedCount", Tests.Count),
                ("scanTruncated", ScanTruncated),
                ("storageTruncated", StorageTruncated),
                ("storedCharacterCount", StoredCharacterCount),
                ("characterLimit", CharacterLimit),
                ("textTruncated", TextTruncated),
                ("truncated", ScanTruncated || StorageTruncated || TextTruncated || TotalMatched > Tests.Count),
                ("filters", UnityTestRecordUtility.FilterObject(Assemblies, TestsFilter, Groups, Categories)),
                ("offset", offset),
                ("count", page.Count),
                ("nextOffset", nextOffset),
                ("hasMore", nextOffset < Tests.Count),
                ("tests", page.Select(test => (object?)test.ToObject()).ToList()));
        }
    }

    [Serializable]
    internal sealed class UnityTestCaseRecord
    {
        public string Id = string.Empty;
        public string UniqueName = string.Empty;
        public string Name = string.Empty;
        public string FullName = string.Empty;
        public string ParentFullName = string.Empty;
        public string Assembly = string.Empty;
        public string Mode = string.Empty;
        public string RunState = string.Empty;
        public string Description = string.Empty;
        public string SkipReason = string.Empty;
        public List<string> Categories = new();
        public List<string> Arguments = new();

        public static UnityTestCaseRecord Create(UnityTestCaseData test) => new()
        {
            Id = test.Id,
            UniqueName = test.UniqueName,
            Name = test.Name,
            FullName = test.FullName,
            ParentFullName = test.ParentFullName,
            Assembly = test.Assembly,
            Mode = test.Mode,
            RunState = test.RunState,
            Description = test.Description,
            SkipReason = test.SkipReason,
            Categories = new List<string>(test.Categories),
            Arguments = new List<string>(test.Arguments)
        };

        public Dictionary<string, object?> ToObject() => EvalData.Obj(
            ("id", Id),
            ("uniqueName", UniqueName),
            ("name", Name),
            ("fullName", FullName),
            ("parentFullName", ParentFullName),
            ("assembly", Assembly),
            ("mode", Mode),
            ("runState", RunState),
            ("description", Description),
            ("skipReason", SkipReason),
            ("categories", Categories.Cast<object?>().ToList()),
            ("arguments", Arguments.Cast<object?>().ToList()));
    }

    internal static class UnityTestRecordUtility
    {
        public static Dictionary<string, object?> FilterObject(
            IEnumerable<string> assemblies,
            IEnumerable<string> tests,
            IEnumerable<string> groups,
            IEnumerable<string> categories) => EvalData.Obj(
            ("assemblies", assemblies.Cast<object?>().ToList()),
            ("tests", tests.Cast<object?>().ToList()),
            ("groups", groups.Cast<object?>().ToList()),
            ("categories", categories.Cast<object?>().ToList()));
    }
}
