# 变更记录

[English](CHANGELOG.md) | **简体中文**

## 未发布

## 4.2.0 — 2026-11-04

- 重写全部 Broker MCP 工具描述与远端工作区 instructions，使说明简洁准确。
- Unity 联邦与远端工作区共用同一远端 Broker 连接：传输、本地导出校验、失败分类、重试退避，以及“JSON-RPC 或 HTTP 拒绝证明调用未执行”的判定。Unity 发现重试改为按文档从发现间隔倍增。
- 两个入口在同一处校验远端身份；工作区也会拒绝解析到同一 Broker 的两个远端。
- 本机 `/remote/mcp` 网关始终显示全部 17 个工具，由没有桌面驱动的对端自行拒绝桌面调用；删除动态目录更新与 `tools/list_changed`。
- 转发的工作区失败保留对端自身的错误码与消息，错误文本不再重复错误码；两个入口的远端认证失败统一为 `RemoteAuthenticationFailed`。
- 对端返回格式错误的数据时报告 `RemoteIncompatible`，不再把无关的本地异常当作远端故障捕获。

## 4.1.0 — 2026-11-04

- 将工作台与安装最新 npm 包入口统一到 YuzeToolkit / Eval Tool；后台安装，缺少 Node 明确报错并保留安装日志。
- 注册响应报告实际 Broker 版本；Unity 固定要求 4.1.0，旧版本在 Console 与工作台提示更新，不阻断连接。

## 4.0.0 - 2026-11-04

- 固定构建 SDK 至 .NET 10.0.403，包含 10.0.14 NativeAOT 安全修复，保留 NuGet 漏洞审计。

- MCP 连接认证统一覆盖全部已注册项目与可用主机能力；两个网络入口共用主机密钥，移除项目 grants、独立工作区密钥及相关 CLI 命令。
- 删除 Unity 项目 token 存储、验证设置与资产、转发链路和等待授权状态；仍要求项目验证的旧 Unity Client 需升级 UPM。
- 桌面观察与操作支持直接指定主机和进程，无需注册文件夹。Cua 保持可选，仍检查真实系统权限、图形用户 worker 与逐次前台模式。
- 保留项目 ID、仅导出本机资源、远端来源前缀及 Unity 协议 2.0，同步授权语义迁移说明。

## 3.3.0 - 2026-11-03

- 统一普通项目与 Unity 项目的文件夹注册。本机 Unity 连接自动持久登记根目录、复用手动注册项目，断开和重启保留 ID；导入远端实例不在本机再次注册。
- 工作区发现与状态返回实时 Unity 能力、来源前缀名称和对应 Unity 实例 ID；普通项目仅远程使用，Unity 断开不影响文件、终端与工作区上下文。
- 新增首次自动注册的明确默认授权、规范路径去重和 Unity 快照中的项目 ID；统一中间路径别名解析，保留既有配置与 Unity 协议 2.0。

## 3.2.0 - 2026-11-03

- 新增可选 `/remote/mcp` 工作区入口，支持项目文件 / 图片、异步终端、AGENTS / Skills、会话租约、恢复事件和本机连接状态页；网络端只导出明确授权的本地项目，不递归转发。
- 新增适配 Cua Driver 0.38.0 的独立图形用户执行器，提供有界窗口观察 / 动作与逐次显式前台模式。Cua 保持可选：15 个基础工具，仅在发现已安装 / 配置且获授权的远端桌面能力时增加两个桌面工具，变化时通知客户端刷新。
- 保留原有三个 Unity MCP 工具和 Broker 协议 2.0，升级不会自动授予工作区权限；Broker、npm 和 Eval Unity Package 版本同步为 3.2.0。

## 3.1.0 - 2026-11-02

- 原生 Broker 支持配置多个远端 MCP、IP/端口与可选服务密钥，将远端本地实例聚合为带来源前缀的项目；网络导出仅开放本地实例，阻止递归导入。
- 增加远端配置 CLI、独立凭据与稳定 Broker 身份、状态诊断、远程租约/编译等待路由；保留结果内容与执行结果未知语义，不自动重放 eval。配置需重启生效，交互式 CLI 执行仍仅支持本地。

- 将 Utility Tool LitJson 依赖替换为独立的 Yuze JSON Tool；`EvalJson` 保持原有动态 CLR 对象树契约，并直接使用新的 DOM 与序列化器。

