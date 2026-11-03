# Remote development workspaces

**English** | [简体中文](REMOTE_WORKSPACES_zh.md) | [User guide](../README.md)

Available in npm 3.2.0 and later. Workspace exports and credentials are opt-in; upgrading
an existing installation does not grant file, terminal or desktop access.

## Two MCP views

| Listener / path | Catalog and scope |
|---|---|
| Local `/mcp` | Existing `unity_status`, `unity_connect`, `eval`; local and federated Unity instances |
| Local `/remote/mcp` | 15 workspace tools plus 2 optional desktop tools; configured remote hosts only |
| Network `/mcp` | Existing local Unity export and its existing authorization |
| Network `/remote/mcp` | Explicitly shared local workspace roots and capability credentials; no onward routing |
| Local `/remote/status` | Browser connection monitor; never exposed by the network listener |

The listener selects the boundary. A header or a caller-supplied flag cannot make the local
workspace endpoint operate on a local project or make a network peer forward to another peer.
A workspace can exist without a running Unity Editor. Its identity is `hostId + workspaceId`,
not its display name, and each connected context has its own remote cwd.

The workspace endpoint uses session-affine Streamable HTTP MCP `2025-11-25`. Initialize it
and retain `Mcp-Session-Id`; `2026-07-28` stateless discovery is explicitly declined so clients
can negotiate the session protocol. The original Unity endpoint's transport is unchanged.

## Folder registration and live Unity capabilities (3.3.0)

A project is a canonical local folder; Unity layout and a running Editor are not required.
Register any existing absolute directory, optionally supplying a display name:

```text
unity workspace add website /absolute/path/to/website "Website"
```

Canonical aliases (including intermediate symlinks and Windows path casing) reuse the existing
ID, name and grants. An ID cannot point to another directory. Parent and child folders remain
separate projects. Existing configuration with duplicate canonical roots is rejected explicitly;
choose one definition instead of silently merging IDs or permissions. Existing 3.2 configuration
needs no migration; a missing display name uses the directory name.

Each local Unity `/unity` registration persists its reported root in the same `workspace.projects`
list, or attaches to the already registered folder. The generated ID survives disconnects, process
exit, Broker restart and reconnect. Imported remote Unity instances never register local folders.
Persistence failures return `ProjectRegistrationFailed`. A reported absolute root that is temporarily
absent still retains the Unity connection and project record with `directoryExists=false`; connecting
a file workspace reports the unavailable directory. `workspace remove` removes configuration, not
files; a subsequent local Unity connection can automatically register that directory again.

Manual and automatic registrations have no grants by default. Configure an explicit policy for
future first-time automatic registrations:

```text
unity workspace defaults files-read workspace-key
unity workspace defaults files-write workspace-key
unity workspace defaults terminal workspace-key
unity workspace defaults desktop off
unity service restart
```

The policy is `workspace.autoRegistrationGrants`, using the same four `*CredentialRef` fields as
projects. It is copied only when creating a project and never rewrites existing grants. CLI changes
still require a Broker restart. Runtime Unity registration becomes visible immediately; it uses the
same config-file lock as the CLI and preserves other fields. Lock contention or persistence failures
reject registration explicitly.

List, connect and status use the same project metadata: stable `workspaceId`, `name`, source-prefixed
`displayName`, first registration source `registeredBy` (`manual` / `unity`), and `directoryExists`.
`kind` is `unity` while a matching local instance is connected and `workspace` otherwise.
`unityState` is `disconnected`, `ready`, `busy` or `authorizationRequired`;
`capabilities.unityConnected` and `unityCanEval` follow the existing Unity execution policy, including
compilation repair mode. `unityInstances` contains only connected instances with exact ID, PID,
phase and authorization state. Multiple instances require explicit selection.

