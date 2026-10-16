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
        [MenuItem("Window/YuzeToolkit/Install Eval Tool (npm)")]
        private static void InstallFromNpm()
        {
            const string command = "npm install --global @yuzetoolkit/unityevaltool && unity service install && unity doctor";
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
#if UNITY_EDITOR_WIN
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec")
                                 ?? throw new InvalidOperationException("The ComSpec environment variable is missing.");
            startInfo.Arguments = "/d /k \"" + command + "\"";
#elif UNITY_EDITOR_OSX
            startInfo.FileName = "osascript";
            startInfo.Arguments = "-e \"tell application \\\"Terminal\\\" to do script \\\"" + command +
                                  "\\\"\" -e \"tell application \\\"Terminal\\\" to activate\"";
#elif UNITY_EDITOR_LINUX
            startInfo.FileName = "x-terminal-emulator";
            startInfo.Arguments = "-e sh -lc \"" + command +
                                  "; printf '\\nPress Enter to close...'; read reply\"";
#else
            throw new PlatformNotSupportedException("The npm installer requires a Windows, macOS, or Linux Editor.");
#endif
            using var terminal = Process.Start(startInfo);
        }

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
