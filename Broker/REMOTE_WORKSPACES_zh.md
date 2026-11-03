# 远程开发工作区

[English](REMOTE_WORKSPACES.md) | **简体中文** | [用户指南](../README_zh.md)

从 npm 3.2.0 起提供。工作区导出和能力授权默认关闭；升级既有安装不会自动开放文件、终端或桌面权限。

## 入口与范围

| 入口 | 工具与访问范围 |
|---|---|
| 本机 `/mcp` | 原有 `unity_status`、`unity_connect`、`eval`，访问本地与聚合的远程 Unity |
| 本机 `/remote/mcp` | 15 个工作区工具与 2 个可选桌面工具，只能选择已配置的远端主机 |
| 网络 `/mcp` | 原有本地 Unity 导出，沿用 Unity 授权 |
| 网络 `/remote/mcp` | 明确共享的本地项目根，按能力鉴权，不再转发其他主机 |
| 本机 `/remote/status` | 浏览器连接状态页，不从网络监听器导出 |

范围由实际监听器决定，客户端 Header 或布尔参数不能覆盖。经本机 `127.0.0.1` 路由到另一台电脑
仍属于远程操作。工作区使用 `hostId + workspaceId` 标识，独立于 Unity 是否启动；每个连接上下文
保留自己的远端 cwd，不存在全局“当前项目”切换。

工作区协议采用有会话的 Streamable HTTP MCP `2025-11-25`。初始化后保留 `Mcp-Session-Id`。
它明确拒绝 `2026-07-28` 无状态发现，允许客户端协商到有会话协议；原 Unity MCP 传输契约不变。

## 文件夹注册与 Unity 能力（3.3.0）

项目以本机的规范化文件夹为身份，不要求 Unity 工程结构。手动注册任意现有目录：

```text
unity workspace add website /absolute/path/to/website "Website"
```

同一目录经相对路径片段、符号链接或 Windows 大小写得到相同规范路径时，复用原 ID、名称与授权。
同一 ID 不能指向另一个目录，父目录与子目录分别注册。配置中已有重复规范路径会明确报错，需先保留一个
项目定义；不会擅自合并不同 ID 的授权。原有 3.2 配置无需迁移，未填写名称时使用目录名称。

本机 Unity 通过 `/unity` 注册时，Broker 自动持久注册其上报根目录；已经手动注册则直接关联原项目。
自动 ID 写入同一个 `workspace.projects` 配置，断开、退出、重启和重新连接不会重新生成。
导入的远端 Unity 不触发本机注册。注册失败明确返回 `ProjectRegistrationFailed`，不报告保存成功。
Unity 上报的绝对目录暂不存在时仍保留 Unity 连接与项目记录，并标记 `directoryExists=false`；文件工作区
连接明确报目录不可用。`workspace remove` 只移除配置，不删除文件；后续新的本机 Unity 连接可以再次自动注册。

手动注册默认不授权。自动注册默认同样不授权；可为以后首次自动注册的项目设置明确策略：

```text
unity workspace defaults files-read workspace-key
unity workspace defaults files-write workspace-key
unity workspace defaults terminal workspace-key
unity workspace defaults desktop off
unity service restart
```

该策略位于 `workspace.autoRegistrationGrants`，字段与项目的四项 `*CredentialRef` 相同。
策略仅在创建新项目时复制，不改变已有项目授权；CLI 修改仍需重启 Broker。
运行中的 Unity 自动注册立即可见，写入时与配置 CLI 共用文件锁，保留其他配置字段；锁冲突或写入失败明确拒绝。

`workspace_list`、`workspace_connect`、`workspace_status` 返回同一份项目语义：

| 字段 | 含义 |
|---|---|
| `workspaceId` / `name` / `displayName` | 稳定 ID、项目名称、带 `[Local]` 或 `[Remote:主机]` 的显示名 |
| `registeredBy` | 首次登记来源 `manual` 或 `unity`，不随当前连接变化 |
| `kind` | 当前有连接的本机 Unity 实例时为 `unity`，否则为 `workspace` |
| `unityState` | `disconnected`、`ready`、`busy` 或 `authorizationRequired` |
| `capabilities.unityConnected` / `unityCanEval` | 实时连接与执行条件，沿用 Unity MCP 的授权、编译修复模式判断 |
| `unityInstances` | 仅包含当前连接实例，附准确 ID、PID、阶段与授权状态；多个实例必须明确选择 |
| `directoryExists` | 目录当前是否存在；目录消失不会自动删除登记 |

