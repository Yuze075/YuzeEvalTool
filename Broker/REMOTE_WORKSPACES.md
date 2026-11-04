# Remote development workspaces

**English** | [简体中文](REMOTE_WORKSPACES_zh.md) | [User guide](../README.md)

A workspace is a registered folder on a Broker host, with or without a running Unity Editor.
From 5.0.0 the single Streamable HTTP endpoint `/mcp` serves both the Unity tools and the
workspace tools. Authorization belongs to the MCP connection: the loopback listener is trusted,
and the optional network listener checks the host credential on every request.

## One endpoint, two views

| Listener | Unity tools | Workspace tools |
|---|---|---|
| Local `127.0.0.1:2347/mcp` | This machine's instances plus configured remotes | Configured remote hosts only; never this machine |
| Network `/mcp` (opt-in) | This machine's instances only | This machine's registered folders only; never forwards |
| Local `/status` | Browser connection monitor; not exposed on the network listener | |

The listener decides the view; no header or argument can change it. Contexts created on one view
are rejected on the other. Peers are imported only as their own local exports, so A→B→C and A↔B
never route recursively, and a remote that resolves to this Broker or duplicates another
configured Broker is rejected.

Each remote has exactly one MCP connection, shared by Unity federation and workspace calls. It is
stateless: no `Mcp-Session-Id` is required, and a Broker restart invalidates only the contexts
and Unity leases on that host.

Register one client entry:

```toml
[mcp_servers.unity_eval]
url = "http://127.0.0.1:2347/mcp"
```

## Configure hosts and folders

On each exporting host, enable the network listener with a credential stored through stdin,
then register folders. Local Unity connections register their project roots automatically and keep
them after disconnects.

```text
unity remote credential office-key
unity remote access token 10.100.0.100 2348 office-key
unity workspace add reliclight-main /absolute/path/to/RelicLight
unity service restart
```

On each calling host, add the peer once:

```text
unity remote add mac_studio 10.100.0.100 2348 office-key
unity service restart
```

Folder identity is the canonical root path; aliases and intermediate symbolic links reuse one ID,
and parent and child folders stay separate projects. `workspace remove` removes configuration,
not files. Across untrusted networks, place the network listener behind an encrypted tunnel or
HTTPS proxy with explicit Host/Origin allowances. SSH keys are never copied or forwarded.

## Tools

| Tool | Purpose |
|---|---|
| `workspace_list` | Remote hosts (state, desktop support) and their workspaces, plus per-host errors |
| `workspace_connect` | Open a context on `hostId` + `workspaceId`; returns cwd, OS, shells, Unity instances, AGENTS guidance and the Skill catalog |
| `workspace_status`, `workspace_disconnect` | Context identity and tasks; explicit close cancels its tasks |
| `workspace_get_guidance` | Root-to-directory `AGENTS.override.md`, otherwise `AGENTS.md` |
| `file_read` | 1–20 files as numbered lines with SHA-256 version, `totalLines`, `lineEnding`, `nextLine` |
| `file_glob` | Paths by glob, newest first |
| `file_grep` | .NET regular expression search: matching files, lines with context, or counts |
| `file_write` | Create (`expectedVersion: "missing"`) or overwrite (current SHA-256) a text file |
| `file_edit` | Exact replacements, optional `replaceAll`; LF edits also match CRLF files |
| `file_apply_diff` | Multi-file unified diff (add, modify, delete, rename) applied all-or-nothing |
| `file_view_image` | PNG, JPEG or WebP as MCP image content |
| `terminal_exec`, `terminal_poll`, `terminal_input`, `terminal_cancel` | Processes with `waitSeconds`, output cursors, stdin and verified cancellation |
| `desktop_observe`, `desktop_act` | Window observation and bounded input through the host's desktop worker |

