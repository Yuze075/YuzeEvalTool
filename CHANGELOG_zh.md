# 变更记录

[English](CHANGELOG.md) | **简体中文**

## 未发布

- 将 Utility Tool LitJson 依赖替换为独立的 Yuze JSON Tool；`EvalJson` 保持原有动态 CLR 对象树契约，并直接使用新的 DOM 与序列化器。

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