Unity disconnects do not close workspace handles or terminal tasks. Read status again to observe
capability changes. Aggregated Unity IDs use `remote:host:instance`, with the original in
`remoteInstanceId`. The original Unity snapshot adds `workspaceId`; Unity execution still uses the
separate `/mcp` `unity_status` → `unity_connect` → `eval` flow and its fresh registry revision.
A workspace context is not a Unity lease. Ordinary projects are available only through other hosts'
remote workspace access; the local gateway cannot target its own ordinary folders. Local Unity MCP
remains available.

## Configure one remote

Keep the existing host/IP/port and Unity credential configuration. Use another credential
reference for workspace capabilities. On both machines, provision the same workspace secret
through stdin using `unity remote credential workspace-key`; never put a secret in argv.
`peer-auth.json` is local user configuration, not a repository file.

On the exporting machine, with an existing network listener:

```text
unity workspace add reliclight-main /absolute/path/to/RelicLight
unity workspace grant reliclight-main files-read workspace-key
unity workspace grant reliclight-main files-write workspace-key
unity workspace grant reliclight-main terminal workspace-key
unity service restart
```

Use the target machine's actual absolute path, including a Windows drive when applicable.
New roots have **no grants**. Only grant the capabilities required. `desktop` is an additional
grant and requires the worker described below. `off` revokes an individual grant. These
changes apply after restarting the Broker; driver configuration also needs a worker restart.

On the calling machine:

```text
unity workspace peer mac_studio workspace-key
unity service restart
unity workspace list
```

`mac_studio` must already exist in `remotes`, for example at `10.100.0.100:2348`.
`workspace peer` stores `workspaceCredentialRef` on that entry; it does not duplicate host
configuration or change its Unity `credentialRef`. A single credential can receive several
capability grants. Sharing a Unity token never implicitly grants terminal, files or desktop.
The workspace network endpoint requires a matching capability credential even if Unity's
network `authMode` is `none`.

Use existing SSH Host entries/SSH Agent for installation and recovery. No SSH private keys
are copied, stored in Broker configuration or forwarded between hosts. Once connected,
commands execute locally on the exporting Broker rather than opening a new SSH connection
for every tool call. `--config <file>` isolates CLI configuration for acceptance runs.

Register a separate HTTP MCP server in the AI client at:

```text
http://127.0.0.1:2347/remote/mcp
```

For Codex's TOML configuration, a separate server entry is sufficient:

```toml
[mcp_servers.unity_workspace]
url = "http://127.0.0.1:2347/remote/mcp"
```

Keep the existing Unity server entry at `/mcp`. Do not expose the aggregate local listener
to a network. Across untrusted networks, use an encrypted tunnel or HTTPS proxy to the
network export listener with explicit Host/Origin allowances, as in the Unity federation guide.

## Context and tools

1. `workspace_list` returns configured remote workspaces and explicit errors for unavailable
   or unconfigured peers.
2. `workspace_connect` takes an exact `hostId`, `workspaceId` and optional project-relative
   `cwd`. It returns an opaque context handle, canonical remote root/cwd, OS, capabilities,
   local Unity matches, applicable project guidance and project Skill summaries.
3. Reuse that context handle in the same MCP session. It is not valid in another session,
   after context closure, lease expiry, or a replaced peer session/Broker. Connecting another
   context does not change the first one's cwd.

| Tools | Contract |
|---|---|
| `workspace_status`, `workspace_disconnect` | Current identity/tasks/capabilities; explicit context closure cancels its owned tasks |
| `workspace_get_guidance` | Root-to-target `AGENTS.override.md`, otherwise `AGENTS.md`; path, scope, hash and raw text |
| `workspace_read_skill` | A selected project Skill or a project-contained reference relative to its remote Skill root |
| `file_read`, `file_search` | Strict UTF-8 paged text and SHA-256; paged path-substring search, not content search |
| `file_apply_patch` | Exact replacements with an expected SHA-256, or `missing` for creation; conflicts write nothing |
| `file_view_image` | PNG/JPEG/WebP bytes as MCP image content, with remote source metadata |
| `terminal_exec`, `terminal_poll`, `terminal_input`, `terminal_cancel` | Async task handles, argv or explicit shell, stdin, output cursors, exit/cancellation state |
| `workspace_events` | Session event cursor and optional 0–30 second wait; explicit retention gaps |
| `desktop_observe`, `desktop_act` | Exact window observations and bounded actions through a user-session worker; background by default |