Unity 断开只移除实时能力，不关闭工作区句柄或终端任务；重新查询状态可见变化。
聚合视图里的 Unity `instanceId` 已转换为同一主机的 `remote:主机:实例`，原值在 `remoteInstanceId`。
原 Unity MCP 快照增加 `workspaceId` 以关联项目；执行仍通过原 `/mcp` 的 `unity_status` → `unity_connect` → `eval`，
使用该入口新查询的 `registryRevision`，不把工作区句柄当成 Unity 租约。
普通项目只由其他主机通过远程工作区使用；本机聚合入口不接受本机普通项目，原本机 Unity MCP 保持可用。

## 配置

复用现有 `remotes` 主机、IP、端口和 Unity 密钥配置，为工作区配置独立密钥引用。
在双方用 `unity remote credential workspace-key` 从 stdin 录入相同密钥，不把密钥放入命令行。
`peer-auth.json` 属于用户本机配置，不纳入项目版本控制。

在已有网络监听器的被访问电脑上执行：

```text
unity workspace add reliclight-main /absolute/path/to/RelicLight
unity workspace grant reliclight-main files-read workspace-key
unity workspace grant reliclight-main files-write workspace-key
unity workspace grant reliclight-main terminal workspace-key
unity service restart
```

路径必须是该电脑上的真实绝对路径，Windows 需包含盘符。新增工作区默认没有任何授权；按需启用
`files-read`、`files-write`、`terminal`、`desktop`。用 `off` 撤销单项授权。配置在 Broker 重启后生效；
桌面驱动相关配置还需要重启独立 worker。

在调用方执行：

```text
unity workspace peer mac_studio workspace-key
unity service restart
unity workspace list
```

`mac_studio` 必须是已有远端 ID，例如指向 `10.100.0.100:2348`。`workspace peer` 在同一条主机配置中
记录 `workspaceCredentialRef`，不修改 Unity 的 `credentialRef`。一把工作区密钥可以获得多个能力授权。
即使 Unity 网络入口使用 `authMode: none`，工作区网络入口仍要求匹配明确的能力密钥。

部署、首次接入和故障恢复沿用现有 SSH Host、SSH Agent 和主机校验。Broker 不复制或保存 SSH 私钥，
不会在每次工具调用时另开 SSH：命令直接由目标机器的 Broker 以其用户身份执行。
验收时使用 `--config <file>` 隔离配置，避免覆盖正式服务。

AI 客户端增加独立 MCP 入口 `http://127.0.0.1:2347/remote/mcp`，保留已有 `/mcp` 配置。
Codex 的 TOML 配置例如：

```toml
[mcp_servers.unity_workspace]
url = "http://127.0.0.1:2347/remote/mcp"
```

跨不可信网络时使用加密隧道或 HTTPS 代理，代理到网络导出监听器，并保留明确的 Host / Origin 允许列表。
不将本机聚合监听器暴露到网络。

## 项目上下文与工具

先通过 `workspace_list` 选择准确的 `hostId`、`workspaceId`，再调用 `workspace_connect`。
可指定项目相对 `cwd`，默认项目根。返回内容包括远端规范化根目录、cwd、OS、能力、精确匹配的
本地 Unity 实例、适用项目规则、项目 Skill 摘要和 `contextHandle`。

句柄只属于创建它的 MCP 会话。其他会话、已关闭上下文、过期租约和被替换的远端 Broker / 会话不能复用。
连接另一个工作区不会修改已有上下文的目录。

| 工具 | 行为 |
|---|---|
| `workspace_status` / `workspace_disconnect` | 查询身份、能力和任务；关闭指定上下文并取消其任务 |
| `workspace_get_guidance` | 读取项目根到目标目录的原文规则，包含来源、作用域、版本和分页信息 |
| `workspace_read_skill` | 读取项目 Skill 正文或相对于其远端目录的项目内引用资料 |
| `file_read` / `file_search` | UTF-8 文本、SHA-256 与分页；文件相对路径子串搜索，不是文件内容搜索 |
| `file_apply_patch` | 基于预期 SHA-256 的精确替换；新文件用 `missing`；冲突不写入 |
| `file_view_image` | 直接转发 PNG / JPEG / WebP 的 MCP 图像内容及来源信息 |
| `terminal_exec` / `terminal_poll` / `terminal_input` / `terminal_cancel` | 异步任务、标准输入、输出游标、退出码和实际取消状态 |
| `workspace_events` | 按事件游标补读，可等待 0–30 秒，明确报告历史缺口 |
| `desktop_observe` / `desktop_act` | 经图形用户会话中的独立执行器观察准确窗口、执行有界后台动作 |

