# Changelog

**English** | [简体中文](CHANGELOG_zh.md)

## 3.0.0 - 2026-08-27

- Move the Unity API from the shared `YuzeToolkit` namespace to `YuzeToolkit.Eval`, the native Broker to `YuzeToolkit.Eval.Broker`, and the Roslyn generator to `YuzeToolkit.Eval.SourceGenerator` while preserving all existing assembly names and protocol identifiers.
- Move Yuze Agent Tool 0.4.0 from mixed `YuzeToolkit` / `YuzeToolkit.UnityAgent` namespaces to `YuzeToolkit.Agent`, update project consumers, and keep its assembly names and persisted data paths stable.
- Reduce the Editor surface to the single **YuzeToolkit > Agent Tool** entry, expose Eval through **YuzeToolkit > Eval Tool**, and remove the repeated Yuze prefix from both Project Settings paths.
- Regenerate the committed analyzer and keep the Broker, npm package and Unity runtime version metadata synchronized; the npm name remains `@yuzetoolkit/unityevaltool` and the Broker protocol remains 2.0.

## 2.1.0 - 2026-08-27

- Replace the Unity package's bundled JSON implementation with `YuzeUtilityTool.LitJson` and expose the eval-specific primitive conversion surface as `EvalJson`.
- Make malformed Agent wire JSON consistently surface as `FormatException`, and update Yuze Agent Tool to 0.3.2 against Yuze Eval Tool 2.1.0.
- Keep the Broker, npm package and Unity runtime version metadata synchronized; the Broker protocol remains 2.0.

## 2.0.7 - 2026-08-24

- Add the Editor-only `tools://Editor/Profiler` helper for global `ProfilerRecorder` metric
  discovery and bounded main-thread CPU PlayMode sampling by exact category/name pairs.
  Cross-frame sessions include warmup, guarded sample windows, raw statistics and invocation
  counts, paged samples, and deterministic cleanup on PlayMode pause/exit, Domain Reload, and
  Editor quit.

## 2.0.6 - 2026-08-18

- Emit an explicit object-form permissive `{}` output schema for the `unity_status` and `unity_connect` Broker
  tools. The MCP C# SDK derives the boolean JSON Schema `true` for `JsonElement` returns, which violates the
  `outputSchema.properties` object requirement of every MCP protocol version the Broker negotiates and made
  strict clients such as kimi-code reject the whole server; `{}` keeps the same `{"result": ...}` wire envelope
  and structuredContent shape.
- Replace the combined instruction-root Player build flag with independent None/EditorOnly/PlayerOnly/All live-path
  scope and build-time embedding controls. Player now loads live Player/All roots before embedded snapshots, all
  default roots use All with embedding disabled, and missing embedded source directories no longer fail builds.
- Make the root configuration a deliberate breaking change through machine schema V12 and project schema V6, with
  no compatibility migration for the removed field.

## 2.0.5 - 2026-08-17

- Pass service-manager arguments as exact vectors on every desktop OS, verify the Windows Scheduled Task action
  after creation, wait for Broker readiness after start operations, and make unavailable service status fail.
- Register the native CLI installation once per process and publish `install.json` through a bounded cross-process
  lock plus same-directory atomic replacement; retry Windows atomic publication while short-lived readers are open.
- Preserve Windows backslashes and empty arguments across the native CLI-to-Unity command protocol, and apply
  case-sensitive project containment on case-sensitive file systems.
- Harden the embedded Agent with ObserveOnly/ConfirmWrites/FullAccess capability modes, Tool risk and Editor/Player
  surfaces, request-time exposure filtering, execution-time enforcement, bounded file roots, and protected deletion.
- Add SHA-256 guarded atomic `file_apply_patch`, safe `unity_snapshot` / `unity_scene_query` inspection Tools,
  explicit `AgentTurnResult`, complete Host stream forwarding, and compilation-resume failure detection.
