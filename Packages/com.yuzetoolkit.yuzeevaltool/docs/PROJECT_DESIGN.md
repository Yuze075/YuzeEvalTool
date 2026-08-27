# Yuze Eval Tool architecture

**English** | [简体中文](PROJECT_DESIGN_zh.md) | [Package README](../README.md)

## Process boundary

```text
AI MCP clients ──HTTP /mcp──┐
                            ├── C# NativeAOT Broker :2347 ──WebSocket /unity── Unity A
native unity CLI ──WS /cli──┘                                      └────────── Unity B
```

The Broker owns discovery, status snapshots, event-driven waits, selection leases, local
candidate-token storage, MCP protocol handling, CLI consoles, and request routing. Unity owns
authorization, main-thread truth, compilation/reload observation, PuerTS VMs, helper tool
registration, and CLI command parsing.

## Source ownership

| Area | Source | Responsibility |
|---|---|---|
| Native Broker | Repository `Broker/src/UnityEvalTool.Broker` | Port 2347, registry, MCP tools, CLI, service management |
| npm packaging | Repository `Broker/npm` | Platform selection, explicit user-service helpers, per-RID packages |
| Unity transport | `Runtime/Broker` | Registration, project-token verification, heartbeat, routed requests, sessions |
| Editor lifecycle | `Editor/Broker` | Stable process identity, Resources authorization settings, compile/reload state, Broker startup |
| Eval engine | `Runtime/Core` and `Runtime/Tools` | PuerTS execution and generated helper modules |
| CLI parser | `Runtime/CLI/EvalCliCommandService*` | Existing command grammar and printable results |

`Broker` and `Roslyn` live at the standalone repository root, outside the UPM package and a Unity project's `Assets` tree, so Unity does not import their .NET/npm sources.

## Registry and selection

Every Unity registration includes a stable per-process `instanceId`, process lifetime,
project path, connection epoch, VM generation, and complete status. Membership changes
increment `registryRevision`. `unity_connect` requires the exact revision from a preceding
status snapshot and creates a random 256-bit handle. Selection is therefore per MCP
workflow or CLI console, never Broker-global.

## Compilation lifecycle

The Editor observes every `CompilationPipeline` cycle. It reports `Compiling`, compiler
counts, `CompilationFailed`, and `Reloading` before the scripting domain disappears.
The Broker retains the disconnected instance and selection lease. After reload, the same
process reconnects with a higher epoch and VM generation, then reports `Ready` after a
main-thread update. Waiting occurs inside the Broker and does not depend on eval. A failed
compilation keeps the old domain loaded and enters executable repair mode.

## Execution guarantees

- Eval requires status discovery, explicit connect, and a valid handle.
- Unity must be connected and either `canEval` or be in `CompilationFailed` repair mode
  before a request is forwarded.
- Repair mode executes the last successfully loaded assemblies so tools can read errors,
  edit source, and request another refresh; it does not expose the failed source as loaded code.
- Each handle/CLI console has a persistent Unity-side PuerTS session.
- Reconnect changes VM generation; sessions lost with the old scripting domain are not
  represented as if they survived.
- Mutating requests are never retried after an uncertain disconnect.

## Security

Kestrel listens on loopback only. The Broker rejects non-loopback Host/Origin values and has
no global authorization policy. MCP/CLI input or a user-edited `auth.json` supplies raw
candidate tokens; the Broker persists and forwards them. Each Unity project defaults to no
verification, or stores a salted PBKDF2 verifier in a Resources asset and refuses every
operation until one candidate matches. A matched connection stays authorized for that
connection lifetime. Port conflicts, invalid credential/config files, invalid project
verification data, and protocol mismatches fail explicitly.
