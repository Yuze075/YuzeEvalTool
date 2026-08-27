# Yuze Agent Tool

**English** | [简体中文](README_zh.md)

Yuze Agent Tool is the shared Editor and Runtime workbench for Unity 2022.3. It depends on
`com.yuzetoolkit.yuzeevaltool` and `com.yuzetoolkit.logtool`, and owns the reusable UI, runtime panel host, DebugPanel lifecycle,
DebugWindow builder API, Agent conversations, Command Line sessions and Unity log viewer.
Its public C# API is under `YuzeToolkit.Agent`; the existing `UnityAgentTool` assembly names
remain stable for asmdef references.

The package manifest directly requires Yuze Eval Tool and Yuze Log Tool. Input System and uGUI are optional
integrations selected by `YUZE_USE_UNITY_INPUT_SYSTEM` and `YUZE_USE_UNITY_UGUI` version defines. Without
Input System the workbench and panels remain available through their normal API but keyboard toggle polling is
omitted. Without uGUI, UI Toolkit interaction remains intact and only synchronization with
`EventSystem.current` is skipped.

## Workbench

The single Editor menu entry **YuzeToolkit > Agent Tool** and the runtime `UnityAgentPanelModule` both create the
same `UnityAgentWorkbenchView`. Its main sidebar has exactly five primary actions:

1. **New conversation** opens an unpersisted draft; its document is created only on first send.
2. **New command line** opens an unpersisted draft; its transcript and process-local VM start on first run.
3. **Debug Panel** displays every runtime `DebugWindowModule.RegisterWindow(...)` registration as a tab.
   Its shell remains available in Edit Mode, but runtime-owned pages are only instantiated while Play Mode is active.
4. **Log** captures Unity logs continuously from Editor domain initialization or runtime startup, independently of
   whether the Log page has been opened. It provides search, type filters, repeat grouping, clear, auto-scroll,
   Stack Trace level, Editor source navigation, local log-file access, a scrollable detail pane and a draggable
   list/detail splitter. Long list rows stay width-bounded and use a one-line summary; the selected entry renders
   its full message in a highlighted card and each stack frame as an individually readable source-aware row.
5. **System Info** displays responsive Agent-styled performance and system cards, while the standalone Runtime overlays preserve their original styling.

Agent and Command Line sessions are listed separately, keep independent input drafts, and support
pinning, archiving and deletion. Archived items leave the main workspace and are restored or deleted
from two separate Settings pages. Settings has six real pages: providers, combined configuration,
Eval connection, Eval Tools, archived conversations and archived command lines. Model discovery warnings
stay inline in the provider page instead of opening repeated dialogs, and all owned choice menus clamp to
the workspace viewport and scroll when their provider, profile or model catalog is long.

Conversation rendering shows User and Assistant text, pending approval cards, and every Tool call as a
collapsed transcript row. Expanding a Tool row reveals its arguments and pending, successful, or failed result.
Tool messages remain fully persisted and are still sent back to the model. The workbench inherits the active Unity PanelSettings / Theme font; it does not bundle, enumerate,
dynamically create or explicitly assign a font.

## Agent loop

The built-in HTTP Agent uses a deliberately small sequential loop: one model response is persisted, each
tool call receives exactly one ordered result, and the model continues until it returns no tool calls. A
turn has a configurable model-step limit (64 by default). Cancellation, an unexpected failure, or a Unity
Domain Reload closes every uncompleted tool call with an explicit error result before the terminal state is
saved, so a later turn never receives an orphaned tool protocol.

Provider profiles store the model context window. When an HTTP conversation approaches that limit, the
complete transcript remains in its conversation document while a semantic summary checkpoint plus the
latest complete message boundary is projected to the model. Transient HTTP network errors, 429 responses,
and recoverable 5xx responses are retried at most twice and only before the first SSE event; partial model
output is never retried.

Every model request receives only the Tools allowed by the conversation permission and current Unity surface,
and execution repeats the same policy check. The modes are:

- **ObserveOnly** exposes bounded read-only Tools. Every new standalone Player conversation starts here.
- **ConfirmWrites** bounds file access to the Unity project in Editor or persistent/cache data in Player and asks
  before every mutating, process, destructive, or full-trust Tool.
- **FullAccess** removes the file boundary and approvals, but deletion still refuses filesystem, user-profile,
  project, and conversation working-directory roots.

