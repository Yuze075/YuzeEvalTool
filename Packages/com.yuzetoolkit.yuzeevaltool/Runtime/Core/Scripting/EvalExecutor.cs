#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit.Eval
{
    public sealed class EvalExecutor
    {
        private const int BusyPollDelayMilliseconds = 50;
        private static readonly SemaphoreSlim GlobalEvalGate = new(1, 1);
        private readonly EvalOptions _options;

        public EvalExecutor(EvalOptions options) => _options = options;

        public async Task<Dictionary<string, object?>> ExecuteAsync(
            EvalSession session,
            string requestId,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var code = EvalData.GetString(arguments, "code") ?? string.Empty;
            var timeout = EvalData.GetInt(arguments, "timeout", _options.DefaultEvalTimeoutSeconds);
            var resetSession = EvalData.GetBool(arguments, "resetSession", false);
            var evalStarted = false;
            var evalCompleted = false;
            var evalTurnAcquired = false;
            var globalEvalTurnAcquired = false;

            void BeginEval()
            {
                if (evalStarted) return;
                session.BeginEval(requestId, code, timeout, resetSession);
                evalStarted = true;
            }

            void CompleteEval(bool success, string error)
            {
                if (evalCompleted) return;
                BeginEval();
                session.CompleteEval(requestId, success, error);
                evalCompleted = true;
            }

            try
            {
                evalTurnAcquired = await session.TryEnterEvalTurnAsync(cancellationToken);
                if (!evalTurnAcquired)
                {
                    const string closedMessage = "Eval session is closing or has been disposed.";
                    return ToolError(closedMessage);
                }

                var busyReason = await WaitForUnityIdleAsync(timeout, cancellationToken);
                if (!string.IsNullOrEmpty(busyReason))
                {
                    var busyMessage =
                        $"{busyReason}. Unity did not become idle within {NormalizeTimeoutSeconds(timeout)}s.";
                    CompleteEval(false, busyMessage);
                    return ToolError(busyMessage);
                }

                await GlobalEvalGate.WaitAsync(cancellationToken);
                globalEvalTurnAcquired = true;

                if (resetSession && session.VmSession != null)
                {
                    var previousSession = session.VmSession;
                    await MainThreadDispatcher.RunAsync(previousSession.Dispose);
                    session.VmSession = null;
                }

                session.VmSession ??= new EvalVmSession(session);

                BeginEval();
                var result = await session.VmSession.ExecuteAsync(requestId, code, timeout, cancellationToken);
                if (result.TryGetValue("success", out var successValue) && successValue is bool success && success)
                {
                    CompleteEval(true, string.Empty);
                    return EvalData.Obj(("content", BuildContent(result)));
                }

                var error = result.TryGetValue("error", out var errorValue) ? Convert.ToString(errorValue) : "eval failed.";
                var stack = result.TryGetValue("stack", out var stackValue) ? Convert.ToString(stackValue) : string.Empty;
                var resultMessage = string.IsNullOrEmpty(stack) ? error ?? string.Empty : $"{error}\nStack: {stack}";
                CompleteEval(false, resultMessage);
                return ToolError(resultMessage);
            }
            catch (OperationCanceledException)
            {
                const string cancelMessage = "eval was canceled.";
                CompleteEval(false, cancelMessage);
                return ToolError(cancelMessage);
            }
            catch (Exception ex)
            {
                CompleteEval(false, ex.Message);
                throw;
            }
            finally
            {
                if (globalEvalTurnAcquired)
                    GlobalEvalGate.Release();
                if (evalTurnAcquired)
                    session.ReleaseEvalTurn();
            }
        }

        public static Dictionary<string, object?> ToolDefinition()
        {
            return EvalData.Obj(
                ("name", "eval"),
                ("description", Description),
                ("inputSchema", EvalData.Obj(
                    ("type", "object"),
                    ("properties", EvalData.Obj(
                        ("code", EvalData.Obj(
                            ("type", "string"),
                            ("description", "An async function declaration named execute. Example: async function execute() { const runtime = await import('tools://Runtime'); return runtime.getState(); }")
                        )),
                        ("timeout", EvalData.Obj(
                            ("type", "number"),
                            ("description", "Execution timeout in seconds. Default is 30.")
                        )),
                        ("resetSession", EvalData.Obj(
                            ("type", "boolean"),
                            ("description", "Reset this eval session's persistent JavaScript VM before executing.")
                        ))
                    )),
                    ("required", EvalData.Arr("code"))
                ))
            );
        }

        private static Dictionary<string, object?> ToolError(string text)
        {
            return EvalData.Obj(
                ("content", EvalData.Arr(EvalData.Obj(("type", "text"), ("text", "Error: " + text)))),
                ("isError", true)
            );
        }

        private static List<object?> BuildContent(Dictionary<string, object?> evalResult)
        {
            var content = new List<object?>();
            var hasValue = evalResult.TryGetValue("hasValue", out var hasValueRaw) && hasValueRaw is bool valueExists && valueExists;
            var text = hasValue && evalResult.TryGetValue("result", out var value)
                ? EvalValueFormatter.ToEvalText(value)
                : "(no return value)";
            content.Add(EvalData.Obj(("type", "text"), ("text", text)));

            if (EvalData.AsArray(evalResult.TryGetValue("images", out var imagesRaw) ? imagesRaw : null) is { } images)
                content.AddRange(images);

            return content;
        }

        private async Task<string?> WaitForUnityIdleAsync(int timeoutSeconds, CancellationToken cancellationToken)
        {
#if UNITY_EDITOR
            var normalizedTimeout = NormalizeTimeoutSeconds(timeoutSeconds);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(normalizedTimeout);
            string? busyReason = null;

            while (DateTime.UtcNow < deadline)
            {
                busyReason = await MainThreadDispatcher.RunAsync(EditorStatusProvider.GetEvalBusyReason);
                if (string.IsNullOrEmpty(busyReason))
                    return null;

                var remaining = deadline - DateTime.UtcNow;
                var delayMilliseconds = Math.Min(BusyPollDelayMilliseconds,
                    Math.Max(1, (int)remaining.TotalMilliseconds));
                await Task.Delay(delayMilliseconds, cancellationToken);
            }

            busyReason = await MainThreadDispatcher.RunAsync(EditorStatusProvider.GetEvalBusyReason);
            return string.IsNullOrEmpty(busyReason) ? null : busyReason;
#else
            await Task.CompletedTask;
            return null;
#endif
        }

        private int NormalizeTimeoutSeconds(int timeoutSeconds)
        {
            var fallback = _options.DefaultEvalTimeoutSeconds <= 0 ? 30 : _options.DefaultEvalTimeoutSeconds;
            var effectiveTimeout = timeoutSeconds <= 0 ? fallback : timeoutSeconds;
            return Math.Max(1, Math.Min(effectiveTimeout, 600));
        }

        private const string Description =
            "Run JavaScript inside Unity through this eval session's persistent PuerTS VM. Define `async function execute() { ... }` " +
            "and return concise serializable data. For unfamiliar Unity work, import `tools://` to read the root summaries, use " +
            "`getToolDetails(path)` or a module's `functions` metadata when needed, then import only the relevant `tools://Path` " +
            "module. Generated tool methods use positional parameters. Prefer helper modules and use `CS.*` only for uncovered " +
            "APIs. Editor-only modules require the Unity Editor. If code schedules an asset refresh or compilation, return from " +
            "execute immediately and do not issue another eval while Unity is busy.";
    }
}