文件路径相对于项目根，也可使用项目内绝对路径。终端命令默认使用上下文 cwd；单次请求可覆盖到项目内
其他目录。文件服务会解析中间符号链接，拒绝越过项目根的路径。预期哈希可阻止普通的陈旧补丁，但这不是
针对任意外部写入者的原子事务，也不是操作系统沙箱。**终端授权允许以 Broker 用户运行程序，不限制程序
只能访问项目文件。** 主动脱离进程树的守护进程不在终端任务的生命周期保证内。

`terminal_exec` 支持 `program + args` 的字面参数模式，或 `command + shell` 的显式 Shell 模式。
普通任务分别返回 stdout / stderr。PTY 合并输出为终端流，可能包含 ANSI 转义序列；macOS / Linux 使用
系统 `script`，Windows 使用 ConPTY。缺少系统能力会明确报错。PTY 尺寸在创建时读取配置，目前没有实时
resize 工具。已完成任务保留到上下文关闭，仍计入 `maxTasksPerContext`。

## 项目规则与 Skills

规则只从项目根逐层解析到目标目录，同目录优先 `AGENTS.override.md`，否则 `AGENTS.md`。
不混入兄弟目录规则，也不读取远端用户全局规则。返回的是带来源的项目数据，不伪装成系统指令。
项目明确引用的其他规则仍应通过远程上下文读取，例如先确认 Git 身份，再读取对应个人规则。
服务无法静态推断任意 Shell 命令涉及的全部目录，也不保证模型必然遵守规则。

默认 Skill 目录是项目 `.agents/skills` 下各直接子目录。可在项目配置 `skillDirectories` 中增加其他
项目相对目录，目录或引用的符号链接不得跨出项目。不会扫描 `~/.agents/skills`、`~/.codex/skills`。
连接返回 Skill 的 ID、name、description、路径、根目录与内容哈希；需要时用 `workspace_read_skill`
读取 `SKILL.md`、references 或 assets，读取不会执行 scripts。目录支持常用文本 scalar、引号和折叠 / 字面
描述块；不能解析的条目在 `errors` 中显式列出。连接和读取时重新发现目录；规则变化后重新读取对应内容。

文本分页使用 UTF-16 字符偏移，返回 `complete`、`nextOffset`、`totalCharacters`。
规则原文未完整返回时，用 `file_read` 继续读取其来源路径。完整规则、Skill 和文件读取需要 `files-read`；
初次连接返回的规则与摘要属于已授权工作区的上下文元数据。MCP 返回不会自动安装客户端原生 Skills，也不会
卸载调用方原有 Skills。文件图片按源尺寸返回，受文件大小预算约束，目前不提供裁剪和缩放；桌面截图另有尺寸配置。

## 断线、恢复与通知

每个 MCP 会话拥有独立的远端传输、上下文、任务和有界事件日志。事件有递增 ID、时间，以及适用的主机、
项目、任务、原因和恢复建议。探测按配置的失败阈值、期限和指数退避执行，状态变化通知一次，持续离线不刷屏。
不同会话独立维护；同一会话的操作串行，长操作可能推迟该会话心跳，操作请求期限给出等待上界。

已知离线或重连中的上下文不会继续发送新操作。`workspace_status` 可返回最后快照，但会带上 `isStale`、
连接状态和 `lastSeenAtUtc`。写入发送后未得到结果时返回 `ExecutionOutcomeUnknown`、
`mayHaveExecuted: true`、`retryAutomatically: false`；恢复后读取真实文件、窗口或任务状态，不自动重放。
列表查询不会擅自销毁持有上下文的远端会话。显式重新连接可能替换已失效的远端会话，其旧句柄一直无效。

取消任务后需要实际退出并完成输出读取才算确认；`cancellation-pending` 不表示成功。
任务输出和事件保留超限时返回 `gap`，必须用新状态快照恢复，不能补造事件或把任务不存在当作执行成功。
会话过期或 DELETE 会关闭其上下文；活动请求和 GET 事件流续租，其他会话按空闲期限过期。
Broker 重启后不保留任务 / 句柄日志。

