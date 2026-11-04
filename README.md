# Yuze Eval Tool

[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-222?logo=unity)](https://unity.com/releases/editor/archive)
[![npm](https://img.shields.io/badge/npm-%40yuzetoolkit%2Funityevaltool-CB3837?logo=npm)](https://www.npmjs.com/package/@yuzetoolkit/unityevaltool)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

**English** | [简体中文](README_zh.md)

Yuze Eval Tool lets AI agents and terminal users inspect and operate local Unity Editor and
Player processes. A native, computer-level Broker provides the MCP endpoint and the
`unity` CLI, while a Unity Package Manager package registers each Unity process with that
Broker. Compilation, Domain Reload, process replacement, and temporary disconnection are
reported explicitly instead of being hidden behind an in-Editor network listener.

The repository also contains the optional Yuze Agent Tool package, which owns the shared
Editor/Runtime Agent workbench, DebugPanel, Command Line, logs, and system monitors.
The public Unity APIs use `YuzeToolkit.Eval` and `YuzeToolkit.Agent`; their existing
`UnityEvalTool*` and `UnityAgentTool*` assembly names remain stable for asmdef references.

## What you install

| Component | Purpose | Required |
|---|---|---|
| `com.yuzetoolkit.yuzeevaltool` | Unity-side Broker client, status reporting, PuerTS eval sessions, CLI commands, and helper modules | Yes |
| `@yuzetoolkit/unityevaltool` | Native Broker, MCP server, `unity` CLI, and current-user background service | Yes |
| `com.yuzetoolkit.yuzeagenttool` | Shared Agent workbench, runtime DebugPanel, performance/system HUDs, log console, command line, and tool catalog | No |

Supported Broker/CLI platforms are macOS, Linux, and Windows on x64 and arm64. The Unity
packages require Unity 2022.3 or newer. Installing the Broker requires Node.js 18 or newer
and npm. WebGL is not a supported Broker target.

## Install

### 1. Prepare the PuerTS backend

Yuze Eval Tool requires `com.tencent.puerts.core` 3.0.2 and exactly one compatible PuerTS
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

The Yuze Eval Tool package declares the core dependency, but it deliberately does not choose
a JavaScript backend for your project.

### 2. Add the Yuze Eval Tool package

In Unity, open **Window > Package Manager**, choose **Add package from git URL**, and enter:

```text
https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeevaltool#v4.1.0
```

The equivalent `Packages/manifest.json` dependency is:

```json
{
  "dependencies": {
    "com.yuzetoolkit.yuzeevaltool": "https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeevaltool#v4.1.0"
  }
}
```

If you are developing against a local clone, use Package Manager's **Add package from
disk** command and select
`Packages/com.yuzetoolkit.yuzeevaltool/package.json` inside that clone.

### 3. Install the Broker and CLI

After installing the Unity Package, choose **YuzeToolkit > Eval Tool > Install Latest npm Package**.
The menu checks Node.js/npm, stops the old service, installs `@latest`, and installs/starts and
checks the Broker in the background. Missing Node.js is an error; the menu never installs Node.js
or nvm. macOS/Linux use the configured login shell so its Node environment is available.
Results appear in the Unity Console through Yuze Log Tool; the full log is
`~/.unityevaltool/npm-install.log`. A failed step stops the remaining commands.
Open **YuzeToolkit > Eval Tool > Workbench** to inspect the connection.

This Unity package requires Broker **4.1.0** or newer. Registration reports the running Broker
version; older, unrecognized or unreported versions produce a Console Warning and a workbench
update notice. This advisory does not disable the connection.

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

Open a project containing the Yuze Eval Tool package and wait for Unity to finish compiling.
Then run:

```bash
unity doctor
unity list
unity connect <instance-id> -- Runtime getState
```

`unity doctor` should report a reachable Broker. `unity list` should show the open Editor
with its project path and current phase; substitute that row's ID in the final command.
The final command proves that Broker-to-Unity
execution works. You can also open **YuzeToolkit > Eval Tool** in the Editor to inspect
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

From 4.0.0, authorization belongs to the MCP connection. The local loopback endpoint is trusted;
the network `/mcp` and `/remote/mcp` endpoints share `remoteAccess` and the same single Bearer
credential on every request. An authorized connection can use every registered project, file
read/write, terminals and available desktop controls. There are no Unity project tokens or
per-project capability grants. Cua remains optional and the operating system must allow its
Accessibility / Screen Recording permissions; MCP authorization cannot grant those system permissions.

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
See the [helper reference](Packages/com.yuzetoolkit.yuzeevaltool/docs/HELPER_MODULES.md).

Reuse a valid handle across Domain Reload and registry changes in the same Unity process.
Reconnect only when the handle expires, becomes invalid, or the Unity process is replaced.
Never automatically retry a mutating `eval` whose connection was interrupted after
dispatch; its result can be outcome-unknown. See [Advanced usage](Packages/com.yuzetoolkit.yuzeevaltool/docs/ADVANCED_USAGE.md)
and the [Broker protocol](Packages/com.yuzetoolkit.yuzeevaltool/docs/BROKER_PROTOCOL.md).

## Remote Unity projects

The local MCP endpoint and `unity list` aggregate registered Unity instances from configured
machines. Display names use `[Local] Project` and `[Remote:remote-id] Project`; original
`projectName` and `projectPath` values remain unchanged. Only registered Editors / Players
are discovered, not unopened projects or instances that the peer imported from elsewhere.

Both machines need a Broker supporting federation. Minimal anonymous setup on a trusted LAN:

```bash
# Remote machine B: use B's network-interface address and an available port
unity remote access none 192.168.1.20 2348
unity service restart

# Local machine A
unity remote add office-pc 192.168.1.20 2348
unity service restart
unity list
```

For service authentication, provision the same secret on both machines with
`unity remote credential office-key < secret.txt` (stdin only, never a command-line secret).
On B use `unity remote access token 192.168.1.20 2348 office-key`; on A use
`unity remote add office-pc 192.168.1.20 2348 office-key`. Restart both services after changes.
An existing ID must be edited in configuration or removed with `unity remote remove office-pc`
before adding it again.

Configuration lives in `~/.unityevaltool/config.json`, service credentials in `peer-auth.json`,
and stable Broker identity in `broker-id`. `unity remote list` validates and lists configuration;
`unity list` reports live peer states and error codes. Configuration is loaded at startup,
so removal and disabling also take effect after restart. `unity remote access off` disables export.

The network `/mcp` exports only directly registered local Unity instances,
including connect and execution. The local endpoint stays at `127.0.0.1:2347/mcp` and exposes
all configured sources through the existing three tools. Remote IDs have the form
`remote:office-pc:<original-instance-id>`; select the exact source instead of matching a remote
path to the local working directory. Remote interactive CLI execution reports `RemoteCliUnsupported`.

Network connection credentials are validated on every request and authorize all registered projects. Use an HTTPS
reverse proxy or encrypted tunnel across untrusted networks; proxy to the network export listener,
not the local aggregate endpoint, and configure explicit Host / Origin allowances. Configure
HTTPS outbound connections with `unity remote add office-pc <host> <port> office-key https`.
See the [Broker protocol](Packages/com.yuzetoolkit.yuzeevaltool/docs/BROKER_PROTOCOL.md)
for configuration, source metadata, disconnects and leases.

## Remote development workspaces

Starting in 3.3.0, any folder can be registered persistently. Local Unity connections automatically
register their roots; disconnects retain projects and general remote capabilities. Unity is a live
capability attached to a stable folder identity.

The opt-in `/remote/mcp` view adds project files and images, terminal tasks, project-scoped
AGENTS/Skills, session events and a separate desktop worker. The local view routes only to
configured peers; the authenticated network view exports only explicitly shared local roots.
The existing `/mcp` catalog remains Unity-only. Both endpoints use the same connection credential and all registered projects are available after authentication.

See [Remote workspace setup and contracts](Broker/REMOTE_WORKSPACES.md) for connection authorization, client
configuration, recovery, desktop prerequisites and the current validation limits. The local
`/remote/status` page shows connection transitions while open; it does not wake an idle AI.

## Optional Agent and runtime debug UI

Install Yuze Agent Tool after Yuze Eval Tool with **Add package from git URL**:

```text
https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeagenttool#v4.1.0
```

Then place `Runtime/Panel/Prefabs/DebugPanel.prefab` from that package in a scene or a
persistent prefab. The panel is not created automatically. Its modules, storage model,
default keys and APIs are documented in the
[Yuze Agent Tool package README](Packages/com.yuzetoolkit.yuzeagenttool/README.md).

The default keys are `F8` for Yuze Agent Tool and `F10` for the Performance and System Information HUDs.

## Security boundary

From 4.0.0, authorization belongs to the MCP connection. The local loopback endpoint is trusted;
the network `/mcp` and `/remote/mcp` endpoints share `remoteAccess` and the same single Bearer
credential on every request. An authorized connection can use every registered project, file
read/write, terminals and available desktop controls. There are no Unity project tokens or
per-project capability grants. Cua remains optional and the operating system must allow its
Accessibility / Screen Recording permissions; MCP authorization cannot grant those system permissions.

Network export is disabled by default. Host/Origin validation and local-only export boundaries remain enforced.
Supported non-WebGL release Players retain arbitrary-JavaScript eval after registering with the trusted local Broker.
See [Editor and Player registration](Packages/com.yuzetoolkit.yuzeevaltool/docs/RUNTIME_SERVICES.md).

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

Source builds require the exact .NET SDK selected by `global.json` (currently 10.0.403),
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

- [Yuze Eval Tool package](Packages/com.yuzetoolkit.yuzeevaltool/README.md)
- [Yuze Agent Tool package](Packages/com.yuzetoolkit.yuzeagenttool/README.md)
- [Advanced usage](Packages/com.yuzetoolkit.yuzeevaltool/docs/ADVANCED_USAGE.md)
- [Helper module reference](Packages/com.yuzetoolkit.yuzeevaltool/docs/HELPER_MODULES.md)
- [Editor and Player registration](Packages/com.yuzetoolkit.yuzeevaltool/docs/RUNTIME_SERVICES.md)
- [Broker protocol](Packages/com.yuzetoolkit.yuzeevaltool/docs/BROKER_PROTOCOL.md)
- [Architecture](Packages/com.yuzetoolkit.yuzeevaltool/docs/PROJECT_DESIGN.md)
- [Broker build and packaging](Broker/README.md)
- [Roslyn source generator](Roslyn/README.md)
- [Changelog](CHANGELOG.md)

## License

[MIT](LICENSE)
