# Yuze Eval Tool Broker Protocol

**English** | [简体中文](BROKER_PROTOCOL_zh.md) | [Package README](../README.md)

This document defines the stable boundary between the computer-level Yuze Eval Tool Broker and Unity clients. The Broker owns discovery, status, selection, waiting and routing. Unity owns PuerTS eval sessions, tool registration and CLI command parsing.

## Endpoints

- `http://127.0.0.1:2347/mcp`: MCP Streamable HTTP endpoint.
- `ws://127.0.0.1:2347/unity`: Unity client connection.
- `ws://127.0.0.1:2347/cli`: interactive CLI connection.
- `http://127.0.0.1:2347/health`: Broker health snapshot.

The Broker binds loopback only. It must not silently choose another port when `2347` is
unavailable. The Broker has no global authorization gate. It persists candidate tokens and
routes them to Unity; each Unity connection owns the verification decision. The health
snapshot keeps `requireToken: false` and reports `storedTokenCount` and
`maxStoredTokenCount`.

MCP may provision candidates with `Authorization: Bearer token[/token...]` or
`X-Yuze Eval Tool-Token`. CLI provisions the same list in `cli/hello` through its public
`--token` option. Values use ASCII letters, digits, `_`, and `-`; `/` is only the list
separator. Explicit input is persisted in `~/.unityevaltool/auth.json`. The default capacity
is five, configurable as `maxStoredTokens` in `~/.unityevaltool/config.json`, with a hard
maximum of 32. Duplicate values are not stored twice.

Manual configuration uses this schema (the singular `token` mirrors the first entry for
older readers):

```json
{
  "schemaVersion": 2,
  "token": "project-one_token",
  "tokens": ["project-one_token", "project-two_token"]
}
```

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

- `authToken`: retained as an empty compatibility field
- `instanceId`: stable across Domain Reload for one Unity process
- `connectionEpoch`: incremented for each Unity-side connection generation
- `processId` and `processStartedAtUtc`
- `projectName`, canonical `projectPath`, `unityVersion`, `packageVersion`
- `environment`: `Editor` or `Player`
- `authorizationRequired`: whether this Unity project requires a token
- `authorizationState`: initial `NotRequired` or `Pending` state
- the complete initial `status`

Only the primary Unity Editor process may register. Asset Import Workers must never register or start the Broker.
The successful response includes `brokerInstanceId`, a value unique to the current Broker
process. Unity discards every retained PuerTS session if this value changes, preventing a
new Broker process from accidentally inheriting sessions that it no longer owns. The same
response includes every currently stored candidate in `tokens`. Unity hashes the candidates
with its project salt and publishes `unity/authorization` as `Authorized`, `Pending`, or
`NotRequired`. New stored candidates are delivered later through the `auth/tokens` event.

A pending Unity remains in discovery/status output, but `ready` waits and every MCP/CLI
operation are rejected with `UnityAuthorizationPending`. Verification happens once per
Unity connection; after one candidate succeeds, later commands do not re-check or expire it.

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
- `UnityAuthorizationPending`
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
