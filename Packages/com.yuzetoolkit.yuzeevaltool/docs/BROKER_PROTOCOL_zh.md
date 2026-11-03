# Yuze Eval Tool Broker 协议

[English](BROKER_PROTOCOL.md) | **简体中文** | [Package README](../README_zh.md)

本文档定义电脑级 Yuze Eval Tool Broker 与 Unity Client 之间的稳定边界。Broker 负责发现、
状态、选择、等待和中转；Unity 负责 PuerTS eval session、Tool 注册和 CLI 命令解析。

## 端点

- `http://127.0.0.1:2347/mcp`：MCP Streamable HTTP 端点。
- `ws://127.0.0.1:2347/unity`：Unity Client 连接。
- `ws://127.0.0.1:2347/cli`：交互式 CLI 连接。
- `http://127.0.0.1:2347/health`：Broker 健康状态快照。

本机入口绑定 loopback。如果 `2347` 不可用，必须明确失败，不得静默改用其它端口。
本机入口不设置全局鉴权门禁，只保存候选 token 并转发给 Unity；验证结果由每条 Unity 连接
自己决定。健康状态中的 `requireToken` 固定为 false，同时报告 `storedTokenCount` 与
`maxStoredTokenCount`。

MCP 可通过 `Authorization: Bearer token[/token...]` 或 `X-Yuze Eval Tool-Token` 录入候选值；
CLI 通过公开的 `--token` 参数把同一列表放入 `cli/hello`。值只允许 ASCII 大小写字母、数字、
`_`、`-`，`/` 仅作为列表分隔符。显式传入的值保存到 `~/.unityevaltool/auth.json`；默认容量
为 5，可在 `~/.unityevaltool/config.json` 用 `maxStoredTokens` 调整，硬上限 32。相同值不会
重复保存。

手工配置使用以下 schema（单数 `token` 镜像第一项，用于兼容旧读取方）：

```json
{
  "schemaVersion": 2,
  "token": "project-one_token",
  "tokens": ["project-one_token", "project-two_token"]
}
```

## 单跳远程聚合

默认关闭网络监听；启用后在独立 IP / 端口提供 `/mcp`，只导出直接注册的本地实例。
监听器决定访问范围，客户端 Header 无法改变范围。网络入口不开放 `/unity`、`/cli`、`/health`，
也不接受导入项目的 ID 或租约；本机 MCP 和 CLI 列表则返回聚合视图。A→B→C 与 A↔B
都不会递归导入。Unity 注册协议仍为 `2.0`，本地 ID 保持不变，但 `remote:` 前缀保留给导入实例。

独立授权的 `/remote/mcp` 工作区协议见 [Broker 远程工作区指南](../../../Broker/REMOTE_WORKSPACES_zh.md)。
它的项目根、密钥、会话句柄和工具目录独立于 Unity 租约，同样只导出自身本地能力。

`config.json` 可与现有 `maxStoredTokens` 共存，例如：

```json
{
  "maxStoredTokens": 5,
  "remotes": [
    { "id": "office-pc", "host": "192.168.1.20", "port": 2348,
      "scheme": "http", "credentialRef": "office-key", "enabled": true }
  ],
  "remoteAccess": {
    "enabled": false, "listenAddress": "192.168.1.10", "port": 2348,
    "authMode": "token", "credentialRef": "incoming-key",
    "allowedHosts": [], "allowedOrigins": []
  }
}
```

`credentialRef` 可省略，表示出站不携带密钥。服务端 `authMode` 为 `token` 或 `none`；
开启 token 模式时必须有凭据。`peer-auth.json` 为独立的名称到密钥映射：

```json
{ "office-key": "replace-with-peer-secret", "incoming-key": "replace-with-export-secret" }
```

这些密钥不进入 `auth.json`，不转发给 Unity 或其他远端。本地调用者的 Header 不透传给远端。
HTTP 网络入口不内建 TLS；HTTPS 由反向代理终止，出站支持 http/https。禁止自动 HTTP 重定向，
避免向其他地址发送凭据。缺失 Origin 可接受；出现 Origin 时必须精确命中 `allowedOrigins`。
Host 必须匹配监听 IP 或 `allowedHosts`；绑定通配地址时必须显式配置 `allowedHosts`。
网络端口不能与本地端口相同。配置在启动时验证并加载，变更需重启服务。

可选顶层时序参数（单位为秒，除并发数）：

