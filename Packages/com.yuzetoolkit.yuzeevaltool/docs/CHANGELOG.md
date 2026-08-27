# Unity package changelog

**English** | [简体中文](CHANGELOG_zh.md)

The repository-level [changelog](../../../CHANGELOG.md) is the canonical release history
for the Unity package, computer-level Broker/CLI, npm packages, and Roslyn generator.

## Unreleased

- Replace `YuzeUtilityTool.LitJson` with the directly declared `com.yuzetoolkit.jsontool` dependency and preserve the `EvalJson` dynamic object-tree protocol.

## 3.0.0 - 2026-08-27

- Move all Unity-facing APIs to `YuzeToolkit.Eval`, update generated metadata and JavaScript/PuerTS type paths, and preserve existing `UnityEvalTool*` assembly names.
- Rename the Editor window and Project Settings menu paths to **YuzeToolkit > Eval Tool** and **Project Settings > YuzeToolkit > Eval Tool**.
- Regenerate the committed Roslyn analyzer for the new attribute and descriptor namespace.

## 2.1.0 - 2026-08-27

- Replace the bundled JSON implementation with the shared `YuzeUtilityTool.LitJson` runtime assembly.
- Expose eval-specific JSON-to-primitive conversion through `EvalJson` and normalize malformed input to `FormatException`.

## 2.0.7 - 2026-08-24

- Add `tools://Editor/Profiler` for exact global metric discovery and bounded cross-frame main-thread
  CPU PlayMode sampling with raw statistics, invocation counts, paged samples, and deterministic
  recorder cleanup.

## 2.0.4 - 2026-08-15

- Add per-project Unity authorization with a Resources salted-verifier asset, UI Toolkit
  settings page, standard Player inclusion, and explicit Pending/Authorized states.
- Let MCP/CLI provision multiple persistent Broker candidates while Unity performs the only
  authorization decision; verification remains disabled by default.

## 2.0.3 - 2026-08-15

- Require explicit non-empty safety metadata for every C# and JavaScript Tool function,
  validate it before registration, and distinguish transient runtime-state writes from scene,
  project, Editor, and durable-data mutations.

- Make loopback-only Broker token authentication opt-in instead of a default prerequisite;
  one explicit environment switch applies the token boundary to MCP, Unity, and CLI.
- Add direct viewport image results, optional Test Framework discovery/run/cancel with
  reload-persistent bounded records, serialized code-usage search, and bounded cross-frame
  observation helpers.
- Add matching English and Simplified Chinese package documentation and route complete
  installation and first-use guidance through the repository README.

## 2.0.2 - 2026-08-13

- Prepare package version 2.0.2 and pin Git URL installation to immutable tag `v2.0.2`.
- Keep the committed source-generator analyzer as a normal Git blob for UPM Git installs
  and deterministic binary validation.
- Return eval results through the Broker as native MCP text/image/error content; per-Unity
  serialization prevents a queued request from executing after its caller has timed out.
- Recursively validate JavaScript descriptors, callable child resolution, safety flags, and
  export identifiers during Tool registration. `PersistsData` describes durable
  non-project writes.
- Create Broker auth tokens atomically across processes, bound unauthenticated sockets, and
  abort stalled close handshakes after a deadline.
- Report nested Tool types, async/Task-like functions, and reserved JavaScript export names
  during source generation instead of waiting for runtime registration.
- Preserve authenticated arbitrary JavaScript eval in supported non-WebGL Release Players
  independently of UnityDebugTool.
- Make `CompilationFailed` an executable repair mode backed by the last successfully loaded
  assemblies so MCP/CLI can read errors, edit source, and refresh again.
- Guide agents to wait through Broker status instead of eval polling and to retain the
  existing handle across same-process registry changes and Domain Reload.

## 2.0.1 - 2026-08-12

- Bind PuerTS sessions to Broker lease and CLI-console lifetimes, including deferred release
  across temporary Unity disconnections.
- Prevent an older Broker reconnect generation from overwriting a newer connection, and
  reset sessions owned by a previous Broker process after replacement.
- Publish Editor status through a throttled normal-update heartbeat without continuously
  requesting additional player-loop updates.
- Synchronize package and Broker versions, and keep Roslyn generator source in the
  repository `Roslyn` directory instead of an embedded source archive.

## 2.0.0 - 2026-08-12

- Replace Unity-hosted MCP/CLI listeners with one authenticated, computer-level NativeAOT
  Broker on `127.0.0.1:2347`.
- Add event-driven Unity discovery, selection handles, compilation waiting, status phases,
  native CLI service management, and six-platform npm packaging.
