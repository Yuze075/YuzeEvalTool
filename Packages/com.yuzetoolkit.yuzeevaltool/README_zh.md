# Yuze Eval Tool Package

[English](README.md) | **简体中文** | [仓库使用指南](../../README_zh.md)

`com.yuzetoolkit.yuzeevaltool` 是 Yuze Eval Tool 的 Unity 侧组件。它让受支持的
Editor 或 Player 向电脑级 Broker 注册，报告生命周期状态，承载持久 PuerTS eval
session，提供 helper module，并保留现有的 Unity 侧 CLI 命令语法。

Unity Package 与必需 Broker/CLI 的安装和首次使用说明见[仓库使用指南](../../README_zh.md)。
本页只说明 Unity Package 自身。

## 要求

- Unity 2022.3 或更高版本。
- `com.tencent.puerts.core` 3.0.2。
- `com.yuzetoolkit.logtool` 1.0.0 或更高版本。manifest 直接声明该依赖；Unity 侧日志直接使用 Yuze Log Tool，不提供宏定义 fallback。
- `com.yuzetoolkit.utilitytool` 1.1.0 或更高版本。Eval JSON 转换复用其中的纯 C# LitJson 程序集。
- 且仅有一个兼容的 PuerTS JavaScript backend。已验证的组合是
  `com.tencent.puerts.quickjs` 3.0.2 与匹配的 core 3.0.2；也可使用同一
  PuerTS 发行系列中受支持的 V8/core 组合。
- 电脑上已安装并运行 `@yuzetoolkit/unityevaltool` Broker/CLI。

同一 Unity 项目中不得安装多个 PuerTS backend。

## 添加 Package

使用 Unity Package Manager 的 **Add package from git URL**：

```text
https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeevaltool#v2.1.0
```

如果使用本地源码 checkout，选择 **Add package from disk**，然后选中该 Package
的 `package.json`。

## Editor 生命周期

主 Editor 进程会在脚本加载后自动启动 Broker Client，Asset Import Worker 会被排除。
打开 **YuzeToolkit > Yuze Eval Tool** 可检查和控制当前进程的注册。该窗口会显示
已安装 Broker、连接状态、Unity phase、eval 可用性、编译计数和已注册 Tool 目录。

该窗口使用包自有的深色 UI Toolkit 主题；按钮、页签、开关、Notice、文本输入、Tooltip、
焦点状态和滚动条均显式定制，不会呈现 Unity 默认控件皮肤或原生 Tooltip/右键菜单视觉。

Editor 会在 eval 之外独立报告导入、编译、编译失败、程序集重载、Play Mode 过渡
和主线程响应状态。Domain Reload 期间，Broker 会保留 Unity 实例与有效选择 handle；
同一进程会以新的 connection epoch 和 VM generation 重连。编译失败时，上一次成功
加载的程序集会继续作为 repair mode 使用。

## Player 生命周期与安全

受支持的非 WebGL Player 会启动隐藏的 `DontDestroyOnLoad` Broker Client，并以可执行
文件目录作为项目路径注册。Release Player 会有意保留与 Editor 相同的任意 JavaScript
eval 能力。它不受 Development Build 开关限制，也不依赖可选的 Yuze Agent Tool Package。

项目 token 验证默认关闭。发行 Player 需要拒绝没有 token 的 Broker 时，在
**Project Settings > YuzeToolkit > Yuze Eval Tool** 中按项目开启。项目只在
`Assets/Resources/UnityEvalToolAuthorizationSettings.asset` 保存加盐 verifier；Unity 会将其
直接打入 Player，并通过标准 Resources API 读取。如果该 eval 能力不适合产品发行版，应把
排除或修改 Package 作为明确的产品决策。WebGL 不是受支持的 Broker 目标。

生命周期细节与公开连接 API 见 [Editor 与 Player 注册](docs/RUNTIME_SERVICES_zh.md)。