| 参数 | 默认值 | 范围 |
|---|---:|---:|
| discoveryIntervalSeconds | 10 | 1–3600 |
| connectionTimeoutSeconds | 10 | 1–300 |
| reconnectMaxDelaySeconds | 60 | 1–3600 |
| remoteRetentionSeconds | 300 | 0–86400 |
| maxConcurrentDiscoveries | 4 | 1–32 |
| transportGraceSeconds | 10 | 1–300 |

每个配置目录的 `broker-id` 持久保存 GUID；`brokerInstanceId` 则每次启动变化。
不要将不同机器的 `broker-id` 一起复制。自连接、同一 Broker 的多个地址和运行期间身份替换
报告 `RemoteIdentityConflict`。不支持导出协议的端点报告 `RemoteIncompatible`。

状态快照新增 `federation: { version: 1, brokerId, brokerInstanceId, view, leaseIdleTimeoutSeconds }`。
导出入口的 `view` 必须为 `local`；本机为 `aggregate`。导入方同时验证所有实例的本地来源。
实例新增 `displayName` 与 `source: { kind, remoteId, brokerId, isStale }`，导入实例 ID 为
`remote:<remoteId>:<原始instanceId>`，真实项目名与路径不变。`remoteBrokers` 逐项报告
`id`、`endpoint`、`state`（connecting / online / offline / disabled）、`errorCode`、
`lastSeenAtUtc`、`projectCount`，不包含凭据。

本地和聚合目录各自维护 revision。聚合连接先验证聚合 revision，再使用发现时对应的远端
revision；不会把本机版本号传给远端。本机 opaque handle 映射到独立远端 handle，不共享 VM。
对远程实例的定向 `unity_status` 返回远端的 `capturedAtUtc`；编译前必须带该实例 ID 或 handle
采样，再将时间标记传回同一来源，不能使用无目标的聚合列表时间比较远端编译。

远端刷新采用有界并发的后台快照请求，失败按指数退避重连，不阻塞本地来源。离线时保留有期限的
快照，标记 `source.isStale=true`、`isConnected=false`、`canEval=false`；过期删除项目快照。
网络短断线不会替换远端租约；恢复后验证 Broker 代次与 Unity 进程身份。Broker 重启、Unity
进程替换或租约到期需要重新发现和连接。活动状态等待不会被本机空闲清理打断，远端始终校验实际租约。
本机失活租约删除后，远端会话按原有 idle timeout 回收。未知结果的 eval 报告
`ExecutionOutcomeUnknown` / `mayHaveExecuted=true`，绝不自动重放；完整 MCP 结果（含图片和错误）原样返回。