File paths are project-relative, or absolute within the authorized project. Terminal cwd
defaults to the context cwd and can be overridden per request within the project. The file
executor checks canonical paths including intermediate symbolic links; it rejects links outside
the root. Files may still be changed concurrently by the user or other processes. The expected
hash prevents ordinary stale patches; this is not an OS filesystem sandbox or an atomic
transaction with arbitrary external writers. Terminal authorization executes programs as the
Broker user and therefore is **not confined to project files**. Detached processes are outside
the terminal task's lifetime guarantee; cancellation reports actual root/stream completion.

For argv mode, supply `program` and literal `args`. For shell mode, supply `command` plus
an explicit `shell` executable. Non-PTY output keeps stdout/stderr separate. PTY output is
merged terminal data, including terminal escape sequences. macOS/Linux use the OS `script`
utility; Windows uses ConPTY. Missing OS support is an explicit task-start error. PTY dimensions
are configured at task creation; live resize is not currently exposed. Completed task records
remain until context closure; `maxTasksPerContext` includes those retained records.

## Project knowledge

Only rules from the project root through the requested target directory are returned.
Sibling-directory and user-global AGENTS files are excluded. The server attributes these as
project data, not system instructions. Follow explicit project references, such as selecting
personal rules after checking Git identity, through the remote context. It cannot statically
infer every directory touched by an arbitrary shell command or guarantee an AI follows rules.

Skills default to immediate child folders of `.agents/skills`. Other project-relative catalog
roots can be configured in `skillDirectories`; links outside the project are rejected.
User-global `~/.agents/skills` and `~/.codex/skills` are not scanned. Connection returns each
Skill's ID, name, description, path/root and content hash. The catalog accepts plain or quoted
text scalars and folded/literal description blocks; malformed entries are reported in `errors`.
`workspace_read_skill` reads full `SKILL.md` or references on demand and never executes scripts.
Catalogs are read afresh on connect/read; reconnect or read guidance again after rule changes.

Text pages use UTF-16 character offsets. `complete`, `nextOffset` and `totalCharacters` make
partial returns explicit; use `file_read` on a returned guidance path to fetch remaining pages.
Full knowledge/file reads require `files-read`; the initial connection's attributed guidance
and Skill summaries are part of workspace metadata. MCP results do not install client-native
Skills or remove the client's own local Skills. Images are currently returned at source size
within the configured file budget; image crop/resize is not provided by `file_view_image`.

## Disconnects, events and outcome uncertainty

Each MCP session owns its peer transports, contexts, tasks and bounded event log. Events have
monotonic IDs and timestamps, plus available host/workspace/task details and recovery advice.
The Broker probes peer sessions, applies configured failure thresholds and exponential retry
delays, and publishes transitions once rather than repeating the same offline message.
Maintenance runs across sessions independently; operations within one session are serialized.
Large/slow operations can delay that session's heartbeat; its request deadline bounds the call.

A known disconnected/reconnecting context rejects new operations without sending them.
`workspace_status` may return its last snapshot with `isStale`, state and `lastSeenAtUtc`.
Transport failure after dispatch of a write returns `ExecutionOutcomeUnknown`,
`mayHaveExecuted: true`, and `retryAutomatically: false`. Inspect actual files/window state or
the task list after recovery. Neither the Broker nor the client should automatically replay it.
Listing does not discard a peer session that owns contexts. Explicit reconnection can replace
a failed peer session; its former context handles remain invalid, even if the same root exists.

`terminal_cancel` verifies exit/output completion; `cancellation-pending` is not success.
Output and event cursors report `gap` when retained history was evicted. Use a fresh status
snapshot to recover, rather than inventing missing events or assuming an absent task succeeded.
Session expiry or DELETE closes its contexts. The server keeps active requests/GET streams alive;
otherwise leases expire after inactivity. A restarted Broker has no persisted task/handle journal.

