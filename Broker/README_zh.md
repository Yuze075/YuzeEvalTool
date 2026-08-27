# Yuze Eval Tool Broker 构建与打包

[English](README.md) | **简体中文** | [使用指南](../README_zh.md)

`Broker` 包含 C# NativeAOT Broker 与 `unity` CLI、相关测试，以及组装 npm Package 的
脚本。本文档只说明可重现的本地构建与打包。发布和 Registry 自动化明确不在
本文档范围内。

## 前置条件

- [`global.json`](../global.json) 选定的精确 .NET SDK，当前为 `10.0.300`；已禁用
  roll-forward。
- Node.js 22，用于仓库构建与打包脚本。安装后的 npm 入口包运行时支持
  Node.js 18 或更高版本。
- 与目标操作系统和架构匹配的 NativeAOT 构建工具链。

每个原生包应在与目标匹配的主机上构建。设置目标名称不会让脚本变成受支持的跨操作系统
工具链。

受支持的目标如下：

| npm platform | npm architecture | .NET RID |
|---|---|---|
| `darwin` | `arm64` | `osx-arm64` |
| `darwin` | `x64` | `osx-x64` |
| `linux` | `arm64` | `linux-arm64` |
| `linux` | `x64` | `linux-x64` |
| `win32` | `arm64` | `win-arm64` |
| `win32` | `x64` | `win-x64` |

## 校验已提交版本

[`version.json`](../version.json) 是 UnityEvalTool/Broker/npm 版本和协议版本的唯一来源。
打包脚本会拒绝 Package manifest、Broker 属性、
Runtime 常量、协议常量或 npm optional dependency 之间的不一致。

在仓库根目录执行：

```bash
cd Broker
node --input-type=module -e "import { resolveAndValidateVersion } from './npm/scripts/version.mjs'; console.log(resolveAndValidateVersion(process.cwd()));"
```

该命令只校验已提交的 metadata，不会修改或注入版本。

## 构建与测试

在仓库根目录执行：

```bash
dotnet build Broker/UnityEvalTool.Broker.slnx -c Release
dotnet test Broker/tests/UnityEvalTool.Broker.Tests/UnityEvalTool.Broker.Tests.csproj -c Release --no-build
```

第一条命令构建 Broker Solution，第二条命令针对该 Release 构建运行 Broker 测试。

Source Generator 使用独立 Solution：

```bash
dotnet test Roslyn/UnityEvalToolRoslyn.sln -c Release --artifacts-path <temporary-output-directory>
```

Analyzer 部署和字节比较见 [Roslyn 指南](../Roslyn/README_zh.md)。

## 打包当前原生平台

在仓库根目录执行：

```bash
cd Broker
node npm/scripts/pack-platform.mjs
```

默认情况下，脚本使用宿主的 `process.platform` 和 `process.arch`。脚本会：

1. 校验所有已提交的版本边界；
2. 使用自包含 Release NativeAOT 参数执行 `dotnet publish`；
3. 将可执行文件、许可证和生成的平台 manifest 放入 staging；
4. 执行 `npm pack --ignore-scripts`。

输出路径使用已选目标：

```text
Broker/artifacts/publish/<rid>/
Broker/artifacts/npm/<platform>-<arch>/
Broker/artifacts/npm/yuzetoolkit-unityevaltool-<platform>-<arch>-<version>.tgz
```

如果与目标匹配的主机需要显式提供检测值，脚本会读取
`UNITY_EVAL_TOOL_PLATFORM`（`darwin`、`linux` 或 `win32`）和
`UNITY_EVAL_TOOL_ARCH`（`arm64` 或 `x64`）。这些变量只选择 Package metadata 和
RID，不会安装跨平台编译工具链。

## 打包 npm 入口

与平台无关的入口包会选择匹配的可选原生依赖，并提供服务管理 helper：

```bash
cd Broker
node npm/scripts/pack-root.mjs
```

输出为：

```text
Broker/artifacts/npm/root/
Broker/artifacts/npm/yuzetoolkit-unityevaltool-<version>.tgz
```

artifacts 目录可能保留较早的本地构建 tarball。应通过 Package 名与已提交版本识别
当前输出，不要把目录中每个 `.tgz` 都当作同一次构建的一部分。

## Package 集合

完整的多平台集合包含一个入口包和六个原生包：

- `@yuzetoolkit/unityevaltool`
- `@yuzetoolkit/unityevaltool-darwin-arm64`
- `@yuzetoolkit/unityevaltool-darwin-x64`
- `@yuzetoolkit/unityevaltool-linux-arm64`
- `@yuzetoolkit/unityevaltool-linux-x64`
- `@yuzetoolkit/unityevaltool-win32-arm64`
- `@yuzetoolkit/unityevaltool-win32-x64`

这些脚本只会生成本地 `.tgz`，不会连接 npm Registry、创建源码控制 tag 或创建
托管 Release。维护者需要自行选择和配置分发流程。

## 源码布局

```text
Broker/
├── src/UnityEvalTool.Broker/       # 原生 Broker、MCP Server、CLI 和服务命令
├── tests/UnityEvalTool.Broker.Tests/ # Broker 测试
├── npm/root/                       # 与平台无关的 npm Package 源码
├── npm/scripts/                    # 版本校验与打包脚本
└── artifacts/                      # 已忽略的本地构建和打包输出
```

安装后的原生可执行文件拥有 Broker、MCP、CLI 中转、Unity 注册和当前用户服务
行为。npm 入口包中的 JavaScript 只负责选择并启动匹配的原生包，以及暴露显式的
服务 helper。

## 许可证

[MIT](../LICENSE)
