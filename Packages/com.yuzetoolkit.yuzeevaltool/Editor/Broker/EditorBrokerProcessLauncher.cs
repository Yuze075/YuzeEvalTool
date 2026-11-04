#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;

namespace YuzeToolkit.Eval
{
    internal static class EditorBrokerProcessLauncher
    {
        private static Process? _installer;
        private static string _installerLog = string.Empty;

        [MenuItem(UnityEvalToolVersion.InstallBrokerMenuPath)]
        private static void InstallFromNpm()
        {
            if (_installer != null) return;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var directory = Path.Combine(home, ".unityevaltool");
            Directory.CreateDirectory(directory);
            _installerLog = Path.Combine(directory, "npm-install.log");
            File.WriteAllText(_installerLog, "Starting the npm installer.\n");
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = home
            };
#if UNITY_EDITOR_WIN
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec")
                                 ?? throw new InvalidOperationException("The ComSpec environment variable is missing.");
            var command = "where node >nul 2>&1 || (echo Node.js was not found. Install Node.js and reopen Unity. & exit /b 1)" +
                          " & node --version && call npm.cmd --version && " +
                          "(where unity >nul 2>&1 & if errorlevel 1 (ver >nul) else (call unity.cmd service stop)) && " +
                          "call npm.cmd install --global @yuzetoolkit/unityevaltool@latest && " +
                          "call unity.cmd service install && call unity.cmd doctor";
            startInfo.Arguments = "/d /s /c \"(" + command + ") > \"" + _installerLog + "\" 2>&1\"";
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            startInfo.FileName = Environment.GetEnvironmentVariable("SHELL")
                                 ?? throw new InvalidOperationException("The SHELL environment variable is missing.");
            var command = "exec > " + ShellQuote(_installerLog) + " 2>&1; " +
                          "command -v node >/dev/null 2>&1 || { printf '%s\\n' 'Node.js was not found. Install Node.js and reopen Unity.'; exit 1; }; " +
                          "set -e; node --version; npm --version; " +
                          "if command -v unity >/dev/null 2>&1; then unity service stop; fi; " +
                          "npm install --global @yuzetoolkit/unityevaltool@latest; unity service install; unity doctor";
            startInfo.Arguments = "-ilc \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
#else
            throw new PlatformNotSupportedException("The npm installer requires a Windows, macOS, or Linux Editor.");
#endif
            // Keep the polling owner alive while npm replaces the native Broker. The child writes
            // directly to its log, so an Editor quit cannot break npm through a closed output pipe.
            EditorApplication.LockReloadAssemblies();
            try
            {
                UnityBrokerClient.Shared.Stop();
                _installer = Process.Start(startInfo) ?? throw new InvalidOperationException("The npm installer process did not start.");
                EditorApplication.update += PollInstaller;
                EditorApplication.quitting += ReleaseInstaller;
                LogSys.Log($"[Yuze Eval Tool] Installing the latest npm package in the background. Log: {_installerLog}");
            }
            catch
            {
                EditorApplication.UnlockReloadAssemblies();
                EditorBrokerBootstrap.Reconnect();
                throw;
            }
        }

        [MenuItem(UnityEvalToolVersion.InstallBrokerMenuPath, true)]
        private static bool CanInstallFromNpm() => _installer == null;

        private static void PollInstaller()
        {
            if (_installer == null || !_installer.HasExited) return;
            var exitCode = _installer.ExitCode;
            ReleaseInstaller();
            EditorBrokerBootstrap.Reconnect();
            var output = File.ReadAllText(_installerLog);
            if (exitCode == 0)
                LogSys.Log($"[Yuze Eval Tool] Installed the latest npm package and started its Broker.\n{output}");
            else
                LogSys.LogError($"[Yuze Eval Tool] npm installation failed (exit {exitCode}). Log: {_installerLog}\n{output}");
        }

        private static void ReleaseInstaller()
        {
            EditorApplication.update -= PollInstaller;
            EditorApplication.quitting -= ReleaseInstaller;
            if (_installer == null) return;
            _installer.Dispose();
            _installer = null;
            EditorApplication.UnlockReloadAssemblies();
        }

        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

        public static bool EnsureRunning()
        {
            var executable = ResolveExecutable();
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return false;
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "broker",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            Process.Start(startInfo);
            return true;
        }

        private static string ResolveExecutable()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("UNITY_EVAL_TOOL_EXECUTABLE");
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return Normalize(fromEnvironment!);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var installPath = Path.Combine(home, ".unityevaltool", "install.json");
            if (!File.Exists(installPath)) return string.Empty;
            var root = EvalData.AsObject(EvalJson.Parse(File.ReadAllText(installPath)))
                       ?? new Dictionary<string, object?>();
            return Normalize(EvalData.GetString(root, "executablePath") ?? string.Empty);
        }

        private static string Normalize(string path)
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
            return Path.GetFullPath(path);
        }
    }
}