## MCP 执行契约

电脑级 Broker 提供 `unity_status`、`unity_connect` 和 `eval`。`eval` 工具会在
已选择 Unity 的 session 中执行如下结构的程序：

```javascript
async function execute() {
  const runtime = await import('tools://Runtime');
  return runtime.getState();
}
```

使用 `tools://` 发现根 module，使用 `tools://<Tool/Path>` 导入具体 module。内置根包括
`Runtime`、`Runtime/Objects`、`Runtime/Components`、`Runtime/Diagnostics`、
`Runtime/Inspect`、`Runtime/Reflection`、`Runtime/ObserveFrames`、`UnityEval` 以及仅
Editor 可用的 `Editor` 层级。Editor 层级包含直接 viewport image、持久化的 Unity Test
Framework 运行状态、有界序列化代码用法搜索，以及通过 `Editor/Profiler` 执行的全局 metric
发现与有界 PlayMode 主线程 CPU `ProfilerRecorder` 采样。应优先使用这些语义 helper，而不是
直接使用 `CS.*` 互操作。

- [Helper module 参考](docs/HELPER_MODULES_zh.md)
- [进阶 session、编译与错误处理](docs/ADVANCED_USAGE_zh.md)
- [Broker 协议](docs/BROKER_PROTOCOL_zh.md)
- [项目架构](docs/PROJECT_DESIGN_zh.md)

## 扩展 Tool 目录

定义带 `[EvalTool]` 的 partial C# class，使用 `[EvalFunction]` 标记导出方法，再由随包
Roslyn Analyzer 生成 `IEvalTool` 元数据。每个函数都必须声明明确的安全级别。Tool 注册
会在 module 可见前校验路径、可调用子 Tool、JavaScript 导出名、参数和安全元数据。
`MutatesRuntimeState` 只表示 log buffer、观察 session 等进程或 Tool 自有的瞬时状态写入，
不代表 Scene、Project、Editor 或持久用户数据发生变化。

需要运行时手工组合 Tool 时，可以继承 `EvalToolBase`，或直接使用 `EvalToolGroup`、
`EvalReadOnlyValueTool<T>`、`EvalWritableValueTool<T>` 与 `EvalActionTool`。这些类型只负责
Tool 树和函数契约，不依赖任何 Debug UI。根 Tool 可通过 `EvalToolRegistry.RegisterRootScoped`
独立注册；释放返回的句柄只会移除该根 Tool 的同一实例。

可通过 `EvalToolRegistry` 注册 loader-backed JavaScript Tool，并通过 `tools://UnityEval`
检查或启停它们。在该 module 上调用 `getJsToolAuthoringPrompt()` 可获取当前编写契约。
返回值应尽量使用可 JSON 序列化的基础类型、列表、字典或由它们组成的数据。

## 固定本机端点

| 端点 | 用途 |
|---|---|
| `http://127.0.0.1:2347/health` | Broker 健康状态 |
| `http://127.0.0.1:2347/mcp` | MCP Streamable HTTP |
| `ws://127.0.0.1:2347/unity` | Unity 注册与中转 |
| `ws://127.0.0.1:2347/cli` | 原生 CLI 控制台 |

Broker 只绑定 loopback，并在 `2347` 端口不可用时明确失败。它不决定鉴权结果：MCP 与 CLI
可把候选 token 录入 `~/.unityevaltool/auth.json`；每条 Unity 连接自行验证，在匹配前以
`Pending` 状态保持可发现但不可执行。

## 从源码构建

该 UPM Package 直接从 `Packages/com.yuzetoolkit.yuzeevaltool` 源码目录使用，没有
额外的 Unity Package 归档脚本。原生 Broker 与 npm 打包源码位于仓库根目录。详见
[Broker 构建指南](../../Broker/README_zh.md) 和 [Roslyn Generator 指南](../../Roslyn/README_zh.md)。

## 许可证

[MIT](LICENSE)
