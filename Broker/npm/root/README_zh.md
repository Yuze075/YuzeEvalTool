# @yuzetoolkit/unityevaltool

[English](README.md) | **简体中文** | [完整文档](https://github.com/Yuze075/YuzeEvalTool/blob/main/README_zh.md)

[Yuze Eval Tool](https://github.com/Yuze075/YuzeEvalTool) 的原生 C# Broker、Streamable HTTP
MCP Server 和 `unity` CLI。npm 入口包会为 macOS、Linux 或 Windows 的 x64/arm64
选择匹配的原生包。

每个需要使用 Yuze Eval Tool 的 Unity 项目还必须安装 Unity 侧 UPM Package。

## 安装

```bash
npm install --global @yuzetoolkit/unityevaltool
unity service install
unity doctor
```

`unity service install` 会创建并启动绑定 `127.0.0.1:2347` 的当前用户后台服务：
macOS 使用 LaunchAgent，Linux 使用 systemd user unit，Windows 使用计划任务。
它不会安装特权系统服务。由于 npm 可能禁用 dependency lifecycle script，服务设置必须
显式执行；请检查该命令的退出状态。

通过 Unity Package Manager 添加 Unity 侧 Package：

```text
https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeevaltool#v3.3.0
```

打开 Unity 项目并等待编译完成，然后执行 `unity list`。只有看到该 Editor 才完成
第一次端到端注册检查；`unity doctor` 单独只检查 Broker 健康状态。

## CLI 快速使用

```bash
unity list
unity                         # 按当前项目目录选择
unity connect <instance-id>   # 精确选择 Unity 实例
unity Runtime getState        # 执行一次 Unity 侧命令
unity eval-js --code "return 1 + 2;"
unity tools
```

`unity` 和 `unity connect` 会打开交互控制台。其 Broker 控制命令是 `:status`、
`:wait`、`:switch`、`:help` 和 `:quit`；其它输入会发送给 Unity 命令解析器。

## MCP

将 Streamable HTTP MCP Client 连接到：

```text
http://127.0.0.1:2347/mcp
```

Unity 项目默认不要求 token 验证，因此 MCP Client 通常只需配置端点 URL。某个 Unity 项目已在
Project Settings 开启验证时，首次通过以下 Header 录入 token：

```text
Authorization: Bearer <token[/另一个-token...]>
```

Broker 会把传入值保存到 `~/.unityevaltool/auth.json`，之后可省略 Header。CLI 等价入口为
`unity --token <token> ...`，也可以直接编辑 `auth.json`。默认最多保存 5 个 token；可在
`~/.unityevaltool/config.json` 用 `maxStoredTokens` 调整，硬上限 32。Broker 只转发候选值；
每个 Unity 自行验证，匹配前保持可发现但不可执行。MCP 流程始终是
`unity_status` → `unity_connect` → `eval`；必须先发现并明确选择。详见
[使用指南](https://github.com/Yuze075/YuzeEvalTool/blob/main/README_zh.md) 和
[Broker 协议](https://github.com/Yuze075/YuzeEvalTool/blob/main/Packages/com.yuzetoolkit.yuzeevaltool/docs/BROKER_PROTOCOL_zh.md)。

## 远程项目

支持把其他机器上的 Broker 本地实例聚合到自己的 MCP 和 `unity list`，显示为
`[Local]` / `[Remote:远端ID]`。只导入一跳，不会再次导入对方的远程项目。

```bash
# 远端：可信局域网的无密钥导出示例
unity remote access none 192.168.1.20 2348
unity service restart
# 本机
unity remote add office-pc 192.168.1.20 2348
unity service restart
unity list
```

服务密钥可用 `unity remote credential <名称> < secret.txt` 从 stdin 保存，然后在
`remote add` 的端口后指定名称；远端使用 `remote access token <IP> <端口> <名称>`。
服务密钥与现有 Unity 项目 token 分开管理。网络入口默认关闭，仅开放本地实例的 `/mcp`。
配置在重启后生效。远程项目使用现有三个 MCP 工具；交互式 CLI 远程执行暂不支持。
更多配置和 HTTPS 部署说明见[远程项目指南](https://github.com/Yuze075/YuzeEvalTool/blob/main/README_zh.md#远程-unity-项目)。

## 服务管理

```bash
unity service status
unity service start
unity service stop
unity service restart
unity service uninstall
```

本机入口只接受 loopback 流量，并在端口 `2347` 被占用时明确失败；可选网络导出使用独立端口。

## 卸载

npm 不会自动执行服务卸载 helper。必须趁 `unity` 可执行文件仍存在时先移除
当前用户服务，确认成功后再移除全局 Package：

```bash
unity service uninstall
npm uninstall --global @yuzetoolkit/unityevaltool
```

如果第一条命令失败，应先解决它报告的服务错误，再继续卸载。

## 许可证

[MIT](https://github.com/Yuze075/YuzeEvalTool/blob/main/LICENSE)

## 普通项目与自动注册

3.3.0 可用 `unity workspace add <id> <绝对目录> [名称]` 注册非 Unity 文件夹，供其他主机远程访问。
Unity 连接会自动注册本机项目根目录；连接 / 断开只改变 Unity 能力，保留项目 ID、文件与终端能力。
自动注册的共享策略用 `unity workspace defaults <能力> <密钥引用|off>` 设置，默认不授权。
完整配置与能力说明见[远程工作区指南](https://github.com/Yuze075/YuzeEvalTool/blob/main/Broker/REMOTE_WORKSPACES_zh.md)。
