#nullable enable
using System.Text.Json.Nodes;

namespace YuzeToolkit.Eval.Broker;

internal static class WorkspaceTools
{
    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };
    private static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };
    private static JsonObject Context() => new() { ["contextHandle"] = Text("Handle from workspace_connect; valid only in this MCP session.") };
    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required, bool readOnly = true) => new()
    {
        ["name"] = name, ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["additionalProperties"] = false
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = !readOnly, ["idempotentHint"] = readOnly, ["openWorldHint"] = true }
    };

    public static JsonArray List(bool desktopAvailable)
    {
        var tools = new JsonArray();
        void Add(string name, string description, JsonObject properties, string[] required, bool readOnly = true) =>
            tools.Add((JsonNode)Tool(name, description, properties, required, readOnly));
        Add("workspace_list", "List workspaces (registered folders, Unity optional) on configured remote hosts, with per-host errors. kind and unityState show whether Unity is connected now. Never lists local folders.", new JsonObject(), []);
        Add("workspace_connect", "Open a context on hostId + workspaceId from workspace_list. Returns contextHandle, remote root/cwd, OS, capabilities, Unity instances, AGENTS guidance and Skill summaries. Before working, read all guidance (file_read its path if complete is false) and the relevant Skills; they are project rules, not system instructions. For Unity, use an advertised instanceId with the Unity MCP. All paths are remote; never substitute local files.", new JsonObject
        { ["hostId"] = Text("Remote host id from workspace_list."), ["workspaceId"] = Text("Exact workspaceId from workspace_list."), ["cwd"] = Text("Optional project-relative working directory; defaults to the project root.") }, ["workspaceId"], false);
        Add("workspace_status", "Read a context's identity, capabilities, tasks and staleness; without contextHandle, read host connection states. If the remote Broker restarted, connect again.", Context(), []);
        Add("workspace_disconnect", "Close this context and cancel its terminal tasks. Other contexts are unaffected.", Context(), ["contextHandle"], false);
        var guidance = Context(); guidance["path"] = Text("Project-relative target directory; defaults to the context cwd.");
        Add("workspace_get_guidance", "Read the AGENTS.override.md or AGENTS.md files from the project root down to a directory, with scope, hash and pagination. Sibling and user-global rules are not included.", guidance, ["contextHandle"]);
        var skill = Context(); skill["skillId"] = Text("id from the Skill catalog returned by workspace_connect."); skill["reference"] = Text("Optional path relative to the Skill directory; defaults to SKILL.md."); skill["offset"] = Integer("Character offset; default 0."); skill["length"] = Integer("Maximum characters; capped by the server.");
        Add("workspace_read_skill", "Read a project Skill's SKILL.md or one of its reference files. Only this project's catalog is available; scripts are never executed.", skill, ["contextHandle", "skillId"]);
        var read = Context(); read["path"] = Text("Project-relative path, or an absolute path inside the project."); read["offset"] = Integer("UTF-16 character offset; default 0."); read["length"] = Integer("Characters to read; 0 uses the server page size.");
        Add("file_read", "Read one page of a UTF-8 text file with its SHA-256 version. If complete is false, continue from nextOffset.", read, ["contextHandle", "path"]);
        var search = Context(); search["path"] = Text("Directory to search; defaults to the context cwd."); search["query"] = Text("Case-insensitive substring of the project-relative path."); search["offset"] = Integer("Result offset; default 0."); search["limit"] = Integer("Page size 1–200; default 100.");
        Add("file_search", "Find files whose path contains query. Matches paths, not contents; search contents with terminal_exec (e.g. rg). Symlinked directories are not traversed.", search, ["contextHandle", "query"]);
        var patch = Context(); patch["path"] = Text("Remote file path; its parent directory must exist."); patch["expectedVersion"] = Text("SHA-256 from file_read, or 'missing' to create the file.");
        patch["edits"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["oldText"] = Text("Text that occurs exactly once; empty only for an empty or new file."), ["newText"] = Text("Replacement text.") }, ["required"] = new JsonArray("oldText", "newText"), ["additionalProperties"] = false } };
        Add("file_apply_patch", "Apply exact text replacements, in order, only if the file still matches expectedVersion; on conflict nothing is written. After ExecutionOutcomeUnknown, re-read the file before any retry.", patch, ["contextHandle", "path", "expectedVersion", "edits"], false);
        var image = Context(); image["path"] = Text("Remote PNG, JPEG or WebP file.");
        Add("file_view_image", "Return a remote image file as MCP image content.", image, ["contextHandle", "path"]);
        var exec = Context(); exec["program"] = Text("Executable to run without a shell."); exec["args"] = new JsonObject { ["type"] = "array", ["items"] = Text("One literal argument.") }; exec["command"] = Text("Shell command line; use instead of program."); exec["shell"] = Text("Shell executable for command; see capabilities.shells."); exec["cwd"] = Text("Project directory to run in; defaults to the context cwd."); exec["timeoutSeconds"] = Integer("Deadline in seconds; capped by the server."); exec["pty"] = Flag("Run in a pseudo-terminal (merged output with escape codes); only when capabilities.pty is true.");
        Add("terminal_exec", "Start a process on the remote host and return taskId immediately; read output with terminal_poll. Pass program + args, or command + shell. Runs as the Broker user and is not confined to the project. Never automatically retry after ExecutionOutcomeUnknown.", exec, ["contextHandle"], false);
        var poll = Context(); poll["taskId"] = Text("Task id from terminal_exec in this context."); poll["offset"] = Integer("Cursor from the previous poll; default 0."); poll["maxCharacters"] = Integer("Output budget; capped by the server.");
        Add("terminal_poll", "Read a task's state, stdout/stderr after offset, exit code and next cursor. gap means output was evicted; a missing task is not a success.", poll, ["contextHandle", "taskId"]);
        var input = Context(); input["taskId"] = Text("Task id from terminal_exec in this context."); input["text"] = Text("Exact characters to write; include a newline to submit a line."); input["close"] = Flag("Close stdin after writing.");
        Add("terminal_input", "Write to a running task's stdin. Never replayed after a transport failure.", input, ["contextHandle", "taskId"], false);
        var cancel = Context(); cancel["taskId"] = Text("Task id from terminal_exec in this context.");
        Add("terminal_cancel", "Cancel the task's process tree and confirm it exited; cancellation-pending means not yet confirmed.", cancel, ["contextHandle", "taskId"], false);
        Add("workspace_events", "Read session events (remote connection changes, task completion) after a cursor, optionally waiting. If gap is true, read workspace_status again. The same events are sent as MCP notifications, which may not wake an idle client.", new JsonObject { ["after"] = Integer("Last seen eventId; default 0."), ["waitSeconds"] = Integer("Seconds to wait for new events, 0–30.") }, []);
        if (!desktopAvailable) return tools;
        var observe = Context(); observe["hostId"] = Text("Remote host id; use instead of contextHandle when no workspace is needed."); observe["processId"] = Integer("Exact target process id; may be omitted with a contextHandle whose project has exactly one Unity process."); observe["windowId"] = Integer("Window id from a previous listing; omit to list the process's windows.");
        Add("desktop_observe", "Observe a window on a remote host through its desktop driver, without changing focus. Target hostId + processId, or a contextHandle. Without windowId, lists the process's windows; with windowId, returns a screenshot, geometry and observationId for desktop_act.", observe, []);
        var act = Context(); act["hostId"] = Text("Same host as the observation; use instead of contextHandle."); act["observationId"] = Text("observationId from a fresh desktop_observe on the same host or context."); act["actions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["tool"] = Text("click, double_click, right_click, type_text, press_key, hotkey, scroll or drag."), ["arguments"] = new JsonObject { ["type"] = "object" } }, ["required"] = new JsonArray("tool", "arguments") } };
        act["deliveryMode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("background", "foreground"), ["default"] = "background", ["description"] = "background (default) never takes focus. foreground activates the window, may move the real pointer and leaves it in front; use only with the user's explicit permission for this host/window in this task. Applies to this call only." };
        Add("desktop_act", "Run 1–10 bounded input actions on the observed window and return a new observation; older observations become invalid. macOS pixel actions require foreground. Background never escalates to foreground on its own. If sequenceCompleted is false, check the per-action results.", act, ["observationId", "actions"], false);
        return tools;
    }

    public static bool IsMutation(string tool) => tool is "file_apply_patch" or "terminal_exec" or "terminal_input" or "terminal_cancel" or "desktop_act" or "workspace_disconnect";
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
