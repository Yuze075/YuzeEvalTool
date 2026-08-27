# Yuze Eval Tool Roslyn Source Generator

[English](README.md) | **简体中文** | [使用指南](../README_zh.md)

Source Generator 会读取带 `YuzeToolkit.Eval.EvalToolAttribute` 的 partial C# Tool class，
以及带 `YuzeToolkit.Eval.EvalFunctionAttribute` 的方法。它生成 `EvalToolRegistry`
使用的 `IEvalTool` 元数据，包括函数描述、参数和安全声明。

生成的 Analyzer DLL 会提交到 Yuze Eval Tool UPM Package，因此通过 Git 安装 UPM Package
时不需要额外构建。

## 前置条件

安装 [`global.json`](../global.json) 选定的精确 .NET SDK，当前为 `10.0.300`。
已禁用 roll-forward。

## 构建与测试

在仓库根目录执行：

```bash
dotnet test Roslyn/UnityEvalToolRoslyn.sln -c Release
dotnet build Roslyn/src/UnityEvalTool.SourceGenerator/UnityEvalTool.SourceGenerator.csproj -c Release
```

直接的 Release 构建会把 Analyzer 写入：

```text
Roslyn/src/UnityEvalTool.SourceGenerator/bin/Release/netstandard2.0/UnityEvalTool.SourceGenerator.dll
```

## 把 Analyzer 部署到 UPM Package

Unity 会导入以下已提交 Analyzer：

```text
Packages/com.yuzetoolkit.yuzeevaltool/Analyzers/UnityEvalTool.SourceGenerator.dll
```

修改 Generator 源码后，以 Release 构建并用新 DLL 替换该文件。保留现有 `.meta`
文件及其 `RoslynAnalyzer` label。不要在 Unity Package 中添加源码归档；普通源码
继续位于 `Roslyn` 目录。

如需执行确定性构建与字节比较，选择一个空的临时输出目录：

```bash
dotnet test Roslyn/UnityEvalToolRoslyn.sln -c Release --artifacts-path <temporary-output-directory>
cmp <temporary-output-directory>/bin/UnityEvalTool.SourceGenerator/release/UnityEvalTool.SourceGenerator.dll Packages/com.yuzetoolkit.yuzeevaltool/Analyzers/UnityEvalTool.SourceGenerator.dll
```

如果当前平台没有 `cmp`，使用等价的二进制比较命令。如果存在差异，表示已提交的
UPM Analyzer 尚未使用当前源码和仓库锁定 SDK 重新生成。

## 源码布局

```text
Roslyn/
├── src/UnityEvalTool.SourceGenerator/       # Generator 实现
├── tests/UnityEvalTool.SourceGenerator.Tests/ # 集成与诊断测试
└── UnityEvalToolRoslyn.sln
```

## 许可证

[MIT](../LICENSE)
