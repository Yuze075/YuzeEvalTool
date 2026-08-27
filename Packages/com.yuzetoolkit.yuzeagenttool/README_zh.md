# Yuze Agent Tool

[English](README.md) | **简体中文**

Yuze Agent Tool 是 Unity 2022.3 下 Editor 与 Runtime 共用的统一工作台。它依赖
`com.yuzetoolkit.yuzeevaltool` 与 `com.yuzetoolkit.logtool`，并统一拥有 UI、运行时浮窗宿主、DebugPanel 生命周期、
DebugWindow Builder、Agent 对话、Command Line 会话和 Unity 日志查看器。

Package manifest 直接依赖 Yuze Eval Tool 与 Yuze Log Tool。Input System 与 uGUI 分别是由
`YUZE_USE_UNITY_INPUT_SYSTEM`、`YUZE_USE_UNITY_UGUI` version define 控制的可选集成。缺少 Input
System 时工作台和面板仍可通过正常 API 使用，只是不编译键盘快捷键轮询；缺少 uGUI 时 UI Toolkit
交互保持可用，仅跳过与 `EventSystem.current` 的选中状态同步。

## 工作台

Editor 菜单 **YuzeToolkit > Yuze Agent Tool** 与运行时 `UnityAgentPanelModule` 都创建同一个
`UnityAgentWorkbenchView`。主侧栏固定包含五个主要操作：

1. **New conversation**：打开未落盘的新对话草稿；首次发送时才创建对话文档。
2. **New command line**：打开未落盘的命令行草稿；首次执行时才创建记录和当前进程的 VM。
3. **Debug Panel**：每个运行时 `DebugWindowModule.RegisterWindow(...)` 注册对应一个页签。Edit Mode
   可以正常打开空外壳，但只有进入 Play Mode 后才实例化依赖运行时数据的页面。
4. **Log**：从 Editor 域初始化或 Runtime 启动开始持续捕获，不依赖是否打开过 Log 页面；提供搜索、
   日志类型过滤、同类合并、清空、自动滚动、Stack Trace 级别、Editor 源文件跳转、本地日志文件入口、
   可滚动详情区以及可拖动的列表/详情分隔条。列表中的长日志始终限制为一行摘要且不会撑宽容器；选中后将
   完整消息放入突出卡片，并把每一条 Stack Frame 分别渲染为可读、可跳转源码的独立行。
5. **System Info**：在工作台内以 Yuze Agent Tool 风格的响应式卡片展示性能与系统信息；独立 Runtime 浮层仍保留原有样式。

Agent 与 Command Line 会话在侧栏中分组显示，各自保存独立输入草稿，并支持 Pin、归档与删除。
归档项不会出现在主界面，只能在 Settings 的两个独立归档页面恢复或永久删除。Settings 是独立的
全工作区页面，固定包含模型提供、组合配置、Eval 连接、Eval Tools 和两个归档管理页面。模型发现告警只在
Provider 页面内联显示，不再反复弹窗；所有自有下拉菜单都会限制在工作区视口内，供应商、Profile 或模型列表
较长时可纵向滚动。

Conversation 会渲染 User/Assistant 文本、待处理审批卡片，并把每个 Tool 调用显示为默认折叠的记录行。
展开后可以查看调用参数以及等待中、成功或失败的结果。Tool 消息仍会完整持久化并返回模型。
工作台继承当前 Unity PanelSettings / Theme 的字体，不打包、枚举、动态创建或
显式指定字体。

## Agent 循环

内建 HTTP Agent 使用刻意保持简单的顺序循环：先持久化一次模型响应，再为每个 ToolCall 按顺序写入且仅写入
一个 ToolResult，最后继续请求模型，直到模型不再调用工具。单轮模型步骤可配置，默认上限为 64。用户停止、
意外失败或 Unity Domain Reload 都会先为未完成工具补充明确的错误结果，再保存终态，后续轮次不会收到孤立的
工具协议。

Provider Profile 保存模型 Context Window。HTTP 对话接近窗口时，对话文档继续保留完整消息；发给模型的内容
改用一份语义摘要检查点和最近的完整消息边界。网络瞬时错误、429 与可恢复的 5xx 最多重试两次，并且只能发生
在收到第一条 SSE 事件之前；任何部分模型输出都不会重试。

每次模型请求只会收到当前对话权限与 Unity 运行表面允许的 Tool，执行前会再做一次相同校验：

- **ObserveOnly**：只暴露有边界的只读 Tool；每个新建独立 Player 对话都从此模式开始。
- **ConfirmWrites**：Editor 文件访问限于 Unity 项目，Player 限于持久化/缓存目录；所有变更、进程、破坏性或 Full Trust Tool 执行前请求确认。
- **FullAccess**：移除文件边界和审批，但删除仍会拒绝文件系统根、用户目录、项目根和对话工作目录。