- Add six focused Yuze Agent Tool EditMode tests for policy, registration lifetime, path boundaries, exact patching,
  terminal failure results, and Tool execution events; bump the Agent package to 0.3.0.
- Let each AGENTS.md and Skill root opt in or out of the `.unityagenttool` namespace, include every package-default
  root in Player content, and make the project defaults resolve directly from ProjectRoot.
- Render long Tool arguments and results as bounded plain-text chunks inside a capped scroll region so one UI Toolkit
  text element cannot exceed Unity 2022.3's vertex limit.
- Make the package JSON the single built-in source for provider-free Yuze Agent Tool defaults, add an optional
  project Resources override, and rebuild missing or invalid machine settings while retaining malformed files.
- Unify Project Settings persistence with the Yuze Agent Tool workspace action that explicitly overwrites the
  provider-free project defaults.
- Keep settings, secrets, histories, and compilation recovery under
  `Application.persistentDataPath/.unityagenttool`, migrating data from the previous direct persistent-data layout.
- Represent instruction roots with stable `AgentPathBase` anchors, an optional `.unityagenttool` namespace, the fixed
  `.agents/skills` suffix for Skills, and relative JSON child paths; normalize matching machine roots through schema V11.

## 2.0.4 - 2026-08-15

- Move token authorization to each Unity connection. Projects default to disabled, store
  only a salted PBKDF2 verifier in a Resources asset, and remain discoverable but refuse all
  operations while verification is pending.
- Make the Broker a credential store/router instead of an authorization gate. MCP Bearer
  input and CLI `--token` persist up to five raw candidate tokens by default, support manual
  `auth.json` configuration, and broadcast candidates to pending Unity clients.
- Include public verification settings in Player builds through Unity's standard Resources
  pipeline, and document the Player binary-patching limitation.

## 2.0.3 - 2026-08-15

- Require every C# and loader-backed JavaScript Eval Function to declare non-empty safety
  metadata, reject invalid read/write combinations during registration, and report transient
  process or Tool-owned writes through the new `MutatesRuntimeState` flag.
- Publish the accumulated Unity package, Broker/CLI, npm, Roslyn, Tool, and Agent changes as
  one immutable 2.0.3 source and artifact set instead of reusing the published 2.0.2 version.

- Split the built-in Agent prompt into explicit Editor-development and standalone-Player diagnosis workflows,
  route every built-in file, process, Skill, and Unity Tool by task target, and provide an executable
  `tools://` discovery quick start. Settings schema migration updates existing default prompts.
- Disable token authentication by default for the loopback Broker so MCP and CLI work from
  endpoint-only configuration. `UNITYEVALTOOL_REQUIRE_TOKEN=true` explicitly restores the
  shared MCP, Unity, and CLI token boundary; health and doctor output expose the active mode.
- Add synchronous Game/Scene/Editor-window image capture, a complete optional Unity Test
  Framework Tool with reload-persistent bounded records, serialized code/member usage
  search, and bounded cross-frame runtime observation sessions.
- Rebuild the public documentation as matching English and Simplified Chinese guides,
  make the repository README the complete installation and first-use entry point, separate
  reproducible source packaging from maintainer-defined distribution, and remove
  host-project-specific development instructions.

## 2.0.2 - 2026-08-13

- Preserve npm metadata lookup failures during artifact preflight and pass tarballs through
  build checks as explicit local paths.
- Pin .NET SDK 10.0.300, exclude source-control revisions from the SourceGenerator
  assembly, and regenerate the committed analyzer so byte-for-byte validation is stable
  across repository layouts.
- Correct committed-version evaluation in build automation before package validation.
- Prepare version `2.0.2`: Yuze Eval Tool, Broker, and npm packages use 2.0.2;
  UnityDebugTool 1.0.1 depends on Yuze Eval Tool 2.0.2.
- Make multi-package artifact validation SHA-bound, concurrency-safe, smoke-tested,
  version-preflighted, and recoverable when an immutable artifact already exists.
