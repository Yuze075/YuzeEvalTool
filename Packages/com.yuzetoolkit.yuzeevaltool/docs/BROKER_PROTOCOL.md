# Yuze Eval Tool Broker Protocol

**English** | [简体中文](BROKER_PROTOCOL_zh.md) | [Package README](../README.md)

This document defines the stable boundary between the computer-level Yuze Eval Tool Broker and Unity clients. The Broker owns discovery, status, selection, waiting and routing. Unity owns PuerTS eval sessions, tool registration and CLI command parsing.

## Endpoints

- `http://127.0.0.1:2347/mcp`: MCP Streamable HTTP endpoint.
- `ws://127.0.0.1:2347/unity`: Unity client connection.
- `ws://127.0.0.1:2347/cli`: interactive CLI connection.
- `http://127.0.0.1:2347/health`: Broker health snapshot.

From 4.0.0, authorization belongs to the MCP connection. The local loopback endpoint is trusted;
the network `/mcp` and `/remote/mcp` endpoints share `remoteAccess` and the same single Bearer
credential on every request. An authorized connection can use every registered project, file
read/write, terminals and available desktop controls. There are no Unity project tokens or
per-project capability grants. Cua remains optional and the operating system must allow its
Accessibility / Screen Recording permissions; MCP authorization cannot grant those system permissions.
The loopback listener fails explicitly if its configured port is unavailable. Health reports process and Unity status; no project-token store exists.

## Single-hop remote aggregation

Network export is disabled by default. When enabled, `/mcp` on a separate address / port exports
only directly registered local Unity instances. The listener chooses the scope;
caller headers cannot override it. Network requests cannot access `/unity`, `/cli`, `/health`,
imported IDs or imported leases. Local MCP and CLI listings provide the aggregate view.
A→B→C and A↔B never recursively import projects. The Unity wire protocol remains `2.0`;
local IDs are unchanged, with `remote:` reserved for imports.

The `/remote/mcp` workspace protocol, using the same connection authorization, is specified in the
[Broker workspace guide](../../../Broker/REMOTE_WORKSPACES.md). Its project roots,
session handles and tool catalog are independent of Unity leases; it also exports only local capabilities.

`config.json` configures the connection boundary:

```json
{
  "remotes": [
    { "id": "office-pc", "host": "192.168.1.20", "port": 2348,
      "scheme": "http", "credentialRef": "office-key", "enabled": true }
  ],
  "remoteAccess": {
    "enabled": false, "listenAddress": "192.168.1.10", "port": 2348,
    "authMode": "token", "credentialRef": "incoming-key",
    "allowedHosts": [], "allowedOrigins": []
  }
}
```

Omit outbound `credentialRef` for anonymous access. Inbound `authMode` is `token` or `none`;
enabled token mode requires a credential. `peer-auth.json` maps names to separate service secrets:

```json
{ "office-key": "replace-with-peer-secret", "incoming-key": "replace-with-export-secret" }
```

Service secrets never enter `auth.json`, Unity registration, or another peer's requests.
Local caller headers are not forwarded. The network listener serves HTTP; terminate HTTPS
at a reverse proxy, while outbound clients support http/https. HTTP redirects are disabled
to prevent credential forwarding. An absent Origin is accepted; a supplied Origin must
exactly match `allowedOrigins`. Host must match the listen IP or `allowedHosts`; wildcard
listening requires explicit allowed hosts. Network and local ports must differ. Configuration
is validated and loaded at startup; restart the service after changes.

Optional top-level settings (seconds, except concurrency):

| Setting | Default | Range |
|---|---:|---:|
| discoveryIntervalSeconds | 10 | 1–3600 |
| connectionTimeoutSeconds | 10 | 1–300 |
| reconnectMaxDelaySeconds | 60 | 1–3600 |
| remoteRetentionSeconds | 300 | 0–86400 |
| maxConcurrentDiscoveries | 4 | 1–32 |
| transportGraceSeconds | 10 | 1–300 |

