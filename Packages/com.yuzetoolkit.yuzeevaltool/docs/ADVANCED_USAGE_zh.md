# 进阶使用

[English](ADVANCED_USAGE.md) | **简体中文** | [Package README](../README_zh.md)

## Eval 契约

Broker 的 `eval` 工具继续接收原有 Unity 侧程序：

```javascript
async function execute() {
  const runtime = await import('tools://Runtime');
  return runtime.getState();
}
```

用 `tools://` 发现根工具，用 `tools://<Tool/Path>` 导入具体工具；只有 helper 不覆盖
操作时才直接使用 PuerTS `CS.*`。返回值必须可以 JSON 序列化。

## Session 行为

每个连接 handle 在 Unity 中对应 `mcp:<handle>`，每个交互终端对应
`cli:<consoleId>`。后续调用复用同一 PuerTS VM，直到 handle/console 被释放、Unity
重载脚本域或请求 `resetSession`。状态和 CLI 控制台会显示 VM generation 变化。

CLI 控制台关闭时 Broker 会释放对应 VM；MCP 连接租约过期时也会释放对应 VM。
如果释放恰逢 Domain Reload，Broker 会为同一个 Unity 进程保留请求，并在重连后派发。
Broker 自身重启时，Unity 会识别新的 `brokerInstanceId`，释放旧 Broker 持有的全部 Session。

## 安全编译流程

1. 调用 `unity_status` 和 `unity_connect`；在即将触发编译的 eval 前再获取一份最新状态，保留其 `capturedAtUtc`。
2. 用 eval 修改代码并且只调用一次 `Editor.scheduleAssetRefresh()`，随后结束本次 eval。
3. Unity 客户端报告 `Compiling`、随后 `Reloading`；传输可能暂时断开。
4. 已连接时使用现有 handle、尚未连接时使用已知 `instanceId` 调用 `unity_status`，设置
   `waitFor: "compilation-complete"`，把请求前保留的 `capturedAtUtc` 作为
   `observedAfterUtc`，并设置足够 timeout。该标记会避免 Unity 尚未发布 `Compiling`
   时旧 `Ready` 快照提前结束等待；实际等待发生在 Broker 中。
5. 检查 `phase`、`canEval` 与编译器计数。同一进程重载或 registry 变化后继续复用
   handle；仅在 handle 失效、过期或 Unity 进程被替换时重新连接。若为
   `CompilationFailed`，在 repair mode 中读取错误、修改源码并重复该流程。

已经派发后连接中断的 eval 不得直接重试。Broker 会标明执行结果是否可能不确定。

## CLI 解析

原生 CLI 会把普通输入交给 `EvalCliCommandService`，因此全局帮助、工具帮助、别名、
引号参数、`eval-js`、日志流和工具命令保持原有行为。`unity tools` 会显示工具路径，
命令应使用其显示的大小写。

## 常见失败类型

- `RegistryChanged`：重新查询状态，再连接。
- `UnityBusy`：通过状态等待，不要循环调用 eval。
- `CompilationFailed`：使用上一次成功程序集的可执行 repair mode；读取编译消息、修复源码并再次刷新。
- `UnityDisconnected`：等待 Broker 中保留的实例重连。
- `ConnectionHandleInvalid`：租约过期或 Unity 进程被替换；重新发现和连接。
- `ExecutionOutcomeUnknown`：先检查 Unity 状态，再决定是否重复修改操作。