- Store the committed Unity analyzer as a normal Git blob so UPM Git installs and binary
  validation receive the actual DLL.
- Remove install/uninstall lifecycle dependence: service setup and removal are explicit,
  checked `unity service install|uninstall` steps around global npm install/uninstall.
- Keep the inherited copyright notice and current copyright consistently in the repository
  and both UPM package license files.
- Return Unity eval output as native MCP text/image blocks with the correct top-level error
  bit instead of nesting CallToolResult-shaped JSON as structured text.
- Serialize commands per Unity connection: queued cancellation and timeout no longer send,
  while an interrupted sent command remains explicitly outcome-unknown and blocks later
  execution until resolved.
- Make cold-start auth-token publication cross-process atomic, bound unauthenticated first
  frames and connection count, and bound every WebSocket close path before aborting an
  unresponsive peer.
- Validate complete JavaScript Tool trees, callable sub-tool resolvers, explicit safety
  flags, and non-reserved export names before registration; add durable-data risk metadata
  and owner-aware root removal.
- Diagnose unsupported nested, asynchronous, and JavaScript-reserved C# Eval functions at
  generation time and make Roslyn integration tests independent of their output directory.
- Rework UnityDebugTool registration rollback, input focus, bounded logs, recursive Tool
  catalog, performance buffers, and IL2CPP preservation. Visual layout metadata no longer
  creates implicit Eval Tools; callers use an explicit Tool tree.
- Preserve authenticated arbitrary JavaScript eval in supported non-WebGL Release Players
  as an intentional runtime contract independent of the optional UnityDebugTool UI.
- Add `com.yuzetoolkit.unitydebugtool` under `Packages` so the runtime debug UI and
  Yuze Eval Tool share one source repository while retaining package-specific READMEs.
- Keep MCP/CLI executable in `CompilationFailed` repair mode through the last successful
  Unity assemblies while continuing to reject compile/import/reload transitions.
- Clarify event-driven compilation waits and same-process handle reuse across registry
  changes, and add Broker state-policy regression tests.

## 2.0.1 - 2026-08-12

- Release Unity-side PuerTS sessions when CLI consoles close or Broker leases expire,
  including deferred release across a temporary Unity disconnect.
- Isolate Unity Broker client connection generations so a stopped reconnect loop cannot
  tear down a newly started connection.
- Detect Broker process replacement and reset Unity-side sessions that belonged to the
  previous Broker.
- Replace the self-sustaining Editor player-loop wakeup with a throttled status heartbeat
  driven by normal Editor updates.
- Keep Broker, Unity package, npm, and runtime versions synchronized from the committed
  `version.json` version.
- Store the Roslyn generator as ordinary repository source in `Roslyn` instead of embedding
  a source archive in the Unity package.

## 2.0.0 - 2026-08-12

- Replace Unity-hosted MCP and CLI listeners with a computer-level C# NativeAOT Broker on
  `127.0.0.1:2347`.
- Add authenticated registration and state reporting for multiple Unity Editor and Player
  processes.
- Add explicit compilation, assembly reload, import, play-mode transition, disconnection,
  and main-thread-stall states.
- Reduce the MCP surface to `unity_status`, `unity_connect`, and `eval`, with mandatory
  discovery and selection before execution.
- Add event-driven readiness and compilation-completion waits that continue across Unity
  Domain Reload.
- Add the native `unity` CLI with project-path auto-selection, instance selection, one-shot
  commands, and an interactive console that reuses Unity's existing parser.
- Add current-user service integration for macOS LaunchAgent, Linux systemd user units, and
  Windows Scheduled Tasks.
- Add npm packaging for macOS, Linux, and Windows on x64 and arm64, with a six-platform
  artifact build matrix.
- Move the Unity Package Manager package to `Packages/com.yuzetoolkit.yuzeevaltool` and the
  Broker source to `Broker`.

This is a protocol and distribution breaking release. Remove legacy UnityCLI installations
and configure MCP clients for the authenticated port 2347 endpoint.
