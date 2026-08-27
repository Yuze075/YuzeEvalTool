# Yuze Eval Tool Broker build and packaging

**English** | [简体中文](README_zh.md) | [User guide](../README.md)

`Broker` contains the C# NativeAOT Broker and `unity` CLI, their tests, and the scripts
that assemble npm packages. This document describes reproducible local build and packaging
only. Publishing and registry automation are intentionally outside its scope.

## Prerequisites

- The exact .NET SDK selected by [`global.json`](../global.json), currently `10.0.300`.
  Roll-forward is disabled.
- Node.js 22 for repository build and packaging scripts. The installed npm entry package
  supports Node.js 18 or newer at runtime.
- The NativeAOT build toolchain for the target operating system and architecture.

Build each native package on a matching host. Setting a target name does not turn the
script into a supported cross-OS toolchain.

Supported targets are:

| npm platform | npm architecture | .NET RID |
|---|---|---|
| `darwin` | `arm64` | `osx-arm64` |
| `darwin` | `x64` | `osx-x64` |
| `linux` | `arm64` | `linux-arm64` |
| `linux` | `x64` | `linux-x64` |
| `win32` | `arm64` | `win-arm64` |
| `win32` | `x64` | `win-x64` |

## Validate committed versions

[`version.json`](../version.json) is the source of truth for the UnityEvalTool/Broker/npm
version and protocol version. The
packaging scripts reject mismatches across package manifests, Broker properties, runtime
constants, protocol constants, and npm optional dependencies.

From the repository root:

```bash
cd Broker
node --input-type=module -e "import { resolveAndValidateVersion } from './npm/scripts/version.mjs'; console.log(resolveAndValidateVersion(process.cwd()));"
```

This command validates committed metadata; it does not edit or inject a version.

## Build and test

From the repository root:

```bash
dotnet build Broker/UnityEvalTool.Broker.slnx -c Release
dotnet test Broker/tests/UnityEvalTool.Broker.Tests/UnityEvalTool.Broker.Tests.csproj -c Release --no-build
```

The first command builds the Broker solution. The second runs the Broker tests against
that Release build.

The source generator has its own solution:

```bash
dotnet test Roslyn/UnityEvalToolRoslyn.sln -c Release --artifacts-path <temporary-output-directory>
```

See the [Roslyn guide](../Roslyn/README.md) for analyzer deployment and byte comparison.

## Package the current native platform

From the repository root:

```bash
cd Broker
node npm/scripts/pack-platform.mjs
```

By default the script uses the host's `process.platform` and `process.arch`. It:

1. validates every committed version boundary;
2. runs `dotnet publish` as a self-contained Release NativeAOT executable;
3. stages the executable, license, and generated platform manifest; and
4. runs `npm pack --ignore-scripts`.

Output paths use the selected target:

```text
Broker/artifacts/publish/<rid>/
Broker/artifacts/npm/<platform>-<arch>/
Broker/artifacts/npm/yuzetoolkit-unityevaltool-<platform>-<arch>-<version>.tgz
```

For a matching host whose detected values need to be supplied explicitly, the script reads
`UNITY_EVAL_TOOL_PLATFORM` (`darwin`, `linux`, or `win32`) and
`UNITY_EVAL_TOOL_ARCH` (`arm64` or `x64`). These variables choose package metadata and the
RID; they do not install a cross-compilation toolchain.

## Package the npm entry

The platform-independent entry package selects the matching optional native dependency and
provides service-management helpers:

```bash
cd Broker
node npm/scripts/pack-root.mjs
```

Outputs are:

```text
Broker/artifacts/npm/root/
Broker/artifacts/npm/yuzetoolkit-unityevaltool-<version>.tgz
```

The artifacts directory may contain tarballs from earlier local builds. Identify the
current output by its package name and committed version instead of treating every `.tgz`
in the directory as part of one build.

## Package set

A complete multi-platform set contains one entry package and six native packages:

- `@yuzetoolkit/unityevaltool`
- `@yuzetoolkit/unityevaltool-darwin-arm64`
- `@yuzetoolkit/unityevaltool-darwin-x64`
- `@yuzetoolkit/unityevaltool-linux-arm64`
- `@yuzetoolkit/unityevaltool-linux-x64`
- `@yuzetoolkit/unityevaltool-win32-arm64`
- `@yuzetoolkit/unityevaltool-win32-x64`

The scripts produce local `.tgz` files only. They do not contact an npm registry, create a
source-control tag, or create a hosted release. Maintainers choose and configure their own
distribution process.

## Source layout

```text
Broker/
├── src/UnityEvalTool.Broker/       # Native Broker, MCP server, CLI, service commands
├── tests/UnityEvalTool.Broker.Tests/ # Broker tests
├── npm/root/                       # Platform-independent npm package source
├── npm/scripts/                    # Version validation and pack scripts
└── artifacts/                      # Ignored local build and package output
```

The installed native executable owns the Broker, MCP, CLI routing, Unity registration, and
current-user service behavior. JavaScript in the entry npm package only selects and starts
the matching native package and exposes explicit service helpers.

## License

[MIT](../LICENSE)
