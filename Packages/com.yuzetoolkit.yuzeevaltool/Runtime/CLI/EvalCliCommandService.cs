#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit
{
    public sealed partial class EvalCliCommandService
    {
        private readonly EvalExecutor _evalExecutor;
        private readonly HashSet<string> _streamedLogKeys = new(StringComparer.Ordinal);
        private bool _logStreamingEnabled;

        public EvalCliCommandService(EvalExecutor evalExecutor)
        {
            _evalExecutor = evalExecutor;
        }

        public async Task<Dictionary<string, object?>> ExecuteLineAsync(
            EvalSession session,
            string requestId,
            string line,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await ExecuteCoreAsync(session, requestId, line, cancellationToken);
                if (_logStreamingEnabled)
                    AppendStreamingLogs(response);
                return response;
            }
            catch (CliCommandException ex)
            {
                return Failure(ex.Message);
            }
        }

        private async Task<Dictionary<string, object?>> ExecuteCoreAsync(
            EvalSession session,
            string requestId,
            string line,
            CancellationToken cancellationToken)
        {
            var tokens = Tokenize(line);
            if (tokens.Count == 0)
                return Success("(empty)");

            var command = tokens[0];
            if (IsHelpToken(command))
                return Success(FormatGlobalHelp());

            if (Is(command, "help"))
                return Success(FormatHelp(tokens.Skip(1).ToList()));

            if (Is(command, "tools"))
                return Success(FormatTools(refresh: false));

            if (Is(command, "refresh"))
                return Success(FormatTools(refresh: true));

            if (Is(command, "quit") || Is(command, "exit"))
                return EvalData.Obj(("success", true), ("text", "bye"), ("exit", true));

            if (Is(command, "session") && tokens.Count > 1 && Is(tokens[1], "reset"))
                return await ExecuteEvalAsync(session, requestId, "async function execute() { return 'session reset'; }",
                    resetSession: true, cancellationToken);

            if (Is(command, "logs") || Is(command, "log"))
                return Success(HandleLogs(tokens.Skip(1).ToList()));

            if (Is(command, "eval") || Is(command, "eval-js"))
            {
                var code = ExtractEvalCode(tokens.Skip(1).ToList());
                return await ExecuteEvalAsync(session, requestId, WrapUserScript(code), resetSession: false,
                    cancellationToken);
            }

            if (TryBuildToolCall(tokens, out var js, out var helpText, out var error))
            {
                if (!string.IsNullOrEmpty(helpText))
                    return Success(helpText);
                return await ExecuteEvalAsync(session, requestId, js, resetSession: false, cancellationToken);
            }

            return Failure(error);
        }

        private async Task<Dictionary<string, object?>> ExecuteEvalAsync(
            EvalSession session,
            string requestId,
            string code,
            bool resetSession,
            CancellationToken cancellationToken)
        {
            var result = await _evalExecutor.ExecuteAsync(session, requestId, EvalData.Obj(
                ("code", code),
                ("resetSession", resetSession)), cancellationToken);
            if (EvalData.GetBool(result, "isError", false))
                return EvalData.Obj(("success", false), ("text", ExtractContentText(result)));
            return EvalData.Obj(("success", true), ("text", ExtractContentText(result)), ("json", result));
        }

        private static string FormatHelp(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
                return FormatGlobalHelp();

            var toolName = args[0];
            if (!TryReadTool(toolName, out var tool, out var error))
                return error;

            if (args.Count == 1 || IsHelpToken(args[0]))
                return FormatToolHelp(tool);

            var methodName = args[1];
            if (IsHelpToken(methodName))
                return FormatToolHelp(tool);

            var function = tool.FindFunction(methodName);
            return function == null
                ? $"Tool '{tool.DisplayName}' has no command '{methodName}'. Use {tool.CommandName} --help."
                : FormatCommandHelp(tool, function);
        }

        private static string FormatGlobalHelp()
        {
            var builder = new StringBuilder();
            builder.AppendLine("Unity-side commands for the computer-level `unity` Broker CLI.");
            builder.AppendLine();
            builder.AppendLine("Use `unity <command...>` for one-shot execution, or `unity connect <instance>` for an interactive console.");
            builder.AppendLine();
            builder.AppendLine("Built-ins:");
            builder.AppendLine("  -h | --help | -help                         Show this help.");
            builder.AppendLine("  help [tool] [command]                       Show global help, a tool's commands, or one command's arguments.");
            builder.AppendLine("  tools                                       List available CLI tools and aliases.");
            builder.AppendLine("  refresh                                     Refresh the tool catalog from Unity.");
            builder.AppendLine("  eval-js --code <js>                         Run inline JavaScript in Unity.");
            builder.AppendLine("  eval-js --file <path>                       Run JavaScript loaded by Unity from a file.");
            builder.AppendLine("  eval-js --stdin                             Run JavaScript read by the CLI from stdin.");
            builder.AppendLine("  eval-js <<'END_JS' ... END_JS               Run JavaScript from heredoc input in REPL.");
            builder.AppendLine("  logs on | logs off | logs status            Control per-connection log echo after command results.");
            builder.AppendLine("  logs dump [count]                           Print recent Unity logs once.");
            builder.AppendLine("  session reset                               Reset the persistent eval JavaScript session.");
            builder.AppendLine("  quit | exit                                 Exit the REPL.");
            builder.AppendLine();
            builder.AppendLine(FormatTools(refresh: false));
            return builder.ToString().TrimEnd();
        }

        private static string FormatTools(bool refresh)
        {
            var tools = ListCliTools(refresh);
            var builder = new StringBuilder();
            builder.AppendLine("Tools:");
            foreach (var tool in tools.Where(tool => !tool.IsAlias).OrderBy(tool => tool.Path, StringComparer.OrdinalIgnoreCase))
                builder.AppendLine($"  {tool.CommandName} - {tool.Description}");

            var aliases = tools.Where(tool => tool.IsAlias).OrderBy(tool => tool.CommandName, StringComparer.OrdinalIgnoreCase).ToList();
            if (aliases.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Aliases:");
                foreach (var alias in aliases)
                    builder.AppendLine($"  {alias.CommandName} -> {alias.Path}");
            }

            builder.AppendLine();
            builder.AppendLine("Use `<tool> --help` to list commands. Use `<tool> <command> --help` for arguments.");
            return builder.ToString().TrimEnd();
        }

        private static string FormatToolHelp(CliTool tool)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{tool.CommandName} - {tool.Description}");
            builder.AppendLine();
            builder.AppendLine("Commands:");
            if (tool.Functions.Count == 0)
            {
                builder.AppendLine("  (no commands)");
            }
            else
            {
                foreach (var function in tool.Functions.OrderBy(function => function.MethodName, StringComparer.OrdinalIgnoreCase))
                    builder.AppendLine($"  {BuildUsage(tool, function)} - {function.Description}");
            }

            builder.AppendLine();
            builder.AppendLine($"Use `{tool.CommandName} <command> --help` for argument details.");
            return builder.ToString().TrimEnd();
        }

        private static string FormatCommandHelp(CliTool tool, CliFunction function)
        {
            var builder = new StringBuilder();
            builder.AppendLine(BuildUsage(tool, function));
            builder.AppendLine(function.Description);
            if (function.Parameters.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Arguments:");
                foreach (var parameter in function.Parameters)
                {
                    var flags = string.Join(", ", parameter.Flags);
                    var valueKind = IsBoolType(parameter.Type)
                        ? "switch; pass flag alone, or true/false"
                        : parameter.Type;
                    var optional = parameter.Optional ? "optional" : "required";
                    var defaultValue = parameter.Optional ? $", default: {FormatDefaultValue(parameter.DefaultValue)}" : string.Empty;
                    builder.AppendLine($"  {flags}  {valueKind} ({optional}{defaultValue})");
                    if (!string.IsNullOrWhiteSpace(parameter.Description))
                        builder.AppendLine($"      {parameter.Description}");
                }
            }

            return builder.ToString().TrimEnd();
        }

        private string HandleLogs(IReadOnlyList<string> args)
        {
            var action = args.Count > 0 ? args[0].ToLowerInvariant() : "status";
            switch (action)
            {
                case "on":
                case "start":
                    SeedStreamedLogs();
                    _logStreamingEnabled = true;
                    return "Unity log echo is enabled for this CLI connection.";
                case "off":
                case "stop":
                    _logStreamingEnabled = false;
                    return "Unity log echo is disabled.";
                case "status":
                    return _logStreamingEnabled
                        ? "Unity log echo is enabled for this CLI connection."
                        : "Unity log echo is disabled.";
                case "dump":
                case "recent":
                    var count = args.Count > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var parsed)
                        ? parsed
                        : 50;
                    return FormatLogEntries(UnityLogBuffer.GetRecent(count, "all"));
                default:
                    throw new CliCommandException("logs usage: logs on | logs off | logs status | logs dump [count].");
            }
        }

        private void SeedStreamedLogs()
        {
            _streamedLogKeys.Clear();
            foreach (var log in UnityLogBuffer.GetRecent(300, "all"))
                _streamedLogKeys.Add(GetLogKey(log));
        }

        private void AppendStreamingLogs(Dictionary<string, object?> response)
        {
            if (!EvalData.GetBool(response, "success", false)) return;
            var logs = UnityLogBuffer.GetRecent(300, "all")
                .Where(log => _streamedLogKeys.Add(GetLogKey(log)))
                .ToList();
            if (logs.Count == 0) return;

            var currentText = EvalData.GetString(response, "text") ?? string.Empty;
            var logText = FormatLogEntries(logs);
            response["text"] = string.IsNullOrWhiteSpace(currentText)
                ? logText
                : currentText + Environment.NewLine + logText;
        }

        private static string FormatLogEntries(IReadOnlyList<object?> logs)
        {
            if (logs.Count == 0) return "(no logs)";
            var builder = new StringBuilder();
            foreach (var raw in logs)
            {
                var log = EvalData.AsObject(raw) ?? new Dictionary<string, object?>();
                var timestamp = EvalData.GetString(log, "timestamp") ?? string.Empty;
                var type = EvalData.GetString(log, "type") ?? string.Empty;
                var message = EvalData.GetString(log, "message") ?? string.Empty;
                var stackTrace = EvalData.GetString(log, "stackTrace") ?? string.Empty;
                var header = string.IsNullOrWhiteSpace(timestamp) ? $"[{type}]" : $"[{timestamp}] [{type}]";
                builder.AppendLine($"{header} {message}");
                if (!string.IsNullOrWhiteSpace(stackTrace))
                    builder.AppendLine(stackTrace);
            }

            return builder.ToString().TrimEnd();
        }

        private static string GetLogKey(object? raw)
        {
            var log = EvalData.AsObject(raw) ?? new Dictionary<string, object?>();
            return (EvalData.GetString(log, "timestamp") ?? string.Empty) + "\n" +
                   (EvalData.GetString(log, "type") ?? string.Empty) + "\n" +
                   (EvalData.GetString(log, "message") ?? string.Empty) + "\n" +
                   (EvalData.GetString(log, "stackTrace") ?? string.Empty);
        }

        private static bool TryBuildToolCall(IReadOnlyList<string> tokens, out string js, out string helpText,
            out string error)
        {
            js = string.Empty;
            helpText = string.Empty;
            error = string.Empty;

            for (var methodIndex = tokens.Count - 1; methodIndex >= 1; methodIndex--)
            {
                var requestedTool = string.Join("/", tokens.Take(methodIndex));
                if (!TryReadTool(requestedTool, out var tool, out _)) continue;

                var methodName = tokens[methodIndex];
                if (IsHelpToken(methodName))
                {
                    helpText = FormatToolHelp(tool);
                    return true;
                }

                var function = tool.FindFunction(methodName);
                if (function == null)
                {
                    error = $"Tool '{requestedTool}' has no command '{methodName}'. Use {tool.CommandName} --help.";
                    return false;
                }

                var remaining = tokens.Skip(methodIndex + 1).ToList();
                if (remaining.Count == 1 && IsHelpToken(remaining[0]))
                {
                    helpText = FormatCommandHelp(tool, function);
                    return true;
                }

                var values = ParseArguments(tool, function, remaining);
                js = GenerateToolCall(tool.Path, function.MethodName, values);
                return true;
            }

            error = $"Unknown CLI command or tool path '{tokens[0]}'. Use tools to list available commands.";
            return false;
        }

        private static List<object?> ParseArguments(CliTool tool, CliFunction function, IReadOnlyList<string> args)
        {
            var values = Enumerable.Repeat<object?>(Missing.Value, function.Parameters.Count).ToList();
            var positional = new Queue<string>();

            for (var i = 0; i < args.Count; i++)
            {
                var argument = args[i];
                if (IsHelpToken(argument))
                    throw new CliCommandException(FormatCommandHelp(tool, function));

                if (!TrySplitFlag(argument, out var flag, out var inlineValue))
                {
                    positional.Enqueue(argument);
                    continue;
                }

                var parameterIndex = -1;
                for (var candidateIndex = 0; candidateIndex < function.Parameters.Count; candidateIndex++)
                {
                    if (!function.Parameters[candidateIndex].Flags.Any(candidate =>
                            string.Equals(candidate, flag, StringComparison.OrdinalIgnoreCase))) continue;
                    parameterIndex = candidateIndex;
                    break;
                }
                if (parameterIndex < 0)
                    throw new CliCommandException($"Unknown flag '{flag}' for {tool.CommandName} {function.MethodName}.");

                var parameter = function.Parameters[parameterIndex];
                if (IsBoolType(parameter.Type))
                {
                    if (inlineValue != null)
                    {
                        values[parameterIndex] = ConvertValue(inlineValue, parameter.Type, flag);
                        continue;
                    }

                    if (i + 1 < args.Count && bool.TryParse(args[i + 1], out var boolValue))
                    {
                        i++;
                        values[parameterIndex] = boolValue;
                    }
                    else
                    {
                        values[parameterIndex] = true;
                    }

                    continue;
                }

                var value = inlineValue;
                if (value == null)
                {
                    if (i + 1 >= args.Count)
                        throw new CliCommandException($"Flag '{flag}' requires a value.");
                    value = args[++i];
                }

                values[parameterIndex] = ConvertValue(value, parameter.Type, flag);
            }

            for (var i = 0; i < function.Parameters.Count && positional.Count > 0; i++)
            {
                if (values[i] is Missing)
                    values[i] = ConvertValue(positional.Dequeue(), function.Parameters[i].Type,
                        function.Parameters[i].Name);
            }

            if (positional.Count > 0)
                throw new CliCommandException($"Too many arguments for {tool.CommandName} {function.MethodName}.");

            for (var i = 0; i < function.Parameters.Count; i++)
            {
                if (values[i] is not Missing) continue;
                if (!function.Parameters[i].Optional)
                    throw new CliCommandException($"Missing required parameter '{function.Parameters[i].Name}'.");
            }

            while (values.Count > 0 && values[^1] is Missing)
                values.RemoveAt(values.Count - 1);

            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is Missing)
                    values[i] = function.Parameters[i].DefaultValue;
            }

            return values;
        }

        private static bool TrySplitFlag(string argument, out string flag, out string? inlineValue)
        {
            flag = string.Empty;
            inlineValue = null;
            if (!argument.StartsWith("-", StringComparison.Ordinal) || argument == "-")
                return false;

            var equalsIndex = argument.IndexOf('=');
            if (equalsIndex > 0)
            {
                flag = argument.Substring(0, equalsIndex);
                inlineValue = argument.Substring(equalsIndex + 1);
            }
            else
            {
                flag = argument;
            }

            return true;
        }

        private static object? ConvertValue(string value, string type, string label)
        {
            if (value.StartsWith("json:", StringComparison.Ordinal))
                return LitJson.Parse(value.Substring("json:".Length));

            if ((value.StartsWith("{", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal)) ||
                (value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal)))
            {
                try
                {
                    return LitJson.Parse(value);
                }
                catch
                {
                    // Keep non-JSON strings as strings.
                }
            }

            var normalized = type.TrimEnd('?').ToLowerInvariant();
            if (normalized is "bool" or "boolean")
            {
                if (bool.TryParse(value, out var boolValue)) return boolValue;
                throw new CliCommandException($"{label} expects a bool value.");
            }

            if (normalized is "byte" or "short" or "int")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                    return intValue;
                throw new CliCommandException($"{label} expects an integer value.");
            }

            if (normalized == "long")
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                    return longValue;
                throw new CliCommandException($"{label} expects a long integer value.");
            }

            if (normalized is "float" or "double" or "decimal")
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                    return doubleValue;
                throw new CliCommandException($"{label} expects a number value.");
            }

            return value;
        }

        private static string GenerateToolCall(string path, string methodName, IReadOnlyList<object?> values)
        {
            var args = string.Join(", ", values.Select(value => LitJson.Stringify(value)));
            return "async function execute() {\n" +
                   $"  const tool = await import('tools://{EscapeJavaScriptString(path)}');\n" +
                   $"  const fn = tool['{EscapeJavaScriptString(methodName)}'];\n" +
                   $"  if (typeof fn !== 'function') throw new Error('Tool function not found: {EscapeJavaScriptString(path)}.{EscapeJavaScriptString(methodName)}');\n" +
                   $"  return await fn({args});\n" +
                   "}";
        }

        private static string ExtractEvalCode(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
                throw new CliCommandException("eval-js requires --code, --file, --stdin, or inline code.");

            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--code":
                    case "-c":
                        if (i + 1 >= args.Count) throw new CliCommandException($"{arg} requires a value.");
                        return args[i + 1];
                    case "--file":
                    case "-f":
                        if (i + 1 >= args.Count) throw new CliCommandException($"{arg} requires a value.");
                        return ReadEvalFile(args[i + 1]);
                    case "--stdin":
                        throw new CliCommandException("eval-js --stdin requires the CLI client to send stdin content as --code.");
                }
            }

            return string.Join(" ", args);
        }

        private static string ReadEvalFile(string path)
        {
            var fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(ToolUtilities.GetProjectRoot(), path));
            if (!File.Exists(fullPath))
                throw new CliCommandException($"Eval script file was not found: {path}");
            return File.ReadAllText(fullPath);
        }

        private static string WrapUserScript(string script)
        {
            if (script.Contains("function execute", StringComparison.Ordinal))
                return script;
            return "async function execute() {\n" + script + "\n}";
        }

        private static List<CliTool> ListCliTools(bool refresh)
        {
            var result = new List<CliTool>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();

            var index = EvalToolRegistry.GetIndex(refresh);
            var roots = EvalData.AsArray(index.TryGetValue("tools", out var rootValue) ? rootValue : null) ??
                        new List<object?>();
            foreach (var root in roots)
            {
                var rootObj = EvalData.AsObject(root);
                var path = rootObj == null ? string.Empty : EvalData.GetString(rootObj, "path") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path))
                    queue.Enqueue(path);
            }

            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                if (!seen.Add(path)) continue;
                if (!TryReadTool(path, out var tool, out _)) continue;
                result.Add(tool);
                foreach (var subTool in tool.SubToolPaths)
                    queue.Enqueue(subTool);
            }

            return result;
        }

        private static bool TryReadTool(string name, out CliTool tool, out string error)
        {
            tool = null!;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Tool name is required.";
                return false;
            }

            Dictionary<string, object?> data;
            try
            {
                data = EvalToolRegistry.GetToolDetails(name, false);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            tool = CliTool.FromDetails(name, data);
            return true;
        }

        private static string BuildUsage(CliTool tool, CliFunction function)
        {
            var builder = new StringBuilder();
            builder.Append(tool.CommandName);
            builder.Append(' ');
            builder.Append(function.MethodName);
            foreach (var parameter in function.Parameters)
            {
                builder.Append(' ');
                builder.Append(parameter.Optional ? '[' : '<');
                builder.Append("--");
                builder.Append(ToKebabCase(parameter.Name));
                if (!IsBoolType(parameter.Type))
                {
                    builder.Append(' ');
                    builder.Append(parameter.Type);
                }

                builder.Append(parameter.Optional ? ']' : '>');
            }

            return builder.ToString();
        }

        private static string ExtractContentText(Dictionary<string, object?> result)
        {
            var content = EvalData.AsArray(result.TryGetValue("content", out var raw) ? raw : null);
            if (content == null) return string.Empty;
            var lines = new List<string>();
            foreach (var item in content)
            {
                var obj = EvalData.AsObject(item);
                if (obj == null) continue;
                var text = EvalData.GetString(obj, "text");
                if (!string.IsNullOrEmpty(text) && text != "(no return value)") lines.Add(text);
            }

            return lines.Count == 0 ? "(no output)" : string.Join(Environment.NewLine, lines);
        }

        private static List<string> Tokenize(string line)
        {
            var result = new List<string>();
            var builder = new StringBuilder();
            var inQuote = false;
            var quote = '\0';
            var escaped = false;
            var tokenStarted = false;

            for (var index = 0; index < line.Length; index++)
            {
                var c = line[index];
                if (escaped)
                {
                    builder.Append(c);
                    tokenStarted = true;
                    escaped = false;
                    continue;
                }

                if (c == '\\' && quote != '\'')
                {
                    var hasEscapedCharacter = index + 1 < line.Length &&
                                              (line[index + 1] is '\\' or '"' or '\'' ||
                                               char.IsWhiteSpace(line[index + 1]));
                    if (hasEscapedCharacter)
                        escaped = true;
                    else
                    {
                        builder.Append(c);
                        tokenStarted = true;
                    }
                    continue;
                }

                if (inQuote)
                {
                    if (c == quote)
                    {
                        inQuote = false;
                        tokenStarted = true;
                        continue;
                    }

                    builder.Append(c);
                    tokenStarted = true;
                    continue;
                }

                if (c is '"' or '\'')
                {
                    inQuote = true;
                    quote = c;
                    tokenStarted = true;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    Flush();
                    continue;
                }

                builder.Append(c);
                tokenStarted = true;
            }

            if (inQuote)
                throw new CliCommandException("Unclosed quote.");
            Flush();
            return result;

            void Flush()
            {
                if (!tokenStarted) return;
                result.Add(builder.ToString());
                builder.Clear();
                tokenStarted = false;
            }
        }

        private static bool Is(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private static bool IsHelpToken(string token) =>
            Is(token, "help") || Is(token, "-h") || Is(token, "--help") || Is(token, "-help");

        private static bool IsBoolType(string type) =>
            string.Equals(type.TrimEnd('?'), "bool", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type.TrimEnd('?'), "boolean", StringComparison.OrdinalIgnoreCase);

        private static string ToKebabCase(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var builder = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) builder.Append('-');
                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c == '_' ? '-' : c);
                }
            }

            return builder.ToString();
        }

        private static string FormatDefaultValue(object? value)
        {
            if (value == null) return "null";
            if (value is string text) return string.IsNullOrEmpty(text) ? "\"\"" : text;
            if (value is bool boolValue) return boolValue ? "true" : "false";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }

        private static string EscapeJavaScriptString(string value) =>
            value.Replace("\\", "\\\\").Replace("'", "\\'");

        private static Dictionary<string, object?> Success(string text) =>
            EvalData.Obj(("success", true), ("text", text));

        private static Dictionary<string, object?> Failure(string error) =>
            EvalData.Obj(("success", false), ("text", error));

    }
}
