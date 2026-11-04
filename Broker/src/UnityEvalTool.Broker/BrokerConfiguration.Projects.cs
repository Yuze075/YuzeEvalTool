#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace YuzeToolkit.Eval.Broker;

internal sealed partial class BrokerConfiguration
{
    private readonly object _projectGate = new();
    private WorkspaceDefinition[] _projects = [];
    [JsonIgnore] public IReadOnlyList<WorkspaceDefinition> Projects => Volatile.Read(ref _projects);

    // Folder identity is persistent; connected Unity instances are a separate live capability.
    // CLI and local Unity registration share this transaction and the existing config-file lock.
    public WorkspaceDefinition RegisterProject(string rootPath, string? id = null, string? displayName = null)
    {
        if (!Path.IsPathFullyQualified(rootPath) || id != null && !Directory.Exists(rootPath))
            throw new InvalidDataException("Project roots must be absolute; manual registration also requires an existing directory.");
        if (id != null) ValidateId(id);
        if (displayName != null && string.IsNullOrWhiteSpace(displayName))
            throw new InvalidDataException("Project display name cannot be blank.");
        var root = WorkspaceFiles.Canonical(rootPath);
        lock (_projectGate)
        {
            var current = FindProject(Projects, root, id);
            if (current != null) return current;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var configLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            RestrictFile(FilePath + ".lock");
            var latest = Load(FilePath, createIdentity: false);
            var project = FindProject(latest.Projects, root, id);
            if (project == null)
            {
                project = new WorkspaceDefinition
                {
                    Id = id ?? "project-" + Guid.NewGuid().ToString("N"), RootPath = root,
                    DisplayName = displayName, RegisteredBy = id == null ? "unity" : "manual"
                };
                var document = ReadDocument(FilePath);
                var workspace = document["workspace"] as JsonObject;
                if (workspace == null) { workspace = new JsonObject(); document["workspace"] = workspace; }
                var projects = workspace["projects"] as JsonArray;
                if (projects == null) { projects = new JsonArray(); workspace["projects"] = projects; }
                projects.Add(JsonSerializer.SerializeToNode(project, BrokerJsonContext.Default.WorkspaceDefinition));
                WriteDocument(FilePath, document, validateConfig: true);
            }
            Volatile.Write(ref _projects, Projects.Append(project).ToArray());
            return project;
        }
    }

    private static WorkspaceDefinition? FindProject(IReadOnlyList<WorkspaceDefinition> projects, string root, string? id)
    {
        if (id != null && projects.Any(project => project.Id == id && !WorkspaceFiles.SamePath(project.RootPath, root)))
            throw new InvalidDataException("Workspace id already belongs to another directory.");
        return projects.SingleOrDefault(project => WorkspaceFiles.SamePath(project.RootPath, root));
    }

    internal static JsonObject ReadDocument(string path) => File.Exists(path)
        ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
          ?? throw new InvalidDataException("Configuration must be a JSON object.")
        : new JsonObject();

    internal static void WriteDocument(string path, JsonObject document, bool validateConfig)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporaryPath, options))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                document.WriteTo(writer);
            if (validateConfig) BrokerConfiguration.Load(temporaryPath, createIdentity: false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