Each configuration directory persists a GUID in `broker-id`; `brokerInstanceId` changes on
every process start. Do not copy broker-id across machines. Self-connections, duplicate
Broker identities behind different addresses, and identity replacement during a run report
`RemoteIdentityConflict`. Endpoints lacking the export contract report `RemoteIncompatible`.

Snapshots add `federation: { version: 1, brokerId, brokerInstanceId, view, leaseIdleTimeoutSeconds }`.
Exports must declare `view: local`; local consumer endpoints declare `aggregate`. Importers
also verify that every entry has local provenance. Entries add `displayName` and
`source: { kind, remoteId, brokerId, isStale }`; imported IDs are
`remote:<remoteId>:<original-instance-id>`. Original project names and paths are unchanged.
`remoteBrokers` reports `id`, `endpoint`, `state` (connecting / online / offline / disabled),
`errorCode`, `lastSeenAtUtc` and `projectCount`, without credentials.

Local and aggregate registries have separate revisions. Connect validates the aggregate
revision, then sends the captured peer revision to that peer. Each local opaque handle maps
to its own remote handle and VM. Targeted remote `unity_status` preserves the remote
`capturedAtUtc`; capture with the instance ID or handle before compilation, then use that
marker on the same source. An untargeted aggregate-list timestamp is not a remote compilation marker.

Background discovery uses bounded concurrency and exponential reconnect backoff without
blocking local sources. Offline snapshots are retained for the configured interval with
`source.isStale=true`, `isConnected=false`, `canEval=false`, then removed. Short network outages
do not replace leases; recovery checks the Broker generation and Unity process identity.
Broker restart, Unity replacement and lease expiry require discovery and reconnect. Active
status waits are not removed by local idle cleanup; the peer remains authoritative for lease
validity. Orphaned peer sessions are reclaimed by the existing idle timeout. Interrupted eval
reports `ExecutionOutcomeUnknown` / `mayHaveExecuted=true` and is never replayed. Complete MCP
results, including images and errors, are forwarded unchanged.