- 将 Broker、npm、Unity Package 与 Runtime 版本同步为 3.1.0；Yuze Agent Tool 更新至 0.4.1，并同步 Eval 依赖版本。Broker 协议仍为 2.0。
- 使用锁定 SDK 从当前 Generator 源码重新生成已提交的 Unity Analyzer，恢复发布流程的确定性二进制校验。

## 3.0.0 - 2026-08-27

- Unity API 从共享的 `YuzeToolkit` 迁移至 `YuzeToolkit.Eval`，原生 Broker 迁移至 `YuzeToolkit.Eval.Broker`，Roslyn Generator 迁移至 `YuzeToolkit.Eval.SourceGenerator`；既有程序集名与协议标识保持不变。
- Yuze Agent Tool 0.4.0 从混用的 `YuzeToolkit` / `YuzeToolkit.UnityAgent` 统一迁移至 `YuzeToolkit.Agent`，同步更新项目调用方，并保持程序集名与持久化数据路径稳定。
- Editor 只保留一个 **YuzeToolkit > Agent Tool** 入口，Eval 使用 **YuzeToolkit > Eval Tool**，两项 Project Settings 路径均省略重复的 Yuze 前缀。
- 重新生成提交到仓库的 Analyzer，并同步 Broker、npm Package 与 Unity Runtime 版本；npm 名称仍为 `@yuzetoolkit/unityevaltool`，Broker 协议仍为 2.0。

## 2.1.0 - 2026-08-27

- 移除 Unity Package 内自带的 JSON 实现，改为复用 `YuzeUtilityTool.LitJson`，并将 Eval 专用的基础对象树转换入口公开为 `EvalJson`。
- Agent 协议 JSON 格式错误统一表现为 `FormatException`，Yuze Agent Tool 升级至 0.3.2 并依赖 Yuze Eval Tool 2.1.0。
- 同步 Broker、npm Package 与 Unity Runtime 的版本元数据；Broker 协议版本仍为 2.0。

## 2.0.7 - 2026-08-24

- 增加仅 Editor 可用的 `tools://Editor/Profiler` helper，以精确 category/name 发现全局 metric，
  并通过 `ProfilerRecorder` 有界采样 PlayMode 主线程 CPU 数据。跨帧 session 支持 warmup、
  带 guard 的采样窗口、raw 统计与 invocation count、sample 分页，并在暂停/退出 PlayMode、
  Domain Reload 和退出 Editor 时确定性清理。

## 2.0.6 - 2026-08-18

- 为 Broker 的 `unity_status` 与 `unity_connect` 工具显式输出对象形式的宽容 `{}` output schema。MCP C# SDK
  对 `JsonElement` 返回值推导出的布尔 JSON Schema `true` 违反了 Broker 所协商的全部 MCP 协议版本对
  `outputSchema.properties` 值必须为对象的要求，导致 kimi-code 等严格客户端拒绝整个服务器；`{}` 保持
  原有 `{"result": ...}` 包装与 structuredContent 线格式不变。
- 将指令根原本混合语义的 Player Build 开关替换为独立的 None/EditorOnly/PlayerOnly/All 实时路径作用域与
  构建快照开关。Player 现在先读取 Player/All 实时根再读取包内快照；四条默认根均为 All 且关闭 Embed；
  快照源目录缺失不再让构建失败。
- 通过本机 schema V12 与项目 schema V6 执行明确的破坏性根配置变更，不为被删除字段提供兼容迁移。

## 2.0.5 - 2026-08-17

- 在所有桌面系统上以精确参数向量调用服务管理器；创建 Windows 计划任务后校验其 Action，启动操作后
  等待 Broker 就绪，并让不可用的服务状态以失败退出。
- 每个原生 CLI 进程只注册一次安装信息，通过有界跨进程锁与同目录原子替换发布 `install.json`；
  Windows 短时读取占用导致原子发布冲突时进行有界重试。
- 在原生 CLI 到 Unity 的命令协议中保留 Windows 反斜杠与空参数，并在区分大小写的文件系统上使用
  区分大小写的项目目录边界判断。
- 为内嵌 Agent 增加 ObserveOnly/ConfirmWrites/FullAccess 能力模式、Tool 风险与 Editor/Player 表面、
  请求前暴露过滤、执行时二次校验、有界文件根与受保护删除。
- 增加由 SHA-256 保护的原子 `file_apply_patch`、安全的 `unity_snapshot` / `unity_scene_query`
  查询 Tool、明确 `AgentTurnResult`、完整 Host 流事件转发与编译续跑失败识别。
