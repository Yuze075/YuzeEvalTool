# Yuze Eval Tool

[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-222?logo=unity)](https://unity.com/releases/editor/archive)
[![npm](https://img.shields.io/badge/npm-%40yuzetoolkit%2Funityevaltool-CB3837?logo=npm)](https://www.npmjs.com/package/@yuzetoolkit/unityevaltool)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

[English](README.md) | **简体中文**

Yuze Eval Tool 让 AI Agent 和终端用户检查、操作本机的 Unity Editor 与 Player。
原生的电脑级 Broker 提供 MCP 端点和 `unity` CLI，Unity Package Manager
包则让每个 Unity 进程向 Broker 注册。编译、Domain Reload、进程替换和临时断线
都会被明确报告，不再被 Unity Editor 内部的网络监听器遮蔽。

仓库还包含可选的 Yuze Agent Tool Package，它统一拥有 Editor/Runtime Agent 工作台、
DebugPanel、Command Line、日志与系统监控。

## 需要安装的组件

| 组件 | 用途 | 是否必需 |
|---|---|---|
| `com.yuzetoolkit.yuzeevaltool` | Unity 侧 Broker Client、状态报告、PuerTS eval session、CLI 命令和 helper module | 是 |
| `@yuzetoolkit/unityevaltool` | 原生 Broker、MCP Server、`unity` CLI 和当前用户后台服务 | 是 |
| `com.yuzetoolkit.yuzeagenttool` | 统一 Agent 工作台、Runtime DebugPanel、性能/系统 HUD、日志、命令行和 Tool 目录 | 否 |

Broker/CLI 支持 macOS、Linux 和 Windows 的 x64 与 arm64。Unity Package 需要
Unity 2022.3 或更高版本。安装 Broker 需要 Node.js 18 或更高版本与 npm。WebGL 不是
受支持的 Broker 目标。

## 安装

### 1. 准备 PuerTS backend

Yuze Eval Tool 需要 `com.tencent.puerts.core` 3.0.2，以及且仅需一个兼容的 PuerTS
JavaScript backend。已验证的组合是 `com.tencent.puerts.quickjs` 3.0.2 和与之
匹配的 core Package；也可使用同一 PuerTS 发行系列中受支持的 V8 backend/core。
不得同时安装 QuickJS 与 V8 backend。

如果使用已验证组合，从官方
[PuerTS Unity 3.0.2 Release](https://github.com/Tencent/puerts/releases/tag/Unity_v3.0.2)
下载 `PuerTS_Core_3.0.2.tar.gz` 和 `PuerTS_Quickjs_3.0.2.tar.gz`。解压两个归档，
然后使用 Package Manager 的 **Add package from disk**，先选择已解压 `core` 目录中的
`package.json`，再选择 `quickjs` 中的 `package.json`。
[PuerTS 官方安装指南](https://github.com/Tencent/puerts/blob/Unity_v3.0.2/doc/unity/en/install.md)
也说明了其它 backend 的安装方式。

Yuze Eval Tool Package 会声明 core 依赖，但不会替项目选择 JavaScript backend。

### 2. 添加 Yuze Eval Tool Package

在 Unity 中打开 **Window > Package Manager**，选择 **Add package from git URL**，输入：

```text
https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeevaltool#v2.0.7
```

对应的 `Packages/manifest.json` 依赖是：

```json
{
  "dependencies": {
    "com.yuzetoolkit.yuzeevaltool": "https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeevaltool#v2.0.7"
  }
}
```

如果要直接针对本地 clone 开发，使用 Package Manager 的 **Add package from disk**，
选择 clone 中的 `Packages/com.yuzetoolkit.yuzeevaltool/package.json`。

### 3. 安装 Broker 与 CLI

全局安装原生 npm Package，然后显式安装当前用户服务：

```bash
npm install --global @yuzetoolkit/unityevaltool
unity service install
unity doctor
```

该服务只为当前用户运行：macOS 使用 LaunchAgent，Linux 使用 systemd user unit，
Windows 使用计划任务。它不需要系统级特权 daemon。由于 npm 可能禁用 dependency
lifecycle script，服务安装是一条明确命令；继续前应确认 `unity service install` 成功。

### 4. 验证 Unity 连接

打开一个已安装 Yuze Eval Tool Package 的项目，等待 Unity 编译完成，然后执行：

```bash
unity doctor
unity list
unity connect <instance-id> -- Runtime getState
```

`unity doctor` 应报告 Broker 可连接；`unity list` 应列出打开的 Editor、项目路径和
当前 phase；把该行 ID 替换到最后一条命令中。最后一条命令用于证明 Broker 到 Unity 的端到端
执行可用。也可以在 Editor 中
打开 **YuzeToolkit > Yuze Eval Tool**，检查注册、连接状态和 eval 可用性。如果没有实例，
请检查 `unity service status`，确认 Package 已成功编译，并确认 loopback 端口 `2347`
未被其它进程占用。

## CLI 快速使用

在 Unity 项目目录中执行 `unity` 可自动选中匹配的 Editor；也可使用
`unity list` 返回的 ID 显式连接：

```bash
unity
unity connect <instance-id>
unity Runtime getState
unity eval-js --code "return 1 + 2;"
unity tools
```

前两条命令会进入交互控制台。其中 `:status`、`:wait`、`:switch`、`:help` 和
`:quit` 用于控制 Broker 连接，其它输入会转发给 Unity 命令解析器。执行
`unity --help` 或 `unity <command> --help` 可查看已安装 CLI 的完整语法。

## MCP 配置

Broker 提供以下 Streamable HTTP MCP 端点：

```text
http://127.0.0.1:2347/mcp
```

项目 token 验证默认关闭，因此 MCP Client 通常只需配置端点 URL。若要保护某个项目或
发行 Player，请打开 **Project Settings > YuzeToolkit > Yuze Eval Tool**，生成或输入 token，
Apply 后开启验证。Unity 只会把加盐 verifier 保存到
`Assets/Resources/UnityEvalToolAuthorizationSettings.asset`，原始 token 不会写入项目。
Unity 会把该资源直接打进 Player，并通过标准 Resources API 读取。

首次可让 MCP Client 携带该 token：

```text
Authorization: Bearer <token[/另一个-token...]>
```

Broker 会把传入值持久化到 `~/.unityevaltool/auth.json`，并发送给仍在等待验证的所有 Unity；
之后 MCP 可以不再携带 Header。原生 CLI 通过 `unity --token <token> ...` 执行同样的首次录入。
也可直接维护 `auth.json`。默认最多保存 5 个不同 token，可在
`~/.unityevaltool/config.json` 通过 `maxStoredTokens` 调整（硬上限 32）。token 只允许 ASCII
大小写字母、数字、`_`、`-`，`/` 用来分隔多个值。

MCP Server 只提供三个工具，必须按此顺序使用：

1. `unity_status` 发现 Unity 实例并报告当前状态。
2. `unity_connect` 使用已知 `registryRevision` 中的准确 `instanceId` 进行选择，
   并返回仅属于当前工作流的不透明 handle。
3. `eval` 在已选择且状态允许的 Unity 中执行 JavaScript。

最小 `eval` 程序如下：

```javascript
async function execute() {
  return 1 + 2;
}
```

内置 `tools://` helper 覆盖常见 Unity 工作流。其中 `tools://Editor/Profiler` 可从全局 Profiler
registry 枚举精确 category/name，并在 PlayMode 中执行有界跨帧主线程 CPU
`ProfilerRecorder` session，无需开启全局 Profiler recording。详见
[helper 参考](Packages/com.yuzetoolkit.yuzeevaltool/docs/HELPER_MODULES_zh.md)。

同一 Unity 进程发生 Domain Reload 或 registry 变化后应继续复用有效 handle；只有
handle 过期、失效或 Unity 进程被替换时才重新连接。修改型 `eval` 如果在派发后
连接中断，不得自动重试，因为其结果可能不确定。详见[进阶使用](Packages/com.yuzetoolkit.yuzeevaltool/docs/ADVANCED_USAGE_zh.md)
和 [Broker 协议](Packages/com.yuzetoolkit.yuzeevaltool/docs/BROKER_PROTOCOL_zh.md)。

## 可选的 Agent 与 Runtime Debug UI

安装 Yuze Eval Tool 后，使用 **Add package from git URL** 添加 Yuze Agent Tool：

```text
https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeagenttool#v2.0.7
```

然后把该 Package 中的 `Runtime/Panel/Prefabs/DebugPanel.prefab` 放入 Scene 或常驻
Prefab。面板不会自动创建。模块、持久化模型、默认快捷键和 API 详见
[Yuze Agent Tool Package README](Packages/com.yuzetoolkit.yuzeagenttool/README_zh.md)。

默认快捷键为：`F8` 打开 Yuze Agent Tool，`F10` 打开 Performance 与 System Information HUD。

## 安全边界

Broker 只绑定 `127.0.0.1:2347`，并拒绝非 loopback Host/Origin。Broker 本身不做全局鉴权，
只负责保存并转发候选 token；每个 Unity 项目或 Player 自己决定是否要求验证，并且只有某个
候选 token 能生成已保存的加盐 verifier 后才接受操作。包含 Yuze Eval Tool 的、受支持的非
WebGL Release Player 会有意向 Broker 注册，并保留任意 JavaScript eval 能力；它不仅限于
Development Build，也不依赖 Yuze Agent Tool。该 verifier 能阻止不知道 token 的普通 Broker
访问，但不能抵御能够修改 Player 二进制的攻击者。详见
[Editor 与 Player 注册](Packages/com.yuzetoolkit.yuzeevaltool/docs/RUNTIME_SERVICES_zh.md)。

## 服务管理与卸载

```bash
unity service status
unity service start
unity service stop
unity service restart
```

卸载时，必须趁 `unity` 可执行文件仍存在时先移除服务。确认第一条命令成功后，
再移除 npm Package：

```bash
unity service uninstall
npm uninstall --global @yuzetoolkit/unityevaltool
```

npm 不会自动执行服务卸载 helper。

## 从源码构建与打包

源码构建需要 `global.json` 选定的精确 .NET SDK（当前为 10.0.300）、Node.js 22，
以及当前操作系统和架构所需的 NativeAOT 工具链。在该仓库 clone 中执行：

```bash
cd Broker
node npm/scripts/pack-platform.mjs
node npm/scripts/pack-root.mjs
```

生成的 `.tgz` 位于 `Broker/artifacts/npm/`。六种操作系统/架构原生包必须在
与目标匹配的主机上构建。完整的构建、测试、版本校验与产物路径见
[Broker 构建指南](Broker/README_zh.md)；Source Generator 构建见 [Roslyn/README_zh.md](Roslyn/README_zh.md)。

上述命令只会生成本地产物。产物如何分发或发布，由维护者自行选择 Registry
和自动化配置，不属于本项目的打包流程。

## 文档

- [Yuze Eval Tool Package](Packages/com.yuzetoolkit.yuzeevaltool/README_zh.md)
- [Yuze Agent Tool Package](Packages/com.yuzetoolkit.yuzeagenttool/README_zh.md)
- [进阶使用](Packages/com.yuzetoolkit.yuzeevaltool/docs/ADVANCED_USAGE_zh.md)
- [Helper module 参考](Packages/com.yuzetoolkit.yuzeevaltool/docs/HELPER_MODULES_zh.md)
- [Editor 与 Player 注册](Packages/com.yuzetoolkit.yuzeevaltool/docs/RUNTIME_SERVICES_zh.md)
- [Broker 协议](Packages/com.yuzetoolkit.yuzeevaltool/docs/BROKER_PROTOCOL_zh.md)
- [项目架构](Packages/com.yuzetoolkit.yuzeevaltool/docs/PROJECT_DESIGN_zh.md)
- [Broker 构建与打包](Broker/README_zh.md)
- [Roslyn Source Generator](Roslyn/README_zh.md)
- [变更记录](CHANGELOG_zh.md)

## 许可证

[MIT](LICENSE)
