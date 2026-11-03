#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace YuzeToolkit.Eval.Broker;

internal sealed class WorkspaceOptions
{
    public List<WorkspaceDefinition> Projects { get; set; } = [];
    public int LeaseSeconds { get; set; } = 1800;
    public int MaxSessions { get; set; } = 64;
    public int MaxContextsPerSession { get; set; } = 16;
    public int EventRetention { get; set; } = 512;
    public int MaxTextCharacters { get; set; } = 32768;
    public int MaxFileBytes { get; set; } = 8388608;
    public int MaxTasksPerContext { get; set; } = 16;
    public int RequestTimeoutSeconds { get; set; } = 60;
    public int HeartbeatFailureThreshold { get; set; } = 1;
    public int MaxTaskOutputCharacters { get; set; } = 262144;
    public int MaxTaskSeconds { get; set; } = 3600;
    public int PtyColumns { get; set; } = 120;
    public int PtyRows { get; set; } = 30;
    public int DesktopObservationSeconds { get; set; } = 30;
    public int MaxDesktopSequenceMilliseconds { get; set; } = 5000;
    public int DesktopDriverTimeoutSeconds { get; set; } = 15;
    public int DesktopMaxImageDimension { get; set; } = 1600;
    public string? DesktopDriverPath { get; set; }
    public string DesktopPipeName { get; set; } = "YuzeEvalTool.Desktop";
    public List<string> DesktopDriverArguments { get; set; } = ["mcp"];

    public void Validate(BrokerConfiguration config)
    {
        BrokerConfiguration.RequireRange(LeaseSeconds, 30, 86400, "workspace.leaseSeconds");
        BrokerConfiguration.RequireRange(MaxSessions, 1, 256, "workspace.maxSessions");
        BrokerConfiguration.RequireRange(MaxContextsPerSession, 1, 128, "workspace.maxContextsPerSession");
        BrokerConfiguration.RequireRange(EventRetention, 8, 10000, "workspace.eventRetention");
        BrokerConfiguration.RequireRange(MaxTextCharacters, 1024, 1048576, "workspace.maxTextCharacters");
        BrokerConfiguration.RequireRange(MaxFileBytes, 1024, 67108864, "workspace.maxFileBytes");
        BrokerConfiguration.RequireRange(MaxTasksPerContext, 1, 128, "workspace.maxTasksPerContext");
        BrokerConfiguration.RequireRange(RequestTimeoutSeconds, 5, 600, "workspace.requestTimeoutSeconds");
        BrokerConfiguration.RequireRange(HeartbeatFailureThreshold, 1, 10, "workspace.heartbeatFailureThreshold");
        BrokerConfiguration.RequireRange(MaxTaskOutputCharacters, 1024, 4194304, "workspace.maxTaskOutputCharacters");
        BrokerConfiguration.RequireRange(MaxTaskSeconds, 1, 86400, "workspace.maxTaskSeconds");
        BrokerConfiguration.RequireRange(PtyColumns, 20, 500, "workspace.ptyColumns");
        BrokerConfiguration.RequireRange(PtyRows, 5, 200, "workspace.ptyRows");
        BrokerConfiguration.RequireRange(DesktopObservationSeconds, 1, 120, "workspace.desktopObservationSeconds");
        BrokerConfiguration.RequireRange(MaxDesktopSequenceMilliseconds, 1, 10000, "workspace.maxDesktopSequenceMilliseconds");
        BrokerConfiguration.RequireRange(DesktopDriverTimeoutSeconds, 1, 120, "workspace.desktopDriverTimeoutSeconds");
        BrokerConfiguration.RequireRange(DesktopMaxImageDimension, 256, 8192, "workspace.desktopMaxImageDimension");
        if (Projects == null || DesktopDriverArguments == null)
            throw new InvalidDataException("Workspace projects and desktopDriverArguments cannot be null.");
        if (string.IsNullOrWhiteSpace(DesktopPipeName) || DesktopPipeName.Length > 80 || DesktopPipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new InvalidDataException("workspace.desktopPipeName must be 1–80 ASCII letters, digits, dots, hyphens or underscores.");
        if (DesktopDriverPath != null && !Path.IsPathFullyQualified(DesktopDriverPath))
            throw new InvalidDataException("workspace.desktopDriverPath must be an absolute executable path.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in Projects)
        {
            if (project == null) throw new InvalidDataException("Workspace projects cannot contain null.");
            BrokerConfiguration.ValidateId(project.Id);
            if (!ids.Add(project.Id)) throw new InvalidDataException("Workspace ids must be unique.");
            if (!Path.IsPathFullyQualified(project.RootPath))
                throw new InvalidDataException("Workspace rootPath must be an absolute path on this host.");
            if (project.SkillDirectories == null || project.SkillDirectories.Any(Path.IsPathFullyQualified))
                throw new InvalidDataException("Skill directories must be project-relative paths.");
            foreach (var reference in project.CredentialReferences) config.ValidateCredential(reference);
        }
    }
}

internal sealed class WorkspaceDefinition
{
    public string Id { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string? FilesReadCredentialRef { get; set; }
    public string? FilesWriteCredentialRef { get; set; }
    public string? TerminalCredentialRef { get; set; }
    public string? DesktopCredentialRef { get; set; }
    public List<string> SkillDirectories { get; set; } = [".agents/skills"];
    [JsonIgnore] public IEnumerable<string?> CredentialReferences =>
        [FilesReadCredentialRef, FilesWriteCredentialRef, TerminalCredentialRef, DesktopCredentialRef];

    public bool Allows(string capability, string authorization, BrokerConfiguration config)
    {
        var reference = capability switch
        {
            "filesRead" => FilesReadCredentialRef,
            "filesWrite" => FilesWriteCredentialRef,
            "terminal" => TerminalCredentialRef,
            "desktop" => DesktopCredentialRef,
            _ => throw new InvalidOperationException("Unknown workspace capability.")
        };
        return reference != null && Matches(config.GetCredential(reference)!, authorization);
    }

    internal static bool Matches(string secret, string authorization) =>
        authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(authorization[7..]));
}
