#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using YuzeToolkit.Eval;

namespace YuzeToolkit.Agent
{
    internal sealed class RuntimeCliRunner : IDisposable
    {
        private readonly EvalCliCommandService _cliService = new(new EvalExecutor(new EvalOptions()));
        private readonly string _sessionId;
        private EvalSession? _cliSession;
        private CancellationTokenSource? _cancellation;

        public RuntimeCliRunner(string sessionId)
        {
            _sessionId = string.IsNullOrWhiteSpace(sessionId)
                ? throw new ArgumentException("Command Line session id is required.", nameof(sessionId))
                : sessionId;
        }

        public void Start()
        {
            _cliSession ??= new EvalSession("unity-agent-cli-" + _sessionId, "cli", "unity-agent");
            _cancellation ??= new CancellationTokenSource();
        }

        public async Task<CliOutput> ExecuteLineAsync(string line)
        {
            if (_cliSession == null || _cancellation == null)
                return new CliOutput("CLI session is not available.", string.Empty, LogType.Error);

            if (IsGlobalHelp(line))
                return new CliOutput(EmbeddedHelp, string.Empty, LogType.Log);

            try
            {
                var response = await _cliService.ExecuteLineAsync(
                    _cliSession,
                    Guid.NewGuid().ToString("N"),
                    line,
                    _cancellation.Token);

                var exitRequested = EvalData.GetBool(response, "exit");
                var text = response.TryGetValue("text", out var value)
                    ? Convert.ToString(value) ?? string.Empty
                    : EvalJson.Stringify(response);
                if (exitRequested)
                    text = "The embedded Command Line has no process to exit. Its session remains open until this Unity process ends.";
                return new CliOutput(text, string.Empty, LogType.Log);
            }
            catch (OperationCanceledException)
            {
                return new CliOutput("CLI command was canceled.", string.Empty, LogType.Warning);
            }
            catch (Exception ex)
            {
                return new CliOutput(ex.Message, ex.ToString(), LogType.Exception);
            }
        }

        public void Dispose()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            _cliSession?.Dispose();
            _cliSession = null;
        }

        private static bool IsGlobalHelp(string line)
        {
            var command = line.Trim();
            return command.Equals("help", StringComparison.OrdinalIgnoreCase) ||
                   command.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                   command.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                   command.Equals("-help", StringComparison.OrdinalIgnoreCase);
        }

        private const string EmbeddedHelp = @"Embedded Yuze Eval Tool Command Line

This page executes one command at a time in the current Unity process and keeps one eval session alive.
The transcript is persisted, but the JavaScript VM is intentionally never restored after Unity restarts.

Supported here:
  help [tool] [command]      Show embedded help or Tool command details.
  tools | refresh           List or refresh the complete Tool command catalog.
  <tool path> <command>     Invoke a registered Tool command.
  eval-js --code <js>       Run one inline JavaScript command.
  eval-js <inline js>       Run inline JavaScript shorthand.
  session reset             Reset the persistent JavaScript session.
  logs dump [count]         Print recent Unity logs once.

Computer-level `unity connect`, stdin, heredoc and external REPL exit semantics are not available in this single-line page.";
    }

    internal readonly struct CliOutput
    {
        public CliOutput(string message, string stackTrace, LogType logType)
        {
            Message = message;
            StackTrace = stackTrace;
            LogType = logType;
        }

        public string Message { get; }

        public string StackTrace { get; }

        public LogType LogType { get; }
    }
}
