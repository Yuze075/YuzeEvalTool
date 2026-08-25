# UnityEvalTool

[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-222?logo=unity)](https://unity.com/releases/editor/archive)
[![npm](https://img.shields.io/badge/npm-%40yuzetoolkit%2Funityevaltool-CB3837?logo=npm)](https://www.npmjs.com/package/@yuzetoolkit/unityevaltool)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

**English** | [简体中文](README_zh.md)

UnityEvalTool lets AI agents and terminal users inspect and operate local Unity Editor and
Player processes. A native, computer-level Broker provides the MCP endpoint and the
`unity` CLI, while a Unity Package Manager package registers each Unity process with that
Broker. Compilation, Domain Reload, process replacement, and temporary disconnection are
reported explicitly instead of being hidden behind an in-Editor network listener.

The repository also contains the optional UnityAgentTool package, which owns the shared
Editor/Runtime Agent workbench, DebugPanel, Command Line, logs, and system monitors.

## What you install

| Component | Purpose | Required |
|---|---|---|
| `com.yuzetoolkit.unityevaltool` | Unity-side Broker client, status reporting, PuerTS eval sessions, CLI commands, and helper modules | Yes |
| `@yuzetoolkit/unityevaltool` | Native Broker, MCP server, `unity` CLI, and current-user background service | Yes |
| `com.yuzetoolkit.unityagenttool` | Shared Agent workbench, runtime DebugPanel, performance/system HUDs, log console, command line, and tool catalog | No |

Supported Broker/CLI platforms are macOS, Linux, and Windows on x64 and arm64. The Unity
packages require Unity 2022.3 or newer. Installing the Broker requires Node.js 18 or newer
and npm. WebGL is not a supported Broker target.

## Install

### 1. Prepare the PuerTS backend

UnityEvalTool requires `com.tencent.puerts.core` 3.0.2 and exactly one compatible PuerTS
JavaScript backend. The tested combination is `com.tencent.puerts.quickjs` 3.0.2 with its
matching core package. A supported V8 backend and core from the same PuerTS release may be
used instead. Do not install QuickJS and V8 backends together.

For the tested setup, download `PuerTS_Core_3.0.2.tar.gz` and
`PuerTS_Quickjs_3.0.2.tar.gz` from the official
[PuerTS Unity 3.0.2 release](https://github.com/Tencent/puerts/releases/tag/Unity_v3.0.2).
Extract both archives, then use Package Manager's **Add package from disk** command to
select `package.json` first in the extracted `core` directory and then in `quickjs`.
The [official PuerTS installation guide](https://github.com/Tencent/puerts/blob/Unity_v3.0.2/doc/unity/en/install.md)
also covers alternative backends.

The UnityEvalTool package declares the core dependency, but it deliberately does not choose
a JavaScript backend for your project.

### 2. Add the UnityEvalTool package

In Unity, open **Window > Package Manager**, choose **Add package from git URL**, and enter:

```text
https://github.com/Yuze075/UnityEvalTool.git?path=/Packages/com.yuzetoolkit.unityevaltool#v2.0.7
```

The equivalent `Packages/manifest.json` dependency is:

```json
{
  "dependencies": {
    "com.yuzetoolkit.unityevaltool": "https://github.com/Yuze075/UnityEvalTool.git?path=/Packages/com.yuzetoolkit.unityevaltool#v2.0.7"
  }
}
```

If you are developing against a local clone, use Package Manager's **Add package from
disk** command and select
`Packages/com.yuzetoolkit.unityevaltool/package.json` inside that clone.

### 3. Install the Broker and CLI

Install the native package globally, then explicitly install its current-user service:

```bash
npm install --global @yuzetoolkit/unityevaltool
unity service install
unity doctor
```

The service runs only for the current user: a LaunchAgent on macOS, a systemd user unit on
Linux, or a Scheduled Task on Windows. It does not require a system-wide privileged daemon.
Service setup is an explicit command because npm dependency lifecycle scripts may be
disabled; verify that `unity service install` succeeds before continuing.

### 4. Verify the Unity connection

Open a project containing the UnityEvalTool package and wait for Unity to finish compiling.
Then run:

```bash
unity doctor
unity list
unity connect <instance-id> -- Runtime getState
```

`unity doctor` should report a reachable Broker. `unity list` should show the open Editor
with its project path and current phase; substitute that row's ID in the final command.
The final command proves that Broker-to-Unity
execution works. You can also open **YuzeToolkit > UnityEvalTool** in the Editor to inspect
registration, connection state, and eval availability. If no instance appears, check
`unity service status`, confirm that the package compiled successfully, and make sure
another process is not using loopback port `2347`.

## CLI quick start

Run `unity` from a Unity project directory to select the matching Editor automatically, or
connect to an ID returned by `unity list`:

```bash
unity
unity connect <instance-id>
unity Runtime getState
unity eval-js --code "return 1 + 2;"
unity tools
```

The first two commands open an interactive console. Within it, `:status`, `:wait`,
`:switch`, `:help`, and `:quit` control the Broker connection. Other input is forwarded to
Unity's command parser. Run `unity --help` or `unity <command> --help` for the installed
CLI's complete command syntax.

## MCP setup

The Broker exposes a Streamable HTTP MCP endpoint at:

```text
http://127.0.0.1:2347/mcp
```

Project token verification is disabled by default, so the MCP client normally needs only
the endpoint URL. To protect one project or shipped Player, open **Project Settings >
YuzeToolkit > UnityEvalTool**, generate or enter a token, apply it, and enable verification.
Unity stores only a salted verifier in
`Assets/Resources/UnityEvalToolAuthorizationSettings.asset`; the original token is not saved
in the project. Unity includes this asset in Player builds and loads it through the standard
Resources API.

Supply that token once from an MCP client:

```text
Authorization: Bearer <token[/another-token...]>
```

The Broker persists supplied tokens in `~/.unityevaltool/auth.json` and offers them to every
connected Unity that is still awaiting verification. Later MCP calls may omit the header.
The native CLI has the same provisioning behavior through `unity --token <token> ...`.
You may also edit `auth.json` directly; the default capacity is five distinct tokens and
`~/.unityevaltool/config.json` can override it with `maxStoredTokens` (hard maximum 32).
Token values allow ASCII letters, digits, `_`, and `-`; `/` separates multiple values.

The server exposes three MCP tools, used in this order:

1. `unity_status` discovers Unity instances and reports their current state.
2. `unity_connect` selects an exact `instanceId` from a known `registryRevision` and
   returns an opaque, workflow-local connection handle.
3. `eval` runs JavaScript in the selected Unity when its state permits evaluation.

A minimal `eval` program is:

```javascript
async function execute() {
  return 1 + 2;
}
```

Built-in `tools://` helpers cover common Unity workflows. In particular,
`tools://Editor/Profiler` can enumerate exact category/name pairs from the global Profiler registry
and run bounded, cross-frame main-thread CPU `ProfilerRecorder` sessions in PlayMode without
enabling global Profiler recording.
See the [helper reference](Packages/com.yuzetoolkit.unityevaltool/docs/HELPER_MODULES.md).

Reuse a valid handle across Domain Reload and registry changes in the same Unity process.
Reconnect only when the handle expires, becomes invalid, or the Unity process is replaced.
Never automatically retry a mutating `eval` whose connection was interrupted after
dispatch; its result can be outcome-unknown. See [Advanced usage](Packages/com.yuzetoolkit.unityevaltool/docs/ADVANCED_USAGE.md)
and the [Broker protocol](Packages/com.yuzetoolkit.unityevaltool/docs/BROKER_PROTOCOL.md).

## Optional Agent and runtime debug UI

Install UnityAgentTool after UnityEvalTool with **Add package from git URL**:

```text
https://github.com/Yuze075/UnityEvalTool.git?path=/Packages/com.yuzetoolkit.unityagenttool#v2.0.7
```

Then place `Runtime/Panel/Prefabs/DebugPanel.prefab` from that package in a scene or a
persistent prefab. The panel is not created automatically. Its modules, storage model,
default keys and APIs are documented in the
[UnityAgentTool package README](Packages/com.yuzetoolkit.unityagenttool/README.md).

The default keys are `F8` for Unity Agent and `F10` for the Performance and System Information HUDs.

## Security boundary

The Broker binds only to `127.0.0.1:2347` and rejects non-loopback Host/Origin values. It does
not authenticate callers globally: it only persists and forwards candidate tokens. Each
Unity project or Player independently decides whether verification is required and accepts
commands only after one candidate produces its stored salted verifier. Supported non-WebGL
release Players that include UnityEvalTool intentionally register with the Broker and retain
arbitrary-JavaScript evaluation; this behavior is not limited to Development Builds and does
not depend on UnityAgentTool. The verifier prevents ordinary Broker access without the token,
but cannot prevent an attacker who can patch the Player binary. See
[Editor and Player registration](Packages/com.yuzetoolkit.unityevaltool/docs/RUNTIME_SERVICES.md).

## Service management and uninstall

```bash
unity service status
unity service start
unity service stop
unity service restart
```

When uninstalling, remove the service while the `unity` executable still exists. Confirm
that the first command succeeds before removing the npm package:

```bash
unity service uninstall
npm uninstall --global @yuzetoolkit/unityevaltool
```

npm does not automatically run the service-uninstall helper.

## Build and package from source

Source builds require the exact .NET SDK selected by `global.json` (currently 10.0.300),
Node.js 22, and the NativeAOT toolchain for the host OS and architecture. From a clone of
this repository, package the current platform and the platform-independent npm entry with:

```bash
cd Broker
node npm/scripts/pack-platform.mjs
node npm/scripts/pack-root.mjs
```

The generated `.tgz` files are written to `Broker/artifacts/npm/`. Native packages for all
six OS/architecture combinations must be built on matching hosts. Detailed build, test,
version-validation, and artifact paths are in the [Broker build guide](Broker/README.md);
the source-generator build is documented in [Roslyn/README.md](Roslyn/README.md).

These commands only build local artifacts. How those artifacts are distributed or
published is intentionally left to each maintainer's own registry and automation setup.

## Documentation

- [UnityEvalTool package](Packages/com.yuzetoolkit.unityevaltool/README.md)
- [UnityAgentTool package](Packages/com.yuzetoolkit.unityagenttool/README.md)
- [Advanced usage](Packages/com.yuzetoolkit.unityevaltool/docs/ADVANCED_USAGE.md)
- [Helper module reference](Packages/com.yuzetoolkit.unityevaltool/docs/HELPER_MODULES.md)
- [Editor and Player registration](Packages/com.yuzetoolkit.unityevaltool/docs/RUNTIME_SERVICES.md)
- [Broker protocol](Packages/com.yuzetoolkit.unityevaltool/docs/BROKER_PROTOCOL.md)
- [Architecture](Packages/com.yuzetoolkit.unityevaltool/docs/PROJECT_DESIGN.md)
- [Broker build and packaging](Broker/README.md)
- [Roslyn source generator](Roslyn/README.md)
- [Changelog](CHANGELOG.md)

## License

[MIT](LICENSE)
