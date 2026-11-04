# 远程开发工作区

[English](REMOTE_WORKSPACES.md) | **简体中文** | [使用指南](../README_zh.md)

工作区是 Broker 主机上注册的文件夹，可以有、也可以没有正在运行的 Unity 编辑器。
从 5.0.0 起，Unity 工具和工作区工具都由同一个 Streamable HTTP 入口 `/mcp` 提供。授权归 MCP 连接：
本机 loopback 入口受信任，可选的网络入口逐请求验证主机密钥。

## 一个入口，两种视图

| 入口 | Unity 工具 | 工作区工具 |
|---|---|---|
| 本机 `127.0.0.1:2347/mcp` | 本机实例和已配置的远端 | 只能访问已配置的远端主机，不能操作本机 |
| 网络 `/mcp`（需开启） | 只有本机实例 | 只有本机注册的文件夹，不转发 |
| 本机 `/status` | 浏览器连接监视页，网络入口不提供 | |

视图由监听入口决定，请求头或参数都无法改变。在一种视图里创建的上下文，在另一种视图中会被拒绝。
对端只按它自己的本地导出被导入，A→B→C 和 A↔B 都不会递归转发；指向本 Broker 或与其他远端重复的配置会被拒绝。

每台远端只有一条 MCP 连接，Unity 联邦和工作区调用共用。连接是无状态的，不需要 `Mcp-Session-Id`；
Broker 重启只会让该主机上的上下文和 Unity 租约失效。

客户端只需注册一项：

```toml
[mcp_servers.unity_eval]
url = "http://127.0.0.1:2347/mcp"
```

## 配置主机与文件夹

在每台导出主机上，通过 stdin 保存密钥并开启网络入口，然后注册文件夹。本机 Unity 连接会自动登记项目根目录，断开后仍保留。

```text
unity remote credential office-key
unity remote access token 10.100.0.100 2348 office-key
unity workspace add reliclight-main /absolute/path/to/RelicLight
unity service restart
```

在每台调用主机上，配置一次对端：

```text
unity remote add mac_studio 10.100.0.100 2348 office-key
unity service restart
```

文件夹身份是规范化后的根路径；别名和中间符号链接复用同一 ID，父目录和子目录是两个独立项目。
`workspace remove` 只删除配置，不删除文件。跨不可信网络时，请把网络入口放在加密隧道或 HTTPS 代理之后，
并显式配置 Host/Origin。SSH 私钥不会被复制或转发。

## 工具

| 工具 | 用途 |
|---|---|
| `workspace_list` | 远端主机（状态、桌面支持）及其工作区，以及每台主机的错误 |
| `workspace_connect` | 按 `hostId` + `workspaceId` 打开上下文；返回 cwd、OS、shell、Unity 实例、AGENTS 规则和 Skill 目录 |
| `workspace_status`、`workspace_disconnect` | 上下文身份与任务；显式关闭会取消其任务 |
| `workspace_get_guidance` | 从根目录到指定目录的 `AGENTS.override.md`，否则为 `AGENTS.md` |
| `file_read` | 读取 1–20 个文件，带行号，返回 SHA-256 版本、`totalLines`、`lineEnding`、`nextLine` |
| `file_glob` | 按 glob 查找路径，最新修改的在前 |
| `file_grep` | .NET 正则搜索：匹配的文件、带上下文的行或计数 |
| `file_write` | 新建（`expectedVersion: "missing"`）或覆盖（当前 SHA-256）文本文件 |
| `file_edit` | 精确替换，可选 `replaceAll`；LF 编辑也能匹配 CRLF 文件 |
| `file_apply_diff` | 跨文件统一 diff（新增、修改、删除、重命名），要么全部应用，要么都不写 |
| `file_view_image` | 以 MCP 图片内容返回 PNG、JPEG 或 WebP |
| `terminal_exec`、`terminal_poll`、`terminal_input`、`terminal_cancel` | 进程执行，支持 `waitSeconds`、输出游标、stdin 和确认式取消 |
| `desktop_observe`、`desktop_act` | 通过主机桌面执行器观察窗口并执行有界输入 |

