using System.Text.Json.Nodes;

namespace YuzeToolkit.UnityEvalTool.Broker;

internal static class InstallMetadataStore
{
    private const int FileOperationAttempts = 100;
    private const int FileOperationDelayMilliseconds = 20;

    public static string ConfigDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                throw new InvalidOperationException("The current user profile directory is unavailable.");
            return Path.Combine(home, ".unityevaltool");
        }
    }

    public static string? GetCurrentExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)) return null;
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        if (!string.Equals(fileName, "unity", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetFullPath(processPath);
    }

    public static void RegisterCurrentExecutable()
    {
        var executable = GetCurrentExecutable();
        if (executable == null) return;
        Directory.CreateDirectory(ConfigDirectory);
        var path = Path.Combine(ConfigDirectory, "install.json");
        var lockPath = path + ".lock";
        using var registrationLock = AcquireRegistrationLock(lockPath);
        SetOwnerOnlyMode(lockPath);
        var document = new JsonObject
        {
            ["executablePath"] = executable,
            ["version"] = BrokerConstants.PackageVersion,
            ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        WriteAtomically(path, document.ToJsonString() + Environment.NewLine);
    }

    private static FileStream AcquireRegistrationLock(string lockPath)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < FileOperationAttempts; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                lastError = ex;
                Thread.Sleep(FileOperationDelayMilliseconds);
            }
        }

        throw new IOException($"Timed out waiting to update Yuze Eval Tool install metadata '{lockPath}'.", lastError);
    }

    private static void WriteAtomically(string path, string contents)
    {
        var temporaryPath = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(true);
            }
            SetOwnerOnlyMode(temporaryPath);
            PublishTemporaryFile(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void PublishTemporaryFile(string temporaryPath, string path)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < FileOperationAttempts; attempt++)
        {
            try
            {
                File.Move(temporaryPath, path, true);
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
                Thread.Sleep(FileOperationDelayMilliseconds);
            }
        }

        throw new IOException($"Timed out publishing Yuze Eval Tool install metadata '{path}'.", lastError);
    }

    private static void SetOwnerOnlyMode(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
