# Yuze Eval Tool 架构

[English](PROJECT_DESIGN.md) | **简体中文** | [Package README](../README_zh.md)

## 进程边界

```text
AI MCP clients ──HTTP /mcp──┐
                            ├── C# NativeAOT Broker :2347 ──WebSocket /unity── Unity A
原生 unity CLI ───WS /cli───┘                                      └────────── Unity B
```

Broker 拥有发现、状态快照、事件驱动等待、选择租约、MCP 连接鉴权、MCP 协议、
CLI 控制台和请求路由。Unity 拥有主线程真实状态、编译/重载观察、PuerTS VM、
helper tool 注册和 CLI 命令解析。

## 源码归属

| 区域 | 源码 | 职责 |
|---|---|---|
| 原生 Broker | 仓库 `Broker/src/UnityEvalTool.Broker` | 2347、注册表、MCP、CLI、用户服务管理 |
| npm 打包 | 仓库 `Broker/npm` | 平台选择、显式用户服务 helper、RID 平台包 |
| Unity 传输 | `Runtime/Broker` | 注册、心跳、中转请求、session |
| Editor 生命周期 | `Editor/Broker` | 稳定进程身份、编译/重载状态、拉起 Broker |
| Eval 引擎 | `Runtime/Core` 与 `Runtime/Tools` | PuerTS 执行和生成的 helper module |
| CLI 解析 | `Runtime/CLI/EvalCliCommandService*` | 现有命令语法和打印结果 |

`Broker` 与 `Roslyn` 位于独立仓库根目录，不在 UPM Package 或 Unity 项目的 `Assets` 树中，因此 Unity 不会导入这些 .NET/npm 源码。

## 注册与选择

每次 Unity 注册包含进程内稳定的 `instanceId`、进程生命周期、项目路径、连接 epoch、
VM generation 和完整状态。成员变化会增加 `registryRevision`。`unity_connect` 必须
提交上一次状态快照的准确 revision，并生成随机 256-bit handle。因此选择属于每个
MCP 工作流或 CLI 控制台，不是 Broker 全局状态。

## 编译生命周期

Editor 观察每一次 `CompilationPipeline`。脚本域消失前会报告 `Compiling`、编译器
计数、`CompilationFailed` 和 `Reloading`。Broker 保留断开的实例和选择租约；重载后
同一进程以更高 epoch/VM generation 重连，并在主线程更新后报告 `Ready`。等待发生在
Broker 内部，不依赖 eval。编译失败不会卸载旧脚本域，而是进入可执行的 repair mode。

## 执行保证

- eval 前必须完成状态发现、明确连接并携带有效 handle。
- Unity 必须已连接，并且 `canEval` 或处于 `CompilationFailed` repair mode，Broker 才会转发请求。
- repair mode 使用上一次成功加载的程序集，以便读取错误、修改源码并再次刷新；失败源码不会被视为已加载代码。
- 每个 handle/CLI 控制台在 Unity 侧拥有持久 PuerTS session。
- 重连会改变 VM generation；旧脚本域丢失的 session 不会被伪装成仍然存在。
- 断开后结果不确定的修改请求绝不自动重试。

## 安全

Kestrel 的本机入口信任 loopback 客户端，并拒绝非 loopback Host/Origin。可选网络入口逐请求
验证主机连接密钥，`/mcp` 和 `/remote/mcp` 使用同一密钥，也可显式配置匿名访问。连接获准后
即可访问全部本地项目和可用的主机操作，不再设置项目 token、工作区 grants 或独立桌面凭据。
文件根目录边界、会话归属、可选 Cua 安装和操作系统权限仍然生效。端口冲突、非法凭据/配置
文件和协议不匹配都会明确失败。

可选网络监听只导出直接注册的本地实例，并逐请求验证独立的服务密钥（也可显式配置无密钥）。
本机注册表聚合远端快照并维护来源限定 ID、独立租约和目录版本；网络入口不再导出这些导入项。
远端生命周期与路由实现归 Broker，Unity 的执行协议不变。配置、来源、断线和鉴权契约以
[Broker 协议](BROKER_PROTOCOL_zh.md#单跳远程聚合) 为准。