路径可以是项目相对路径，也可以是项目内的绝对路径。文件操作会解析每一级路径（包括中间符号链接），拒绝离开根目录的路径。
在 git 工作树内，`file_glob` 和 `file_grep` 遵循 git 的忽略规则（`git ls-files --cached --others --exclude-standard`）；
其他情况下所有普通文件都参与。含 `/` 的 glob 匹配相对搜索目录的路径，不含 `/` 的匹配任意深度的文件名。
`file_grep` 跳过二进制文件、超过 `maxFileBytes` 的文件和非 UTF-8 内容。

`file_apply_diff` 在项目根目录运行 `git apply`：写入前检查所有 hunk，拒绝离开根目录或穿过符号链接的路径。
项目位于更大的仓库内时，diff 路径仍相对项目根目录。git 工作树内的 glob/grep 和 diff 都需要 Broker 的 PATH 中有 `git`。

终端程序以 Broker 用户身份运行，**不局限于项目文件**。使用 `program` 加字面量 `args`，或 `command` 加明确的 `shell`。
`terminal_exec` 最多等待 `waitSeconds`（默认 30 秒）并返回完成结果；否则用 `terminal_poll` 轮询，它也可以等待新输出。
PTY 输出合并且包含转义序列（macOS/Linux 用 `script`，Windows 用 ConPTY）。脱离的子进程不受任务生命周期约束。

## 推荐流程

1. 先 `workspace_list`，再 `workspace_connect`；用 `file_read` 阅读返回的规则和与任务相关的 Skill。它们是标注来源的项目规则数据，不是系统指令。
2. 小改动：用 `file_read` 拿到版本后执行 `file_edit` 或 `file_write`。跨文件改动：`file_apply_diff`。
3. 大规模重构：在本地克隆仓库，在独立分支上修改并推送；再用 `terminal_exec` 在远端主机上把干净的工作树快进到该分支，然后构建、测试、验证 Unity。这样每次同步都有 git 记录，冲突会被报告，而不是静默覆盖他人的修改。
4. 构建和测试用 `terminal_exec` 在远端执行；Unity 检查对返回的 `remote:<host>:<id>` 实例使用 `unity_status`、`unity_connect`、`eval`。
5. `terminal_exec` 运行在 Broker 所在的用户会话中，macOS 上可以使用登录钥匙串里的 git 和 CLI 凭据，SSH 会话通常做不到。

## 上下文、故障与结果不确定

上下文是租约，不是 MCP 会话。闲置超过 `leaseSeconds` 后过期并关闭其任务；`maxContexts` 限制同时打开的上下文数。
网关上下文记录远端 Broker 实例；远端重启后返回 `ContextHandleInvalid`，需要重新连接。
远端离线时，调用不发送即被拒绝，`workspace_status` 返回带 `isStale: true` 的最后快照。

写操作发出后传输失败，返回 `ExecutionOutcomeUnknown`，并带 `mayHaveExecuted: true` 和 `retryAutomatically: false`。
JSON-RPC 错误或 HTTP 401/403/404 证明调用没有执行。转发的失败保留对端自己的错误码和消息。
重试前先检查文件、任务列表或窗口；Broker 和客户端都不会自动重放。
`terminal_cancel` 在确认进程退出且输出流结束之前返回 `cancellation-pending`。保留的输出被淘汰时，输出游标报告 `gap`。

## 桌面执行器

桌面权限按主机划分：目标为 `hostId` 加确切的 `processId`，或其项目只有一个 Unity 进程的 `contextHandle`，两者不能同时传。
没有配置驱动的主机，桌面调用返回 `ToolUnavailable`；`workspace_list` 会报告每台主机的 `desktopToolsAvailable`。

