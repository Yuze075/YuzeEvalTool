# Unity Package 变更记录

[English](CHANGELOG.md) | **简体中文**

仓库级[变更记录](../../../CHANGELOG_zh.md) 是 Unity Package、电脑级 Broker/CLI、npm Package 和
Roslyn Generator 的规范发行历史。

## 2.0.7 - 2026-08-24

- 增加 `tools://Editor/Profiler`，按精确 metric 发现全局 registry，并在 PlayMode 中有界跨帧
  采样主线程 CPU 数据，返回 raw 统计、invocation count 与分页 sample，并确定性清理 recorder。

## 2.0.4 - 2026-08-15

- 增加按项目配置的 Unity 鉴权，包括 Resources 加盐 verifier Asset、UI Toolkit 设置页、
  标准 Player 打包和明确的 Pending/Authorized 状态。
- MCP/CLI 可向 Broker 录入多个持久候选值，但唯一鉴权决策仍由 Unity 执行；验证默认关闭。

## 2.0.3 - 2026-08-15

- 要求每个 C# 与 JavaScript Tool 函数提供显式非空 safety metadata，在注册前完成校验，
  并把瞬时 Runtime 状态写入与 Scene、Project、Editor 及持久数据写入区分开。

- 将仅 loopback 的 Broker token 认证从默认前置改为显式可选；一个环境开关会同时为
  MCP、Unity 与 CLI 应用 token 边界。
- 增加直接 viewport image 结果、带跨 reload 持久有界记录的可选 Test Framework
  发现/运行/取消、序列化代码用法搜索和有界跨帧观察 helper。
- 增加一一对应的英文与简体中文 Package 文档，并把完整安装与首次使用指南统一导向
  仓库 README。

## 2.0.2 - 2026-08-13

- 准备 Package 2.0.2，并将 Git URL 安装锁定到不可变 tag `v2.0.2`。
- 将已提交 Source Generator Analyzer 保存为普通 Git blob，用于 UPM Git 安装和确定性
  二进制校验。
- eval 结果以原生 MCP text/image/error content 跨 Broker 传输；每 Unity 串行化可防止已超时
  调用方的排队请求之后继续执行。
- Tool 注册时递归校验 JavaScript descriptor、可调用子节点解析、safety flag 和导出标识符。
  `PersistsData` 表示持久的非项目写入。
- 跨进程原子创建 Broker auth token，限制未认证 socket，并在 deadline 后中止停滞的关闭握手。
- Source Generation 阶段直接报告嵌套 Tool 类型、async/Task-like 函数和 JavaScript 保留导出名，
  不再等到 Runtime 注册。
- 受支持的非 WebGL Release Player 有意保留经认证的任意 JavaScript eval，不依赖 UnityDebugTool。
- `CompilationFailed` 改为使用上一次成功加载程序集的可执行 repair mode，让 MCP/CLI
  可以读取错误、修改源码并再次刷新。
- 引导 Agent 通过 Broker 状态等待，而不是轮询 eval；同一进程的 registry 变化与 Domain Reload
  后保留现有 handle。

## 2.0.1 - 2026-08-12

- 将 PuerTS session 绑定到 Broker 租约与 CLI 控制台生命周期，包括在 Unity 临时断线期间延后释放。
- 防止旧 Broker 重连 generation 覆盖更新连接，并在 Broker 进程被替换后重置属于旧进程的 session。
- 通过节流的普通 update 心跳发布 Editor 状态，不持续请求额外 player-loop update。
- 同步 Package 与 Broker 版本，并将 Roslyn Generator 源码保存在仓库 `Roslyn` 目录，不再嵌入源码归档。

## 2.0.0 - 2026-08-12

- 用绑定 `127.0.0.1:2347` 的单个经认证电脑级 NativeAOT Broker 替换 Unity 内托管的 MCP/CLI listener。
- 增加事件驱动 Unity 发现、选择 handle、编译等待、状态 phase、原生 CLI 服务管理和六平台 npm 打包。
