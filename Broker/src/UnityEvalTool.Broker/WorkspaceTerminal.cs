#nullable enable
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace YuzeToolkit.Eval.Broker;

internal sealed class WorkspaceTerminal : IDisposable
{
    private readonly object _sync = new();
    private readonly Process _process;
    private readonly StreamReader _stdout;
    private readonly StreamReader? _stderr;
    private readonly StreamWriter _stdin;
    private readonly WorkspaceWindowsPty? _windowsPty;
    private readonly bool _pty;
    private readonly WorkspaceOptions _options;
    private readonly Queue<(long Offset, string Stream, string Text)> _output = new();
    private long _nextOffset;
    private int _retainedCharacters;
    private string _state = "running";
    private int? _exitCode;
    private string? _failure;
    private bool _cancellationSent;
    private readonly Task _completion;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Deadline { get; }
    public bool Finished { get { lock (_sync) return _state != "running"; } }

    public WorkspaceTerminal(WorkspaceFiles files, string cwd, JsonObject arguments, WorkspaceOptions options)
    {
        _options = options;
        var timeout = arguments["timeoutSeconds"]?.GetValue<int>() ?? options.MaxTaskSeconds;
        if (timeout < 1 || timeout > options.MaxTaskSeconds) throw WorkspaceFiles.Error("InvalidRequest", "Task timeout exceeds the configured limit.");
        Deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
        _pty = arguments["pty"]?.GetValue<bool>() == true;
        var command = arguments["command"]?.GetValue<string>();
        var program = arguments["program"]?.GetValue<string>();
        var explicitShell = arguments["shell"]?.GetValue<string>();
        if ((command != null) == (program != null) || command != null && string.IsNullOrWhiteSpace(explicitShell))
            throw WorkspaceFiles.Error("InvalidRequest", "Provide program and args, or command with an explicit shell executable.");
        var start = new ProcessStartInfo
        {
            FileName = program ?? explicitShell!, WorkingDirectory = files.DirectoryPath(cwd),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        if (program != null)
        {
            foreach (var argument in arguments["args"]?.AsArray() ?? [])
                start.ArgumentList.Add(argument?.GetValue<string>() ?? throw WorkspaceFiles.Error("InvalidRequest", "args must contain strings."));
        }
        else
        {
            var shellName = Path.GetFileNameWithoutExtension(explicitShell!).ToLowerInvariant();
            if (shellName is "powershell" or "pwsh")
            {
                start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command"); start.ArgumentList.Add(command!);
            }
            else if (shellName == "cmd")
            {
                start.ArgumentList.Add("/d"); start.ArgumentList.Add("/s"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(command!);
            }
            else
            {
                start.ArgumentList.Add("-c"); start.ArgumentList.Add(command!);
            }
        }
        if (_pty && OperatingSystem.IsWindows())
        {
            _windowsPty = new WorkspaceWindowsPty(start, (short)options.PtyColumns, (short)options.PtyRows);
            _process = _windowsPty.Process; _stdout = _windowsPty.Output; _stdin = _windowsPty.Input;
        }
        else
        {
            if (_pty)
            {
                var executable = start.FileName; var argv = start.ArgumentList.ToArray();
                start.FileName = "script"; start.ArgumentList.Clear(); start.ArgumentList.Add("-q");
                if (OperatingSystem.IsMacOS())
                {
                    start.ArgumentList.Add("/dev/null"); start.ArgumentList.Add(executable);
                    foreach (var argument in argv) start.ArgumentList.Add(argument);
                }
                else
                {
                    start.ArgumentList.Add("-e"); start.ArgumentList.Add("-f"); start.ArgumentList.Add("-c");
                    start.ArgumentList.Add(string.Join(" ", new[] { executable }.Concat(argv).Select(value => "'" + value.Replace("'", "'\"'\"'") + "'")));
                    start.ArgumentList.Add("/dev/null");
                }
            }
            _process = new Process { StartInfo = start };
            if (!_process.Start()) throw WorkspaceFiles.Error("TaskStartFailed", "The process could not be started.");
            _stdout = _process.StandardOutput; _stderr = _process.StandardError; _stdin = _process.StandardInput;
        }
        _completion = CompleteAsync();
    }

    private async Task ReadAsync(StreamReader reader, string stream)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            lock (_sync)
            {
                _output.Enqueue((_nextOffset, stream, new string(buffer, 0, count)));
                _nextOffset += count;
                _retainedCharacters += count;
                while (_retainedCharacters > _options.MaxTaskOutputCharacters && _output.TryDequeue(out var old))
                    _retainedCharacters -= old.Text.Length;
            }
        }
    }