服务提供 GET SSE `notifications/message`、`Last-Event-ID` 补读、`workspace://events` Resource 和
`workspace_events` 工具。客户端必须实际监听或轮询；**发出通知不代表闲置模型被唤醒，也不保证客户端 UI 展示。**
本机 `/remote/status` 页面打开时轮询连接变化，显示离线提示与历史；用户允许浏览器通知后还可收到桌面通知。
页面也检测本机 Broker 中断，并在重启后重新初始化。页面关闭、浏览器挂起时不保证通知，页面本身不能唤醒 AI。

## 桌面执行器

Cua 是可选组件。未配置驱动、配置的可执行文件不存在，或当前凭据没有任何工作区桌面授权时，网络入口
不显示 `desktop_observe`、`desktop_act`。本机聚合入口先显示 15 个工具，发现或连接到具备桌面能力的在线
远端后才增加这两个工具；聚合入口自己不需要安装 Cua。卸载驱动或最后一个具备桌面能力的远端离线后，
工具重新隐藏。目录变化发送 `notifications/tools/list_changed`，客户端应据此刷新工具列表。
文件、终端和 Unity 功能不依赖 Cua。驱动安装不等于系统授权与 worker 就绪，后两项在桌面调用时检查。

当前适配要求 **Cua Driver 0.38.0**。使用官方平台发行包并校验其 SHA-256；其他版本需要先验证契约。
Broker 不捆绑安装驱动、不导入其全局 Skills、不绕过系统授权，也不调用嵌套 AI 模型。

```text
unity workspace desktop-driver /absolute/path/to/cua-driver
unity workspace grant reliclight-main desktop workspace-key
unity service restart
unity desktop worker
```

最后一条必须在同一用户已登录的图形会话运行，且事先完成驱动系统授权。自定义配置时每条命令传
`--config <file>`。`unity service` 管理 Broker，不管理独立 worker；需要自动启动时由使用者配置图形用户
登录启动项。远程工具调用不会隐式启动桌面执行器。

- macOS：由 Cua 稳定应用身份持有屏幕录制 / 辅助功能权限。`permissions grant` 可能显示系统界面，必须
  先取得本次前台操作许可，不能把 SSH 登录解释为已获得桌面权限。
- Windows：worker 拒绝 session 0，必须运行在已登录交互用户会话。管道 ACL / 客户端校验采用真实用户 SID，
  允许同一账户的提升权限 Broker 与普通权限 GUI worker 通信，不向其他账户放开权限。
- Linux：必须存在 X11 / Wayland 图形会话。当前所配置 Linux 主机没有图形 display，桌面能力不可用。

未指定 `processId` 时，只在该工作区存在唯一、精确路径匹配的本地 Unity 注册实例时自动关联。
否则需要明确进程 ID；未指定 `windowId` 时列出该进程窗口，选定返回的窗口 ID 后取得图像、树、尺寸 / 缩放和
`observationId`。显式选择其他进程不会使其被伪称为 Unity 项目窗口。

动作必须引用同一上下文的新鲜观察，验证进程启动身份并投递到已观察的 PID / 窗口。
`desktop_act.deliveryMode` 默认 `background`；调用方必须先取得用户对当前主机 / 窗口的明确前台许可，
才能为当次调用指定 `foreground`。前台模式先验证目标窗口已置前，再执行整个序列，可能移动实际鼠标，
完成后保留目标窗口在前台，避免逐动作恢复旧焦点破坏点击后输入。许可不会保存为会话默认值。
服务端只能验证明确的调用参数，不能替调用客户端验证人类是否同意。子动作不能覆盖当次模式或目标。
已实测 Cua Driver 0.38.0 的 macOS 像素后台路由可能临时激活目标，因此 macOS 像素动作在默认模式下
会在发送前返回 `ForegroundRequired`；不会静默升级。截图仍可在后台读取。
支持 1–10 个有界 `click`、`double_click`、`right_click`、`type_text`、`press_key`、`hotkey`、`scroll`、
`drag` 动作，单个文字输入最多 1,024 字符。目标覆盖、全桌面输入、子动作模式覆盖、zoom 坐标、无界持续时间、
跨调用保持按键 / 鼠标按下均被拒绝。整段序列开始前检查目标和预算，驱动仍会逐项验证具体动作参数；后续
动作被拒绝可能产生部分执行，通过 `sequenceCompleted: false` 和各项结果明确返回。