- 增加 6 个定向 Yuze Agent Tool EditMode 测试，覆盖策略、注册生命期、路径边界、精确 Patch、
  Turn 失败结果与 Tool 执行事件；Agent Package 版本升至 0.3.0。
- 让每条 AGENTS.md 与 Skill 根独立选择是否使用 `.unityagenttool` 命名空间，将四条 Package 默认根全部加入
  Player 内容，并让 ProjectRoot 默认根直接从项目根解析。
- 将长 Tool 参数与结果拆成有界纯文本块，并放入限制高度的滚动区域，避免单个 UI Toolkit 文本元素超过
  Unity 2022.3 的顶点上限。
- 将 Package JSON 设为 Yuze Agent Tool 无 Provider 默认值的唯一内置来源，增加可选的项目 Resources 覆盖，
  并在本机配置缺失或损坏时重建，同时保留损坏文件。
- 让 Project Settings 持久化与 Yuze Agent Tool 工作台中显式覆盖无 Provider 项目默认值的动作共用同一入口。
- 将设置、密钥、历史与编译恢复数据统一放在 `Application.persistentDataPath/.unityagenttool`，并从上一版直接
  写入 persistentDataPath 的布局迁移数据。
- 使用稳定 `AgentPathBase`、可选 `.unityagenttool` 命名空间、Skill 固定 `.agents/skills` 后缀与 JSON 相对
  子路径表示指令根，并通过本机设置 schema V11 规范化同 ID 默认根。

## 2.0.4 - 2026-08-15

- 将 token 鉴权下沉到每条 Unity 连接。项目默认关闭，只在 Resources Asset 保存加盐 PBKDF2
  verifier；验证 Pending 时仍可发现，但拒绝所有操作。
- Broker 改为凭据存储与转发层，不再充当鉴权门禁。MCP Bearer 输入和 CLI `--token` 默认最多
  持久化 5 个原始候选 token，支持手工维护 `auth.json`，并向 Pending Unity 广播候选值。
- 通过 Unity 标准 Resources 管线把公开 verifier 配置加入 Player，并明确无法抵御 Player
  二进制补丁的安全边界。

## 2.0.3 - 2026-08-15

- 要求所有 C# 与 loader-backed JavaScript Eval Function 声明非空安全 metadata，在注册阶段
  拒绝非法读写组合，并通过新增的 `MutatesRuntimeState` 标记进程或 Tool 自有的瞬时状态写入。
- 将 2.0.2 发布后累积的 Unity Package、Broker/CLI、npm、Roslyn、Tool 与 Agent 变化作为
  同一套不可变 2.0.3 源码和产物发布，不再复用已公开的 2.0.2 版本。

- 将内建 Agent Prompt 明确拆分为 Editor 开发与独立 Player 诊断工作流，按任务对象引导全部文件、
  进程、Skill 和 Unity Tool，并提供可执行的 `tools://` 发现入口；设置 schema 迁移会更新已有默认提示词。
- loopback Broker 默认关闭 token 认证，使 MCP 与 CLI 只配置端点即可工作；通过
  `UNITYEVALTOOL_REQUIRE_TOKEN=true` 可显式恢复 MCP、Unity 与 CLI 共用的 token 边界，
  健康状态和 doctor 输出会公开当前模式。
- 增加同步 Game/Scene/Editor 窗口图像捕获、带跨 reload 持久有界记录的完整可选 Unity
  Test Framework Tool、序列化代码/member 用法搜索，以及有界跨帧 Runtime 观察 session。
- 将公共文档重构为一一对应的英文与简体中文指南；让仓库 README 成为完整的安装与
  首次使用入口；将可重现的源码打包与维护者自行定义的分发拆开；移除与宿主项目
  绑定的开发说明。

## 2.0.2 - 2026-08-13

- 在产物预检中保留 npm metadata 查询失败，并使构建检查通过显式本地路径传递 tarball。
- 锁定 .NET SDK 10.0.300，从 SourceGenerator 程序集中排除源码控制 revision，并重新生成
  已提交 Analyzer，使字节级验证不受仓库布局影响。
- 修正构建自动化中已提交版本的求值方式，再执行 Package 校验。
- 准备版本 `2.0.2`：Yuze Eval Tool、Broker 与 npm Package 使用 2.0.2；
  UnityDebugTool 1.0.1 依赖 Yuze Eval Tool 2.0.2。
