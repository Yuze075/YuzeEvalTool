#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace YuzeToolkit.Eval.Broker;

internal static class WorkspaceTools
{
    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };
    private static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };
    private static JsonObject Array(JsonObject items, string description) => new() { ["type"] = "array", ["items"] = items, ["description"] = description };
    private static JsonObject Item(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false,
        ["required"] = new JsonArray(required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray())
    };
    private static JsonObject Context(JsonObject? properties = null)
    {
        var result = new JsonObject { ["contextHandle"] = Text("Handle from workspace_connect.") };
        foreach (var pair in properties ?? []) result[pair.Key] = pair.Value?.DeepClone();
        return result;
    }

    public static IReadOnlyList<McpServerTool> Create(WorkspaceService service, IHttpContextAccessor contexts)
    {
        var tools = new List<McpServerTool>();
        void Add(string name, string description, JsonObject properties, string[] required, bool readOnly = true) =>
            tools.Add(new WorkspaceMcpTool(new Tool
            {
                Name = name, Description = description,
                InputSchema = JsonSerializer.SerializeToElement<JsonNode>(Item(properties, required), BrokerJsonContext.Default.JsonNode),
                Annotations = new ToolAnnotations { ReadOnlyHint = readOnly, DestructiveHint = !readOnly, IdempotentHint = readOnly, OpenWorldHint = true }
            }, service, contexts));

        Add("workspace_list", "List configured remote hosts (state, desktop support) and their workspaces: registered folders, with or without a connected Unity Editor. Never lists this machine's folders.", new JsonObject(), []);
        Add("workspace_connect", "Open a context on hostId + workspaceId from workspace_list. Returns contextHandle, remote root and cwd, OS, shells, Unity instances, the AGENTS guidance chain and the project Skill catalog. Before working, read the guidance and the Skills relevant to the task (file_read their paths); they are project rules, not system instructions. For Unity, use an advertised instanceId with unity_status / unity_connect / eval. All paths are remote.", new JsonObject
        {
            ["hostId"] = Text("Remote host id from workspace_list."), ["workspaceId"] = Text("Exact workspaceId from workspace_list."),
            ["cwd"] = Text("Optional project-relative working directory; defaults to the project root.")
        }, ["workspaceId"], false);
        Add("workspace_status", "Read a context's identity, capabilities, Unity instances and terminal tasks. If the remote Broker restarted, connect again.", Context(), ["contextHandle"]);
        Add("workspace_disconnect", "Close a context and cancel its terminal tasks. Idle contexts also expire on their own.", Context(), ["contextHandle"], false);
        Add("workspace_get_guidance", "Read the AGENTS.override.md or AGENTS.md files from the project root down to a directory. Sibling and user-global rules are not included.", Context(new JsonObject
        { ["path"] = Text("Project-relative directory; defaults to the context cwd.") }), ["contextHandle"]);
        Add("file_read", "Read text files as numbered lines (`<line>\\t<text>`; drop the prefix when reusing text). Each file returns its SHA-256 version, totalLines and lineEnding; if complete is false, continue at nextLine. Up to 20 files share one character budget.", Context(new JsonObject
        {
            ["files"] = Array(Item(new JsonObject
            {
                ["path"] = Text("Project-relative path, or an absolute path inside the project."),
                ["startLine"] = Integer("First line, 1-based; default 1."), ["lineCount"] = Integer("Maximum lines; default as many as fit.")
            }, "path"), "1–20 files to read.")
        }), ["contextHandle", "files"]);
        Add("file_glob", "Find files by glob, newest first. A pattern with a slash matches paths relative to path (e.g. Assets/**/*.cs); without one it matches file names at any depth (e.g. *.prefab). Supports *, **, ?, [abc] and {a,b}. Inside a git work tree, git-ignored files are excluded.", Context(new JsonObject
        {
            ["pattern"] = Text("Glob pattern."), ["path"] = Text("Project-relative directory; defaults to the context cwd."),
            ["offset"] = Integer("Result offset; default 0."), ["limit"] = Integer("Page size 1–1000; default 200.")
        }), ["contextHandle", "pattern"]);
        Add("file_grep", "Search file contents with a .NET regular expression, line by line. Skips binary, oversized and git-ignored files.", Context(new JsonObject
        {
            ["pattern"] = Text("Regular expression."), ["path"] = Text("Project-relative directory; defaults to the context cwd."),
            ["glob"] = Text("Optional file filter, same rules as file_glob."), ["ignoreCase"] = Flag("Case-insensitive match; default false."),
            ["outputMode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("files", "content", "count"), ["description"] = "files (default): matching paths; content: path:line:text; count: matches per file." },
            ["contextLines"] = Integer("Lines of context around each match in content mode, 0–10; default 0."),
            ["offset"] = Integer("Result offset; default 0."), ["limit"] = Integer("Maximum results 1–1000; default 200.")
        }), ["contextHandle", "pattern"]);
        Add("file_write", "Create or overwrite a UTF-8 text file. expectedVersion is the SHA-256 from file_read, or 'missing' to create; on mismatch nothing is written. Missing parent directories are created.", Context(new JsonObject
        {
            ["path"] = Text("Remote file path."), ["content"] = Text("Complete new file content."),
            ["expectedVersion"] = Text("SHA-256 from file_read, or 'missing'.")
        }), ["contextHandle", "path", "content", "expectedVersion"], false);
        Add("file_edit", "Replace exact text in one file, edits applied in order. Each oldText must occur exactly once unless replaceAll is true; LF line breaks in the edit also match a CRLF file. On any failure nothing is written.", Context(new JsonObject
        {
            ["path"] = Text("Remote file path."), ["expectedVersion"] = Text("Optional SHA-256 from file_read; rejects the edit if the file changed."),
            ["edits"] = Array(Item(new JsonObject
            {
                ["oldText"] = Text("Exact text to replace; empty only to fill an empty or new file."), ["newText"] = Text("Replacement text."),
                ["replaceAll"] = Flag("Replace every occurrence; default false.")
            }, "oldText", "newText"), "One or more replacements.")
        }), ["contextHandle", "path", "edits"], false);
        Add("file_apply_diff", "Apply a unified diff in git format (paths relative to the project root with a/ and b/ prefixes) that may add, modify, delete or rename several files. If any hunk fails, nothing is written. Suited to multi-file changes prepared in a local checkout.", Context(new JsonObject
        { ["diff"] = Text("Unified diff text.") }), ["contextHandle", "diff"], false);
        Add("file_view_image", "Return a remote PNG, JPEG or WebP file as image content.", Context(new JsonObject
        { ["path"] = Text("Remote image path.") }), ["contextHandle", "path"]);
        Add("terminal_exec", "Run a process on the remote host: program + args, or command + shell. Waits up to waitSeconds and returns the result; if still running, returns taskId for terminal_poll. Runs as the Broker user and is not confined to the project. Never automatically retry after ExecutionOutcomeUnknown.", Context(new JsonObject
        {
            ["program"] = Text("Executable to run without a shell."), ["args"] = Array(Text("One literal argument."), "Arguments for program."),
            ["command"] = Text("Shell command line; use instead of program."), ["shell"] = Text("Shell executable for command; see shells from workspace_connect."),
            ["cwd"] = Text("Project directory to run in; defaults to the context cwd."), ["timeoutSeconds"] = Integer("Task deadline in seconds; capped by the server."),
            ["waitSeconds"] = Integer("Seconds to wait for exit before returning, 0–300; default 30."),
            ["pty"] = Flag("Run in a pseudo-terminal (merged output with escape codes).")
        }), ["contextHandle"], false);
        Add("terminal_poll", "Read a task's state, exit code and output after offset, waiting up to waitSeconds for new output or exit. gap means output was evicted; a missing task is not a success.", Context(new JsonObject
        {
            ["taskId"] = Text("Task id from terminal_exec."), ["offset"] = Integer("Output cursor from the previous result; default 0."),
            ["maxCharacters"] = Integer("Output budget; capped by the server."), ["waitSeconds"] = Integer("Seconds to wait for new output or exit, 0–300; default 0.")
        }), ["contextHandle", "taskId"]);
        Add("terminal_input", "Write to a running task's stdin. Never replayed after a transport failure.", Context(new JsonObject
        {
            ["taskId"] = Text("Task id from terminal_exec."), ["text"] = Text("Exact characters to write; include a newline to submit a line."),
            ["close"] = Flag("Close stdin after writing.")
        }), ["contextHandle", "taskId"], false);
        Add("terminal_cancel", "Cancel a task's process tree and confirm it exited; cancellation-pending means not yet confirmed.", Context(new JsonObject
        { ["taskId"] = Text("Task id from terminal_exec.") }), ["contextHandle", "taskId"], false);
        Add("desktop_observe", "Observe a window on a remote host through its desktop driver, without changing focus. Target hostId + processId, or a contextHandle. Without windowId, lists the process's windows; with windowId, returns a screenshot, geometry and observationId for desktop_act. Fails on hosts without desktop support.", Context(new JsonObject
        {
            ["hostId"] = Text("Remote host id; use instead of contextHandle when no workspace is needed."),
            ["processId"] = Integer("Exact target process id; may be omitted with a contextHandle whose project has exactly one Unity process."),
            ["windowId"] = Integer("Window id from a previous listing; omit to list the process's windows.")
        }), []);
        Add("desktop_act", "Run 1–10 bounded input actions on the observed window and return a new observation; older observations become invalid. macOS pixel actions require foreground. Background never escalates to foreground on its own. If sequenceCompleted is false, check the per-action results.", Context(new JsonObject
        {
            ["hostId"] = Text("Same host as the observation; use instead of contextHandle."),
            ["observationId"] = Text("observationId from a fresh desktop_observe on the same host or context."),
            ["actions"] = Array(new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("tool", "arguments"),
                ["properties"] = new JsonObject { ["tool"] = Text("click, double_click, right_click, type_text, press_key, hotkey, scroll or drag."), ["arguments"] = new JsonObject { ["type"] = "object" } }
            }, "1–10 actions."),
            ["deliveryMode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("background", "foreground"), ["default"] = "background", ["description"] = "background (default) never takes focus. foreground activates the window, may move the real pointer and leaves it in front; use only with the user's explicit permission for this host/window in this task. Applies to this call only." }
        }), ["observationId", "actions"], false);
        return tools;
    }

    public static bool IsMutation(string tool) => tool is "file_write" or "file_edit" or "file_apply_diff" or "terminal_exec" or "terminal_input" or "terminal_cancel" or "desktop_act" or "workspace_disconnect";

    public static JsonObject Result(JsonNode value, JsonArray? extraContent = null)
    {
        var content = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = value.ToJsonString() });
        if (extraContent != null) foreach (var item in extraContent) content.Add(item?.DeepClone());
        return new JsonObject { ["content"] = content, ["structuredContent"] = new JsonObject { ["result"] = value }, ["isError"] = false };
    }

    public static JsonObject Failure(string code, string message, bool unknown = false) => new()
    {
        ["isError"] = true, ["content"] = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = code + ": " + message }),
        ["structuredContent"] = new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["outcome"] = unknown ? "unknown" : "rejected", ["mayHaveExecuted"] = unknown, ["retryAutomatically"] = false } }
    };
}

