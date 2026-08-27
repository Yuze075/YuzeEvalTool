# Yuze Eval Tool Roslyn source generator

**English** | [简体中文](README_zh.md) | [User guide](../README.md)

The source generator reads partial C# tool classes marked with
`YuzeToolkit.EvalToolAttribute` and methods marked with
`YuzeToolkit.EvalFunctionAttribute`. It emits the `IEvalTool` metadata used by
`EvalToolRegistry`, including function descriptors, parameters, and safety declarations.

The generated analyzer DLL is committed in the Yuze Eval Tool UPM package so Git-based UPM
installs do not need a separate build step.

## Prerequisites

Install the exact .NET SDK selected by [`global.json`](../global.json), currently
`10.0.300`. Roll-forward is disabled.

## Build and test

From the repository root:

```bash
dotnet test Roslyn/UnityEvalToolRoslyn.sln -c Release
dotnet build Roslyn/src/UnityEvalTool.SourceGenerator/UnityEvalTool.SourceGenerator.csproj -c Release
```

The direct Release build writes the analyzer to:

```text
Roslyn/src/UnityEvalTool.SourceGenerator/bin/Release/netstandard2.0/UnityEvalTool.SourceGenerator.dll
```

## Deploy the analyzer to the UPM package

Unity imports the committed analyzer at:

```text
Packages/com.yuzetoolkit.yuzeevaltool/Analyzers/UnityEvalTool.SourceGenerator.dll
```

After changing generator source, build it in Release and replace that DLL with the newly
built file. Keep the existing `.meta` file and its `RoslynAnalyzer` label. Do not add a
source archive to the Unity package; the ordinary source remains under `Roslyn`.

For a deterministic build-and-compare check, choose an empty temporary output directory:

```bash
dotnet test Roslyn/UnityEvalToolRoslyn.sln -c Release --artifacts-path <temporary-output-directory>
cmp <temporary-output-directory>/bin/UnityEvalTool.SourceGenerator/release/UnityEvalTool.SourceGenerator.dll Packages/com.yuzetoolkit.yuzeevaltool/Analyzers/UnityEvalTool.SourceGenerator.dll
```

Use the platform's equivalent binary comparison command when `cmp` is unavailable. A
difference means the committed UPM analyzer has not been regenerated from the current
source with the repository's pinned SDK.

## Source layout

```text
Roslyn/
├── src/UnityEvalTool.SourceGenerator/       # Generator implementation
├── tests/UnityEvalTool.SourceGenerator.Tests/ # Integration and diagnostic tests
└── UnityEvalToolRoslyn.sln
```

## License

[MIT](../LICENSE)