The endpoint provides GET SSE `notifications/message`, `Last-Event-ID` replay and the
`workspace://events` resource, as well as `workspace_events` polling. The client must actually
listen or poll. These messages do **not** guarantee an idle model wakes or a desktop client
displays them. The local `/remote/status` page polls while open, displays connection changes and
offers browser desktop notifications after the user's browser permission. It also detects the
local Broker becoming unavailable and reinitializes after restart. It cannot wake an AI, and
no notification is promised while the page is closed or its browser suspends it.

## Desktop worker

Cua is optional. Without an installed, explicitly configured driver and a desktop-authorized
workspace, the network catalog omits `desktop_observe` and `desktop_act`. The local gateway
starts with 15 tools and adds those two only after discovering or connecting an online peer
that exports desktop capability; it does not require Cua on the gateway itself. Driver removal
or loss of the last capable peer hides them again. Catalog changes emit
`notifications/tools/list_changed`; clients must refresh their tool lists. Files, terminals and
Unity remain available without Cua. Installation is distinct from OS permissions and worker
readiness, which are checked when desktop tools are invoked.

The adapter currently requires **Cua Driver 0.38.0**; other versions are rejected until their
contract is validated. Install its official platform distribution and verify its release checksum.
No driver binary, global Skill pack, permission bypass or nested AI model is installed by this Broker.

```text
unity workspace desktop-driver /absolute/path/to/cua-driver
unity workspace grant reliclight-main desktop workspace-key
unity service restart
unity desktop worker
```

Run the last command in the same user's logged-in **graphical session**, after granting the
driver its required OS permissions. For custom configuration, pass `--config <file>` to each
command. The worker is a separate process; `unity service` manages the Broker, not this worker.
The operator can use the OS's interactive logon startup mechanism for the worker. It is not
automatically installed or launched by a remote tool call.

On macOS, Cua's stable application identity owns screen-recording/accessibility permissions;
use its explicit `permissions grant` setup only with permission for those foreground dialogs.
On Windows, the worker refuses session 0; use the logged-in interactive account, not a
noninteractive service task. Its pipe ACL and client ownership check use the account's user
SID so elevated Broker and ordinary GUI worker logons can communicate without granting other
accounts access. Unix uses same-user pipes. Linux requires an actual X11/Wayland session.
The supplied Linux host has no graphical display and cannot be used as a desktop target.

`desktop_observe` without a process ID only binds an exact single locally registered Unity
project match. Otherwise supply an exact process ID; omitted window ID lists that process's
windows. Select one returned window ID to obtain image/tree, geometry/scale and `observationId`.
An explicit process target is operator-selected; it does not assert that an arbitrary app is Unity.

`desktop_act` consumes a fresh observation owned by the same context. It checks process start
identity and fixes delivery to the observed PID/window. `deliveryMode` defaults to `background`.
The calling agent must obtain explicit user permission for the current host/window before setting
`foreground` on an individual call. It verifies activation of the exact target before the sequence,
may move the hardware pointer, and leaves that window in front. This prevents per-action focus
restoration from breaking click-then-type. The mode does not persist as a session default.
The server validates explicit arguments, not human consent.
Cua Driver 0.38.0's macOS pixel background route was observed to temporarily activate its target,
so macOS pixel actions now return `ForegroundRequired` before dispatch in the default mode.
Observations remain available in the background; there is no automatic escalation.
Sequences contain 1–10
bounded `click`, `double_click`, `right_click`, `type_text`, `press_key`, `hotkey`, `scroll` or
`drag` calls. Text is capped at 1,024 characters per action. Target and execution bounds are
validated before dispatch; caller-supplied target/mode overrides, desktop scope, zoom frames,
unbounded durations and persistent key/button holds are rejected. Pixel sequences start with
at most one pixel action. Clicks use the driver's atomic capture ID; other pixel actions recheck
window bounds and screenshot geometry before dispatch. That recheck is not a frame-accurate
game guarantee. Every mutation invalidates earlier worker observations. The driver validates
each action's own schema when called; a later refusal can leave a partially completed sequence,
reported with `sequenceCompleted: false` and individual action results.

