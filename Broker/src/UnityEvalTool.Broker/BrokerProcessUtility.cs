using System.Diagnostics;

namespace YuzeToolkit.Eval.Broker;

internal static class BrokerProcessUtility
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments,
        bool throwOnFailure = true, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = string.Join(Environment.NewLine,
            new[] { await stdout, await stderr }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        if (throwOnFailure && process.ExitCode != 0)
            throw new InvalidOperationException(
                $"'{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}. {output}");
        return (process.ExitCode, output);
    }

    public static void StartDetachedBroker()
    {
        var executable = InstallMetadataStore.GetCurrentExecutable()
                         ?? throw new InvalidOperationException("Run the published `unity` executable, not `dotnet unity.dll`.");
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "broker",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = InstallMetadataStore.ConfigDirectory
        });
    }
}