Paths are project-relative or absolute inside the project. Files resolve every component,
including intermediate symbolic links, and reject paths that leave the root. Inside a git work
tree, `file_glob` and `file_grep` use git's ignore rules (`git ls-files --cached --others
--exclude-standard`); elsewhere every regular file counts. A glob containing `/` matches paths
relative to the search directory; without one it matches file names at any depth. `file_grep`
skips binary files, files above `maxFileBytes` and lines that are not UTF-8.

`file_apply_diff` runs `git apply` from the project root: every hunk is checked before anything is
written, and paths outside the root or behind symbolic links are rejected. When the project is
inside a larger repository, the diff is still relative to the project root. `git` must be on the
Broker's PATH for glob/grep inside git work trees and for diffs.

Terminal programs run as the Broker user and are **not confined to project files**. Use `program`
plus literal `args`, or `command` plus an explicit `shell`. `terminal_exec` waits up to
`waitSeconds` (default 30) and returns the finished result; otherwise poll with `terminal_poll`,
which can also wait for new output. PTY output is merged and includes escape sequences
(macOS/Linux `script`, Windows ConPTY). Detached processes are outside the task lifetime.

## Recommended workflow

1. `workspace_list`, then `workspace_connect`; read the returned guidance and the Skills relevant
   to the task with `file_read`. They are project rules, attributed as data, not system instructions.
2. Small changes: `file_edit` or `file_write` with the version from `file_read`.
   Multi-file changes: `file_apply_diff`.
3. Large refactors: clone the repository locally, work on a dedicated branch, push it, and use
   `terminal_exec` on the remote host to fast-forward its working tree when clean, build, test and
   verify Unity. Git then records every synchronization and reports conflicts instead of
   overwriting someone else's work.
4. Run builds and tests remotely with `terminal_exec`; run Unity checks with `unity_status`,
   `unity_connect` and `eval` on the advertised `remote:<host>:<id>` instance.
5. `terminal_exec` runs inside the Broker's user session, so on macOS it can use the login
   keychain for git and CLI credentials, which an SSH session usually cannot.

## Contexts, failures and outcome uncertainty

A context is a lease, not an MCP session. It expires after `leaseSeconds` of inactivity, closing
its tasks; `maxContexts` bounds open contexts. A gateway context records the remote Broker
instance: after a remote restart it returns `ContextHandleInvalid`, and the caller connects again.
While a remote is offline, calls are rejected without dispatch and `workspace_status` returns the
last snapshot with `isStale: true`.

A transport failure after dispatching a write returns `ExecutionOutcomeUnknown` with
`mayHaveExecuted: true` and `retryAutomatically: false`. A JSON-RPC error or HTTP 401/403/404
proves the call did not run. Forwarded failures keep the peer's own error code and message.
Inspect files, task lists or windows before any retry; neither the Broker nor the client replays.
`terminal_cancel` reports `cancellation-pending` until exit and stream completion are confirmed.
Output cursors report `gap` when retained output was evicted.

## Desktop worker

Desktop access is host-level: target `hostId` plus an exact `processId`, or a `contextHandle`
whose project has exactly one Unity process. Never supply both. Without a configured driver,
desktop calls on that host return `ToolUnavailable`; `workspace_list` reports
`desktopToolsAvailable` per host.

The adapter requires **Cua Driver 0.38.0**. Install its official distribution, then:

```text
unity workspace desktop-driver /absolute/path/to/cua-driver
unity service restart
unity desktop worker
```

Run the worker in the same user's logged-in graphical session after granting the driver its
OS permissions; the Broker service never starts desktop input. On Windows the worker refuses
session 0, and its pipe is restricted to the user's SID. Linux requires an X11/Wayland session.

`desktop_observe` without `windowId` lists windows; with one it returns a screenshot, geometry and
`observationId`. `desktop_act` consumes a fresh observation, checks the process start time, and
runs 1–10 bounded actions (`click`, `double_click`, `right_click`, `type_text`, `press_key`,
`hotkey`, `scroll`, `drag`; text up to 1,024 characters). `deliveryMode` defaults to `background`;
`foreground` activates the exact window, may move the pointer and leaves it in front, so the
calling agent must get the user's explicit permission for that host and window first. macOS pixel
actions require foreground. A later refusal can leave a partly completed sequence, reported with
`sequenceCompleted: false`. Taps and bounded gestures only: no held keys, unlocking or gameplay.

## Upgrading to 5.0

- Remove the `/remote/mcp` client entry; keep only `/mcp`. `/remote/status` moved to `/status`.
- `file_search` became `file_glob`, `file_apply_patch` became `file_edit`, `file_read` reads
  numbered lines from several files, and `workspace_read_skill` is replaced by `file_read` on the
  catalog path. `workspace_events`, SSE notifications and `tools/list_changed` were removed.
- `workspace.maxSessions`, `maxContextsPerSession`, `eventRetention` and
  `heartbeatFailureThreshold` are ignored; use `maxContexts`.
- Upgrade every host together: a 5.0 gateway reports `RemoteIncompatible` for workspace calls to
  older peers. Unity federation remains compatible and the Unity wire protocol stays 2.0.

## Configuration reference

`workspace` in `config.json`; values are validated at startup.

| Property | Default | Purpose |
|---|---:|---|
| `projects` | `[]` | Registered roots: ID, rootPath, display name, registration source, Skill directories |
| `leaseSeconds` | 1800 | Context idle lifetime |
| `maxContexts` | 64 | Open contexts on this Broker |
| `requestTimeoutSeconds` | 60 | Workspace operation deadline, plus any `waitSeconds` |
| `maxTextCharacters` / `maxFileBytes` | 32768 / 8388608 | Text page and per-file budgets |
| `maxTasksPerContext` | 16 | Running and retained tasks |
| `maxTaskOutputCharacters` / `maxTaskSeconds` | 262144 / 3600 | Task output retention and runtime limit |
| `ptyColumns` / `ptyRows` | 120 / 30 | Initial PTY size |
| `desktopObservationSeconds` | 30 | Observation lifetime |
| `maxDesktopSequenceMilliseconds` | 5000 | Sequence duration budget |
| `desktopDriverTimeoutSeconds` | 15 | Driver response deadline |
| `desktopMaxImageDimension` | 1600 | Screenshot longest edge |
| `desktopDriverPath` / `desktopDriverArguments` | unset / `["mcp"]` | Cua executable and stdio arguments |
| `desktopPipeName` | `YuzeEvalTool.Desktop` | Same-user worker IPC name |

Skill catalogs come from immediate child folders of each `skillDirectories` entry (default
`.agents/skills`). Remote discovery and retry use `discoveryIntervalSeconds`,
`connectionTimeoutSeconds`, `transportGraceSeconds` and `reconnectMaxDelaySeconds`; the retry delay
doubles from the discovery interval up to the cap.