Fresh package defaults use ConfirmWrites. Existing valid machine settings keep their explicitly persisted mode.
Tools declare risk, Editor/Player surfaces, and future parallel-safety metadata; the public registry returns a
disposable registration handle so optional modules can remove their own Tools safely.

The built-in Editor and Runtime system prompts are English. They define the Unity role and actual Tool names.
`file_read_text` returns a SHA-256 for files up to 64 MB; `file_apply_patch` accepts files up to 16 MB and requires
that hash, exact old-text occurrence counts, and
performs an atomic replacement with a bounded diff. `unity_snapshot` and `unity_scene_query` provide safe Unity
inspection in ObserveOnly. The full-trust `unity_eval_js` remains available only after the conversation permits
mutating Tools. Exact arguments and detailed execution contracts remain in the structured Tool schemas.

## Standalone Agent boundary

The Agent loop, sessions, approvals, context compaction, Tool dispatch and `unity_eval_js` execution all run
inside the Unity process. The default host directly creates the HTTP model Provider and an in-process
Yuze Eval Tool `EvalExecutor`; it does not start or connect to Codex, a Broker, MCP or the computer-level CLI.
The separate Eval connection page manages optional external access to Yuze Eval Tool and is not an Agent runtime
dependency. Process and shell Tools are Editor-only and start a requested program only when the model explicitly
calls them from a mode that exposes them.

`UnityAgentHost.SendMessageAsync` returns `AgentTurnResult` with the persisted terminal state, error, and usage.
Failures are no longer represented by a successfully completed untyped Task. `UnityAgentHost.StreamEvent` forwards
all Provider stream events plus Tool execution start/completion events with the owning session id; one subscriber
failure is logged without aborting the Agent turn. Compilation recovery checks every returned turn result before
removing its retry marker.

OpenAI models use the OpenAI Responses API with an API key. A ChatGPT/Codex subscription is not an embeddable
Provider credential, so Yuze Agent Tool does not read Codex login caches or expose Codex App Server. Existing
`codex-app-server` profiles are migrated to the standard OpenAI API preset and use the API key saved directly in
their machine-local Provider profile.

In Editor, active conversations are paused when script compilation starts. The package writes a
process- and project-bound recovery marker to `Application.persistentDataPath/.unityagenttool`, interrupts and persists the
turn, then appends one continuation message after a successful Domain Reload or a failed compilation. The
continuation includes compiler counts and tells the Agent to re-inspect Unity state because cached Unity
objects and the JavaScript VM do not survive a reload. A marker from another Editor process is discarded
instead of automatically running work after a restart.

## Persistence

All machine-local Yuze Agent Tool data uses `Application.persistentDataPath/.unityagenttool`. Settings derived from the
package/project defaults and user-owned Provider profiles are stored separately:

```text
settings.json              Package/Project-default-derived machine settings
providers.json             User-created Provider profiles and the selected default profile
```

The directory also contains fixed folders:

```text
AgentConversations/       Agent conversation documents
CommandLineHistory/       Command Line documents and selected-session state
UnityAgentEditorCompilationRecovery.json  Editor-only active-turn recovery marker
```

Data written directly under `Application.persistentDataPath` by the previous layout is migrated by type when the
corresponding file or history is not already present in `.unityagenttool`.

Command Line input, output and drafts survive Unity restarts. JavaScript `EvalSession` instances do not.
Provider profiles, including their API keys, stay in the machine-local `providers.json`. The package-owned
`Runtime/Resources/UnityAgentPackageSettings.json` is the only built-in source for provider-free defaults;
configuration values are not duplicated in C#. An optional
`Assets/Resources/UnityAgentProjectSettings.json` overrides it and is included in Player builds. Project Settings
shows the package JSON until the project override is saved, while **Overwrite Project Settings** in the Yuze Agent Tool
configuration page writes the same provider-free projection through the same validated asset path.
Each AGENTS.md or Skill root independently chooses whether to insert a `.unityagenttool` folder below its
`AgentPathBase`. Skill roots always add the fixed `.agents/skills` directory after that optional namespace, and
`relativePath` stores only the remaining child path. `scope` independently selects direct path discovery in Editor,
Player, both, or neither. `embedInPlayerBuild` copies a build-time snapshot into Player content independently of that
scope; missing snapshot source directories are skipped without failing the build. In Player, live Player/All roots
have priority over embedded snapshots. All four package-default roots use `All` with embedding disabled: the two
ProjectRoot entries disable `.unityagenttool` and resolve to the project root / project `.agents/skills`, while the
two PersistentData entries keep it enabled. Machine settings schema V13 and project schema V6 intentionally replace the old
combined build flag without backward-compatible root migration.

