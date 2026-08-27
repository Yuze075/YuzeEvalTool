using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace YuzeToolkit.UnityEvalTool.Broker;

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsedArguments = ExtractTokenOption(args);
        args = parsedArguments.Arguments;
        var suppliedTokenList = parsedArguments.TokenList;
        var command = args.Length == 0 ? string.Empty : args[0].ToLowerInvariant();
        if (command is "-h" or "--help" or "help")
        {
            Console.WriteLine(HelpText);
            return 0;
        }
        if (command == "service")
        {
            Console.WriteLine(await UserServiceManager.ExecuteAsync(args.Length > 1 ? args[1] : "status",
                cancellationToken));
            return 0;
        }
        if (command == "doctor") return await DoctorAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(suppliedTokenList, cancellationToken);
        if (command is "list" or "status")
        {
            PrintRegistry(await connection.ListAsync(cancellationToken));
            return 0;
        }

        string selector;
        IReadOnlyList<string> unityCommand;
        var enterConsole = args.Length == 0;
        if (command == "connect")
        {
            if (args.Length < 2) throw new InvalidOperationException("unity connect requires an instance id, prefix, or project path.");
            selector = args[1];
            var separator = Array.IndexOf(args, "--", 2);
            unityCommand = separator >= 0 ? args.Skip(separator + 1).ToArray() : Array.Empty<string>();
            enterConsole = unityCommand.Count == 0;
        }
        else
        {
            selector = string.Empty;
            unityCommand = args;
        }

        var registry = await connection.ListAsync(cancellationToken);
        var instance = ResolveInstance(registry, selector);
        var revision = registry.GetProperty("registryRevision").GetInt64();
        await connection.ConnectUnityAsync(instance.GetProperty("instanceId").GetString()!, revision, cancellationToken);
        if (!enterConsole)
        {
            await WaitUntilExecutableAsync(connection, cancellationToken);
            PrintCliResult(await connection.ExecuteAsync(RebuildCommandLine(unityCommand), cancellationToken));
            return 0;
        }

        await RunConsoleAsync(connection, instance, cancellationToken);
        return 0;
    }

    private static async Task<BrokerCliConnection> OpenConnectionAsync(string? tokenList,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var connection = new BrokerCliConnection();
            try
            {
                await connection.ConnectAsync(tokenList, cancellationToken);
                return connection;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await connection.DisposeAsync();
                if (attempt == 0) BrokerProcessUtility.StartDetachedBroker();
                await Task.Delay(100, cancellationToken);
            }
        }
        throw new BrokerOperationException(BrokerErrorCodes.BrokerUnavailable,
            $"Unable to connect to the local Broker on port {BrokerConstants.Port}: {lastError?.Message}");
    }

    private static async Task RunConsoleAsync(BrokerCliConnection connection, JsonElement initialInstance,
        CancellationToken cancellationToken)
    {
        var projectName = initialInstance.GetProperty("projectName").GetString() ?? "Unity";
        long lastVmGeneration = -1;
        Console.WriteLine($"Connected to {projectName}. Broker commands: :status, :wait, :switch, :help, :quit");
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = await connection.StatusAsync("snapshot", 0, cancellationToken);
            var selected = status.GetProperty("selectedUnity");
            var phase = selected.ValueKind == JsonValueKind.Null
                ? "Disconnected"
                : selected.GetProperty("status").GetProperty("phase").GetString() ?? "Unknown";
            if (selected.ValueKind != JsonValueKind.Null)
            {
                var generation = selected.GetProperty("status").GetProperty("vmGeneration").GetInt64();
                if (lastVmGeneration >= 0 && generation != lastVmGeneration)
                    Console.WriteLine($"[Unity reconnected; PuerTS VM generation changed {lastVmGeneration} -> {generation}]");
                lastVmGeneration = generation;
                projectName = selected.GetProperty("projectName").GetString() ?? projectName;
            }
            Console.Write($"unity[{projectName}|{phase}]> ");
            var line = Console.ReadLine();
            if (line == null || line is ":quit" or ":exit" or ":disconnect") return;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                switch (line.Trim())
                {
                    case ":help":
                        Console.WriteLine("Broker commands: :status, :wait, :switch, :help, :quit. All other input is parsed by Unity's existing CLI command service.");
                        continue;
                    case ":status":
                        PrintRegistry(status);
                        continue;
                    case ":wait":
                        PrintRegistry(await connection.StatusAsync("ready", 600, cancellationToken));
                        continue;
                    case ":switch":
                        var registry = await connection.ListAsync(cancellationToken);
                        PrintRegistry(registry);
                        Console.Write("instance> ");
                        var selector = Console.ReadLine() ?? string.Empty;
                        var target = ResolveInstance(registry, selector);
                        await connection.ConnectUnityAsync(target.GetProperty("instanceId").GetString()!,
                            registry.GetProperty("registryRevision").GetInt64(), cancellationToken);
                        projectName = target.GetProperty("projectName").GetString() ?? projectName;
                        lastVmGeneration = -1;
                        continue;
                }

                await WaitUntilExecutableAsync(connection, cancellationToken);
                PrintCliResult(await connection.ExecuteAsync(line, cancellationToken));
            }
            catch (BrokerOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }
    }

    private static async Task WaitUntilExecutableAsync(BrokerCliConnection connection,
        CancellationToken cancellationToken)
    {
        var snapshot = await connection.StatusAsync("snapshot", 0, cancellationToken);
        var selected = snapshot.GetProperty("selectedUnity");
        if (selected.ValueKind == JsonValueKind.Null)
            throw new BrokerOperationException(BrokerErrorCodes.UnityDisconnected, "Selected Unity is unavailable.");
        var authorizationState = selected.TryGetProperty("authorizationState", out var authorizationElement)
            ? authorizationElement.GetString() ?? "NotRequired"
            : "NotRequired";
        if (string.Equals(authorizationState, "Pending", StringComparison.Ordinal))
            throw new BrokerOperationException(BrokerErrorCodes.UnityAuthorizationPending,
                "Unity is connected but its project token has not been verified. Supply --token or update auth.json.");
        var status = selected.GetProperty("status");
        if (CanExecute(status)) return;
        var phase = status.GetProperty("phase").GetString() ?? "Unknown";
        Console.WriteLine($"[Waiting for Unity: {phase}]");
        var terminal = await connection.StatusAsync("ready", 600, cancellationToken);
        var terminalSelected = terminal.GetProperty("selectedUnity");
        if (terminalSelected.ValueKind == JsonValueKind.Null ||
            !CanExecute(terminalSelected.GetProperty("status")))
            throw new BrokerOperationException(BrokerErrorCodes.UnityBusy,
                "Unity stopped waiting without reaching an executable state.");
    }

    private static bool CanExecute(JsonElement status) =>
        status.GetProperty("canEval").GetBoolean() ||
        string.Equals(status.GetProperty("phase").GetString(), "CompilationFailed", StringComparison.Ordinal);

    private static JsonElement ResolveInstance(JsonElement registry, string selector)
    {
        var instances = registry.GetProperty("unityInstances").EnumerateArray()
            .Where(item => item.GetProperty("isConnected").GetBoolean()).Select(item => item.Clone()).ToList();
        if (instances.Count == 0)
            throw new BrokerOperationException(BrokerErrorCodes.UnityNotFound, "No connected Unity instances were found.");
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var normalizedSelector = TryNormalizePath(selector);
            var matches = instances.Where(instance =>
                string.Equals(instance.GetProperty("instanceId").GetString(), selector, StringComparison.Ordinal) ||
                (instance.GetProperty("instanceId").GetString()?.StartsWith(selector, StringComparison.Ordinal) ?? false) ||
                string.Equals(TryNormalizePath(instance.GetProperty("projectPath").GetString() ?? string.Empty),
                    normalizedSelector, PathComparison)).ToList();
            return matches.Count switch
            {
                1 => matches[0],
                0 => throw new BrokerOperationException(BrokerErrorCodes.UnityNotFound,
                    $"No connected Unity matched '{selector}'."),
                _ => throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                    $"Selector '{selector}' matched multiple Unity instances; use the full instanceId.")
            };
        }

        var current = TryNormalizePath(Directory.GetCurrentDirectory());
        var pathMatches = instances.Where(instance => IsUnderPath(current,
                TryNormalizePath(instance.GetProperty("projectPath").GetString() ?? string.Empty)))
            .OrderByDescending(instance => instance.GetProperty("projectPath").GetString()?.Length ?? 0).ToList();
        if (pathMatches.Count > 0) return pathMatches[0];
        if (instances.Count == 1) return instances[0];
        throw new BrokerOperationException(BrokerErrorCodes.DiscoveryRequired,
            "Multiple Unity instances are connected and the current directory does not identify one. Use `unity list` then `unity connect <instanceId>`. ");
    }

    private static void PrintRegistry(JsonElement registry)
    {
        Console.WriteLine($"Registry revision: {registry.GetProperty("registryRevision").GetInt64()}");
        foreach (var instance in registry.GetProperty("unityInstances").EnumerateArray())
        {
            var status = instance.GetProperty("status");
            Console.WriteLine($"{instance.GetProperty("instanceId").GetString()}  " +
                              $"{instance.GetProperty("projectName").GetString()}  " +
                              $"{(instance.TryGetProperty("authorizationState", out var auth) ? auth.GetString() : "NotRequired")}  " +
                              $"{status.GetProperty("phase").GetString()}  " +
                              $"PID {instance.GetProperty("processId").GetInt32()}  " +
                              instance.GetProperty("projectPath").GetString());
        }
        if (registry.TryGetProperty("selectedUnity", out var selected) && selected.ValueKind != JsonValueKind.Null)
            Console.WriteLine("Selected: " + selected.GetProperty("instanceId").GetString());
    }

    private static void PrintCliResult(JsonElement result)
    {
        if (result.TryGetProperty("text", out var text) && !string.IsNullOrEmpty(text.GetString()))
            Console.WriteLine(text.GetString());
        else
            Console.WriteLine(result.GetRawText());
        if (result.TryGetProperty("success", out var success) && !success.GetBoolean())
            throw new InvalidOperationException(text.GetString() ?? "Unity CLI command failed.");
    }

    private static async Task<int> DoctorAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Executable: " + (InstallMetadataStore.GetCurrentExecutable() ?? "unpublished/dotnet host"));
        var tokenStore = new AuthTokenStore();
        Console.WriteLine($"Auth file: {tokenStore.FilePath} ({tokenStore.GetTokens().Count}/{tokenStore.MaxStoredTokens} stored tokens)");
        Console.WriteLine("Auth config: " + tokenStore.ConfigPath);
        Console.WriteLine("Broker endpoint: http://127.0.0.1:2347");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var health = await client.GetStringAsync($"http://{BrokerConstants.Host}:{BrokerConstants.Port}/health",
                cancellationToken);
            Console.WriteLine("Broker: " + health);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("BrokerUnavailable: " + ex.Message);
            return 3;
        }
    }

    private static string RebuildCommandLine(IReadOnlyList<string> args) => string.Join(" ", args.Select(argument =>
        string.IsNullOrEmpty(argument) || argument.Any(char.IsWhiteSpace) || argument.Contains('"') ||
        argument.Contains('\'') || argument.Contains('\\')
            ? "\"" + argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : argument));

    private static (string? TokenList, string[] Arguments) ExtractTokenOption(IReadOnlyList<string> args)
    {
        string? tokenList = null;
        var remaining = new List<string>(args.Count);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--token", StringComparison.Ordinal))
            {
                if (tokenList != null)
                    throw new InvalidOperationException("--token can only be supplied once.");
                if (++index >= args.Count)
                    throw new InvalidOperationException("--token requires a value.");
                tokenList = args[index];
                continue;
            }
            if (argument.StartsWith("--token=", StringComparison.Ordinal))
            {
                if (tokenList != null)
                    throw new InvalidOperationException("--token can only be supplied once.");
                tokenList = argument["--token=".Length..];
                if (tokenList.Length == 0)
                    throw new InvalidOperationException("--token requires a value.");
                continue;
            }
            remaining.Add(argument);
        }
        return (tokenList, remaining.ToArray());
    }

    private static string TryNormalizePath(string value)
    {
        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return value; }
    }

    private static bool IsUnderPath(string path, string parent) =>
        string.Equals(path, parent, PathComparison) ||
        path.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison) ||
        path.StartsWith(parent + Path.AltDirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private const string HelpText = """
Yuze Eval Tool Broker CLI

  unity --token <token[/token...]> [command]
                                Persist one or more Unity project tokens before continuing.
  unity                         Auto-select Unity for the current directory and enter its console.
  unity list                    List registered Unity instances and states.
  unity status                  Alias for list.
  unity connect <instance>      Select by id/prefix/project path and enter a console.
  unity connect <instance> -- <command...>
                                Execute one Unity-side CLI command.
  unity <command...>            Auto-select by current directory and execute once.
  unity doctor                  Diagnose executable, optional auth file, port and Broker health.
  unity service <action>        install|uninstall|start|stop|restart|status.
  unity broker                  Run the foreground Broker host on 127.0.0.1:2347.

Inside a console, :status, :wait, :switch, :help and :quit are Broker commands.
Every other line is parsed inside Unity by the existing EvalCliCommandService.
""";
}
