#nullable enable
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace YuzeToolkit.Eval.Broker;

// The Broker never starts desktop input in its service session. A separately launched
// current-user worker owns the driver, observation cache and serial desktop action stream.
internal sealed class WorkspaceDesktop(WorkspaceOptions options, BrokerRegistry registry)
{
    public Task<JsonObject> ObserveAsync(string session, WorkspaceContext context, JsonObject args, CancellationToken token)
    {
        var request = (JsonObject)args.DeepClone();
        if (request["processId"] == null)
        {
            var candidates = registry.GetSnapshot().UnityInstances.Where(u => u.Source.Kind == "local" && u.IsConnected && WorkspaceFiles.SamePath(u.ProjectPath, context.Files!.Root)).ToArray();
            if (candidates.Length != 1) throw WorkspaceFiles.Error("DesktopTargetRequired", "Supply an exact processId; this workspace does not have one unambiguous registered Unity process.");
            request["processId"] = candidates[0].ProcessId;
        }
        request["owner"] = session + ":" + context.Handle; request["operation"] = "observe";
        return SendAsync(request, false, token);
    }

    public Task<JsonObject> ActAsync(string session, WorkspaceContext context, JsonObject args, CancellationToken token)
    {
        var request = (JsonObject)args.DeepClone(); request["owner"] = session + ":" + context.Handle; request["operation"] = "act";
        return SendAsync(request, true, token);
    }

    private async Task<JsonObject> SendAsync(JsonObject request, bool mutation, CancellationToken token)
    {
        if (options.DesktopDriverPath == null) throw WorkspaceFiles.Error("DesktopDriverUnavailable", "Configure the desktop driver and run `unity desktop worker` in the logged-in graphical user session.");
        using var pipe = new NamedPipeClientStream(".", options.DesktopPipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | (OperatingSystem.IsWindows() ? PipeOptions.None : PipeOptions.CurrentUserOnly));
        try { await pipe.ConnectAsync(2000, token); }
        catch (TimeoutException) { throw WorkspaceFiles.Error("DesktopSessionUnavailable", "No same-user desktop worker is listening. A noninteractive Broker service cannot operate the desktop."); }
        if (OperatingSystem.IsWindows())
        {
            // .NET 10 CurrentUserOnly compares TokenOwner, which differs between elevated
            // service and ordinary GUI logons. The worker explicitly owns its pipe as TokenUser.
            using var identity = WindowsIdentity.GetCurrent();
            if (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) != identity.User)
                throw WorkspaceFiles.Error("DesktopSessionUnavailable", "The desktop pipe is not owned by this Windows user.");
        }
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var sent = false;
        try
        {
            sent = true;
            await writer.WriteLineAsync(request.ToJsonString().AsMemory(), token);
            var line = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(options.MaxDesktopSequenceMilliseconds + options.DesktopDriverTimeoutSeconds * 3000), token);
            return line == null ? throw new IOException("Desktop worker disconnected before returning its result.") : JsonNode.Parse(line)!.AsObject();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            if (mutation && sent) throw new BrokerOperationException("ExecutionOutcomeUnknown", "Desktop action was dispatched but its result was not received. Inspect the window before retrying.", true);
            throw WorkspaceFiles.Error("DesktopUnavailable", "Desktop observation did not complete.");
        }
    }
}

internal static class WorkspaceDesktopWorker
{
    private sealed record Observation(string Owner, int ProcessId, DateTime ProcessStarted, long WindowId, DateTimeOffset CapturedAt, JsonObject State);
    private static readonly HashSet<string> Actions = ["click", "double_click", "right_click", "type_text", "press_key", "hotkey", "scroll", "drag"];