The worker serializes input, returns driver action results and a post-action observation, and
stops accepting work after an unconfirmed driver timeout. Bounded driver actions pair their own
press/release; there are no persistent held-key leases across calls. Background-unavailable
results are returned without trying foreground input. This first version supports taps and
bounded gestures, not sustained movement keys, remote unlocking, audio/video streaming or
deterministic gameplay. Do not infer game support from a successful ordinary desktop control.

## Configuration reference

`workspace` in `config.json` has the following defaults; values are validated at startup.

| Property | Default | Purpose |
|---|---:|---|
| `projects` | `[]` | Explicit roots; each has ID, rootPath, capability credential refs and Skill directories |
| `leaseSeconds` | 1800 | Inactive MCP session lifetime |
| `maxSessions` / `maxContextsPerSession` | 64 / 16 | Session/context bounds |
| `requestTimeoutSeconds` | 60 | Remote workspace operation deadline |
| `heartbeatFailureThreshold` | 1 | Consecutive failed heartbeat responses before offline |
| `eventRetention` | 512 | Events retained per session |
| `maxTextCharacters` / `maxFileBytes` | 32768 / 8388608 | Text page/input and individual file/request budgets |
| `maxTasksPerContext` | 16 | Running and retained completed tasks |
| `maxTaskOutputCharacters` / `maxTaskSeconds` | 262144 / 3600 | Task output retention and maximum runtime |
| `ptyColumns` / `ptyRows` | 120 / 30 | Initial PTY size |
| `desktopObservationSeconds` | 30 | Observation lifetime |
| `maxDesktopSequenceMilliseconds` | 5000 | Sequence dispatch/duration budget |
| `desktopDriverTimeoutSeconds` | 15 | Individual driver response deadline |
| `desktopMaxImageDimension` | 1600 | Desktop screenshot longest edge |
| `desktopDriverPath` | unset | Explicit installed Cua executable |
| `desktopDriverArguments` | `["mcp"]` | Driver stdio invocation |
| `desktopPipeName` | `YuzeEvalTool.Desktop` | Same-user worker IPC name; use another name for an isolated worker |

Workspace probing reuses `discoveryIntervalSeconds`, `connectionTimeoutSeconds`,
`transportGraceSeconds` and `reconnectMaxDelaySeconds` from the host configuration. The heartbeat
deadline is connection timeout plus transport grace. Retry delay doubles from the discovery
interval up to the configured reconnect cap. Driver calls have separate deadlines, so a slow
desktop action does not imply that the entire machine's Unity endpoint is unavailable.

## Validation boundary

This revision has been exercised through two real HTTP Brokers, a real Windows exporting
process, macOS and Windows PTYs, and Codex 0.163.0 app-server tool discovery. The local Unity
catalog remained three tools; optional-driver checks verified 15 base tools, 17 with desktop
capability, and live list-change notifications on installation/removal. Fault injection verified
lost write responses without replay, known-offline rejection, stale snapshots, recovered task
lookup, Broker replacement, event gaps, task deadline/output retention and real SSE messages.
The status page's online/offline transitions and narrow layout were checked in headless Chromium.

Mac Studio OS permissions were granted with explicit operator permission. The final NativeAOT
remote MCP path was verified against the real Unity window: screenshots, explicit foreground
click-then-Chinese-input, Project display-slider dragging, resized screenshots and stale-geometry
rejection. Default macOS pixel input is rejected before dispatch; nested actions cannot override
the delivery mode. Action delivery reports were checked against returned images.
Windows GUI-worker/service IPC has been exercised, but actual Windows input and the new foreground
path remain unverified. Linux without a GUI is unavailable. Unity gameplay and sustained keys
remain unverified, as does Codex UI logging or idle-model wakeup. Foreground Editor acceptance
does not establish pure-background or gameplay support; consult the delivery record before deployment.