新安装的 Package 默认使用 ConfirmWrites；已有且有效的本机设置保留明确保存的模式。Tool 现在声明
风险、Editor/Player 表面和未来并行安全 metadata；公开注册表返回可释放句柄，可选模块能安全移除自己的 Tool。

内建 Editor/Runtime System Prompt 统一使用英文，声明 Unity 角色与真实 Tool 名。`file_read_text`
对不超过 64 MB 的文件返回 SHA-256；`file_apply_patch` 最多处理 16 MB，必须提供该 Hash 和旧文本精确出现次数，通过原子替换写盘并返回
有界 Diff。`unity_snapshot` 与 `unity_scene_query` 为 ObserveOnly 提供安全 Unity 查询；Full Trust
的 `unity_eval_js` 只在对话允许变更 Tool 后可用。每次模型请求仍会携带完整结构化 Tool schema。

## 独立 Agent 边界

AgentLoop、会话、审批、上下文压缩、Tool 调度和 `unity_eval_js` 全部在 Unity 进程内运行。默认 Host 直接创建
HTTP 模型 Provider 与 Yuze Eval Tool 的进程内 `EvalExecutor`，不会启动或连接 Codex、Broker、MCP 或电脑级 CLI。
Settings 中独立的 Eval 连接页面只管理外部程序访问 Yuze Eval Tool 的可选能力，不是 Agent 运行依赖。
Process/Shell Tool 只在 Editor 暴露，并且只在当前模式允许、模型明确调用后启动指定程序。

`UnityAgentHost.SendMessageAsync` 现在返回包含持久化终态、错误和用量的 `AgentTurnResult`，不再用
“成功完成的无类型 Task”表示 Agent 内部失败。`UnityAgentHost.StreamEvent` 会转发全部 Provider 流事件以及
Tool 执行开始/结束事件，并携带对话 ID；单个订阅者异常只记录，不会中断 Agent。编译恢复会校验每个续跑结果后再清理 marker。

OpenAI 模型通过 API Key 调用 OpenAI Responses API。ChatGPT/Codex 订阅不是可嵌入的 Provider 凭据，因此
Yuze Agent Tool 不读取 Codex 登录缓存，也不再提供 Codex App Server。历史 `codex-app-server` Profile 会迁移为
标准 OpenAI API 预设，并直接使用机器本地 Provider Profile 中保存的 API Key。

Editor 中若活动对话触发脚本编译，本包会先在 `Application.persistentDataPath/.unityagenttool` 写入同时绑定当前项目与 Editor
进程的恢复 marker，再中断并持久化该轮。成功编译与 Domain Reload 后，或失败编译结束后，系统会追加一次包含
编译错误/警告数量的续跑消息，并要求 Agent 重新检查 Unity 状态；Domain Reload 不会保留缓存 Unity 对象和
JavaScript VM。其它 Editor 进程留下的 marker 会被删除，不会在下次启动时自动执行旧任务。

## 持久化

所有 Yuze Agent Tool 本机数据统一使用 `Application.persistentDataPath/.unityagenttool`。依赖
Package/Project Default 创建的机器配置与用户创建的 Provider 配置分开保存：

```text
settings.json              Package/Project Default 派生的机器配置
providers.json             用户创建的 Provider Profile 与默认 Profile
```

该目录还包含以下固定内容：

```text
AgentConversations/       Agent 对话文档
CommandLineHistory/       命令行文档与当前选择状态
UnityAgentEditorCompilationRecovery.json  仅 Editor 使用的活动轮次恢复 marker
```

旧布局直接写在 `Application.persistentDataPath` 下的数据，会在 `.unityagenttool` 中尚无对应文件或历史时按类型迁移。

命令行输入、输出和草稿会跨 Unity 重启保存；JavaScript `EvalSession` 不恢复。Provider Profile（包括 API Key）
直接写入本机 `providers.json`，不会进入默认配置。Package 自带的
`Runtime/Resources/UnityAgentPackageSettings.json` 是无 Provider 默认值的唯一内置来源，C# 不重复保存
配置值。可选的 `Assets/Resources/UnityAgentProjectSettings.json` 覆盖 Package 默认并进入 Player。
项目覆盖尚未保存时，Project Settings 直接显示 Package JSON；Yuze Agent Tool 配置页的
**Overwrite Project Settings** 也通过同一套校验和资源写入入口保存当前无 Provider 配置。
每条 AGENTS.md 或 Skill 根都可独立决定是否在 `AgentPathBase` 下追加 `.unityagenttool`。Skill 根会在这个可选
命名空间之后固定追加 `.agents/skills`，`relativePath` 只表示剩余的可选子路径。`scope` 独立决定实时路径在
Editor、Player、两者或都不启用；`embedInPlayerBuild` 则不受 scope 影响，将构建时内容快照复制进 Player。
快照源目录不存在时跳过且不让构建失败。Player 中 Player/All 实时根的优先级高于包内快照。Package 默认的
四条根全部使用 All 且关闭 Embed：两条 ProjectRoot 根关闭 `.unityagenttool`，分别解析为项目根与项目
`.agents/skills`；两条 PersistentData 根保持启用。机器配置 schema V13 与项目 schema V6 直接替换旧的混合构建
开关，不提供根配置的向后迁移。

