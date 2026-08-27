# Editor 与 Player 注册

[English](RUNTIME_SERVICES.md) | **简体中文** | [Package README](../README_zh.md)

Yuze Eval Tool 不再让 Unity 自己监听 MCP 或 CLI。电脑级 Broker 必须已安装并运行；
Editor 和受支持的非 WebGL Player 都主动向它注册。

## Editor

主 Editor 进程会自动启动 `UnityBrokerClient`，Asset Import Worker 会被
`EditorProcessGuard` 排除。如果无法连接 Broker，Unity 会读取
`~/.unityevaltool/install.json`，尝试拉起已安装的原生程序。

`EditorBrokerStatusMonitor` 捕获编译和程序集重载状态。Domain Reload 前，客户端会
发布 `Reloading` 并断开；之后使用同一进程 instance ID 和更高 VM generation 重连。

项目鉴权在 **Project Settings > YuzeToolkit > Eval Tool** 配置，单一制作源为
`Assets/Resources/UnityEvalToolAuthorizationSettings.asset`。Asset 不存在或 `RequireToken` 为
false 时关闭验证。Apply token 会创建随机 salt，只保存 `PBKDF2-HMAC-SHA256-v1` verifier；
原始 token 绝不会序列化进项目。

## Player

`UnityBrokerRuntimeBootstrap` 会在非 Editor 构建中创建隐藏的 `DontDestroyOnLoad`
runner，报告运行时心跳和播放状态，以可执行程序目录作为项目路径注册，并在退出时
发布 `Exiting`。Unity 会通过标准 Resources 管线把鉴权 Asset 加入 Player，运行时按资源名
直接加载，不使用额外的构建后复制或生成文件。Broker 仍由已安装的用户服务负责托管。

这是明确保留的正式产品契约，不是仅供 Editor 或 Development Build 使用的降级路径：
受支持的 Release Player 同样会注册，并接受任意 JavaScript eval。可选的 Yuze Agent Tool
UI Package 与 Yuze Eval Tool 的 Player runtime client 相互独立。验证默认关闭；开启后 Player
以 `Pending` 注册，用内嵌 salt 对 Broker 发送的候选 token 做 hash，只有某个 verifier 匹配后
才接受命令。Broker 只在本机保存原始候选值，不决定鉴权结果。该边界能防止不知道 token 的
普通访问，但不能抵御可修改 Player 二进制的攻击者。集成本 Package 的项目除非明确改变产品
设计，否则应完整保留所选择的链路。

WebGL 不是受支持的 Broker 目标，因为该平台无法使用本地 ClientWebSocket/
当前用户服务模型。

## 原生程序与用户服务

已发布的 `unity` 原生程序在每个进程启动时只注册一次自身绝对路径。它在有界跨进程锁内写入
同目录完整临时文件，再以原子替换发布 `~/.unityevaltool/install.json`。读取端因此只会看到
替换前或替换后的完整文档，不会遇到原地截断产生的半写入窗口。

`unity service install|start|restart` 以精确参数向量调用 launchd、systemd 或 Windows Task
Scheduler，并在报告成功前等待 Broker 健康端点。Windows 安装还会读取刚创建的任务 XML，确认
Action 的 Command 是当前原生程序，Arguments 严格为 `broker`。平台服务不可用时，status 命令
以失败退出；Windows status 还会校验已保存的任务 Action 与 Broker 健康状态。

## 公共运行时接口

- `UnityBrokerClient.Shared.IsConnected`
- `UnityBrokerClient.Shared.AuthorizationState`
- `UnityBrokerClient.Shared.Identity`
- `UnityBrokerClient.Shared.GetSessionSnapshots("mcp:")`
- `UnityBrokerClient.Shared.GetSessionSnapshots("cli:")`
- 用 `UnityBrokerClient.Shared.Stop()` / `Start()` 显式重连

Yuze Agent Tool 的 Command Line 与 Eval 设置页面使用共享 Broker client，
不自己做服务发现、进程启动或维护独立 listener。
