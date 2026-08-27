using System.Security;

namespace YuzeToolkit.Eval.Broker;

internal static class UserServiceManager
{
    // Deployed service compatibility ID. This intentionally follows the retained npm/CLI identity.
    private const string ServiceId = "com.yuzetoolkit.unityevaltool";

    public static async Task<string> ExecuteAsync(string action, CancellationToken cancellationToken)
    {
        var executable = InstallMetadataStore.GetCurrentExecutable()
                         ?? throw new InvalidOperationException("Service management requires the published `unity` executable.");
        action = string.IsNullOrWhiteSpace(action) ? "status" : action.ToLowerInvariant();
        if (OperatingSystem.IsMacOS()) return await ExecuteMacAsync(action, executable, cancellationToken);
        if (OperatingSystem.IsLinux()) return await ExecuteLinuxAsync(action, executable, cancellationToken);
        if (OperatingSystem.IsWindows()) return await ExecuteWindowsAsync(action, executable, cancellationToken);
        throw new PlatformNotSupportedException("Yuze Eval Tool user services support macOS, Linux, and Windows.");
    }

    private static async Task<string> ExecuteMacAsync(string action, string executable,
        CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plist = Path.Combine(home, "Library", "LaunchAgents", ServiceId + ".plist");
        var uid = (await BrokerProcessUtility.RunAsync("id", ["-u"], cancellationToken: cancellationToken)).Output.Trim();
        var domain = "gui/" + uid;
        var target = domain + "/" + ServiceId;
        switch (action)
        {
            case "install":
                Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
                File.WriteAllText(plist, BuildLaunchAgent(executable));
                await BrokerProcessUtility.RunAsync("launchctl", ["bootout", domain, plist], false,
                    cancellationToken);
                await BrokerProcessUtility.RunAsync("launchctl", ["bootstrap", domain, plist], true,
                    cancellationToken);
                await BrokerProcessUtility.RunAsync("launchctl", ["kickstart", "-k", target], true,
                    cancellationToken);
                await EnsureBrokerReadyAsync(cancellationToken);
                return $"Installed and started LaunchAgent {ServiceId}.";
            case "uninstall":
                await BrokerProcessUtility.RunAsync("launchctl", ["bootout", domain, plist], false,
                    cancellationToken);
                if (File.Exists(plist)) File.Delete(plist);
                return $"Removed LaunchAgent {ServiceId}.";
            case "start":
                var loaded = await BrokerProcessUtility.RunAsync("launchctl", ["print", target], false,
                    cancellationToken);
                if (loaded.ExitCode != 0)
                    await BrokerProcessUtility.RunAsync("launchctl", ["bootstrap", domain, plist], true,
                        cancellationToken);
                await BrokerProcessUtility.RunAsync("launchctl", ["kickstart", target], true,
                    cancellationToken);
                await EnsureBrokerReadyAsync(cancellationToken);
                return $"Started LaunchAgent {ServiceId}.";
            case "restart":
                await BrokerProcessUtility.RunAsync("launchctl", ["kickstart", "-k", target], true,
                    cancellationToken);
                await EnsureBrokerReadyAsync(cancellationToken);
                return $"Restarted LaunchAgent {ServiceId}.";
            case "stop":
                await BrokerProcessUtility.RunAsync("launchctl", ["bootout", domain, plist], false,
                    cancellationToken);
                return $"Stopped LaunchAgent {ServiceId}.";
            case "status":
                var status = await BrokerProcessUtility.RunAsync("launchctl", ["print", target], true,
                    cancellationToken);
                return status.Output;
            default:
                throw new InvalidOperationException("service action must be install, uninstall, start, stop, restart, or status.");
        }
    }

    private static async Task<string> ExecuteLinuxAsync(string action, string executable,
        CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var unitDirectory = Path.Combine(home, ".config", "systemd", "user");
        var unitPath = Path.Combine(unitDirectory, "unityevaltool.service");
        switch (action)
        {
            case "install":
                Directory.CreateDirectory(unitDirectory);
                File.WriteAllText(unitPath, BuildSystemdUnit(executable));
                await BrokerProcessUtility.RunAsync("systemctl", ["--user", "daemon-reload"], cancellationToken: cancellationToken);
                await BrokerProcessUtility.RunAsync("systemctl", ["--user", "enable", "--now", "unityevaltool.service"],
                    cancellationToken: cancellationToken);
                await EnsureBrokerReadyAsync(cancellationToken);
                return "Installed and started systemd user unit unityevaltool.service.";
            case "uninstall":
                await BrokerProcessUtility.RunAsync("systemctl", ["--user", "disable", "--now", "unityevaltool.service"], false,
                    cancellationToken);
                if (File.Exists(unitPath)) File.Delete(unitPath);
                await BrokerProcessUtility.RunAsync("systemctl", ["--user", "daemon-reload"], false, cancellationToken);
                return "Removed systemd user unit unityevaltool.service.";
            case "start":
            case "restart":
                await BrokerProcessUtility.RunAsync("systemctl", ["--user", action, "unityevaltool.service"],
                    cancellationToken: cancellationToken);
                await EnsureBrokerReadyAsync(cancellationToken);
                return action == "start"
                    ? "Started unityevaltool.service."
                    : "Restarted unityevaltool.service.";
            case "stop":
                await BrokerProcessUtility.RunAsync("systemctl", ["--user", "stop", "unityevaltool.service"],
                    cancellationToken: cancellationToken);
                return "Stopped unityevaltool.service.";
            case "status":
                return (await BrokerProcessUtility.RunAsync("systemctl",
                    ["--user", "status", "unityevaltool.service", "--no-pager"], true, cancellationToken)).Output;
            default:
                throw new InvalidOperationException("service action must be install, uninstall, start, stop, restart, or status.");
        }
    }