- 让多 Package 产物校验具备 SHA 绑定、并发安全、冒烟测试、版本预检，以及遇到已有不可变
  产物时的可恢复性。
- 将已提交 Unity Analyzer 存为普通 Git blob，确保 UPM Git 安装和二进制校验获得真实 DLL。
- 取消对 install/uninstall lifecycle 的依赖：全局 npm 安装/卸载前后通过明确、可检查的
  `unity service install|uninstall` 设置或移除服务。
- 在仓库与两个 UPM Package 许可证中一致保留继承的版权声明和当前版权。
- Unity eval 输出改为带正确顶层 error bit 的原生 MCP text/image block，不再把
  CallToolResult 形状的 JSON 嵌套为结构化文本。
- 每条 Unity 连接串行执行命令：排队期取消或超时的请求不再发送；已发送但被中断的命令
  保持明确的结果不确定状态，在解决前阻止后续执行。
- 让冷启动 auth token 发布跨进程原子化，限制未认证首帧和连接数，并为每条 WebSocket 关闭
  路径设置边界，超时后中止无响应 peer。
- 注册前校验完整 JavaScript Tool 树、可调用子 Tool resolver、显式 safety flag 和非保留导出名；
  增加持久数据风险 metadata 与感知 owner 的根移除。
- 在生成阶段诊断不支持的嵌套、异步和 JavaScript 保留名 C# Eval 函数，并让 Roslyn 集成测试
  不依赖其输出目录。
- 重做 UnityDebugTool 注册回滚、输入焦点、有界日志、递归 Tool 目录、性能 buffer 和 IL2CPP
  保留。视觉布局 metadata 不再隐式创建 Eval Tool，调用方使用显式 Tool 树。
- 将受支持的非 WebGL Release Player 中经认证的任意 JavaScript eval 作为正式 Runtime 契约保留，
  不依赖可选 UnityDebugTool UI。
- 在 `Packages` 下增加 `com.yuzetoolkit.unitydebugtool`，让 Runtime Debug UI 与 Yuze Eval Tool
  共享一个源码仓库，同时保留各自 Package README。
- 编译失败时通过上一次成功的 Unity 程序集保持 MCP/CLI 的 `CompilationFailed` repair mode
  可执行，同时继续拒绝编译/导入/重载过渡。
- 说清事件驱动编译等待与同进程跨 registry 变化的 handle 复用，并增加 Broker 状态策略
  回归测试。

## 2.0.1 - 2026-08-12

- CLI 控制台关闭或 Broker 租约过期时释放 Unity 侧 PuerTS session，包括在 Unity 临时断线期间
  延后释放。
- 隔离 Unity Broker Client 的连接 generation，防止已停止的重连循环拆除新建连接。
- 检测 Broker 进程替换，并重置属于上一 Broker 的 Unity 侧 session。
- 使用普通 Editor update 驱动的节流状态心跳，替换会自我维持的 Editor player-loop wakeup。
- 使 Broker、Unity Package、npm 和 Runtime 版本与已提交 `version.json` 版本保持一致。
- 将 Roslyn Generator 作为普通仓库源码保存在 `Roslyn`，不再向 Unity Package 嵌入源码归档。

## 2.0.0 - 2026-08-12

- 用绑定 `127.0.0.1:2347` 的电脑级 C# NativeAOT Broker 替换 Unity 内托管的 MCP 与 CLI listener。
- 为多个 Unity Editor 和 Player 进程增加认证注册与状态报告。
- 增加明确的编译、程序集重载、导入、Play Mode 过渡、断线和主线程卡死状态。
- 将 MCP 表面缩减为 `unity_status`、`unity_connect` 和 `eval`，并要求执行前必须完成发现和选择。
- 增加跨 Unity Domain Reload 继续运行的事件驱动就绪与编译完成等待。
- 增加原生 `unity` CLI，包含按项目路径自动选择、实例选择、单次命令和复用 Unity 现有解析器的
  交互控制台。
- 增加 macOS LaunchAgent、Linux systemd user unit 和 Windows 计划任务的当前用户服务集成。
- 增加面向 macOS、Linux 和 Windows x64/arm64 的 npm 打包及六平台产物构建矩阵。
- 将 Unity Package Manager Package 移到 `Packages/com.yuzetoolkit.yuzeevaltool`，将 Broker 源码移到 `Broker`。

该版本对协议和分发方式都有破坏性变更。请移除旧 UnityCLI 安装，并把 MCP Client 配置为经认证的
2347 端口。
