#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit
{
    public enum EvalExecutionStatus
    {
        Idle,
        Running,
        Succeeded,
        Failed
    }

    public sealed class EvalSessionSnapshot
    {
        public string Id { get; internal set; } = string.Empty;

        public string ProtocolVersion { get; internal set; } = string.Empty;

        public string ClientName { get; internal set; } = string.Empty;

        public DateTime CreatedAtUtc { get; internal set; }

        public DateTime LastSeenUtc { get; internal set; }

        public bool HasEvalSession { get; internal set; }

        public EvalExecutionStatus EvalStatus { get; internal set; }

        public string CurrentRequestId { get; internal set; } = string.Empty;

        public string CurrentCodeSummary { get; internal set; } = string.Empty;

        public DateTime CurrentEvalStartedAtUtc { get; internal set; }

        public double CurrentEvalElapsedMs { get; internal set; }

        public int CurrentTimeoutSeconds { get; internal set; }

        public bool CurrentResetSession { get; internal set; }

        public bool HasLastEval { get; internal set; }

        public string LastRequestId { get; internal set; } = string.Empty;

        public EvalExecutionStatus LastEvalStatus { get; internal set; }

        public DateTime LastEvalStartedAtUtc { get; internal set; }

        public DateTime LastEvalFinishedAtUtc { get; internal set; }

        public double LastEvalDurationMs { get; internal set; }

        public string LastEvalError { get; internal set; } = string.Empty;

        public int TotalEvalCount { get; internal set; }

        public int FailedEvalCount { get; internal set; }

        public int ActiveToolFunctionCount { get; internal set; }

        public string CurrentToolFunctionName { get; internal set; } = string.Empty;

        public DateTime CurrentToolFunctionStartedAtUtc { get; internal set; }

        public double CurrentToolFunctionElapsedMs { get; internal set; }

        public bool HasLastToolFunction { get; internal set; }

        public string LastToolFunctionName { get; internal set; } = string.Empty;

        public bool LastToolFunctionSucceeded { get; internal set; }

        public DateTime LastToolFunctionFinishedAtUtc { get; internal set; }

        public double LastToolFunctionDurationMs { get; internal set; }

        public string LastToolFunctionError { get; internal set; } = string.Empty;

        public int TotalToolFunctionCount { get; internal set; }

        public int FailedToolFunctionCount { get; internal set; }
    }

    public sealed class EvalSession : IDisposable
    {
        private const int CodeSummaryMaxLength = 180;
        private readonly object _syncRoot = new();
        private readonly SemaphoreSlim _evalGate = new(1, 1);
        private DateTime _lastSeenUtc;
        private EvalExecutionStatus _evalStatus = EvalExecutionStatus.Idle;
        private string _currentRequestId = string.Empty;
        private string _currentCodeSummary = string.Empty;
        private DateTime _currentEvalStartedAtUtc;
        private int _currentTimeoutSeconds;
        private bool _currentResetSession;
        private bool _hasLastEval;
        private string _lastRequestId = string.Empty;
        private EvalExecutionStatus _lastEvalStatus = EvalExecutionStatus.Idle;
        private DateTime _lastEvalStartedAtUtc;
        private DateTime _lastEvalFinishedAtUtc;
        private double _lastEvalDurationMs;
        private string _lastEvalError = string.Empty;
        private int _totalEvalCount;
        private int _failedEvalCount;
        private int _activeToolFunctionCount;
        private string _currentToolFunctionName = string.Empty;
        private DateTime _currentToolFunctionStartedAtUtc;
        private bool _hasLastToolFunction;
        private string _lastToolFunctionName = string.Empty;
        private bool _lastToolFunctionSucceeded;
        private DateTime _lastToolFunctionFinishedAtUtc;
        private double _lastToolFunctionDurationMs;
        private string _lastToolFunctionError = string.Empty;
        private int _totalToolFunctionCount;
        private int _failedToolFunctionCount;
        private bool _disposeWhenEvalCompletes;
        private bool _isClosing;
        private bool _evalTurnActive;

        public EvalSession(string id, string protocolVersion, string clientName)
        {
            Id = id;
            ProtocolVersion = protocolVersion;
            ClientName = clientName;
            CreatedAtUtc = DateTime.UtcNow;
            _lastSeenUtc = CreatedAtUtc;
        }

        public string Id { get; }

        public string ProtocolVersion { get; }

        public string ClientName { get; }

        public DateTime CreatedAtUtc { get; }

        public DateTime LastSeenUtc
        {
            get
            {
                lock (_syncRoot) return _lastSeenUtc;
            }
        }

        public bool IsEvalRunning
        {
            get
            {
                lock (_syncRoot) return _evalStatus == EvalExecutionStatus.Running;
            }
        }

        public EvalVmSession? VmSession { get; set; }

        public async Task<bool> TryEnterEvalTurnAsync(CancellationToken cancellationToken)
        {
            lock (_syncRoot)
            {
                if (_isClosing) return false;
            }

            await _evalGate.WaitAsync(cancellationToken);
            lock (_syncRoot)
            {
                if (!_isClosing)
                {
                    _evalTurnActive = true;
                    return true;
                }
            }

            _evalGate.Release();
            return false;
        }

        public void ReleaseEvalTurn()
        {
            var shouldDispose = false;
            lock (_syncRoot)
            {
                _evalTurnActive = false;
                if (_disposeWhenEvalCompletes && _evalStatus != EvalExecutionStatus.Running)
                {
                    _disposeWhenEvalCompletes = false;
                    shouldDispose = true;
                }
            }

            if (shouldDispose)
            {
                VmSession?.Dispose();
                VmSession = null;
            }

            _evalGate.Release();
        }

        public void Touch()
        {
            lock (_syncRoot)
                _lastSeenUtc = DateTime.UtcNow;
        }

        public void BeginEval(string requestId, string code, int timeoutSeconds, bool resetSession)
        {
            lock (_syncRoot)
            {
                _evalStatus = EvalExecutionStatus.Running;
                _currentRequestId = requestId;
                _currentCodeSummary = SummarizeCode(code);
                _currentEvalStartedAtUtc = DateTime.UtcNow;
                _currentTimeoutSeconds = timeoutSeconds;
                _currentResetSession = resetSession;
            }
        }

        public void CompleteEval(string requestId, bool success, string error)
        {
            lock (_syncRoot)
            {
                var now = DateTime.UtcNow;
                var startedAt = _currentEvalStartedAtUtc == default ? now : _currentEvalStartedAtUtc;
                _hasLastEval = true;
                _lastRequestId = string.IsNullOrWhiteSpace(requestId) ? _currentRequestId : requestId;
                _lastEvalStatus = success ? EvalExecutionStatus.Succeeded : EvalExecutionStatus.Failed;
                _lastEvalStartedAtUtc = startedAt;
                _lastEvalFinishedAtUtc = now;
                _lastEvalDurationMs = Math.Max(0, (now - startedAt).TotalMilliseconds);
                _lastEvalError = success ? string.Empty : error;
                _totalEvalCount++;
                if (!success) _failedEvalCount++;

                _evalStatus = EvalExecutionStatus.Idle;
                _currentRequestId = string.Empty;
                _currentCodeSummary = string.Empty;
                _currentEvalStartedAtUtc = default;
                _currentTimeoutSeconds = 0;
                _currentResetSession = false;
            }

            DisposePendingEvalSession();
        }

        public void BeginToolFunction(string toolFunctionName)
        {
            lock (_syncRoot)
            {
                _activeToolFunctionCount++;
                _currentToolFunctionName = toolFunctionName;
                _currentToolFunctionStartedAtUtc = DateTime.UtcNow;
            }
        }

        public void CompleteToolFunction(string toolFunctionName, bool success, string error)
        {
            lock (_syncRoot)
            {
                var now = DateTime.UtcNow;
                var startedAt = _currentToolFunctionStartedAtUtc == default ? now : _currentToolFunctionStartedAtUtc;
                _hasLastToolFunction = true;
                _lastToolFunctionName = toolFunctionName;
                _lastToolFunctionSucceeded = success;
                _lastToolFunctionFinishedAtUtc = now;
                _lastToolFunctionDurationMs = Math.Max(0, (now - startedAt).TotalMilliseconds);
                _lastToolFunctionError = success ? string.Empty : error;
                _totalToolFunctionCount++;
                if (!success) _failedToolFunctionCount++;

                _activeToolFunctionCount = Math.Max(0, _activeToolFunctionCount - 1);
                if (_activeToolFunctionCount == 0)
                {
                    _currentToolFunctionName = string.Empty;
                    _currentToolFunctionStartedAtUtc = default;
                }
            }
        }

        public EvalSessionSnapshot ToSnapshot()
        {
            lock (_syncRoot)
            {
                var now = DateTime.UtcNow;
                return new EvalSessionSnapshot
                {
                    Id = Id,
                    ProtocolVersion = ProtocolVersion,
                    ClientName = ClientName,
                    CreatedAtUtc = CreatedAtUtc,
                    LastSeenUtc = _lastSeenUtc,
                    HasEvalSession = VmSession != null,
                    EvalStatus = _evalStatus,
                    CurrentRequestId = _currentRequestId,
                    CurrentCodeSummary = _currentCodeSummary,
                    CurrentEvalStartedAtUtc = _currentEvalStartedAtUtc,
                    CurrentEvalElapsedMs = _currentEvalStartedAtUtc == default ? 0 : Math.Max(0, (now - _currentEvalStartedAtUtc).TotalMilliseconds),
                    CurrentTimeoutSeconds = _currentTimeoutSeconds,
                    CurrentResetSession = _currentResetSession,
                    HasLastEval = _hasLastEval,
                    LastRequestId = _lastRequestId,
                    LastEvalStatus = _lastEvalStatus,
                    LastEvalStartedAtUtc = _lastEvalStartedAtUtc,
                    LastEvalFinishedAtUtc = _lastEvalFinishedAtUtc,
                    LastEvalDurationMs = _lastEvalDurationMs,
                    LastEvalError = _lastEvalError,
                    TotalEvalCount = _totalEvalCount,
                    FailedEvalCount = _failedEvalCount,
                    ActiveToolFunctionCount = _activeToolFunctionCount,
                    CurrentToolFunctionName = _currentToolFunctionName,
                    CurrentToolFunctionStartedAtUtc = _currentToolFunctionStartedAtUtc,
                    CurrentToolFunctionElapsedMs = _currentToolFunctionStartedAtUtc == default ? 0 : Math.Max(0, (now - _currentToolFunctionStartedAtUtc).TotalMilliseconds),
                    HasLastToolFunction = _hasLastToolFunction,
                    LastToolFunctionName = _lastToolFunctionName,
                    LastToolFunctionSucceeded = _lastToolFunctionSucceeded,
                    LastToolFunctionFinishedAtUtc = _lastToolFunctionFinishedAtUtc,
                    LastToolFunctionDurationMs = _lastToolFunctionDurationMs,
                    LastToolFunctionError = _lastToolFunctionError,
                    TotalToolFunctionCount = _totalToolFunctionCount,
                    FailedToolFunctionCount = _failedToolFunctionCount,
                };
            }
        }

        public void Dispose()
        {
            if (IsEvalRunning || IsEvalTurnActive)
            {
                lock (_syncRoot)
                {
                    _isClosing = true;
                    _disposeWhenEvalCompletes = true;
                }
                return;
            }

            lock (_syncRoot)
                _isClosing = true;

            VmSession?.Dispose();
            VmSession = null;
        }

        private bool IsEvalTurnActive
        {
            get
            {
                lock (_syncRoot) return _evalTurnActive;
            }
        }

        private void DisposePendingEvalSession()
        {
            var shouldDispose = false;
            lock (_syncRoot)
            {
                if (_disposeWhenEvalCompletes && _evalStatus != EvalExecutionStatus.Running)
                {
                    _disposeWhenEvalCompletes = false;
                    shouldDispose = true;
                }
            }

            if (!shouldDispose) return;
            VmSession?.Dispose();
            VmSession = null;
        }

        private static string SummarizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "(empty)";
            var summary = code.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (summary.Contains("  "))
                summary = summary.Replace("  ", " ");
            return summary.Length <= CodeSummaryMaxLength ? summary : summary[..CodeSummaryMaxLength] + "...";
        }
    }
}
