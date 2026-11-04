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
公开 Unity API 分别使用 `YuzeToolkit.Eval` 与 `YuzeToolkit.Agent`；供 asmdef 引用的
`UnityEvalTool*` 与 `UnityAgentTool*` 程序集名保持不变。

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
https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeevaltool#v4.0.0
```

对应的 `Packages/manifest.json` 依赖是：

```json
{
  "dependencies": {
    "com.yuzetoolkit.yuzeevaltool": "https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeevaltool#v4.0.0"
  }
}
```

如果要直接针对本地 clone 开发，使用 Package Manager 的 **Add package from disk**，
选择 clone 中的 `Packages/com.yuzetoolkit.yuzeevaltool/package.json`。

### 3. 安装 Broker 与 CLI

已安装 Unity Package 后，可点击 **Window > YuzeToolkit > Install Eval Tool (npm)**。
菜单会打开终端，依次执行以下命令，任一步失败便停止后续命令，并保留终端输出。
终端环境需要能找到 Node.js/npm；Linux 还需要 `x-terminal-emulator`。
Windows 若在服务安装时提示拒绝访问，请在管理员终端重试 `unity service install`。

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
打开 **YuzeToolkit > Eval Tool**，检查注册、连接状态和 eval 可用性。如果没有实例，
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

从 4.0.0 起，授权统一归 MCP 连接所有。本机 loopback 入口为受信任入口；网络 `/mcp` 与
`/remote/mcp` 共用 `remoteAccess`，每次请求验证同一把 Bearer 密钥。连接通过认证后即可访问
全部已注册项目、文件读写、终端和可用的桌面控制，不再设置 Unity 项目 token 或项目能力授权。
Cua 保持可选；操作系统仍须允许它的辅助功能 / 屏幕录制权限，MCP 密钥不能代替系统授权。

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

## 远程 Unity 项目

本机 MCP 入口和 `unity list` 可以聚合多台机器的 Unity 实例。展示名称使用
`[Local] 项目名` 或 `[Remote:远端ID] 项目名`；`projectName`、`projectPath` 保留真实值。
只发现已注册的 Editor / Player，不扫描未打开的工程，也不导入远端从其他机器加载的项目。

双方需要使用支持远程聚合的 Broker。在可信局域网中，不使用服务密钥的最小配置为：

```bash
# 被连接的电脑 B：填写 B 的网卡 IP，端口可自选
unity remote access none 192.168.1.20 2348
unity service restart

# 本机 A
unity remote add office-pc 192.168.1.20 2348
unity service restart
unity list
```

使用服务密钥时，先在双方通过 `unity remote credential office-key < secret.txt`
录入相同密钥（从 stdin 读取，不作为命令行参数）。然后 B 使用
`unity remote access token 192.168.1.20 2348 office-key`，A 使用
`unity remote add office-pc 192.168.1.20 2348 office-key`。修改后分别重启服务。
已有相同 ID 时请编辑配置，或先 `unity remote remove office-pc` 再添加。

配置写入 `~/.unityevaltool/config.json`，服务密钥独立保存在 `peer-auth.json`，稳定机器身份
保存在 `broker-id`。`unity remote list` 校验并列出配置，`unity list` 显示运行中的远端状态与错误码。
配置在服务启动时加载；删除或禁用远端也在重启后生效。`unity remote access off` 关闭网络导出。

网络 `/mcp` 只允许发现、连接、执行该 Broker 直接注册的本地实例。
本机入口仍为 `127.0.0.1:2347/mcp`，使用原来的三个工具访问所有已配置来源。
远程实例 ID 形如 `remote:office-pc:<原实例ID>`；必须明确选择来源，不能用本机工作目录匹配远程路径。
远程交互式 CLI 执行暂不支持，选择远程实例时会明确报告 `RemoteCliUnsupported`。

网络入口在每个请求中验证连接密钥，验证成功即取得全部已注册项目的访问能力。跨不可信网络使用 HTTPS 反向代理或加密隧道；代理应指向网络导出端口，
并保留明确的 Host / Origin 允许列表，不能转发到本机聚合入口。HTTPS 出站可用
`unity remote add office-pc <主机名> <端口> office-key https` 配置。
完整配置、来源字段、断线和租约语义见 [Broker 协议](Packages/com.yuzetoolkit.yuzeevaltool/docs/BROKER_PROTOCOL_zh.md)。

## 远程开发工作区

3.3.0 起支持任意文件夹的持久注册；本机 Unity 连接会自动登记项目，断开后保留文件夹与远程能力。
项目身份稳定，Unity 能力按实际连接动态变化。

可选的 `/remote/mcp` 视图提供项目文件与图片、终端任务、项目范围的 AGENTS / Skills、
会话事件和独立桌面执行器。本机视图只路由到配置的远端；经过鉴权的网络视图只导出明确共享的
本地项目根目录。原 `/mcp` 保持三个 Unity 工具，两个入口共用同一连接密钥，不再额外授予项目能力。

配置、授权、客户端接入、故障恢复和桌面验证边界见[远程工作区指南](Broker/REMOTE_WORKSPACES_zh.md)。
本机 `/remote/status` 页面打开时会展示连接变化；它不能唤醒闲置的 AI 会话。

## 可选的 Agent 与 Runtime Debug UI

安装 Yuze Eval Tool 后，使用 **Add package from git URL** 添加 Yuze Agent Tool：

```text
https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeagenttool#v4.0.0
```

然后把该 Package 中的 `Runtime/Panel/Prefabs/DebugPanel.prefab` 放入 Scene 或常驻
Prefab。面板不会自动创建。模块、持久化模型、默认快捷键和 API 详见
[Yuze Agent Tool Package README](Packages/com.yuzetoolkit.yuzeagenttool/README_zh.md)。

默认快捷键为：`F8` 打开 Yuze Agent Tool，`F10` 打开 Performance 与 System Information HUD。

## 安全边界

从 4.0.0 起，授权统一归 MCP 连接所有。本机 loopback 入口为受信任入口；网络 `/mcp` 与
`/remote/mcp` 共用 `remoteAccess`，每次请求验证同一把 Bearer 密钥。连接通过认证后即可访问
全部已注册项目、文件读写、终端和可用的桌面控制，不再设置 Unity 项目 token 或项目能力授权。
Cua 保持可选；操作系统仍须允许它的辅助功能 / 屏幕录制权限，MCP 密钥不能代替系统授权。

网络入口默认关闭，仍执行 Host / Origin 校验并仅导出本机项目。受支持的非 WebGL Release Player
注册到受信任本机 Broker 后保留任意 JavaScript eval。详见
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

源码构建需要 `global.json` 选定的精确 .NET SDK（当前为 10.0.403）、Node.js 22，
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