一段序列最多在开头包含一个像素动作。click 使用驱动原子消费的 capture ID；其他像素动作执行前重新检查
窗口位置、尺寸与截图比例。该检查不承诺 Unity 逐帧确定性。任意动作都会使 worker 中旧观察失效。
输入由 worker 串行执行，返回各动作结果和动作后的新观察；驱动请求超时后停止接受后续动作，避免未知输入重叠。
驱动的有界动作自行配对按下 / 释放，不保留跨调用按键租约；后台不可用时明确返回，不自动改用前台。
目前支持点按与有界手势，不支持持续按住移动键、远程解锁、连续音视频或确定性游戏输入。

## 配置参考

`config.json` 的 `workspace` 字段默认值如下，启动时检查有效范围：

| 字段 | 默认值 | 用途 |
|---|---:|---|
| `projects` | `[]` | 显式项目 ID、rootPath、各能力密钥引用及 skillDirectories |
| `leaseSeconds` | 1800 | 空闲 MCP 会话存活时间 |
| `maxSessions` / `maxContextsPerSession` | 64 / 16 | 会话与上下文数量上限 |
| `requestTimeoutSeconds` | 60 | 远程工作区操作请求期限 |
| `heartbeatFailureThreshold` | 1 | 连续探测失败达到几次后标记离线 |
| `eventRetention` | 512 | 每会话保留的事件数量 |
| `maxTextCharacters` / `maxFileBytes` | 32768 / 8388608 | 文本页 / 输入预算、单文件 / 请求字节预算 |
| `maxTasksPerContext` | 16 | 包括已完成记录在内的任务上限 |
| `maxTaskOutputCharacters` / `maxTaskSeconds` | 262144 / 3600 | 输出保留量与任务最长运行秒数 |
| `ptyColumns` / `ptyRows` | 120 / 30 | PTY 初始尺寸 |
| `desktopObservationSeconds` | 30 | 桌面观察有效秒数 |
| `maxDesktopSequenceMilliseconds` | 5000 | 一段动作的投递 / 持续时间预算 |
| `desktopDriverTimeoutSeconds` | 15 | 单次驱动调用的响应期限 |
| `desktopMaxImageDimension` | 1600 | 桌面截图最长边 |
| `desktopDriverPath` | 未设置 | 明确安装的驱动绝对路径 |
| `desktopDriverArguments` | `["mcp"]` | 驱动 stdio 参数 |
| `desktopPipeName` | `YuzeEvalTool.Desktop` | 同用户 IPC 名称，隔离验收使用不同名称 |

主机探测复用 `discoveryIntervalSeconds`、`connectionTimeoutSeconds`、`transportGraceSeconds`、
`reconnectMaxDelaySeconds`。单次心跳期限为连接超时加传输宽限；退避从发现间隔倍增到重连上限。
桌面请求使用独立期限，一个桌面组件故障不代表整台电脑的 Unity 服务不可用。

## 已验证与尚未验收

开发中已使用两个实际 HTTP Broker、真实 Windows 导出进程、macOS / Windows PTY 和 Codex 0.163.0
app-server 工具发现进行验证。原 `/mcp` 实际只显示 3 个工具，新入口最多显示 17 个；可选 Cua 联调确认缺少驱动时为 15 个，安装 / 卸载后动态刷新。故障注入覆盖响应丢失后
不重放、已知离线拒绝、陈旧快照、恢复后原任务查询、Broker 替换、事件缺口、任务期限、输出预算及 SSE 通知。
状态页在线 / 离线变化和窄屏布局已在无界面 Chromium 中检查。

Mac Studio 经本次明确许可完成系统授权，最终 NativeAOT 远程 MCP 链路已验证真实 Unity 窗口截图、
显式前台点击后中文输入、Project 显示滑块拖拽、窗口尺寸变化后的截图更新及旧观察拒绝。
默认 macOS 像素输入在发送前拒绝，子动作不能覆盖模式；前台动作逐项成功不代替回传图像的实际效果检查。
Windows 图形用户 worker / 服务会话 IPC 已另行联调，其实际窗口输入与新增前台路径尚未验收；无图形会话的
Linux 不支持桌面。Unity 游戏输入和持续按键未验证，也未证明 Codex 展示 MCP 日志通知或唤醒闲置模型。
部署前应核对本次交付记录中的平台范围；前台 Unity Editor 操作通过不代表纯后台或游戏操作通过。