适配器要求 **Cua Driver 0.38.0**。安装官方发行版后执行：

```text
unity workspace desktop-driver /absolute/path/to/cua-driver
unity service restart
unity desktop worker
```

在授予驱动所需的系统权限后，于同一用户已登录的图形会话中运行执行器；Broker 服务本身从不发起桌面输入。
Windows 上执行器拒绝在 session 0 运行，管道只允许该用户 SID 访问。Linux 需要 X11/Wayland 会话。

`desktop_observe` 不带 `windowId` 时列出窗口；带上则返回截图、几何信息和 `observationId`。
`desktop_act` 使用新鲜的观察结果，校验进程启动时间，执行 1–10 个有界动作（`click`、`double_click`、`right_click`、
`type_text`、`press_key`、`hotkey`、`scroll`、`drag`；文本最多 1,024 字符）。`deliveryMode` 默认 `background`；
`foreground` 会激活目标窗口、可能移动鼠标并让窗口留在前台，调用方必须先取得用户对该主机和窗口的明确许可。
macOS 像素动作需要 foreground。后续动作被拒绝时，序列可能只完成一部分，会以 `sequenceCompleted: false` 报告。
只支持点击和有界手势，不支持按住按键、解锁或游戏操作。

## 升级到 5.0

- 删除客户端里的 `/remote/mcp` 配置，只保留 `/mcp`。`/remote/status` 改为 `/status`。
- `file_search` 改为 `file_glob`，`file_apply_patch` 改为 `file_edit`，`file_read` 改为按行读取多个文件，
  `workspace_read_skill` 由对 Skill 目录路径执行 `file_read` 取代。删除 `workspace_events`、SSE 通知和 `tools/list_changed`。
- `workspace.maxSessions`、`maxContextsPerSession`、`eventRetention`、`heartbeatFailureThreshold` 不再使用，改用 `maxContexts`。
- 所有主机一起升级：5.0 网关调用旧版对端的工作区工具时会报告 `RemoteIncompatible`。Unity 联邦保持兼容，Unity 注册协议仍为 2.0。

## 配置参考

`config.json` 中的 `workspace`，启动时校验。

| 属性 | 默认值 | 用途 |
|---|---:|---|
| `projects` | `[]` | 注册的根目录：ID、rootPath、显示名、登记来源、Skill 目录 |
| `leaseSeconds` | 1800 | 上下文闲置寿命 |
| `maxContexts` | 64 | 本 Broker 同时打开的上下文数 |
| `requestTimeoutSeconds` | 60 | 工作区操作期限，另加 `waitSeconds` |
| `maxTextCharacters` / `maxFileBytes` | 32768 / 8388608 | 文本页与单文件预算 |
| `maxTasksPerContext` | 16 | 运行中及保留的任务数 |
| `maxTaskOutputCharacters` / `maxTaskSeconds` | 262144 / 3600 | 任务输出保留量与运行时限 |
| `ptyColumns` / `ptyRows` | 120 / 30 | 初始 PTY 尺寸 |
| `desktopObservationSeconds` | 30 | 观察结果寿命 |
| `maxDesktopSequenceMilliseconds` | 5000 | 动作序列时长预算 |
| `desktopDriverTimeoutSeconds` | 15 | 驱动响应期限 |
| `desktopMaxImageDimension` | 1600 | 截图最长边 |
| `desktopDriverPath` / `desktopDriverArguments` | 未设置 / `["mcp"]` | Cua 可执行文件与 stdio 参数 |
| `desktopPipeName` | `YuzeEvalTool.Desktop` | 同用户执行器 IPC 名称 |

Skill 目录取自每个 `skillDirectories` 条目（默认 `.agents/skills`）的直接子目录。远端发现与重试使用
`discoveryIntervalSeconds`、`connectionTimeoutSeconds`、`transportGraceSeconds` 和 `reconnectMaxDelaySeconds`；
重试间隔从发现间隔开始倍增，直到上限。
