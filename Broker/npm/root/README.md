# @yuzetoolkit/unityevaltool

**English** | [简体中文](README_zh.md) | [Full documentation](https://github.com/Yuze075/YuzeEvalTool#readme)

Native C# Broker, Streamable HTTP MCP server, and `unity` CLI for
[Yuze Eval Tool](https://github.com/Yuze075/YuzeEvalTool). The npm entry package selects a
matching native package for macOS, Linux, or Windows on x64 or arm64.

The Unity-side UPM package must also be installed in every Unity project you want to use.

## Install

```bash
npm install --global @yuzetoolkit/unityevaltool
unity service install
unity doctor
```

`unity service install` creates and starts a current-user background service bound to
`127.0.0.1:2347`: a LaunchAgent on macOS, a systemd user unit on Linux, or a Scheduled Task
on Windows. It does not install a privileged system service. Service setup is explicit
because npm dependency lifecycle scripts may be disabled; check the command's exit status.

Add the Unity-side package through Unity Package Manager:

```text
https://github.com/Yuze075/YuzeEvalTool.git?path=/Packages/com.yuzetoolkit.yuzeevaltool#v3.0.0
```

Open the Unity project, wait for compilation, then run `unity list`. Seeing that Editor is
the first end-to-end registration check; `unity doctor` alone checks Broker health.

## CLI quick start

```bash
unity list
unity                         # Select by the current project directory
unity connect <instance-id>   # Select an exact Unity instance
unity Runtime getState        # Execute one Unity-side command
unity eval-js --code "return 1 + 2;"
unity tools
```

`unity` and `unity connect` open an interactive console. Its Broker controls are `:status`,
`:wait`, `:switch`, `:help`, and `:quit`; other input is sent to Unity's command parser.

## MCP

Connect a Streamable HTTP MCP client to:

```text
http://127.0.0.1:2347/mcp
```

Unity projects do not require token verification by default, so MCP clients normally need
only the endpoint URL. When a Unity project has enabled verification in Project Settings,
provision its token once by sending:

```text
Authorization: Bearer <token[/another-token...]>
```

The Broker stores supplied values in `~/.unityevaltool/auth.json`; later calls may omit the
header. The CLI equivalent is `unity --token <token> ...`, and `auth.json` may also be edited
directly. Its default capacity is five tokens; set `maxStoredTokens` in
`~/.unityevaltool/config.json` to change it, up to 32. The Broker only forwards candidates;
each Unity verifies its own token and stays discoverable but non-executable while pending.
The MCP workflow is always
`unity_status` → `unity_connect` → `eval`; discovery and explicit selection are mandatory.
See the [user guide](https://github.com/Yuze075/YuzeEvalTool#readme) and
[protocol specification](https://github.com/Yuze075/YuzeEvalTool/blob/main/Packages/com.yuzetoolkit.yuzeevaltool/docs/BROKER_PROTOCOL.md).

## Service management

```bash
unity service status
unity service start
unity service stop
unity service restart
unity service uninstall
```

The Broker accepts loopback traffic only and fails explicitly when port `2347` is occupied.

## Uninstall

npm does not automatically run the service-uninstall helper. Remove the current-user
service while the `unity` executable still exists, verify that it succeeds, and only then
remove the global package:

```bash
unity service uninstall
npm uninstall --global @yuzetoolkit/unityevaltool
```

If the first command fails, resolve the reported service error before continuing.

## License

[MIT](https://github.com/Yuze075/YuzeEvalTool/blob/main/LICENSE)
