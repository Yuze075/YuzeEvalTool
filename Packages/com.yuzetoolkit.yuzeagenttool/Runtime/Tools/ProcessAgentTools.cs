#nullable enable
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace YuzeToolkit.Agent
{
    internal sealed class ProcessAgentTool : IAgentTool
    {
        private const int MaximumTimeoutSeconds = 3_600;
        private readonly AgentProcessRunner _runner;

        public ProcessAgentTool(AgentProcessRunner runner)
        {
            _runner = runner;
            Descriptor = new AgentToolDescriptor(
                "process_exec",
                "Run a local executable directly and capture stdout, stderr and exit code.",
                AgentToolAccess.Write,
                AgentToolRisk.Process,
                AgentToolSurface.Editor,
                false,
                AgentToolArguments.ObjectSchema(AgentJson.Object(
                        ("executable", AgentToolArguments.StringProperty("Executable name or absolute path.")),
                        ("arguments", AgentJson.Object(
                            ("type", "array"),
                            ("items", AgentJson.Object(("type", "string"))),
                            ("description", "Argument vector passed to the executable."))),
                        ("workingDirectory", AgentToolArguments.StringProperty(
                            "Working directory. Relative paths use the conversation working directory.")),
                        ("timeoutSeconds", AgentToolArguments.IntegerProperty("Execution timeout in seconds.", 1))),
                    "executable"));
        }

        public AgentToolDescriptor Descriptor { get; }

        public async Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            if (!AgentShellResolver.IsSupported)
                return AgentToolResult.Error($"Process execution is not supported on {AgentPaths.RuntimePlatform}.");
            var executable = AgentToolArguments.RequiredString(arguments, "executable");
            var argumentList = AgentToolArguments.OptionalStrings(arguments, "arguments");
            var workingDirectoryValue = AgentToolArguments.OptionalString(arguments, "workingDirectory");
            var workingDirectory = string.IsNullOrWhiteSpace(workingDirectoryValue)
                ? context.WorkingDirectory
                : AgentPath.Resolve(context, workingDirectoryValue);
            var timeout = Math.Min(MaximumTimeoutSeconds,
                Math.Max(1, AgentToolArguments.OptionalInt(arguments, "timeoutSeconds",
                    context.DefaultTimeoutSeconds)));
            var result = await _runner.RunAsync(executable, argumentList, workingDirectory, timeout,
                cancellationToken).ConfigureAwait(false);
            return result.StartError.Length > 0
                ? AgentToolResult.Error(result.StartError)
                : AgentToolResult.Success(result.ToJson());
        }
    }

    internal sealed class ShellAgentTool : IAgentTool
    {
        private const int MaximumTimeoutSeconds = 3_600;
        private readonly AgentProcessRunner _runner;

        public ShellAgentTool(AgentProcessRunner runner)
        {
            _runner = runner;
            Descriptor = new AgentToolDescriptor(
                "shell_exec",
                "Run a script with zsh, sh, bash, PowerShell or cmd on supported desktop platforms.",
                AgentToolAccess.Write,
                AgentToolRisk.Process,
                AgentToolSurface.Editor,
                false,
                AgentToolArguments.ObjectSchema(AgentJson.Object(
                        ("script", AgentToolArguments.StringProperty("Complete shell script.")),
                        ("shell", AgentToolArguments.StringProperty(
                            "Optional shell id or executable path: zsh, sh, bash, pwsh, powershell or cmd.")),
                        ("workingDirectory", AgentToolArguments.StringProperty(
                            "Working directory. Relative paths use the conversation working directory.")),
                        ("timeoutSeconds", AgentToolArguments.IntegerProperty("Execution timeout in seconds.", 1))),
                    "script"));
        }

        public AgentToolDescriptor Descriptor { get; }

        public async Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            if (!AgentShellResolver.IsSupported)
                return AgentToolResult.Error($"Shell execution is not supported on {AgentPaths.RuntimePlatform}.");

            var script = AgentToolArguments.RequiredString(arguments, "script");
            var shell = AgentToolArguments.OptionalString(arguments, "shell");
            var workingDirectoryValue = AgentToolArguments.OptionalString(arguments, "workingDirectory");
            var workingDirectory = string.IsNullOrWhiteSpace(workingDirectoryValue)
                ? context.WorkingDirectory
                : AgentPath.Resolve(context, workingDirectoryValue);
            var timeout = Math.Min(MaximumTimeoutSeconds,
                Math.Max(1, AgentToolArguments.OptionalInt(arguments, "timeoutSeconds",
                    context.DefaultTimeoutSeconds)));
            var invocation = AgentShellResolver.Resolve(shell);
            var temporaryDirectory = Path.Combine(
                AgentPaths.GetBasePath(AgentPathBase.TemporaryCache), "Scripts");
            Directory.CreateDirectory(temporaryDirectory);
            var scriptPath = Path.Combine(temporaryDirectory,
                Guid.NewGuid().ToString("N") + invocation.ScriptExtension);
            File.WriteAllText(scriptPath, script);
            try
            {
                var shellArguments = invocation.CreateArguments(scriptPath);
                var result = await _runner.RunAsync(invocation.Executable, shellArguments, workingDirectory, timeout,
                    cancellationToken).ConfigureAwait(false);
                return result.StartError.Length > 0
                    ? AgentToolResult.Error(result.StartError)
                    : AgentToolResult.Success(result.ToJson());
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }
    }

    internal sealed class AgentProcessRunner
    {
        private const int MaxOutputCharacters = 1_000_000;
        private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromSeconds(5);

        public async Task<AgentProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var result = new AgentProcessResult();
            var output = new StringBuilder();
            var error = new StringBuilder();
            var outputSync = new object();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                        ? AgentPaths.ProjectRoot
                        : workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            void Append(StringBuilder builder, string? line)
            {
                if (line == null) return;
                lock (outputSync)
                {
                    if (builder.Length >= MaxOutputCharacters)
                    {
                        result.Truncated = true;
                        return;
                    }
                    var remaining = MaxOutputCharacters - builder.Length;
                    if (line.Length + 1 > remaining)
                    {
                        builder.Append(line, 0, Math.Max(0, remaining));
                        result.Truncated = true;
                    }
                    else
                    {
                        builder.AppendLine(line);
                    }
                }
            }

            process.OutputDataReceived += (_, eventArgs) => Append(output, eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => Append(error, eventArgs.Data);
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult(true);
            try
            {
                if (!process.Start())
                {
                    result.StartError = $"Failed to start process '{executable}'.";
                    return result;
                }
            }
            catch (Exception exception)
            {
                result.StartError = $"Failed to start process '{executable}': {exception.Message}";
                return result;
            }

            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.HasExited) exited.TrySetResult(true);

            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => canceled.TrySetResult(true));
            var timeout = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            var completed = await Task.WhenAny(exited.Task, timeout, canceled.Task).ConfigureAwait(false);
            var wasCanceled = completed == canceled.Task;
            if (wasCanceled || completed == timeout)
            {
                result.TimedOut = completed == timeout;
                result.TerminationError = TryKill(process);
                if (!process.HasExited)
                {
                    var terminationWait = await Task.WhenAny(exited.Task, Task.Delay(TerminationGracePeriod))
                        .ConfigureAwait(false);
                    if (terminationWait != exited.Task && string.IsNullOrWhiteSpace(result.TerminationError))
                        result.TerminationError = "The process did not exit within 5 seconds after termination was requested.";
                }
            }

            if (process.HasExited)
            {
                exited.TrySetResult(true);
                await exited.Task.ConfigureAwait(false);
                process.WaitForExit();
                result.ExitCode = process.ExitCode;
            }
            lock (outputSync)
            {
                result.StandardOutput = output.ToString();
                result.StandardError = error.ToString();
            }
            if (wasCanceled)
            {
                if (!string.IsNullOrWhiteSpace(result.TerminationError))
                    throw new OperationCanceledException(
                        "Process execution was canceled, but termination could not be confirmed: " +
                        result.TerminationError, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return result;
        }

        private static string TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                return string.Empty;
            }
            catch (Exception exception) when (exception is InvalidOperationException ||
                                              exception is Win32Exception ||
                                              exception is NotSupportedException)
            {
                return exception.GetType().Name + ": " + exception.Message;
            }
        }

        private static string QuoteArgument(string value)
        {
            if (value.Length > 0 && value.All(character =>
                    !char.IsWhiteSpace(character) && character != '"' && character != '\\'))
                return value;
            var result = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    continue;
                }
                result.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
            result.Append('\\', backslashes * 2).Append('"');
            return result.ToString();
        }
    }

    internal sealed class AgentProcessResult
    {
        public int ExitCode { get; set; } = -1;

        public string StandardOutput { get; set; } = string.Empty;

        public string StandardError { get; set; } = string.Empty;

        public bool TimedOut { get; set; }

        public bool Truncated { get; set; }

        public string StartError { get; set; } = string.Empty;

        public string TerminationError { get; set; } = string.Empty;

        public string ToJson()
        {
            return AgentJson.Stringify(AgentJson.Object(
                ("exitCode", ExitCode),
                ("stdout", StandardOutput),
                ("stderr", StandardError),
                ("timedOut", TimedOut),
                ("truncated", Truncated),
                ("terminationError", TerminationError)));
        }
    }

    internal sealed class AgentShellInvocation
    {
        public string Executable { get; set; } = string.Empty;

        public string ScriptExtension { get; set; } = string.Empty;

        public Func<string, IReadOnlyList<string>> CreateArguments { get; set; } = _ => Array.Empty<string>();
    }

    internal static class AgentShellResolver
    {
        public static bool IsSupported => AgentPaths.RuntimePlatform is
            RuntimePlatform.OSXEditor or RuntimePlatform.OSXPlayer or
            RuntimePlatform.WindowsEditor or RuntimePlatform.WindowsPlayer or
            RuntimePlatform.LinuxEditor or RuntimePlatform.LinuxPlayer;

        public static AgentShellInvocation Resolve(string requested)
        {
            var value = requested.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                if (AgentPaths.RuntimePlatform is RuntimePlatform.WindowsEditor or RuntimePlatform.WindowsPlayer)
                    value = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                else if (AgentPaths.RuntimePlatform is RuntimePlatform.OSXEditor or RuntimePlatform.OSXPlayer)
                    value = "/bin/zsh";
                else
                    value = "/bin/sh";
            }

            var name = Path.GetFileNameWithoutExtension(value).ToLowerInvariant();
            return name switch
            {
                "zsh" => new AgentShellInvocation
                {
                    Executable = value == "zsh" ? "/bin/zsh" : value,
                    ScriptExtension = ".zsh",
                    CreateArguments = path => new[] { "-f", path }
                },
                "sh" => new AgentShellInvocation
                {
                    Executable = value == name ? "/bin/sh" : value,
                    ScriptExtension = ".sh",
                    CreateArguments = path => new[] { path }
                },
                "bash" => new AgentShellInvocation
                {
                    Executable = value == name ? "/bin/bash" : value,
                    ScriptExtension = ".sh",
                    CreateArguments = path => new[] { "--noprofile", "--norc", path }
                },
                "pwsh" or "powershell" => new AgentShellInvocation
                {
                    Executable = value,
                    ScriptExtension = ".ps1",
                    CreateArguments = path => new[]
                    {
                        "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path
                    }
                },
                "cmd" => new AgentShellInvocation
                {
                    Executable = value,
                    ScriptExtension = ".cmd",
                    CreateArguments = path => new[] { "/D", "/Q", "/C", path }
                },
                _ => throw new ArgumentException($"Unsupported shell '{requested}'. Use zsh, sh, bash, pwsh, powershell, cmd or an executable path with one of those names.")
            };
        }
    }
}
