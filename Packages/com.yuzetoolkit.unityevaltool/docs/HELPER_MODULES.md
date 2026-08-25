# Helper Reference

**English** | [简体中文](HELPER_MODULES_zh.md) | [Package README](../README.md) | [Runtime services](RUNTIME_SERVICES.md) | [Architecture](PROJECT_DESIGN.md) | [Advanced usage](ADVANCED_USAGE.md)

[![Runtime](https://img.shields.io/badge/Runtime-7%20modules-2ecc71)](#runtime-helpers)
[![Editor](https://img.shields.io/badge/Editor-12%20modules-3498db)](#editor-helpers)
[![Catalog](https://img.shields.io/badge/Tool%20catalog-1%20module-8e44ad)](#tool-catalog)
[![Tool](https://img.shields.io/badge/Broker%20MCP-3%20tools-orange)](../../../README.md#mcp-setup)

After `unity_status` and `unity_connect`, the Broker `eval` tool runs inside the selected Unity. Within that eval, agents import helper modules from `tools://` and `tools://<Tool/Path>`. Built-in modules are generated from partial C# classes marked with `[EvalTool(name, description)]`; the source generator emits their `IEvalTool` metadata. Each C# module exports semantic functions that validate the tool is enabled, call public C# instance methods through PuerTS, and leave final result formatting to the Unity-side executor. Project and package JavaScript extensions are loaded explicitly through `tools://UnityEval`.

Generated C# helpers should prefer primitives, `List<T>`, `Dictionary<string, TValue>`, or data composed from those types. The server returns those values as JSON text content, which is the most stable and recommended tool result shape.

Use helper modules first for common workflows because they expose compact descriptions and stable return data. When a helper does not cover the task, run Unity/C# APIs directly with PuerTS `CS.*` interop inside `eval`; promote repeated project-specific code into a C# tool or explicitly loaded JavaScript helper.

Start discovery with:

```javascript
async function execute() {
  const index = await import('tools://');
  return index.description;
}
```

## Module Index

| Category | Modules |
|---|---|
| Tool catalog | `tools://UnityEval` |
| Runtime helpers | `tools://Runtime`, `tools://Runtime/Objects`, `tools://Runtime/Components`, `tools://Runtime/Diagnostics`, `tools://Runtime/Reflection`, `tools://Runtime/Inspect`, `tools://Runtime/ObserveFrames` |
| Editor helpers | `tools://Editor`, `tools://Editor/Assets`, `tools://Editor/Importers`, `tools://Editor/Scenes`, `tools://Editor/Prefabs`, `tools://Editor/Serialized`, `tools://Editor/Project`, `tools://Editor/Profiler`, `tools://Editor/Pipeline`, `tools://Editor/Tests`, `tools://Editor/CodeUsages`, `tools://Editor/Validation` |

Runtime helpers can run in Editor or Runtime/Player when the underlying Unity API is available. Editor helpers require `UnityEditor` and fail clearly in Runtime/Player.

Generated helper functions use positional arguments, such as `const assets = await import('tools://Editor/Assets'); assets.find('t:Prefab', 20, ['Assets'])`. Each generated C# module exposes `functions[].description`, ordered `functions[].parameters`, declared safety flags, conditional safety hints such as `conditionalRequiresConfirmation`, and `isEnabled()` for the current enabled state.

## Tool Catalog

### `tools://UnityEval`

Catalog inspection, enabled-state management, and JavaScript Tool authoring guidance.

| Function | Purpose | Safety |
|---|---|---|
| `listTools(refresh?)` | List registered C# and loader-backed JavaScript Tools. | Read-only |
| `getToolDetails(name, refresh?)` | Return complete metadata for one Tool path. | Read-only |
| `setToolEnabled(name, enabled)` | Enable or disable one C# or JavaScript Tool; Editor state persists by Tool path. | Mutates Editor state |
| `getJsToolAuthoringPrompt()` | Return the current loader-backed JavaScript Tool authoring contract. | Read-only |

## Runtime Helpers

### `tools://Runtime`

Environment state and Unity logs.

| Function | Purpose | Safety |
|---|---|---|
| `getState()` | Environment, Unity version, platform, play state, paths, active scene, registered tools. | Read-only |
| `getRecentLogs(count?, type?)` | MCP-captured Unity logs. | Read-only |
| `clearLogs()` | Clear the MCP log buffer. | Mutates transient runtime state |

### `tools://Runtime/Objects`

Scene GameObject, hierarchy, and Transform operations.

| Function | Purpose | Safety |
|---|---|---|
| `find(name, limit?)` | Find active GameObjects by exact `name`; returns lightweight selectors. | Read-only |
| `findOne(name)` | Find the first active GameObject by exact `name`; returns a lightweight selector. | Read-only |
| `findByPath(path, includeInactive?)` | Find one GameObject by exact hierarchy path. | Read-only |
| `findByTag(tag, limit?)` | Find active GameObjects by tag using Unity tag lookup. | Read-only |
| `get(target)` | Inspect one GameObject. | Read-only |
| `create(name?, primitive?, parent?, localPosition?, position?, localScale?)` | Create an empty or primitive GameObject. | Mutates scene |
| `destroy(target, confirm?)` | Destroy a GameObject. | Requires `confirm: true` |
| `duplicate(target, name?)` | Duplicate a GameObject. | Mutates scene |
| `setParent(target, parent?, worldPositionStays?)` | Change hierarchy parent. | Mutates scene |
| `setTransform(target, position?, localPosition?, rotationEuler?, localRotationEuler?, localScale?)` | Set position, rotation, or scale. | Mutates scene |
| `setActive(target, active)` | Change active state. | Mutates scene |
| `setNameLayerTag(target, name?, layer?, tag?)` | Change name, layer, or tag. | Mutates scene |

### `tools://Runtime/Components`

Component reads, edits, and instance method calls.

| Function | Purpose | Safety |
|---|---|---|
| `list(target)` | List components on a GameObject. | Read-only |
| `get(target, type?, index?, includeValues?)` | Read one component by type/index. Defaults to member definitions only; pass `includeValues = true` to invoke public getters. | Read-only |
| `find(typeName, limit?, includeInactive?)` | Find live Components by C# type name; returns component summaries and GameObject selectors. | Read-only |
| `add(target, type)` | Add a Component. | Mutates scene |
| `remove(target, type?, index?, confirm?)` | Remove a Component. | Requires `confirm: true` |
| `setProperty(target, type, member, value, index?, includeNonPublic?, includeStatic?, confirmDangerous?)` | Set one field/property. | Mutates component |
| `setProperties(target, type, values, index?, includeNonPublic?, includeStatic?, confirmDangerous?)` | Set multiple fields/properties. | Mutates component |
| `callMethod(target, type, method, args?, index?, includeNonPublic?, confirmDangerous?)` | Call an instance method. | Method-dependent |
| `listTypes(query?, limit?)` | Search available Component types. | Read-only |

`Runtime/Objects.find` intentionally no longer accepts `path`, `tag`, `component`, or `includeInactive` selector modes. Use `findByPath`, `findByTag`, or `Runtime/Components.find` so each query has one clear cost model. `Runtime/Components.get` does not read every public property value by default because Unity component getters can be expensive; request values only when needed. Non-public method calls require `includeNonPublic: true` and `confirmDangerous: true`.

### `tools://Runtime/Diagnostics`

Read-only runtime diagnostics.

| Function | Purpose |
|---|---|
| `listCameras()` | Scene cameras and common settings. |
| `getPhysicsState()` | Physics2D/3D settings plus Collider and Rigidbody summaries. |
| `getGraphicsState()` | Render pipeline, quality, color space. |
| `listCanvases()` | Canvas objects, render settings, and EventSystems. |
| `listLoadedTextures(limit?)` | Loaded texture objects with size and type. |

### `tools://Runtime/Inspect`

Formatting helpers for C#/Unity object references.

| Function | Purpose |
|---|---|
| `describe(value?, depth?)` | Return a default summary DTO. |
| `format(value?, mode?, depth?)` | Format a value with mode `default`, `summary`, `name`, `path`, `text`, `json`, or `yaml`. |
| `toName(value?)` | Return a Unity/C# object's name. |
| `toPath(value?)` | Return a scene hierarchy path or asset path. |
| `toJson(value?, mode?, depth?)` | Return a JSON string for a formatted value. |
| `toYaml(value?, depth?)` | Return a YAML string for a formatted value. |

### `tools://Runtime/Reflection`

C# type discovery and static method calls for project-specific APIs.

| Function | Purpose | Safety |
|---|---|---|
| `getNamespaces()` | List public namespaces. | Read-only |
| `getTypes(namespaceName)` | List public types in a namespace. | Read-only |
| `getTypeDetails(fullName)` | List public members for a type. | Read-only |
| `findMethods(query?, type?, includeNonPublic?, confirmDangerous?, limit?)` | Search public methods. | Non-public search requires `confirmDangerous: true` |
| `callStaticMethod(type, method, args?, includeNonPublic?, confirmDangerous?)` | Call a static method. | Non-public call requires `confirmDangerous: true` |

### `tools://Runtime/ObserveFrames`

Bounded cross-frame observation of public fields and readable properties. Component probes use
`{ name, kind: "component", target, type, member, index? }`; static probes use
`{ name, kind: "static", type, member }`.

| Function | Purpose | Safety |
|---|---|---|
| `start(probes, maxFrames?, intervalFrames?, maxSamples?, until?, label?)` | Capture an initial sample and start sampling on later Editor updates or Player frames. `until` accepts `{ probe, op, value? }`, where `op` is `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `truthy`, or `falsy`. | Read-only, long-running |
| `get(id, offset?, limit?)` | Read a page of at most 500 samples and current completion state. | Read-only |
| `list(status?, limit?)` | List retained session summaries. | Read-only |
| `cancel(id)` | Stop sampling while retaining captured samples. | In-memory state only |
| `release(id)` | Release a session and its samples. | In-memory state only |

A session is limited to 32 probes, 36,000 observation frames, and 10,000 samples; at most eight
sessions run concurrently and 64 are retained. Formatted values and retained session data also
have explicit character budgets; a session completes with `storage-limit` before retaining more
than 8,388,608 JSON characters. Sampling invokes only the explicitly named field/property getter.
Known scalar and Unity value types are formatted directly; arrays plus exact `List<>` and
`Dictionary<,>` values are expanded to depth 4 and 128 entries, while arbitrary custom objects are
returned as type summaries without calling `ToString()` or inspecting additional properties. A
single string is limited to 4,096 characters and one formatted value to 32,768 characters. In Edit
Mode, one observation frame means one Editor update. Sessions are process-memory state and do not
survive Domain Reload; use the test tool for reload-persistent test execution state.

## Editor Helpers

### `tools://Editor`

Editor state, compilation, selection, menu commands, play mode, and screenshots.

| Function | Purpose | Safety |
|---|---|---|
| `getState()` | Editor state, active scene, selection summary. | Read-only |
| `getCompilationState()` | Unity-side diagnostic request state and error/warning counts. Use Broker `unity_status` for waiting. | Read-only |
| `requestScriptCompilation()` | Request script compilation once, then return from eval and wait through Broker status; exits PlayMode first when needed. | May trigger reload |
| `scheduleAssetRefresh()` | Request script-safe AssetDatabase refresh once, then return from eval and wait through Broker status; exits PlayMode first when needed. | May trigger reload |
| `getCompilerMessages(count?)` | Recent compiler-like errors/warnings. | Read-only |
| `getSelection()` / `setSelection(items)` | Read or set Editor selection. | Selection mutation |
| `executeMenuItem(path, confirm?)` | Execute an Editor menu item. | Non-UnityEvalTool menu requires `confirm: true` |
| `setPlayMode(isPlaying)` / `setPause(isPaused)` | Control play/pause state. | Changes Editor state |
| `screenshotGameView(path?)` | Capture Game View. | Writes screenshot file |
| `captureViewport(target?, maxLongEdge?, windowQuery?)` | Synchronously return a Game View, Scene View, or visible Editor-window PNG as an MCP image plus dimensions and source metadata. | Read-only |

`captureViewport` accepts `game`, `scene`, or `editor_window`. `maxLongEdge = 0` preserves the
source size; values from 1 through 8192 downscale proportionally when needed. Arbitrary Editor
windows must be the selected visible tab and Unity must be the foreground application. The older
file-writing `screenshotGameView` contract remains unchanged. Game and Scene render textures are
downscaled on the GPU before readback. Hard limits reject an Editor-window source above 8,388,608
pixels, any output above 16,777,216 pixels, or an encoded PNG above 33,554,432 bytes; choose a
smaller `maxLongEdge` when an output exceeds those bounds.

### `tools://Editor/Assets`

AssetDatabase search, project text IO, dependencies, scripts, materials, and script-safe refresh.

| Function | Purpose | Safety |
|---|---|---|
| `find(filter, limit?, folders?)` / `findPaths(filter, limit?, folders?)` / `findNames(filter, limit?, folders?)` | Search assets with Unity filters as summaries, paths, or names. | Read-only |
| `getInfo(path)` | Asset metadata. | Read-only |
| `readText(path)` / `writeText(path, text, refresh?, confirmOverwrite?)` | Read or write text assets. | Write mutates project |
| `createFolder(name, parent?)` | Create an AssetDatabase folder. Defaults `parent` to `Assets`. | Mutates project |
| `copy(from, to, confirmOverwrite?)` | Copy an asset. | Mutates project |
| `move(from, to, confirm?)` | Move or rename an asset. | Requires `confirm: true` |
| `deleteAsset(path, confirm?)` | Delete an asset. | Requires `confirm: true` |
| `refreshNow()` | Request script-safe AssetDatabase refresh and return compile/import state; exits PlayMode first when needed. | May trigger reload |
| `getDependencies(path, recursive?)` | Asset dependencies. | Read-only |
| `findReferences(path, folders, limit?)` | Search asset references inside explicit folders. Defaults to 100 results when limit is omitted. | Read-only |
| `createScript(path, className?, namespaceName?, confirmOverwrite?)` | Create a MonoBehaviour script. | May trigger reload |
| `applyScriptTextEdits(path, edits, refresh?, confirm?)` | Patch a script file. | May trigger reload |
| `createMaterial(path, shaderName?, properties?, confirmOverwrite?)` | Create a Material asset. | Mutates project |

### `tools://Editor/Importers`

AssetImporter inspection and edits.

| Function | Purpose | Safety |
|---|---|---|
| `get(path, includeProperties?, propertyLimit?)` | Importer summary and optional serialized properties. Property enumeration is capped. | Read-only |
| `setProperty(path, propertyPath, value, saveAndReimport?, confirm?)` | Set one importer SerializedProperty. | Mutates importer |
| `setMany(path, changes, saveAndReimport?, confirm?)` | Set many importer properties; `changes` accepts a `{ propertyPath: value }` map or `{ propertyPath, value }[]`. | Mutates importer |
| `reimport(path, confirm?)` | Force reimport. | Requires `confirm: true` |

### `tools://Editor/Scenes`

Scene files and open scene hierarchy.

| Function | Purpose | Safety |
|---|---|---|
| `listOpenScenes()` | List open scenes. | Read-only |
| `getSceneHierarchy(depth?, includeComponents?, limit?)` | Read open scene hierarchy. | Read-only |
| `openScene(path, mode?, confirm?, saveDirtyScenes?)` | Open a scene. | Changes Editor state |
| `createScene(path?, setup?, mode?, confirm?, saveDirtyScenes?)` | Create a new scene. | Mutates project/session |
| `saveScene()` / `saveSceneAs(path)` | Save scene. | Writes scene asset |
| `setActiveScene(path)` | Set active scene. | Changes Editor state |

### `tools://Editor/Prefabs`

Prefab instance, asset, stage, override, and unpack operations.

| Function | Purpose | Safety |
|---|---|---|
| `instantiate(path, parent?, position?)` | Instantiate a Prefab asset. | Mutates scene |
| `createFromObject(target, path, confirmOverwrite?)` | Save a scene object as Prefab. | Mutates project |
| `createVariant(basePath, path, confirmOverwrite?)` | Create a Prefab variant. | Mutates project |
| `openStage(path)` / `closeStage()` / `saveStage()` | Prefab Stage workflow. | Changes Editor/project state |
| `getOverrides(target)` | Read Prefab overrides. | Read-only |
| `applyOverrides(target, confirm?)` | Apply overrides. | Requires `confirm: true` |
| `revertOverrides(target, confirm?)` | Revert overrides. | Requires `confirm: true` |
| `unpack(target, mode?, confirm?)` | Unpack a Prefab instance. | Requires `confirm: true` |

### `tools://Editor/Serialized`

SerializedObject and Inspector property reads/writes.

| Function | Purpose | Safety |
|---|---|---|
| `get(target, propertyPath?, limit?)` | Read one `propertyPath`, or up to `limit` visible properties when no path is provided. | Read-only |
| `set(target, propertyPath, value, confirm?)` | Set one SerializedProperty value. | Mutates object/asset |
| `setMany(target, changes, confirm?)` | Set multiple SerializedProperty values; `changes` accepts a `{ propertyPath: value }` map or `{ propertyPath, value }[]`. | Mutates object/asset |
| `resizeArray(target, propertyPath, size, confirm?)` | Resize serialized array. | Mutates object/asset |
| `insertArrayElement(target, propertyPath, index?, confirm?)` | Insert array element. | Mutates object/asset |
| `deleteArrayElement(target, propertyPath, index, confirm?)` | Delete array element. | Mutates object/asset |

Targets can be `assetPath`, `guid`, `instanceId`, or a GameObject selector.
Supported write value types include integer, boolean, float, string, color, object reference, enum, Vector2/3/4, Quaternion, Vector2Int/3Int, Rect/RectInt, Bounds/BoundsInt, and AnimationCurve. Unsupported property kinds fail with an explicit error.

### `tools://Editor/CodeUsages`

Bounded serialized usage search for one compiled `MonoScript` and an optional member name.

| Function | Purpose | Safety |
|---|---|---|
| `search(scriptPath, folders, member?, limit?)` | Without `member`, find MonoBehaviour and ScriptableObject attachment points. With `member`, find matching serialized fields, UnityEvent method bindings (including unresolved targets), and AnimationEvent function-name matches. | Read-only, long-running |

`folders` is required and must contain one AssetDatabase folder or an array of folders under
`Assets` or `Packages`; no implicit project-wide scan occurs. The result reports candidate,
serialized-owner, result, property, and YAML fallback limits together with truncation reasons and
bounded per-asset errors. Candidate paths retained by the tool are split across bounded asset-type
quotas and the result includes per-type search statistics; Unity's `AssetDatabase.FindAssets`
still allocates its own GUID array before those tool-side bounds can apply. UnityEvent results
distinguish a `missingTarget` (`fileID: 0`) from a nonzero serialized target that is not live-loaded.
AnimationEvent records are explicitly name-only candidates because an AnimationClip does not
serialize the receiving C# type. LDtk files are excluded.

### `tools://Editor/Project`

Project-level diagnostics.

| Function | Purpose |
|---|---|
| `getProjectSettings()` | Product/company/application id, tags, layers. |
| `getProfilerState()` | Profiler availability and recording flags. |
| `getToolState()` | Active Editor tool, pivot mode, pivot rotation. |

### `tools://Editor/Profiler`

Global Profiler metric discovery plus bounded CPU `ProfilerRecorder` sampling in stable, unpaused
PlayMode. Sampling can target the main thread or all threads. This helper does not enable the global
Profiler or Profiler Window recording.

| Function | Purpose | Safety |
|---|---|---|
| `listAvailable(category?, nameContains?, limit?)` | List globally registered metrics with exact category/name, unit, and data type. `category` is exact; `nameContains` is case-insensitive; both accept at most 512 characters. | Read-only |
| `start(metrics, warmupFrames?, sampleFrames?, label?, threadScope?)` | Start CPU sampling for 1..16 exact `{category,name}` pairs after 0..36,000 warmup frames and retain 1..10,000 Player frames. `threadScope` is exactly `main-thread` (default) or `all-threads`. Category/name strings accept at most 512 characters. | Runtime state, long-running |
| `get(id, includeSamples?, offset?, limit?)` | Read validity, status, raw total/min/mean/mean-per-invocation/p50/p95/max, invocation count, unit, and data type. Time metrics also include millisecond fields. Optional `{value,count}` samples are paged per metric, at most 500 each. | Read-only |
| `cancel(id)` | Stop an active session and retain the samples captured so far. | Runtime state |
| `release(id)` | Dispose an active recorder if necessary and release all retained samples. | Runtime state |

`listAvailable` enumerates Unity's global Profiler registry, so its results can include GPU-only or
worker-thread metrics. Its response reports `discoveryScope: global-profiler-registry` and
`samplingScope: main-thread-cpu`, the default scope, plus the supported thread scopes; discovery does
not guarantee that a metric emits CPU samples in the selected scope. `start` resolves each exact
category/name pair after warmup, so counters initialized during warmup can become available. With
zero warmup, resolution and recorder creation happen immediately. Recorders always use
`SumAllSamplesInFrame`. `main-thread` additionally uses `CollectOnlyOnCurrentThread`, while
`all-threads` omits that option and collects matching samples across threads;
`invocationCount` is the sum of `ProfilerRecorderSample.Count`, which keeps multiple marker
invocations per Player frame visible. Raw values remain in the reported Unity unit, including
nanoseconds for time metrics. `meanPerInvocation` divides the raw total by invocation count, so a
frame containing multiple ticks is not mistaken for one invocation. Time metrics additionally
return `totalMs`, `minMs`, `meanFrameMs`, `meanPerInvocationMs`, `p50Ms`, `p95Ms`, and `maxMs`.
Percentiles use nearest-rank selection over Player-frame samples; `p50`/`p95` and their millisecond
forms are not per-marker-invocation percentiles when one frame contains multiple invocations.

Both `start` and `get` return the selected `threadScope`, a matching `samplingScope`, and
machine-readable aggregation metadata: `sampleAggregation: sum-all-samples-per-player-frame`,
`percentileScope: sample-frame`, and `meanPerInvocationScope: marker-invocation`. They also report
`timeAggregationSemantics`, `canExceedWallClockFrameTime`, `scopeWarning`,
`counterSampleScope: sample-frame`, and
`counterMultipleWritesSemantics: value-counters-may-report-last-written-value-per-player-frame`.
A time value under `all-threads` is accumulated concurrent-thread time, not wall-clock frame time:
it sums matching spans across threads in a Player frame and can exceed that frame's wall-clock
duration. In particular, an all-thread `Semaphore.WaitForSignal` result measures accumulated waiting
spans, not CPU work, and must not be added to or interpreted as wall-clock frame duration.
A counter recorder sample therefore represents one Player frame. In particular, repeatedly assigning
a value-style counter during one frame generally exposes the producer's last flushed/written value,
not the sum of all assignments; interpret counters according to their producer contract.

One guard frame is recorded on each edge. When Unity returns the full guarded window, those two
samples are excluded and `completeFrameWindow` is true; otherwise all available samples are kept
and the field is false. The store allows four active and sixteen retained sessions. PlayMode
pause/exit, Domain Reload, and Editor quit stop and dispose every active recorder. A pause
retains captured data with completion reason `play-mode-paused`; sessions are in-memory and do not
survive Domain Reload. Raw sample offsets accept 0..10,000 and page sizes are clamped to 1..500.

### `tools://Editor/Pipeline`

Package Manager, Test Runner, and BuildPipeline workflows.

| Function | Purpose | Safety |
|---|---|---|
| `listPackages()` | List registered packages. | Read-only |
| `addPackage(packageId, confirm?)` | Add a package. | Requires `confirm: true` |
| `removePackage(packageName, confirm?)` | Remove a package. | Requires `confirm: true` |
| `searchPackages(packageName?)` | Search package registry. | Read-only/request |
| `getPackageRequest(id)` | Poll package request. | Read-only |
| `runTests(mode?, testName?)` / `getTestRun(id)` | Compatibility entry points that delegate to `Editor/Tests`. | Optional Test Framework; unsupported without it |
| `getBuildSettings()` | Read build scenes and target. | Read-only |
| `buildPlayer(locationPathName, confirm?)` | Build player. | Requires `confirm: true` |
| `getBuild(id)` | Poll build request. | Read-only |

### `tools://Editor/Tests`

Unity Test Framework discovery and callback-driven execution. The stable Tool remains visible when
the optional package is absent and then reports that `com.unity.test-framework` 1.4.0 or newer is
required.

| Function | Purpose | Safety |
|---|---|---|
| `list(mode?, assemblies?, tests?, groups?, categories?, limit?)` | Start asynchronous EditMode or PlayMode discovery and return a `listId`. The discovery limit is 1..5000 and defaults to 500. | Read-only, long-running |
| `getList(listId, offset?, limit?)` | Read a discovery page of at most 500 parameterized leaf tests. | Read-only |
| `run(mode?, assemblies?, tests?, groups?, categories?)` | Start one filtered test run and return its official `runId`. | May enter Play Mode or reload |
| `get(runId, detail?, offset?, limit?)` | Read `summary`, `failures`, or `all` results in pages of at most 500. | Read-only |
| `cancel(runId)` | Ask Unity Test Framework to cancel an active run. | Changes test-run state |

Runs and discovery records are persisted under `Library`, with 32 runs and 16 discovery requests
retained. The optional adapter re-registers official TestRunner callbacks after Domain Reload so an
active framework job can continue updating the same record. A lost framework job and a discovery
whose callback was lost to reload become explicit terminal records instead of blocking later work.
Because Unity Test Framework 1.4 callbacks are process-global and contain no run id, this Tool starts
only when no framework job is active and accepts callbacks only while its official GUID is the unique
active job; interference becomes `OwnershipConflict` and the owned run is canceled rather than
guessing attribution.

Discovery visits at most 50,000 tree nodes, retains at most the requested 1..5,000 tests, and stores
at most 2 MiB of text. Run details retain at most 10,000 leaf results and 2 MiB of text, with explicit
count/text truncation fields. `groups` and `categories` each accept at most 32 expressions of at most
256 characters. Their conservative regex subset excludes groups, lookarounds, counted repetitions,
backreferences, and unbounded wildcard repetition; local matching also has a 100 ms timeout. Exact
assembly/test filters allow at most 256 values of 1,024 characters each.

### `tools://Editor/Validation`

Project health checks.

| Function | Purpose |
|---|---|
| `run(folders?, limit?, includeProjectWideChecks?)` | Run loaded-scene missing-reference checks by default; project-wide checks require explicit opt-in. |
| `missingScripts(folders?, limit?)` | Find missing MonoBehaviour scripts. Prefab scanning only runs inside explicit folders. |
| `missingReferences(limit?)` | Find broken serialized references in loaded scenes. |
| `serializedFieldTooltips(folders, limit?)` | Check serialized fields for `[Tooltip]` inside explicit folders. |

## Common Workflows

| Goal | Start With |
|---|---|
| Check Unity environment | `tools://Runtime#getState()` |
| Inspect scene objects | `tools://Runtime/Objects` |
| Read live component data | `tools://Runtime/Components` |
| Observe values across frames | `tools://Runtime/ObserveFrames` |
| Sample Profiler markers/counters | `tools://Editor/Profiler` |
| Edit Inspector fields | `tools://Editor/Serialized` |
| Capture an Editor viewport | `tools://Editor#captureViewport()` |
| Search project assets | `tools://Editor/Assets#find()` |
| Find serialized code usages | `tools://Editor/CodeUsages` |
| Modify importer settings | `tools://Editor/Importers` |
| Work with Prefabs | `tools://Editor/Prefabs` |
| Discover or run tests | `tools://Editor/Tests` |
| Run builds | `tools://Editor/Pipeline` |
| Call custom static C# API | `tools://Runtime/Reflection` |
| Run one-off Unity/C# code | PuerTS `CS.*` interop in `eval` |
| Use low-level commands | [Advanced notes](ADVANCED_USAGE.md) |