    private static async Task<string> ExecuteWindowsAsync(string action, string executable,
        CancellationToken cancellationToken)
    {
        const string taskName = "Yuze Eval Tool Broker";
        var taskRun = $"\"{executable}\" broker";
        switch (action)
        {
            case "install":
                await BrokerProcessUtility.RunAsync("schtasks",
                    ["/Create", "/F", "/SC", "ONLOGON", "/TN", taskName, "/TR", taskRun],
                    cancellationToken: cancellationToken);
                await ValidateWindowsTaskActionAsync(taskName, executable, cancellationToken);
                await BrokerProcessUtility.RunAsync("schtasks", ["/Run", "/TN", taskName],
                    cancellationToken: cancellationToken);
                await EnsureBrokerReadyAsync(cancellationToken);
                return $"Installed and started current-user task '{taskName}'.";
            case "uninstall":
                await BrokerProcessUtility.RunAsync("schtasks", ["/Delete", "/F", "/TN", taskName], false,
                    cancellationToken);
                return $"Removed task '{taskName}'.";
            case "start":
            case "restart":
                if (action == "restart")
                    await BrokerProcessUtility.RunAsync("schtasks", ["/End", "/TN", taskName], false, cancellationToken);
                await BrokerProcessUtility.RunAsync("schtasks", ["/Run", "/TN", taskName],
                    cancellationToken: cancellationToken);
                await EnsureBrokerReadyAsync(cancellationToken);
                return $"Started task '{taskName}'.";
            case "stop":
                await BrokerProcessUtility.RunAsync("schtasks", ["/End", "/TN", taskName], false, cancellationToken);
                return $"Stopped task '{taskName}'.";
            case "status":
                await ValidateWindowsTaskActionAsync(taskName, executable, cancellationToken);
                var result = await BrokerProcessUtility.RunAsync("schtasks",
                    ["/Query", "/TN", taskName, "/V", "/FO", "LIST"], true, cancellationToken);
                await EnsureBrokerReadyAsync(cancellationToken);
                return result.Output;
            default:
                throw new InvalidOperationException("service action must be install, uninstall, start, stop, restart, or status.");
        }
    }

    private static string BuildLaunchAgent(string executable)
    {
        var escaped = SecurityElement.Escape(executable) ?? executable;
        var logRoot = SecurityElement.Escape(InstallMetadataStore.ConfigDirectory) ?? InstallMetadataStore.ConfigDirectory;
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>{ServiceId}</string>
<key>ProgramArguments</key><array><string>{escaped}</string><string>broker</string></array>
<key>RunAtLoad</key><true/><key>KeepAlive</key><true/>
<key>StandardOutPath</key><string>{logRoot}/broker.out.log</string>
<key>StandardErrorPath</key><string>{logRoot}/broker.err.log</string>
</dict></plist>
""";
    }

    internal static string BuildSystemdUnit(string executable) => $"""
[Unit]
Description=Yuze Eval Tool local Broker
After=default.target

[Service]
ExecStart={QuoteSystemdArgument(executable)} broker
Restart=on-failure
RestartSec=1

[Install]
WantedBy=default.target
""";

    private static string QuoteSystemdArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new InvalidOperationException("The systemd executable path is empty or contains control characters.");
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal) + "\"";
    }

    private static async Task ValidateWindowsTaskActionAsync(string taskName, string executable,
        CancellationToken cancellationToken)
    {
        var query = await BrokerProcessUtility.RunAsync("schtasks", ["/Query", "/TN", taskName, "/XML"],
            true, cancellationToken);
        var document = System.Xml.Linq.XDocument.Parse(query.Output);
        var action = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Exec")
                     ?? throw new InvalidDataException($"Scheduled task '{taskName}' has no executable action.");
        var command = action.Elements().FirstOrDefault(element => element.Name.LocalName == "Command")?.Value.Trim()
                      ?? string.Empty;
        var arguments = action.Elements().FirstOrDefault(element => element.Name.LocalName == "Arguments")?.Value.Trim()
                        ?? string.Empty;
        var normalizedCommand = command.Trim('"');
        if (!string.Equals(Path.GetFullPath(normalizedCommand), Path.GetFullPath(executable),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(arguments, "broker", StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Scheduled task '{taskName}' has an invalid action. Expected '{executable}' with argument 'broker', " +
                $"but found '{command}' with arguments '{arguments}'.");
    }

    private static async Task EnsureBrokerReadyAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
        Exception? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(
                    $"http://{BrokerConstants.Host}:{BrokerConstants.Port}/health", cancellationToken);
                if (response.IsSuccessStatusCode) return;
                lastError = new HttpRequestException(
                    $"Broker health endpoint returned HTTP {(int)response.StatusCode}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                                       !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException(
            $"The user service command completed, but the Yuze Eval Tool Broker did not become ready on " +
            $"{BrokerConstants.Host}:{BrokerConstants.Port}.", lastError);
    }
}