Interactive CLI execution is local only: remote selection reports `RemoteCliUnsupported`, and
cwd / path matching considers local instances only. Configuration commands and setup are in
the [main guide](../../../README.md#remote-unity-projects). For isolated deployment use
`unity broker --config <config.json> --local-port <port>`; credentials and identity are relative
to that configuration directory, without updating the default install record.
`unity remote --config <config.json> ...` manages the same isolated configuration.

## Envelope

Unity and CLI WebSockets exchange one UTF-8 JSON object per WebSocket message:

```json
{
  "protocol": "2.0",
  "type": "request",
  "id": "globally-unique-request-id",
  "method": "eval/execute",
  "payload": {}
}
```

`type` is `request`, `response`, or `event`. A failed response has an `error` object with stable `code`, human-readable `message`, and `mayHaveExecuted` when execution outcome is uncertain.

## Unity registration

The first Unity message must be `unity/register`. Its payload contains:

- `instanceId`: stable across Domain Reload for one Unity process
- `connectionEpoch`: incremented for each Unity-side connection generation
- `processId` and `processStartedAtUtc`
- `projectName`, canonical `projectPath`, `unityVersion`, `packageVersion`
- `environment`: `Editor` or `Player`
- the complete initial `status`

Only the primary Unity Editor process may register. Asset Import Workers must never register or start the Broker.
The successful response includes `brokerInstanceId`, a value unique to the current Broker
process. Unity discards every retained PuerTS session if this value changes, preventing a
new Broker process from accidentally inheriting sessions that it no longer owns.
The response contains no project tokens and Unity has no authorization-pending state. Legacy clients
that explicitly register with project verification enabled receive `UnityUpgradeRequired`; upgrade
the Unity package to 4.0.0 or later. Unity protocol 2.0 and the evaluation lifecycle are retained.

## Status

Unity publishes `unity/status` events. A status contains independent transport and main-thread observations plus:

- `phase`: `Starting`, `Ready`, `Importing`, `Compiling`, `CompilationFailed`, `Reloading`, `PlayModeTransition`, `MainThreadStalled`, or `Exiting`
- `canEval`
- `busyReason`
- `mainThreadTick`
- `isPlaying`, `isPaused`, `isUpdating`
- `compilationCycleId`, compiler error/warning counts and last compilation timestamps
- `vmGeneration`

The Broker determines transport connectivity itself. A live socket does not prove that the Unity main thread is responsive. When the main-thread tick is stale, the Broker derives `MainThreadStalled` even if Unity's last published phase was `Compiling` or `Reloading`; `busyReason` preserves that last reported phase.

## Broker-to-Unity requests

- `eval/execute`: `sessionId`, `requestId`, `code`, `timeoutSeconds`, `resetSession`
- `cli/execute`: `sessionId`, `requestId`, raw `line`
- `session/release`: dispose a named Unity-side eval session
- `broker/ping`: transport-level liveness check

Unity executes `eval/execute` and `cli/execute` while `canEval` is true. `CompilationFailed`
is also executable repair mode for compatibility with clients that predate the repair-mode
`canEval` flag; execution uses the last successfully loaded assemblies. The Broker never
retries an interrupted mutating request automatically.

## Selection handles

There is no process-global selected Unity. `unity_connect` creates an opaque, unguessable `connectionHandle` bound to one registered `instanceId`. MCP calls and CLI consoles carry their own handle. A status snapshot returns `registryRevision`; connect must submit that revision so a stale discovery result cannot silently target a changed registry.

Handles survive registry revision changes and a temporary Domain Reload disconnect for the
same process lifetime, while status exposes the new `connectionEpoch` and `vmGeneration`.
A revision change matters only when creating a new handle. Existing handles expire after
inactivity and become invalid when their instance exits or is replaced. Closing a CLI console,
replacing its selection, or expiring a lease releases the associated Unity-side PuerTS session.
If Unity is temporarily disconnected, the Broker retains the release request for the same
process lifetime and sends it after reconnection.

## Compilation and reload

Every observed Unity compilation receives a `compilationCycleId`, including compilations not initiated through eval. Unity publishes `Compiling` at `CompilationPipeline.compilationStarted`, compiler counts during assembly completion, `CompilationFailed` on errors, and `Reloading` before assembly reload. After reconnect, Unity publishes `Ready` only after a stable main-thread update.

`unity_status` may wait for `ready` or `compilation-complete`. `ready` means execution is
available and therefore returns for normal `Ready` or `CompilationFailed` repair mode;
`compilation-complete` returns after either a successful or failed compilation. Callers must
inspect `phase`, `canEval`, and compiler counts. Before selection, wait by `instanceId`; after
selection, prefer the existing opaque handle. Waiting is event-driven in the Broker and never
runs inside Unity eval. Use `compilationCycleId` for cycle matching; the legacy `requestId`
status parameter is only a deprecated alias and never means the Unity-side request id returned
by `scheduleAssetRefresh`. Immediately before an eval that may compile,
retain a fresh snapshot's `capturedAtUtc` and pass it as `observedAfterUtc`; this prevents an
older cycle or stale `Ready` sample from completing the wait.

## Stable error codes

- `AuthenticationFailed`
- `UnityUpgradeRequired`
- `ProtocolMismatch`
- `InvalidRequest`
- `DiscoveryRequired`
- `RegistryChanged`
- `UnityNotFound`
- `ConnectionHandleRequired`
- `ConnectionHandleInvalid`
- `UnityDisconnected`
- `UnityBusy`
- `CompilationFailed`
- `RequestTimedOut`
- `ExecutionOutcomeUnknown`
- `BrokerUnavailable`

Broker 3.3.0 persists canonical project folders on local Unity registration, reusing existing
workspace IDs. Disconnects retain projects; imported remote instances do not register locally.
Unity snapshots add optional `workspaceId`. Registration request fields and protocol 2.0 are
unchanged; connection authorization covers workspace file/terminal access.