Only machine settings schema V13 and Provider settings schema V1 are accepted. V10, V11, and V12 combined
`settings.json` documents are unsupported and are handled as malformed documents: the file is retained with a
timestamped `.invalid-*` suffix, then the machine layer is rebuilt from the effective defaults.
Their `providerProfiles` are never extracted, and no backward-compatible split migration is performed. When
`providers.json` is missing or malformed, only the Provider layer is created or recovered; a valid `settings.json`
is not replaced. The first Provider document is seeded with the built-in OpenAI profile, while subsequent Provider
profiles are user-owned and are never regenerated from Package/Project defaults. A valid existing machine file is
never changed implicitly by Project Settings. Edit project defaults through
**Edit > Project Settings > YuzeToolkit > Agent Tool**; the page covers permission, Editor/Runtime prompts, Tool
limits, and ordered AGENTS.md/Skill roots. The machine settings layer is schema V13 and the separate Provider layer
is schema V1. Editor Play Mode uses the Editor prompt; the Runtime prompt is reserved for standalone Players.

## Runtime Host

`DebugPanel`, now fully owned by this package, owns one full-screen `UIDocument` and drives `IDebugPanelModule` lifetimes. When
Input System is installed it also drives module toggle keys, with `UnityAgentPanelModule` as the unified F8 workspace. Its header drags the whole window; the
upper-right handle resizes width and height freely inside the panel bounds. Collapse hides the full
content and resize hit area, releases focus, and remains independent from System Info visibility.
The window is bottom-left anchored and its geometry is persisted with `PlayerPrefs`. This package also
supplies the normal composition prefab and the protected System Info / Performance views. The dependency direction is:

```text
Yuze Agent Tool -> Yuze Eval Tool
```

The separate UnityDebugTool package no longer exists.

## DebugWindow API

DebugWindow registration moved into this package and uses the `YuzeToolkit.Agent` namespace:

```csharp
using YuzeToolkit.Agent;

var handle = DebugWindowModule.RegisterWindow(window =>
{
    window.SetTitle("Player");
    window.AddReadOnly("State", () => player.StateName);
    window.AddPrimaryButton("Reset", player.Reset);
    window.AddTextArea("Lua", () => luaCode, value => luaCode = value);
    window.AddChoice("Template", () => templateNames, () => selectedTemplate, value => selectedTemplate = value);
});
```

Registrations do not require a scene host. `DebugWindowModule` only registers visual windows; it never
creates, registers, or disposes an `IEvalTool`. Feature owners implement automation independently and
register its lifetime through `EvalToolRegistry.RegisterRootScoped`. `AddButton` is a neutral action,
`AddPrimaryButton` is the page's primary action, and `AddPreviousButton` / `AddNextButton` are directional
actions. Default boolean, enum, foldout, range, and progress controls use the Agent palette and package-owned
interaction styling instead of Unity's default skin.
`AddTextArea` provides a package-styled multiline editor, `AddReadOnlyTextArea` provides the same presentation for
configuration-owned code, and `AddChoice` provides a package-owned string popup for runtime option lists. Both
controls keep the same binding refresh and popup lifetime rules as the existing fields.
For foldouts whose contents are rebuilt dynamically, use `AddFoldout(label, isOpenGetter, setOpen, configure)`;
the getter and setter keep the foldout state in the feature-owned model instead of resetting it during a window rebuild.
Dynamic choice bindings are refreshed every frame without closing an unchanged popup.

## Assemblies

- `UnityAgentTool`: Agent core, all shared UI, DebugPanel, DebugWindow API, Command Line and Log; Input System shortcuts and uGUI EventSystem synchronization are optional.
- `UnityAgentTool.Editor`: EditorWindow and Editor Broker settings bridge.
- `UnityAgentTool.Editor.Tests`: optional focused EditMode tests when Unity Test Framework 1.4+ is installed.

The package does not expose the old Runtime Console registry, tab-provider assemblies, Eval runtime
page, compatibility providers or DebugWindow MonoBehaviour host.