    public static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length == 0 || args[0] != "worker") throw new InvalidOperationException("Usage: unity desktop worker [--config <path>]. Run in the logged-in graphical user session, after granting the desktop driver its OS permissions.");
        var index = Array.IndexOf(args, "--config");
        var config = BrokerConfiguration.Load(index >= 0 && index + 1 < args.Length ? args[index + 1] : BrokerConfiguration.DefaultPath);
        var options = config.Workspace;
        if (options.DesktopDriverPath == null || !File.Exists(options.DesktopDriverPath)) throw new InvalidOperationException("workspace.desktopDriverPath must identify the installed Cua Driver executable.");
        if (OperatingSystem.IsWindows() && Process.GetCurrentProcess().SessionId == 0)
            throw new InvalidOperationException("DesktopSessionUnavailable: start the worker in the logged-in user's interactive session, not session 0.");
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            throw new InvalidOperationException("DesktopSessionUnavailable: no X11 or Wayland display is available.");
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        { Command = options.DesktopDriverPath, Arguments = options.DesktopDriverArguments, Name = "YuzeEvalTool.DesktopDriver" }),
            new McpClientOptions { ProtocolVersion = "2025-06-18", ClientInfo = new Implementation { Name = "YuzeEvalTool.DesktopWorker", Version = BrokerConstants.PackageVersion } }, cancellationToken: token);
        if (client.ServerInfo.Version != "0.38.0")
            throw new InvalidOperationException("DesktopDriverIncompatible: this adapter requires Cua Driver 0.38.0. Validate another version before changing this contract.");
        var observations = new Dictionary<string, Observation>(StringComparer.Ordinal);
        Console.WriteLine("Desktop worker ready; same-user IPC, background default, foreground only by explicit per-call selection, no persistent held input.");
        while (!token.IsCancellationRequested)
        {
            using var pipe = CreatePipe(options.DesktopPipeName);
            await pipe.WaitForConnectionAsync(token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var dispatched = false;
            try
            {
                var line = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
                if (line == null || line.Length > options.MaxTextCharacters) throw WorkspaceFiles.Error("InvalidRequest", "Invalid or oversized desktop IPC request.");
                var request = JsonNode.Parse(line)!.AsObject();
                foreach (var pair in observations.Where(pair => DateTimeOffset.UtcNow - pair.Value.CapturedAt > TimeSpan.FromSeconds(options.DesktopObservationSeconds)).ToArray()) observations.Remove(pair.Key);
                var owner = WorkspaceService.Required(request, "owner");
                // Each context has its own driver snapshot namespace as well as its own Broker handle.
                owner = WorkspaceFiles.Version(Encoding.UTF8.GetBytes(owner));
                JsonObject response;
                if (WorkspaceService.Required(request, "operation") == "observe")
                {
                    var pid = WorkspaceService.Number(request, "processId");
                    using var process = Process.GetProcessById(pid);
                    var window = request["windowId"]?.GetValue<long>() ?? 0;
                    if (OperatingSystem.IsWindows() && process.SessionId != Process.GetCurrentProcess().SessionId)
                        throw WorkspaceFiles.Error("DesktopSessionUnavailable", "The target process does not belong to this interactive user session.");
                    if (window == 0)
                    {
                        var listed = await DriverCallAsync(client, "list_windows", new JsonObject { ["pid"] = pid, ["session"] = owner }, options, token);
                        response = listed["isError"]?.GetValue<bool>() == true ? listed : WorkspaceTools.Result(new JsonObject
                        { ["processId"] = pid, ["driverState"] = listed["structuredContent"]?.DeepClone(), ["mode"] = "background" }, listed["content"]?.DeepClone().AsArray());
                    }
                    else response = await ObserveAsync(client, observations, owner, pid, process.StartTime.ToUniversalTime(), window, options, token);
                }
                else
                {
                    var deliveryMode = WorkspaceService.Text(request, "deliveryMode", "background");
                    if (deliveryMode is not ("background" or "foreground"))
                        throw WorkspaceFiles.Error("InvalidRequest", "deliveryMode must be background or foreground. Foreground requires the caller to obtain explicit user permission for this host/window first.");
                    var id = WorkspaceService.Required(request, "observationId");
                    if (!observations.Remove(id, out var observation) || observation.Owner != owner)
                        throw WorkspaceFiles.Error("ObservationExpired", "The observation is expired, used, or owned by another context. Observe again.");
                    using var process = Process.GetProcessById(observation.ProcessId);
                    if (process.StartTime.ToUniversalTime() != observation.ProcessStarted) throw WorkspaceFiles.Error("DesktopTargetChanged", "The observed process was replaced.");
                    var actions = request["actions"]?.AsArray() ?? throw WorkspaceFiles.Error("InvalidRequest", "actions is required.");
                    if (actions.Count is < 1 or > 10) throw WorkspaceFiles.Error("InvalidRequest", "Desktop sequences require 1–10 bounded actions.");
                    var pixelActions = 0;
                    var needsGeometryCheck = false;
                    var durationBudget = 0;
                    // Validate target and execution bounds for the entire sequence before dispatch.
                    foreach (var action in actions)
                    {
                        var command = action?.AsObject() ?? throw WorkspaceFiles.Error("InvalidRequest", "Invalid desktop action.");
                        var actionName = WorkspaceService.Required(command, "tool");
                        if (!Actions.Contains(actionName)) throw WorkspaceFiles.Error("UnsupportedDesktopAction", "Only bounded window actions are allowed; global and held input are excluded.");
                        var parameters = command["arguments"]?.AsObject() ?? throw WorkspaceFiles.Error("InvalidRequest", "Action arguments are required.");
                        if (parameters.ContainsKey("pid") || parameters.ContainsKey("window_id") || parameters.ContainsKey("session") || parameters.ContainsKey("scope") || parameters.ContainsKey("target") || parameters.ContainsKey("from_zoom") || parameters.ContainsKey("debug_image_out") ||
                            parameters.ContainsKey("delivery_mode") && WorkspaceService.Text(parameters, "delivery_mode") != deliveryMode)
                            throw WorkspaceFiles.Error("InvalidDesktopTarget", "Actions must use the observed target and the call's deliveryMode; a nested argument cannot override either.");
                        if (parameters["text"]?.GetValue<string>() is { Length: > 1024 }) throw WorkspaceFiles.Error("InvalidRequest", "Desktop text is bounded to 1024 characters per action.");
                        if (parameters["count"]?.GetValue<int>() is < 1 or > 3) throw WorkspaceFiles.Error("InvalidRequest", "Click count must be 1–3.");
                        if (parameters["amount"]?.GetValue<int>() is < 1 or > 50 || parameters["steps"]?.GetValue<int>() is < 1 or > 200)
                            throw WorkspaceFiles.Error("InvalidRequest", "Scroll amount or drag steps exceed the bounded driver contract.");
                        foreach (var parameter in parameters.Where(pair => pair.Key.Contains("duration", StringComparison.Ordinal) || pair.Key.Contains("delay", StringComparison.Ordinal) || pair.Key.Contains("timeout", StringComparison.Ordinal)))
                            if (parameter.Value?.GetValue<int>() is not { } duration || duration < 0 || duration > options.MaxDesktopSequenceMilliseconds)
                                throw WorkspaceFiles.Error("InvalidRequest", "Action duration exceeds the sequence budget.");
                        durationBudget += actionName == "drag" ? WorkspaceService.Number(parameters, "duration_ms", 500) : 0;
                        if (durationBudget > options.MaxDesktopSequenceMilliseconds) throw WorkspaceFiles.Error("InvalidRequest", "The sequence's total requested duration exceeds its budget.");
                        if (parameters.ContainsKey("x") || parameters.ContainsKey("from_x"))
                        {
                            // Driver 0.38.0's macOS pixel route can temporarily activate the target
                            // even in background mode. Reject before dispatch instead of promising
                            // focus preservation or silently using foreground input.
                            if (OperatingSystem.IsMacOS() && deliveryMode == "background")
                                throw WorkspaceFiles.Error("ForegroundRequired", "Cua Driver 0.38.0 cannot guarantee focus-preserving macOS pixel input. No action was sent. Obtain explicit user permission, then make a new call with deliveryMode=foreground.");
                            pixelActions++;
                            if (pixelActions > 1 || actions[0] != action) throw WorkspaceFiles.Error("ObservationRequired", "A sequence may begin with one pixel action. Observe again before another pixel action.");
                            var capture = FindString(observation.State["structuredContent"], "capture_id");
                            if (capture == null) throw WorkspaceFiles.Error("ObservationRequired", "The driver did not provide a capture identity; use an observed accessibility element.");
                            if (parameters["capture_id"] != null && parameters["capture_id"]!.GetValue<string>() != capture)
                                throw WorkspaceFiles.Error("ObservationExpired", "The action's capture does not belong to this observation.");
                            if (actionName == "click") parameters["capture_id"] = capture;
                            else
                            {
                                if (parameters.ContainsKey("capture_id")) throw WorkspaceFiles.Error("InvalidRequest", "This driver action does not accept capture_id.");
                                needsGeometryCheck = true;
                            }
                        }
                        if (parameters["element_token"]?.GetValue<string>() is { } element &&
                            !(element.StartsWith((FindString(observation.State["structuredContent"], "snapshot_id") ?? "invalid") + ":", StringComparison.Ordinal)))
                            throw WorkspaceFiles.Error("ObservationExpired", "The element does not belong to this observation.");
                    }
                    if (needsGeometryCheck)
                    {
                        // Non-click driver tools lack an atomic capture_id contract. Recheck their
                        // exact target geometry without replacing this context's accessibility tokens.
                        var current = await DriverCallAsync(client, "get_window_state", new JsonObject
                        { ["pid"] = observation.ProcessId, ["window_id"] = observation.WindowId, ["session"] = owner,
                            ["include_accessibility_tree"] = false, ["display_only"] = true, ["full_output"] = true,
                            ["max_image_dimension"] = options.DesktopMaxImageDimension }, options, token);
                        if (current["isError"]?.GetValue<bool>() == true) throw WorkspaceFiles.Error("ObservationExpired", "The observed window could not be checked again.");
                        foreach (var key in new[] { "window_bounds", "screenshot_width", "screenshot_height", "screenshot_scale" })
                        {
                            var before = Find(observation.State["structuredContent"], key);
                            var after = Find(current["structuredContent"], key);
                            if (key != "screenshot_scale" && (before == null || after == null) || !JsonNode.DeepEquals(before, after))
                                throw WorkspaceFiles.Error("DesktopTargetChanged", "The window position, size or screenshot scale changed. Observe again.");
                        }
                    }
                    // Activation or input can invalidate another context's view of the desktop.
                    observations.Clear();
                    if (deliveryMode == "foreground")
                    {
                        // Per-action foreground delivery restores its previous app. Establish the
                        // exact target once so a click followed by typing keeps the same focus.
                        dispatched = true;
                        var activated = await DriverCallAsync(client, "bring_to_front", new JsonObject
                        { ["pid"] = observation.ProcessId, ["window_id"] = observation.WindowId, ["session"] = owner }, options, token);
                        if (activated["isError"]?.GetValue<bool>() == true || Find(activated["structuredContent"], "activated")?.GetValue<bool>() != true)
                            throw WorkspaceFiles.Error("ForegroundUnavailable", "The driver did not verify activation of the observed window. No sequence action was sent; inspect the foreground state before retrying.");
                    }
                    var deadline = DateTimeOffset.UtcNow.AddMilliseconds(options.MaxDesktopSequenceMilliseconds);
                    var completed = new JsonArray();
                    foreach (var action in actions.OfType<JsonObject>())
                    {
                        if (DateTimeOffset.UtcNow >= deadline) throw new BrokerOperationException("ExecutionOutcomeUnknown", "The sequence deadline elapsed after earlier actions. Inspect state; do not replay the sequence.", completed.Count != 0);
                        var parameters = (JsonObject)action["arguments"]!.DeepClone();
                        parameters["pid"] = observation.ProcessId; parameters["window_id"] = observation.WindowId;
                        parameters["delivery_mode"] = deliveryMode;
                        parameters["session"] = owner;
                        dispatched = true;
                        var result = await DriverCallAsync(client, WorkspaceService.Required(action, "tool"), parameters, options, token);
                        completed.Add((JsonNode)result);
                        if (result["isError"]?.GetValue<bool>() == true) break;
                    }
                    response = await ObserveAsync(client, observations, owner, observation.ProcessId, observation.ProcessStarted, observation.WindowId, options, token);
                    if (response["structuredContent"]?["result"] is JsonObject details)
                    {
                        details["actionResults"] = completed;
                        details["deliveryMode"] = deliveryMode;
                        details["sequenceCompleted"] = completed.Count == actions.Count && !completed.OfType<JsonObject>().Any(item => item["isError"]?.GetValue<bool>() == true);
                        response["content"]![0]!["text"] = details.ToJsonString();
                        response["isError"] = !details["sequenceCompleted"]!.GetValue<bool>();
                    }
                    else throw new BrokerOperationException("ExecutionOutcomeUnknown", "Actions were sent but the final window observation failed. Inspect before any retry.", true);
                }
                await writer.WriteLineAsync(response.ToJsonString().AsMemory(), token);
            }
            catch (Exception ex) when (ex is BrokerOperationException or IOException or TimeoutException or JsonException or ArgumentException or InvalidOperationException or ModelContextProtocol.McpException or System.ComponentModel.Win32Exception or FormatException or OverflowException)
            {
                var error = ex as BrokerOperationException;
                try { await writer.WriteLineAsync(WorkspaceTools.Failure(dispatched ? "ExecutionOutcomeUnknown" : error?.Code ?? "DesktopDriverFailure", ex.Message, dispatched || error?.MayHaveExecuted == true).ToJsonString().AsMemory(), token); }
                catch (IOException) { /* Disconnected caller must treat dispatched mutations as unknown. */ }
                // A timed-out driver request may still be running. End this worker instead of
                // accepting another action concurrently with an unconfirmed input operation.
                if (ex is TimeoutException) return 1;
            }
        }
        return 0;
    }

    private static NamedPipeServerStream CreatePipe(string name)
    {
        if (!OperatingSystem.IsWindows()) return new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Desktop worker has no Windows user SID.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security, HandleInheritability.None);
    }

    private static async Task<JsonObject> ObserveAsync(McpClient client, Dictionary<string, Observation> observations,
        string owner, int pid, DateTime started, long window, WorkspaceOptions options, CancellationToken token)
    {
        var result = await DriverCallAsync(client, "get_window_state", new JsonObject
        { ["pid"] = pid, ["window_id"] = window, ["session"] = owner, ["full_output"] = true,
            ["max_elements"] = 250, ["max_image_dimension"] = options.DesktopMaxImageDimension }, options, token);
        if (result["isError"]?.GetValue<bool>() == true) return result;
        foreach (var pair in observations.Where(pair => pair.Value.Owner == owner).ToArray()) observations.Remove(pair.Key);
        var id = Guid.NewGuid().ToString("N");
        // Retain only the driver's small structured identity/geometry, not another image copy.
        observations[id] = new Observation(owner, pid, started, window, DateTimeOffset.UtcNow,
            new JsonObject { ["structuredContent"] = result["structuredContent"]?.DeepClone() });
        return WorkspaceTools.Result(new JsonObject
        {
            ["observationId"] = id, ["processId"] = pid, ["processStartedAtUtc"] = started.ToString("O"), ["windowId"] = window,
            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["driverState"] = result["structuredContent"]?.DeepClone(),
            ["mode"] = "background", ["inputPolicy"] = "Bounded actions only; no persistent held keys or buttons. Inspect the returned image/state to verify effects."
        }, result["content"]?.DeepClone().AsArray());
    }

    private static async Task<JsonObject> DriverCallAsync(McpClient client, string tool, JsonObject args, WorkspaceOptions options, CancellationToken token)
    {
        var parameters = args.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value, BrokerJsonContext.Default.JsonNode));
        var result = await client.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = parameters }, token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(options.DesktopDriverTimeoutSeconds), token);
        return JsonSerializer.SerializeToNode(result, BrokerJsonContext.Default.CallToolResult)!.AsObject();
    }

    private static string? FindString(JsonNode? node, string key) => Find(node, key)?.GetValue<string>();

    private static JsonNode? Find(JsonNode? node, string key)
    {
        if (node is not JsonObject obj) return null;
        if (obj[key] is { } value) return value;
        foreach (var child in obj.Select(pair => pair.Value))
            if (Find(child, key) is { } found) return found;
        return null;
    }
}
