# Helper 参考

[English](HELPER_MODULES.md) | **简体中文** | [Package README](../README_zh.md) | [Runtime 服务](RUNTIME_SERVICES_zh.md) | [项目架构](PROJECT_DESIGN_zh.md) | [进阶使用](ADVANCED_USAGE_zh.md)

[![Runtime](https://img.shields.io/badge/Runtime-7%20modules-2ecc71)](#runtime-helpers)
[![Editor](https://img.shields.io/badge/Editor-12%20modules-3498db)](#editor-helpers)
[![Catalog](https://img.shields.io/badge/Tool%20catalog-1%20module-8e44ad)](#tool-目录)
[![Tool](https://img.shields.io/badge/Broker%20MCP-3%20tools-orange)](../../../README_zh.md#mcp-配置)

完成 `unity_status` 和 `unity_connect` 后，Broker 的 `eval` 会在选中的 Unity 内运行。Agent 在这个 eval 中从 `tools://` 和 `tools://<Tool/Path>` import helper module。内置 module 从带 `[EvalTool(name, description)]` 的 partial C# class 生成；source generator 会为它们生成 `IEvalTool` 元数据。每个 C# module 导出语义函数，每次调用都会确认 tool 仍处于启用状态，再通过 PuerTS 调用 C# public 实例方法，并把返回值交给 Unity 侧 executor 格式化。项目和其他包的 JavaScript 扩展需要通过 `tools://UnityEval` 显式加载。

生成的 C# helper 应优先返回基础类型、`List<T>`、`Dictionary<string, TValue>` 或由这些类型组成的数据。服务端会把这类结果作为 JSON text content 返回，这是最稳定、最推荐的工具返回形态。

常见流程优先使用 helper module，因为它们的说明更集中、返回数据更稳定。helper 未覆盖时，可以在 `eval` 中通过 PuerTS `CS.*` 直接调用 Unity/C# API；反复使用的项目专用逻辑应沉淀成 C# tool 或显式加载的 JavaScript helper。

Discovery 起点：

```javascript
async function execute() {
  const index = await import('tools://');
  return index.description;
}
```

## 模块索引

| 分类 | 模块 |
|---|---|
| Tool 目录 | `tools://UnityEval` |
| Runtime helpers | `tools://Runtime`, `tools://Runtime/Objects`, `tools://Runtime/Components`, `tools://Runtime/Diagnostics`, `tools://Runtime/Reflection`, `tools://Runtime/Inspect`, `tools://Runtime/ObserveFrames` |
| Editor helpers | `tools://Editor`, `tools://Editor/Assets`, `tools://Editor/Importers`, `tools://Editor/Scenes`, `tools://Editor/Prefabs`, `tools://Editor/Serialized`, `tools://Editor/Project`, `tools://Editor/Profiler`, `tools://Editor/Pipeline`, `tools://Editor/Tests`, `tools://Editor/CodeUsages`, `tools://Editor/Validation` |

Runtime helper 可在 Editor 或 Runtime/Player 中运行，前提是底层 Unity API 可用。Editor helper 依赖 `UnityEditor`，在 Runtime/Player 中会明确失败。

生成 helper 函数使用位置参数，例如 `const assets = await import('tools://Editor/Assets'); assets.find('t:Prefab', 20, ['Assets'])`。生成的 C# module 会暴露 `functions[].description`、有序的 `functions[].parameters`、显式声明的 safety flags、`conditionalRequiresConfirmation` 等条件安全提示，也会导出 `isEnabled()` 用于读取当前启用状态。

## Tool 目录

### `tools://UnityEval`

用于检查目录、管理启用状态，以及获取 JavaScript Tool 编写指导。

| 函数 | 用途 | 安全 |
|---|---|---|
| `listTools(refresh?)` | 列出已注册的 C# 与 loader-backed JavaScript Tool。 | 只读 |
| `getToolDetails(name, refresh?)` | 返回一个 Tool 路径的完整 metadata。 | 只读 |
| `setToolEnabled(name, enabled)` | 启用或停用 C# / JavaScript Tool；Editor 中按 Tool 路径持久化。 | 修改 Editor 状态 |
| `getJsToolAuthoringPrompt()` | 返回当前 loader-backed JavaScript Tool 编写契约。 | 只读 |

## Runtime Helpers

### `tools://Runtime`

环境状态和 Unity 日志。

| 函数 | 用途 | 安全 |
|---|---|---|
| `getState()` | 环境、Unity 版本、平台、播放状态、路径、active scene、已注册 tools。 | 只读 |
| `getRecentLogs(count?, type?)` | MCP 捕获的 Unity 日志。 | 只读 |
| `clearLogs()` | 清空 MCP log buffer。 | 修改瞬时 Runtime 状态 |

### `tools://Runtime/Objects`

Scene GameObject、hierarchy 和 Transform 操作。

| 函数 | 用途 | 安全 |
|---|---|---|
| `find(name, limit?)` | 按精确 `name` 查找 active GameObject，返回轻量 selector。 | 只读 |
| `findOne(name)` | 按精确 `name` 查找第一个 active GameObject，返回轻量 selector。 | 只读 |
| `findByPath(path, includeInactive?)` | 按精确 hierarchy path 查找一个 GameObject。 | 只读 |
| `findByTag(tag, limit?)` | 使用 Unity tag 查询 active GameObject。 | 只读 |
| `get(target)` | 检查单个 GameObject。 | 只读 |
| `create(name?, primitive?, parent?, localPosition?, position?, localScale?)` | 创建空对象或 primitive GameObject。 | 修改场景 |
| `destroy(target, confirm?)` | 销毁 GameObject。 | 需要 `confirm: true` |
| `duplicate(target, name?)` | 复制 GameObject。 | 修改场景 |
| `setParent(target, parent?, worldPositionStays?)` | 修改 hierarchy 父对象。 | 修改场景 |
| `setTransform(target, position?, localPosition?, rotationEuler?, localRotationEuler?, localScale?)` | 设置位置、旋转或缩放。 | 修改场景 |
| `setActive(target, active)` | 修改 active 状态。 | 修改场景 |
| `setNameLayerTag(target, name?, layer?, tag?)` | 修改 name、layer 或 tag。 | 修改场景 |

### `tools://Runtime/Components`

Component 读取、编辑和实例方法调用。

| 函数 | 用途 | 安全 |
|---|---|---|
| `list(target)` | 列出 GameObject 上的 Component。 | 只读 |
| `get(target, type?, index?, includeValues?)` | 按 type/index 读取一个 Component。默认只返回成员定义；传 `includeValues = true` 才调用 public getter。 | 只读 |
| `find(typeName, limit?, includeInactive?)` | 按 C# 类型名查找 live Component，返回组件摘要和 GameObject selector。 | 只读 |
| `add(target, type)` | 添加 Component。 | 修改场景 |
| `remove(target, type?, index?, confirm?)` | 删除 Component。 | 需要 `confirm: true` |
| `setProperty(target, type, member, value, index?, includeNonPublic?, includeStatic?, confirmDangerous?)` | 设置一个 field/property。 | 修改 Component |
| `setProperties(target, type, values, index?, includeNonPublic?, includeStatic?, confirmDangerous?)` | 设置多个 fields/properties。 | 修改 Component |
| `callMethod(target, type, method, args?, index?, includeNonPublic?, confirmDangerous?)` | 调用 instance method。 | 取决于方法 |
| `listTypes(query?, limit?)` | 搜索可用 Component 类型。 | 只读 |

`Runtime/Objects.find` 不再接受 `path`、`tag`、`component` 或 `includeInactive` selector 模式。需要使用 `findByPath`、`findByTag` 或 `Runtime/Components.find`，让每种查询都有明确成本模型。`Runtime/Components.get` 默认不读取所有 public property value，因为 Unity Component getter 可能很重；只有确实需要值时才显式请求。非 public method 调用需要 `includeNonPublic: true` 和 `confirmDangerous: true`。

### `tools://Runtime/Diagnostics`

只读运行时诊断。

| 函数 | 用途 |
|---|---|
| `listCameras()` | Scene cameras 和常用设置。 |
| `getPhysicsState()` | Physics2D/3D 设置以及 Collider、Rigidbody 摘要。 |
| `getGraphicsState()` | Render pipeline、quality、color space。 |
| `listCanvases()` | Canvas 对象、render settings 和 EventSystem。 |
| `listLoadedTextures(limit?)` | 已加载 Texture 对象的尺寸和类型。 |

### `tools://Runtime/Inspect`

C#/Unity 对象引用格式化辅助。

| 函数 | 用途 |
|---|---|
| `describe(value?, depth?)` | 返回默认摘要 DTO。 |
| `format(value?, mode?, depth?)` | 用 `default`、`summary`、`name`、`path`、`text`、`json`、`yaml` 格式化值。 |
| `toName(value?)` | 返回 Unity/C# 对象名称。 |
| `toPath(value?)` | 返回场景层级路径或资产路径。 |
| `toJson(value?, mode?, depth?)` | 返回格式化值的 JSON 字符串。 |
| `toYaml(value?, depth?)` | 返回格式化值的 YAML 字符串。 |

### `tools://Runtime/Reflection`

项目自定义 API 的 C# 类型发现和 static method 调用。

| 函数 | 用途 | 安全 |
|---|---|---|
| `getNamespaces()` | 列出 public namespaces。 | 只读 |
| `getTypes(namespaceName)` | 列出某 namespace 下的 public types。 | 只读 |
| `getTypeDetails(fullName)` | 列出某 type 的 public members。 | 只读 |
| `findMethods(query?, type?, includeNonPublic?, confirmDangerous?, limit?)` | 搜索 public methods。 | 非 public 搜索需要 `confirmDangerous: true` |
| `callStaticMethod(type, method, args?, includeNonPublic?, confirmDangerous?)` | 调用 static method。 | 非 public 调用需要 `confirmDangerous: true` |

### `tools://Runtime/ObserveFrames`

对 public field 和可读 property 做有界跨帧观察。Component probe 使用
`{ name, kind: "component", target, type, member, index? }`；static probe 使用
`{ name, kind: "static", type, member }`。

| 函数 | 用途 | 安全 |
|---|---|---|
| `start(probes, maxFrames?, intervalFrames?, maxSamples?, until?, label?)` | 先取得初始 sample，再在后续 Editor update 或 Player frame 采样。`until` 接受 `{ probe, op, value? }`，`op` 可为 `eq`、`ne`、`gt`、`gte`、`lt`、`lte`、`truthy` 或 `falsy`。 | 只读、长时间运行 |
| `get(id, offset?, limit?)` | 每页读取最多 500 个 sample 及当前完成状态。 | 只读 |
| `list(status?, limit?)` | 列出保留的 session 摘要。 | 只读 |
| `cancel(id)` | 停止采样但保留已有 sample。 | 仅修改内存状态 |
| `release(id)` | 释放 session 及其 sample。 | 仅修改内存状态 |

每个 session 最多包含 32 个 probe、36,000 个观察帧和 10,000 个 sample；最多同时运行
8 个 session，并保留 64 个。格式化 value 和保留的 session 数据也有明确字符预算；保留的
JSON 达到 8,388,608 个字符前，session 会以 `storage-limit` 完成。采样只会调用明确指定的
field/property getter。已知标量和 Unity value type 会直接格式化；array 以及准确的 `List<>`、
`Dictionary<,>` 最多展开 4 层和 128 个条目，任意自定义对象只返回类型摘要，不会调用
`ToString()` 或继续读取其它 property。单个字符串最多 4,096 字符，单个格式化 value 最多
32,768 字符。Edit Mode 下，一个观察帧表示一次 Editor update。session 是进程内存状态，
不跨 Domain Reload；需要跨 reload 保留测试执行状态时应使用 Tests Tool。

## Editor Helpers

### `tools://Editor`

Editor 状态、编译、Selection、菜单、播放模式和截图。

| 函数 | 用途 | 安全 |
|---|---|---|
| `getState()` | Editor 状态、active scene、selection 摘要。 | 只读 |
| `getCompilationState()` | Unity 侧诊断 request 状态及错误/警告计数；等待编译使用 Broker `unity_status`。 | 只读 |
| `requestScriptCompilation()` | 只请求一次脚本编译，随后结束 eval 并通过 Broker 状态等待；必要时先退出 PlayMode。 | 可能触发 reload |
| `scheduleAssetRefresh()` | 只请求一次脚本安全 AssetDatabase refresh，随后结束 eval 并通过 Broker 状态等待；必要时先退出 PlayMode。 | 可能触发 reload |
| `getCompilerMessages(count?)` | 最近类似编译器的错误/警告。 | 只读 |
| `getSelection()` / `setSelection(items)` | 读取或设置 Editor selection。 | 修改 selection |
| `executeMenuItem(path, confirm?)` | 执行 Editor menu item。 | 非 Yuze Eval Tool 菜单需要 `confirm: true` |
| `setPlayMode(isPlaying)` / `setPause(isPaused)` | 控制播放/暂停状态。 | 改变 Editor 状态 |
| `screenshotGameView(path?)` | 捕获 Game View。 | 写入截图文件 |
| `captureViewport(target?, maxLongEdge?, windowQuery?)` | 同步返回 Game View、Scene View 或可见 Editor 窗口 PNG；MCP 结果同时包含 image、尺寸和来源 metadata。 | 只读 |

`captureViewport` 接受 `game`、`scene` 或 `editor_window`。`maxLongEdge = 0` 保留来源尺寸；
1 到 8192 会在必要时等比缩小。任意 Editor 窗口必须是当前选中的可见 tab，且 Unity 必须
位于前台。旧的文件写入接口 `screenshotGameView` 保持不变。Game 与 Scene render texture
会先在 GPU 上缩小，再执行 readback。Editor 窗口来源超过 8,388,608 像素、任意输出超过
16,777,216 像素，或编码后的 PNG 超过 33,554,432 bytes 时会明确失败；此时应传入更小的
`maxLongEdge`。

### `tools://Editor/Assets`

AssetDatabase 搜索、项目文本 IO、依赖、脚本、材质和脚本安全刷新。

| 函数 | 用途 | 安全 |
|---|---|---|
| `find(filter, limit?, folders?)` / `findPaths(filter, limit?, folders?)` / `findNames(filter, limit?, folders?)` | 使用 Unity filter 搜索资产，分别返回摘要、路径或名称。 | 只读 |
| `getInfo(path)` | 资产元数据。 | 只读 |
| `readText(path)` / `writeText(path, text, refresh?, confirmOverwrite?)` | 读写文本资源。 | 写入会修改项目 |
| `createFolder(name, parent?)` | 创建 AssetDatabase 文件夹。`parent` 默认是 `Assets`。 | 修改项目 |
| `copy(from, to, confirmOverwrite?)` | 复制资产。 | 修改项目 |
| `move(from, to, confirm?)` | 移动或重命名资产。 | 需要 `confirm: true` |
| `deleteAsset(path, confirm?)` | 删除资产。 | 需要 `confirm: true` |
| `refreshNow()` | 请求脚本安全 AssetDatabase refresh 并返回编译/导入状态；必要时先退出 PlayMode。 | 可能触发 reload |
| `getDependencies(path, recursive?)` | 资产依赖。 | 只读 |
| `findReferences(path, folders, limit?)` | 在明确 folders 范围内查找资产引用。省略 limit 时最多返回 100 条。 | 只读 |
| `createScript(path, className?, namespaceName?, confirmOverwrite?)` | 创建 MonoBehaviour 脚本。 | 可能触发 reload |
| `applyScriptTextEdits(path, edits, refresh?, confirm?)` | patch 脚本文件。 | 可能触发 reload |
| `createMaterial(path, shaderName?, properties?, confirmOverwrite?)` | 创建 Material 资产。 | 修改项目 |

### `tools://Editor/Importers`

AssetImporter 检查和编辑。

| 函数 | 用途 | 安全 |
|---|---|---|
| `get(path, includeProperties?, propertyLimit?)` | Importer 摘要和可选 serialized properties。属性枚举会被限制数量。 | 只读 |
| `setProperty(path, propertyPath, value, saveAndReimport?, confirm?)` | 设置一个 importer SerializedProperty。 | 修改 importer |
| `setMany(path, changes, saveAndReimport?, confirm?)` | 设置多个 importer properties；`changes` 可为 `{ propertyPath: value }` map 或 `{ propertyPath, value }[]`。 | 修改 importer |
| `reimport(path, confirm?)` | 强制重新导入。 | 需要 `confirm: true` |

### `tools://Editor/Scenes`

Scene 文件和已打开 Scene hierarchy。

| 函数 | 用途 | 安全 |
|---|---|---|
| `listOpenScenes()` | 列出已打开 Scene。 | 只读 |
| `getSceneHierarchy(depth?, includeComponents?, limit?)` | 读取已打开 Scene hierarchy。 | 只读 |
| `openScene(path, mode?, confirm?, saveDirtyScenes?)` | 打开 Scene。 | 改变 Editor 状态 |
| `createScene(path?, setup?, mode?, confirm?, saveDirtyScenes?)` | 创建新 Scene。 | 修改项目/session |
| `saveScene()` / `saveSceneAs(path)` | 保存 Scene。 | 写入 Scene asset |
| `setActiveScene(path)` | 设置 active scene。 | 改变 Editor 状态 |

### `tools://Editor/Prefabs`

Prefab instance、asset、stage、override 和 unpack 操作。

| 函数 | 用途 | 安全 |
|---|---|---|
| `instantiate(path, parent?, position?)` | 实例化 Prefab asset。 | 修改场景 |
| `createFromObject(target, path, confirmOverwrite?)` | 把 scene object 保存为 Prefab。 | 修改项目 |
| `createVariant(basePath, path, confirmOverwrite?)` | 创建 Prefab variant。 | 修改项目 |
| `openStage(path)` / `closeStage()` / `saveStage()` | Prefab Stage 工作流。 | 改变 Editor/project 状态 |
| `getOverrides(target)` | 读取 Prefab overrides。 | 只读 |
| `applyOverrides(target, confirm?)` | Apply overrides。 | 需要 `confirm: true` |
| `revertOverrides(target, confirm?)` | Revert overrides。 | 需要 `confirm: true` |
| `unpack(target, mode?, confirm?)` | Unpack Prefab instance。 | 需要 `confirm: true` |

### `tools://Editor/Serialized`

SerializedObject 和 Inspector property 读写。

| 函数 | 用途 | 安全 |
|---|---|---|
| `get(target, propertyPath?, limit?)` | 读取一个 `propertyPath`；不传路径时最多读取 `limit` 个可见 property。 | 只读 |
| `set(target, propertyPath, value, confirm?)` | 设置一个 SerializedProperty value。 | 修改对象/资产 |
| `setMany(target, changes, confirm?)` | 设置多个 SerializedProperty value；`changes` 可为 `{ propertyPath: value }` map 或 `{ propertyPath, value }[]`。 | 修改对象/资产 |
| `resizeArray(target, propertyPath, size, confirm?)` | 调整 serialized array 大小。 | 修改对象/资产 |
| `insertArrayElement(target, propertyPath, index?, confirm?)` | 插入数组元素。 | 修改对象/资产 |
| `deleteArrayElement(target, propertyPath, index, confirm?)` | 删除数组元素。 | 修改对象/资产 |

target 可以是 `assetPath`、`guid`、`instanceId` 或 GameObject selector。
支持写入的常见类型包括 integer、boolean、float、string、color、object reference、enum、Vector2/3/4、Quaternion、Vector2Int/3Int、Rect/RectInt、Bounds/BoundsInt 和 AnimationCurve。不支持的 property 类型会明确报错。

### `tools://Editor/CodeUsages`

针对一个已编译 `MonoScript` 与可选 member name 的有界序列化用法搜索。

| 函数 | 用途 | 安全 |
|---|---|---|
| `search(scriptPath, folders, member?, limit?)` | 不传 `member` 时查找 MonoBehaviour 与 ScriptableObject 挂载点；传入后查找序列化字段、UnityEvent 方法绑定（含未解析 target）以及 AnimationEvent 函数名匹配。 | 只读、长时间运行 |

`folders` 必填，必须是 `Assets` 或 `Packages` 下的一个 AssetDatabase folder 或 folder 数组；
不会隐式扫描整个项目。结果会报告候选资产、serialized owner、结果、单对象 property 与 YAML
fallback 的上限，以及截断原因和有界的逐资产错误。工具保留的候选路径会按资产类型配额限制，
结果同时返回逐类型搜索统计；Unity 的 `AssetDatabase.FindAssets` 仍会在工具侧限制生效前分配
自身的 GUID array。UnityEvent 结果会区分 `fileID: 0` 的 `missingTarget`，以及非零但未 live-load
的 serialized target。AnimationEvent 记录会明确标为仅函数名候选，因为 AnimationClip 不会
序列化接收它的 C# 类型。LDtk 文件会被排除。

### `tools://Editor/Project`

项目级诊断。

| 函数 | 用途 |
|---|---|
| `getProjectSettings()` | Product/company/application id、tags、layers。 |
| `getProfilerState()` | Profiler 可用性和 recording flags。 |
| `getToolState()` | 当前 Editor tool、pivot mode、pivot rotation。 |

### `tools://Editor/Profiler`

从全局 Profiler registry 发现 metric，并在稳定且未暂停的 PlayMode 中，以有界方式通过
`ProfilerRecorder` 采样主线程或全部线程的 CPU 数据。该 helper 不会开启全局 Profiler 或
Profiler Window recording。

| 函数 | 用途 | 安全 |
|---|---|---|
| `listAvailable(category?, nameContains?, limit?)` | 列出全局注册 metric 的准确 category/name、unit 和 data type。`category` 精确匹配，`nameContains` 不区分大小写，二者最多 512 个字符。 | 只读 |
| `start(metrics, warmupFrames?, sampleFrames?, label?, threadScope?)` | warmup 0..36,000 帧后对 1..16 个精确 `{category,name}` 采样 CPU 数据，保留 1..10,000 个 Player frame；`threadScope` 只接受 `main-thread`（默认）或 `all-threads`，category/name 最多 512 个字符。 | Runtime 状态、长时间运行 |
| `get(id, includeSamples?, offset?, limit?)` | 读取 validity、状态、raw total/min/mean/mean-per-invocation/p50/p95/max、invocation count、unit 和 data type；time metric 还会返回毫秒字段。可按 metric 分页返回 `{value,count}`，每页最多 500 条。 | 只读 |
| `cancel(id)` | 停止 active session，并保留已采集 sample。 | Runtime 状态 |
| `release(id)` | 必要时 dispose active recorder，并释放全部已保留 sample。 | Runtime 状态 |

`listAvailable` 枚举 Unity 的全局 Profiler registry，因此结果可能包含只在 GPU 或工作线程产生
数据的 metric。响应通过 `discoveryScope: global-profiler-registry` 与
`samplingScope: main-thread-cpu`（默认范围）及支持的 thread scope 明确范围；发现到 metric
不代表它会在所选范围产生 CPU sample。`start` 会在 warmup 结束后解析每个精确 category/name，
因此 warmup 期间才完成静态初始化的 counter 也能变为可用；warmup 为 0 时立即解析并创建
recorder。Recorder 总是使用 `SumAllSamplesInFrame`；`main-thread` 还会使用
`CollectOnlyOnCurrentThread`，`all-threads` 则省略该 option 并采集各线程的匹配 sample；
`invocationCount` 是 `ProfilerRecorderSample.Count` 之和，因此同一 Player frame 内多次 marker
调用不会丢失。raw value 保持 Unity 报告的 unit；time metric 仍为 nanoseconds。百分位采用
nearest-rank。`meanPerInvocation` 使用 raw total 除以 invocation count，避免把同一帧中的多个
tick 当作一次调用；time metric 还会返回 `totalMs`、`minMs`、`meanFrameMs`、
`meanPerInvocationMs`、`p50Ms`、`p95Ms` 与 `maxMs`。百分位基于 Player-frame sample；同一帧
包含多次调用时，`p50`/`p95` 及其毫秒字段不是每次 marker 调用的百分位。

`start` 与 `get` 都返回所选 `threadScope`、对应 `samplingScope` 及机器可读聚合元数据：
`sampleAggregation: sum-all-samples-per-player-frame`、`percentileScope: sample-frame`、
`meanPerInvocationScope: marker-invocation`、`timeAggregationSemantics`、
`canExceedWallClockFrameTime`、`scopeWarning`、`counterSampleScope: sample-frame`，以及
`counterMultipleWritesSemantics: value-counters-may-report-last-written-value-per-player-frame`。
`all-threads` 下的时间是并发线程累计时间，不是墙钟帧时间：它会合计一个 Player frame 内各线程
的匹配 span，结果可以超过该帧的墙钟时长。特别是全线程 `Semaphore.WaitForSignal` 表示累计等待
span，不是 CPU 工作，不能加入墙钟帧时间或把它解释成墙钟帧耗时。
Counter recorder 的每个 sample 因而代表一个 Player frame；特别是同一帧反复赋值的 value-style
counter 通常只暴露 producer 最后 flush/write 的值，而不是所有赋值之和，必须结合 producer
契约解释。

采样窗口两侧各包含一个 guard frame。Unity 返回完整 guarded window 时会排除这两个 sample，
并令 `completeFrameWindow` 为 true；否则保留所有可用 sample，并令该字段为 false。最多同时运行
4 个 session、保留 16 个 session。暂停/退出 PlayMode、Domain Reload 和退出 Editor 都会停止
并 dispose 所有 active recorder；暂停会保留已采集数据，completion reason 为
`play-mode-paused`。session 仅存在于内存，不跨 Domain Reload。raw sample offset 接受
0..10,000，page size 会 clamp 到 1..500。

### `tools://Editor/Pipeline`

Package Manager、Test Runner 和 BuildPipeline 工作流。

| 函数 | 用途 | 安全 |
|---|---|---|
| `listPackages()` | 列出 registered packages。 | 只读 |
| `addPackage(packageId, confirm?)` | 添加 package。 | 需要 `confirm: true` |
| `removePackage(packageName, confirm?)` | 移除 package。 | 需要 `confirm: true` |
| `searchPackages(packageName?)` | 搜索 package registry。 | 只读/request |
| `getPackageRequest(id)` | 查询 package request。 | 只读 |
| `runTests(mode?, testName?)` / `getTestRun(id)` | 委托给 `Editor/Tests` 的兼容入口。 | 可选 Test Framework；未安装时不支持 |
| `getBuildSettings()` | 读取 build scenes 和 target。 | 只读 |
| `buildPlayer(locationPathName, confirm?)` | 构建 player。 | 需要 `confirm: true` |
| `getBuild(id)` | 查询 build request。 | 只读 |

### `tools://Editor/Tests`

Unity Test Framework 的测试发现与 callback 驱动执行。未安装可选 Package 时，稳定 Tool 仍会
显示，并明确提示需要 `com.unity.test-framework` 1.4.0 或更高版本。

| 函数 | 用途 | 安全 |
|---|---|---|
| `list(mode?, assemblies?, tests?, groups?, categories?, limit?)` | 启动异步 EditMode 或 PlayMode 测试发现并返回 `listId`。发现上限为 1..5000，默认 500。 | 只读、长时间运行 |
| `getList(listId, offset?, limit?)` | 每页读取最多 500 个参数化叶测试。 | 只读 |
| `run(mode?, assemblies?, tests?, groups?, categories?)` | 启动一次过滤后的测试并返回官方 `runId`。 | 可能进入 Play Mode 或 reload |
| `get(runId, detail?, offset?, limit?)` | 以每页最多 500 条读取 `summary`、`failures` 或 `all` 结果。 | 只读 |
| `cancel(runId)` | 请求 Unity Test Framework 取消 active run。 | 修改测试运行状态 |

run 与 discovery record 保存在 `Library` 下，最多保留 32 个 run 和 16 个 discovery request。
可选 adapter 会在 Domain Reload 后重新注册官方 TestRunner callback，让 active framework job
继续更新同一记录。丢失的 framework job，以及因 reload 丢失 callback 的 discovery，会转成明确的
terminal record，不会阻塞后续工作。Unity Test Framework 1.4 的 callback 是进程全局的，并且
不包含 run id；因此本工具只会在没有其它 framework job 时启动，并且只有当自身官方 GUID 是唯一
active job 时才接受 callback。发现干扰时会记录 `OwnershipConflict` 并取消自身 run，不会猜测归属。

discovery 最多访问 50,000 个 tree node，保留请求指定的 1..5,000 个测试，并存储最多 2 MiB 文本。
run detail 最多保留 10,000 个叶结果和 2 MiB 文本，计数与文本截断都会显式返回。`groups` 与
`categories` 各自最多接受 32 个、每个最多 256 字符的表达式；保守 regex 子集禁止 group、
lookaround、计数重复、backreference 与无界 wildcard 重复，本地匹配另有 100 ms timeout。
assembly/test 精确过滤各自最多接受 256 个、每个最多 1,024 字符的值。

### `tools://Editor/Validation`

项目健康检查。

| 函数 | 用途 |
|---|---|
| `run(folders?, limit?, includeProjectWideChecks?)` | 默认只运行 loaded-scene 缺失引用检查；项目级检查需要显式开启。 |
| `missingScripts(folders?, limit?)` | 查找缺失 MonoBehaviour scripts。Prefab 扫描只在明确 folders 范围内运行。 |
| `missingReferences(limit?)` | 查找 loaded scenes 中损坏的 serialized references。 |
| `serializedFieldTooltips(folders, limit?)` | 在明确 folders 范围内检查序列化字段是否有 `[Tooltip]`。 |

## 常见工作流

| 目标 | 优先入口 |
|---|---|
| 检查 Unity 环境 | `tools://Runtime#getState()` |
| 检查场景对象 | `tools://Runtime/Objects` |
| 读取 live component 数据 | `tools://Runtime/Components` |
| 跨帧观察数据 | `tools://Runtime/ObserveFrames` |
| 采样 Profiler marker/counter | `tools://Editor/Profiler` |
| 编辑 Inspector 字段 | `tools://Editor/Serialized` |
| 捕获 Editor viewport | `tools://Editor#captureViewport()` |
| 搜索项目资源 | `tools://Editor/Assets#find()` |
| 查找序列化代码用法 | `tools://Editor/CodeUsages` |
| 修改 importer 设置 | `tools://Editor/Importers` |
| 处理 Prefab | `tools://Editor/Prefabs` |
| 发现或运行测试 | `tools://Editor/Tests` |
| 运行构建 | `tools://Editor/Pipeline` |
| 调用自定义 static C# API | `tools://Runtime/Reflection` |
| 执行一次性 Unity/C# 代码 | `eval` 中的 PuerTS `CS.*` interop |
| 使用底层 commands | [高级说明](ADVANCED_USAGE_zh.md) |