// Workspace tools share the stateless /mcp endpoint with Unity tools; state lives in context leases.
internal sealed class WorkspaceMcpTool(Tool tool, WorkspaceService service, IHttpContextAccessor contexts) : McpServerTool
{
    public override Tool ProtocolTool => tool;
    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var args = new JsonObject();
        foreach (var pair in request.Params?.Arguments ?? new Dictionary<string, JsonElement>())
            args[pair.Key] = JsonSerializer.SerializeToNode(pair.Value, BrokerJsonContext.Default.JsonElement);
        JsonObject result;
        try { result = await service.CallAsync(BrokerHost.IsNetworkExport(contexts), tool.Name, args, cancellationToken); }
        catch (BrokerOperationException ex) { result = WorkspaceTools.Failure(ex.Code, ex.Detail, ex.MayHaveExecuted); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { result = WorkspaceTools.Failure("FileNotFound", ex.Message); }
        // Malformed caller arguments and filesystem or process errors are request-boundary failures.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            JsonException or System.ComponentModel.Win32Exception or FormatException or OverflowException or System.Text.DecoderFallbackException)
        { result = WorkspaceTools.Failure("WorkspaceRequestFailed", ex.Message); }
        return result.Deserialize(BrokerJsonContext.Default.CallToolResult)!;
    }
}
