# Editor and Player registration

**English** | [简体中文](RUNTIME_SERVICES_zh.md) | [Package README](../README.md)

Yuze Eval Tool has no Unity-hosted MCP or CLI listener. The computer-level Broker must be
installed and running. Editor and supported non-WebGL Player processes register outbound
to it.

## Editor

The primary Editor process starts `UnityBrokerClient` automatically. Asset Import Workers
are rejected by `EditorProcessGuard`. If the Broker cannot be reached, Unity reads
`~/.unityevaltool/install.json` and attempts to launch the installed native executable.

Compilation and assembly reload status is captured by `EditorBrokerStatusMonitor`. The
client publishes `Reloading` before Domain Reload, disconnects, and reconnects with the
same process instance ID and a higher VM generation.

From 4.0.0, the Broker owns connection authorization. Unity has no project token, verifier asset or
authorization settings page; a registered instance accepts requests according to its live execution state.

## Player

`UnityBrokerRuntimeBootstrap` creates a hidden `DontDestroyOnLoad` runner in non-Editor
builds. It reports runtime heartbeat/play state, registers the executable folder as the
project path, and publishes `Exiting` on application quit. The installed user service hosts the Broker.
Supported release Players retain arbitrary-JavaScript eval, independent of Development Build and
the optional Yuze Agent Tool UI. The same MCP connection authorization applies to Editor and Player.
Remove the obsolete authorization asset when upgrading a project that used it.

WebGL is not a supported Broker target because the local ClientWebSocket/current-user
service model is unavailable there.

## Native executable and user service

The published `unity` executable registers its absolute path once when each process starts.
It publishes `~/.unityevaltool/install.json` under a bounded cross-process lock by writing a
complete same-directory temporary file and atomically replacing the previous document.
Readers therefore observe either the previous complete document or the new complete document,
never a truncate-in-place window.

`unity service install|start|restart` invokes launchd, systemd, or Windows Task Scheduler with
an exact argument vector and waits for the Broker health endpoint before reporting success.
On Windows, installation also reads the created task XML and verifies that its executable
Command is the current native binary and its Arguments value is exactly `broker`. The status
command fails when the platform service is unavailable; Windows status additionally validates
the stored task action and Broker health.

## Public runtime surface

- `UnityBrokerClient.Shared.IsConnected`
- `UnityBrokerClient.Shared.Identity`
- `UnityBrokerClient.Shared.GetSessionSnapshots("mcp:")`
- `UnityBrokerClient.Shared.GetSessionSnapshots("cli:")`
- `UnityBrokerClient.Shared.Stop()` / `Start()` for an explicit reconnect

Yuze Agent Tool consumes the shared Broker client for its Command Line and Eval settings
pages. It does not own service discovery, process launch, or a
separate listener.
