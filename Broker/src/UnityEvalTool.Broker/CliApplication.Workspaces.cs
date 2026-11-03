#nullable enable
using System.Text.Json.Nodes;

namespace YuzeToolkit.Eval.Broker;

internal static partial class CliApplication
{
    private static int ConfigureWorkspaces(string[] args)
    {
        var path = BrokerConfiguration.DefaultPath;
        var index = Array.IndexOf(args, "--config");
        if (index >= 0)
        {
            if (index + 1 >= args.Length) throw new InvalidOperationException("--config requires a file path.");
            path = Path.GetFullPath(args[index + 1]); args = args.Take(index).Concat(args.Skip(index + 2)).ToArray();
        }
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
Remote workspaces (opt-in; changes apply after Broker/desktop worker restart):
  unity workspace list
  unity workspace add <workspace-id> <absolute-project-root> [display-name]
  unity workspace defaults files-read|files-write|terminal|desktop <credential-ref|off>
  unity workspace remove <workspace-id>
  unity workspace grant <workspace-id> files-read|files-write|terminal|desktop <credential-ref|off>
  unity workspace peer <existing-remote-id> <workspace-credential-ref|off>
  unity workspace desktop-driver <absolute-executable-path|off>
  unity desktop worker [--config <file>]

Create secrets with `unity remote credential <ref>` through stdin, never argv.
Local Unity connections automatically register their project folders and retain them after disconnect.
Manual registrations start with no grants. New automatic registrations copy autoRegistrationGrants, empty by default.
Default changes affect future registrations only; existing Unity access does not grant workspace access.
The local /remote/mcp view operates only on configured peers. /mcp retains its Unity-only catalog.
The network /remote/mcp view exports explicitly configured local workspaces only.
Use /remote/status on the local Broker to watch connections in a browser.
Run the desktop worker in a logged-in graphical user session; it never grants OS permissions.
Terminal cwd is a convenience, not a shell sandbox. Files remain scoped to the configured root.
Use --config <file> for isolated configuration. Edit config.json for timing, budgets and Skill directories.
""");
            return 0;
        }
        if (args[0] == "list")
        {
            var config = BrokerConfiguration.Load(path, false);
            Console.WriteLine("Configuration: " + path);
            foreach (var project in config.Projects)
                Console.WriteLine(project.Id + "  " + project.Name + "  " + project.RootPath + "  grants=" + string.Join(",", new[]
                { project.FilesReadCredentialRef == null ? null : "files-read", project.FilesWriteCredentialRef == null ? null : "files-write", project.TerminalCredentialRef == null ? null : "terminal", project.DesktopCredentialRef == null ? null : "desktop" }.Where(value => value != null)));
            foreach (var peer in config.Remotes) Console.WriteLine("Peer " + peer.Id + ": " + (peer.WorkspaceCredentialRef == null ? "workspace disabled" : "workspace credential configured"));
            return 0;
        }
        if (args[0] == "add")
        {
            if (args.Length is not (3 or 4)) throw new InvalidOperationException("Usage: unity workspace add <id> <absolute-root> [display-name]");
            var project = BrokerConfiguration.Load(path, false).RegisterProject(args[2], args[1], args.Length == 4 ? args[3] : null);
            Console.WriteLine("Registered " + project.Id + " (" + project.Name + ") at " + project.RootPath + ". Restart the Broker to load CLI changes.");
            return 0;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var configLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        BrokerConfiguration.RestrictFile(path + ".lock");
        var root = BrokerConfiguration.ReadDocument(path);
        var workspace = root["workspace"] as JsonObject;
        if (workspace == null) { workspace = new JsonObject(); root["workspace"] = workspace; }
        var projects = workspace["projects"] as JsonArray;
        if (projects == null) { projects = new JsonArray(); workspace["projects"] = projects; }
        switch (args[0])
        {
            case "remove":
                if (args.Length != 2) throw new InvalidOperationException("Usage: unity workspace remove <id>");
                projects.Remove(FindWorkspace(projects, args[1])); break;
            case "defaults":
                if (args.Length != 3) throw new InvalidOperationException("Usage: unity workspace defaults files-read|files-write|terminal|desktop <ref|off>");
                var defaults = workspace["autoRegistrationGrants"] as JsonObject;
                if (defaults == null) { defaults = new JsonObject(); workspace["autoRegistrationGrants"] = defaults; }
                defaults[GrantProperty(args[1])] = args[2] == "off" ? null : args[2]; break;
            case "grant":
                if (args.Length != 4) throw new InvalidOperationException("Usage: unity workspace grant <id> files-read|files-write|terminal|desktop <ref|off>");
                var property = GrantProperty(args[2]);
                FindWorkspace(projects, args[1])[property] = args[3] == "off" ? null : args[3]; break;
            case "peer":
                if (args.Length != 3) throw new InvalidOperationException("Usage: unity workspace peer <existing-remote-id> <ref|off>");
                var remote = root["remotes"]?.AsArray().OfType<JsonObject>().SingleOrDefault(peer => peer["id"]?.GetValue<string>() == args[1])
                    ?? throw new InvalidDataException("Configure the host once with `unity remote add` before granting workspace access.");
                remote["workspaceCredentialRef"] = args[2] == "off" ? null : args[2]; break;
            case "desktop-driver":
                if (args.Length != 2) throw new InvalidOperationException("Usage: unity workspace desktop-driver <absolute-executable-path|off>");
                if (args[1] != "off" && (!Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1]))) throw new InvalidDataException("The desktop driver executable does not exist at the absolute path.");
                workspace["desktopDriverPath"] = args[1] == "off" ? null : args[1]; break;
            default: throw new InvalidOperationException("Unknown workspace command. Run `unity workspace help`.");
        }
        BrokerConfiguration.WriteDocument(path, root, true);
        Console.WriteLine("Saved. Restart the Broker and, when applicable, its desktop worker to apply.");
        return 0;
    }

    private static string GrantProperty(string capability) => capability switch
    {
        "files-read" => "filesReadCredentialRef", "files-write" => "filesWriteCredentialRef",
        "terminal" => "terminalCredentialRef", "desktop" => "desktopCredentialRef",
        _ => throw new InvalidDataException("Unknown workspace capability.")
    };

    private static JsonObject FindWorkspace(JsonArray projects, string id) => projects.OfType<JsonObject>().SingleOrDefault(project => project["id"]?.GetValue<string>() == id)
        ?? throw new InvalidDataException("Workspace id was not found.");
}