    private async Task CompleteAsync()
    {
        try
        {
            await Task.WhenAll(ReadAsync(_stdout, _pty ? "pty" : "stdout"), _stderr == null ? Task.CompletedTask : ReadAsync(_stderr, "stderr"), WaitForExitAsync());
            lock (_sync)
            {
                _exitCode = _process.ExitCode;
                if (_state == "running") _state = _cancellationSent ? "cancelled" : "completed";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { lock (_sync) { _state = "unknown"; _failure = ex.Message; } }
    }

    // Waits until the task exits or, unless untilExit, output beyond offset exists.
    public async Task<JsonObject> WaitAsync(int seconds, long offset, int maxCharacters, bool untilExit, CancellationToken token)
    {
        if (seconds is < 0 or > 300) throw WorkspaceFiles.Error("InvalidRequest", "waitSeconds must be 0–300.");
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < until && !Finished)
        {
            if (!untilExit) lock (_sync) if (_nextOffset > offset) break;
            await Task.Delay(100, token);
        }
        return Snapshot(offset, maxCharacters);
    }

    public JsonObject Snapshot(long offset, int maxCharacters)
    {
        lock (_sync)
        {
            if (offset < 0 || offset > _nextOffset) throw WorkspaceFiles.Error("InvalidCursor", "Output cursor is outside this task's range.");
            var first = _output.TryPeek(out var head) ? head.Offset : _nextOffset;
            var chunks = new JsonArray();
            var remaining = Math.Min(Math.Max(maxCharacters, 0), _options.MaxTextCharacters);
            var next = Math.Max(offset, first);
            foreach (var chunk in _output)
            {
                if (remaining == 0) break;
                if (chunk.Offset + chunk.Text.Length <= next) continue;
                var start = (int)Math.Max(0, next - chunk.Offset);
                var count = Math.Min(remaining, chunk.Text.Length - start);
                chunks.Add((JsonNode)new JsonObject { ["offset"] = chunk.Offset + start, ["stream"] = chunk.Stream, ["text"] = chunk.Text.Substring(start, count) });
                next = chunk.Offset + start + count;
                remaining -= count;
            }
            return new JsonObject
            {
                ["taskId"] = Id, ["state"] = _state, ["exitCode"] = _exitCode, ["error"] = _failure,
                ["output"] = chunks, ["firstAvailableOffset"] = first, ["nextOffset"] = next,
                ["endOffset"] = _nextOffset, ["gap"] = offset < first, ["hasMore"] = next < _nextOffset,
                ["deadlineUtc"] = Deadline.ToString("O"), ["pty"] = _pty
            };
        }
    }

    public async Task WriteAsync(string input, bool close, CancellationToken token)
    {
        if (input.Length > _options.MaxTextCharacters) throw WorkspaceFiles.Error("InvalidRequest", "Input exceeds workspace.maxTextCharacters.");
        if (Finished) throw WorkspaceFiles.Error("TaskFinished", "The process no longer accepts input.");
        try
        {
            await _stdin.WriteAsync(input.AsMemory(), token);
            await _stdin.FlushAsync(token);
            if (close) _stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            throw new BrokerOperationException("ExecutionOutcomeUnknown", "stdin delivery was interrupted; some input may already have reached the process. Inspect its output before retrying.", true);
        }
    }

    public void Cancel(string reason)
    {
        lock (_sync)
        {
            if (_state != "running") return;
            _failure = reason;
            try
            {
                if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _cancellationSent = true; }
            }
            catch (InvalidOperationException) when (_process.HasExited) { /* Process exited during cancellation. */ }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _failure = "Cancellation could not be confirmed: " + ex.Message;
            }
            // Cancellation is only confirmed after WaitForExit and both output streams complete.
        }
    }

    public async Task<JsonObject> CancelAsync()
    {
        Cancel("Cancellation requested through the workspace context.");
        try { await _completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { return new JsonObject { ["taskId"] = Id, ["state"] = "cancellation-pending" }; }
        return Snapshot(0, _options.MaxTextCharacters);
    }

    public void Dispose()
    {
        Cancel("Workspace context closed or expired.");
        _ = _completion.ContinueWith(_ => { _windowsPty?.Dispose(); _process.Dispose(); }, TaskScheduler.Default);
    }

    private async Task WaitForExitAsync()
    {
        await _process.WaitForExitAsync();
        _windowsPty?.CloseConsole();
    }
}