只接受机器配置 schema V13 与 Provider 配置 schema V1。V10、V11、V12 的旧合并式 `settings.json` 均不支持，
会按损坏文档处理：主文件先保留为带时间戳的 `.invalid-*` 文件，再基于当前有效 Default 重建机器配置层。
旧文件中的 `providerProfiles` 不会被提取，也不执行向后兼容的拆分迁移。`providers.json` 不存在或损坏时，
只重建 Provider 配置层，不会替换有效的 `settings.json`。首次创建 Provider 文件时会写入内置 OpenAI Profile；
之后的 Provider 完全由用户管理，不会再次从 Package/Project Default 生成。有效的现有本机配置不会被
Project Settings 隐式改写。通过
**Edit > Project Settings > YuzeToolkit > Yuze Agent Tool** 编辑权限、Editor/Runtime Prompt、Tool 限制与
有序 AGENTS.md/Skill 根目录。Editor Play Mode 使用 Editor Prompt，Runtime Prompt 只用于独立 Player。

## Runtime 宿主

`DebugPanel` 管理唯一的全屏 `UIDocument` 与 `IDebugPanelModule` 生命周期，全部实现已归入本包。
安装 Input System 时它还管理模块快捷键，`UnityAgentPanelModule` 是 F8 统一工作台：标题栏拖动整个窗口，右上角手柄可以在面板边界内任意调整
宽高。折叠会真正隐藏全部内容与缩放命中区、释放焦点，并且不影响 System Info 的独立显隐。
窗口以左下角为锚点，几何通过 `PlayerPrefs` 保存。本包同时提供标准组合 Prefab，以及保留原视觉的
System Info / Performance。依赖方向为：

```text
Yuze Agent Tool -> Yuze Eval Tool
```

独立 UnityDebugTool Package 已删除。

## DebugWindow API

DebugWindow 注册已移动到本包，但继续使用 `YuzeToolkit` 命名空间：

```csharp
var handle = DebugWindowModule.RegisterWindow(window =>
{
    window.SetTitle("Player");
    window.AddReadOnly("State", () => player.StateName);
    window.AddPrimaryButton("Reset", player.Reset);
    window.AddTextArea("Lua", () => luaCode, value => luaCode = value);
    window.AddChoice("Template", () => templateNames, () => selectedTemplate, value => selectedTemplate = value);
});
```

注册不依赖场景宿主。`DebugWindowModule` 只注册视觉窗口，不创建、注册或释放 `IEvalTool`；自动化入口必须
由功能所有者单独实现，并通过 `EvalToolRegistry.RegisterRootScoped` 独立注册和释放。`AddButton` 是普通动作，
`AddPrimaryButton` 用于页面主动作，`AddPreviousButton` / `AddNextButton` 用于方向操作。布尔、枚举、折叠、
范围和进度等默认控件均使用 Agent 调色板和包自有交互样式，不依赖 Unity 默认皮肤。
`AddTextArea` 提供包自有样式的多行编辑框，`AddReadOnlyTextArea` 提供同样样式的只读配置代码区域，
`AddChoice` 提供可在运行时更新选项的包自有字符串下拉框；这些控件与既有字段共用绑定刷新和弹窗生命周期规则。
动态重建窗口中的折叠对象可使用 `AddFoldout(label, isOpenGetter, setOpen, configure)`，由调用方模型保存展开状态，
这样增删内容触发窗口重建时不会把用户已经打开的折叠对象重置为关闭。
动态选择器的绑定会每帧刷新，但选项和当前值未变化时不会关闭已经打开的弹窗。

## 程序集

- `UnityAgentTool`：Agent Core、统一 UI、DebugPanel、DebugWindow、Command Line 与 Log；Input System 快捷键和 uGUI EventSystem 同步均为可选集成。
- `UnityAgentTool.Editor`：EditorWindow 与 Editor Broker 设置桥接。
- `UnityAgentTool.Editor.Tests`：安装 Unity Test Framework 1.4+ 时可用的定向 EditMode 测试。

旧 Runtime Console registry、tab provider 程序集、Runtime Eval 页面、兼容 Provider 与
DebugWindow MonoBehaviour 宿主均已删除。
