#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace YuzeToolkit
{
    internal static class EditorBrokerProcessLauncher
    {
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