交互式 CLI 执行仅支持本地项目，远端选择报告 `RemoteCliUnsupported`，且 cwd / 路径匹配只考虑
本地实例。配置命令和首次使用见[主指南](../../../README_zh.md#远程-unity-项目)。
隔离部署可用 `unity broker --config <config.json> --local-port <port>`；凭据与身份跟随配置目录，
不会更新默认安装记录。`unity remote --config <config.json> ...` 管理同一隔离配置。

## 消息封装

Unity 和 CLI WebSocket 每条 WebSocket 消息交换一个 UTF-8 JSON object：

```json
{
  "protocol": "2.0",
  "type": "request",
  "id": "globally-unique-request-id",
  "method": "eval/execute",
  "payload": {}
}
```

`type` 为 `request`、`response` 或 `event`。失败响应包含 `error` object，其中有稳定
`code`、人类可读的 `message`，以及在执行结果不确定时出现的 `mayHaveExecuted`。

## Unity 注册

Unity 的第一条消息必须是 `unity/register`。其 payload 包含：

- `authToken`：保留为空的兼容字段
- `instanceId`：在单个 Unity 进程的 Domain Reload 之间保持稳定
- `connectionEpoch`：每个 Unity 侧连接 generation 递增
- `processId` 和 `processStartedAtUtc`
- `projectName`、规范化 `projectPath`、`unityVersion`、`packageVersion`
- `environment`：`Editor` 或 `Player`
- `authorizationRequired`：该 Unity 项目是否要求 token
- `authorizationState`：初始 `NotRequired` 或 `Pending` 状态
- 完整的初始 `status`

只有主 Unity Editor 进程可以注册。Asset Import Worker 绝对不得注册或启动 Broker。
成功响应包含 `brokerInstanceId`，它在当前 Broker 进程中唯一。如果该值改变，Unity
会丢弃所有保留的 PuerTS session，避免新 Broker 进程意外继承已不属于它的 session。
同一响应会在 `tokens` 中带上当前保存的所有候选值。Unity 使用项目 salt 对候选值做 hash，
并通过 `unity/authorization` 发布 `Authorized`、`Pending` 或 `NotRequired`。之后新增的候选值
通过 `auth/tokens` event 发送。

Pending Unity 仍会出现在发现和状态结果中，但 `ready` 等待以及所有 MCP/CLI 操作都会以
`UnityAuthorizationPending` 拒绝。验证对每次 Unity 连接只执行一次；任一候选值成功后，
后续命令不会重复验证，也不设置过期时间。

## 状态

Unity 发布 `unity/status` event。状态包含互相独立的传输和主线程观察，以及：

- `phase`：`Starting`、`Ready`、`Importing`、`Compiling`、`CompilationFailed`、
  `Reloading`、`PlayModeTransition`、`MainThreadStalled` 或 `Exiting`
- `canEval`
- `busyReason`
- `mainThreadTick`
- `isPlaying`、`isPaused`、`isUpdating`
- `compilationCycleId`、编译器错误/警告计数和上次编译时间戳
- `vmGeneration`

Broker 自行判定传输连接状态。存活 socket 不能证明 Unity 主线程仍在响应。如果主线程
tick 过期，即使 Unity 最后发布的 phase 是 `Compiling` 或 `Reloading`，Broker 也会派生
`MainThreadStalled`；`busyReason` 会保留最后一次报告的 phase。

## Broker 到 Unity 的请求

- `eval/execute`：`sessionId`、`requestId`、`code`、`timeoutSeconds`、`resetSession`
- `cli/execute`：`sessionId`、`requestId`、原始 `line`
- `session/release`：释放指定的 Unity 侧 eval session
- `broker/ping`：传输层存活检查

Unity 在 `canEval` 为 true 时执行 `eval/execute` 和 `cli/execute`。为兼容早于 repair-mode
`canEval` 标记的 Client，`CompilationFailed` 也是可执行的 repair mode；执行使用上一次
成功加载的程序集。Broker 绝不自动重试被中断的修改请求。

## 选择 handle

不存在进程全局的“已选 Unity”。`unity_connect` 会创建一个不透明、不可猜测的
`connectionHandle`，并将其绑定到一个已注册 `instanceId`。MCP 调用与 CLI 控制台各自
携带 handle。状态快照会返回 `registryRevision`；连接时必须提交该 revision，避免过期
发现结果在 registry 变化后静默指向错误目标。

在同一进程生命周期中，handle 可以跨 registry revision 变化和临时 Domain Reload 断线存活；
状态会显示新的 `connectionEpoch` 和 `vmGeneration`。revision 变化只影响新 handle 的创建。
现有 handle 在长时间无活动后过期，并在实例退出或被替换后失效。关闭 CLI 控制台、
替换其选择或租约过期时，都会释放对应的 Unity 侧 PuerTS session。如果 Unity 暂时断线，
Broker 会在同一进程生命周期内保留释放请求，并在重连后发送。

## 编译与重载

每一次被观察到的 Unity 编译都会获得 `compilationCycleId`，包括不是由 eval 发起的编译。
Unity 在 `CompilationPipeline.compilationStarted` 时发布 `Compiling`，在程序集编译完成时更新
编译器计数，出现错误时发布 `CompilationFailed`，并在程序集重载前发布 `Reloading`。
重连后，Unity 只在主线程稳定更新后才发布 `Ready`。

`unity_status` 可以等待 `ready` 或 `compilation-complete`。`ready` 表示可以执行，因此
正常 `Ready` 与 `CompilationFailed` repair mode 都会返回；`compilation-complete` 会在
编译成功或失败后返回。调用方必须检查 `phase`、`canEval` 和编译器计数。选择前按
`instanceId` 等待；选择后优先使用现有不透明 handle。等待在 Broker 中以事件驱动方式运行，
绝不在 Unity eval 内运行。使用 `compilationCycleId` 匹配 cycle；旧 `requestId` 状态参数
只是已弃用别名，绝不表示 `scheduleAssetRefresh` 返回的 Unity 侧 request ID。在可能触发编译
的 eval 之前，保留最新快照的 `capturedAtUtc`，并将其作为 `observedAfterUtc` 传入；
这会防止更早 cycle 或过期 `Ready` 样本提前完成等待。

## 稳定错误码

- `AuthenticationFailed`
- `UnityAuthorizationPending`
- `ProtocolMismatch`
- `InvalidRequest`
- `DiscoveryRequired`
- `RegistryChanged`
- `UnityNotFound`
- `ConnectionHandleRequired`
- `ConnectionHandleInvalid`
- `UnityDisconnected`
- `UnityBusy`
- `CompilationFailed`
- `RequestTimedOut`
- `ExecutionOutcomeUnknown`
- `BrokerUnavailable`
