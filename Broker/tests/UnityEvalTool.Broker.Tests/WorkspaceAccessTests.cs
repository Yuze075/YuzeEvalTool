#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace YuzeToolkit.Eval.Broker.Tests;

public sealed class WorkspaceAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yuze-workspace-access-" + Guid.NewGuid().ToString("N"));

    private BrokerConfiguration Configuration()
    {
        Directory.CreateDirectory(_root);
        return BrokerConfiguration.Load(Path.Combine(_root, "config.json"));
    }

    [Fact]
    public async Task AuthenticatedExportListsEveryRegisteredFolderWithoutGrants()
    {
        var config = Configuration();
        var first = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        var manual = config.RegisterProject(first, "manual");
        var automatic = config.RegisterProject(second);
        var service = new WorkspaceService(config, new BrokerRegistry(config));
        await using var session = new WorkspaceSession(true, "authenticated-connection", config.Workspace);
        var result = await service.CallAsync(session, "workspace_list", new JsonObject(), CancellationToken.None);
        var projects = result["structuredContent"]!["result"]!["projects"]!.AsArray();
        Assert.Equal(2, projects.Count);
        Assert.Contains(projects, p => p!["workspaceId"]!.GetValue<string>() == manual.Id);
        Assert.Contains(projects, p => p!["workspaceId"]!.GetValue<string>() == automatic.Id);
        foreach (var project in projects)
        {
            var capabilities = project!["capabilities"]!;
            Assert.True(capabilities["filesRead"]!.GetValue<bool>());
            Assert.True(capabilities["filesWrite"]!.GetValue<bool>());
            Assert.True(capabilities["terminal"]!.GetValue<bool>());
            Assert.False(capabilities["desktopAvailable"]!.GetValue<bool>());
        }
        using var document = JsonDocument.Parse(File.ReadAllText(config.FilePath));
        Assert.DoesNotContain("CredentialRef", document.RootElement.GetProperty("workspace").GetRawText());
        Assert.Equal(automatic.Id, BrokerConfiguration.Load(config.FilePath).RegisterProject(second).Id);
    }

    [Fact]
    public async Task MissingOptionalDriverHidesAndRejectsDesktopTools()
    {
        var config = Configuration();
        config.Workspace.DesktopDriverPath = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var service = new WorkspaceService(config, new BrokerRegistry(config));
        await using var session = new WorkspaceSession(true, "authenticated-connection", config.Workspace);
        var tools = await service.ListToolsAsync(session, CancellationToken.None);
        Assert.DoesNotContain(tools, tool => tool!["name"]!.GetValue<string>().StartsWith("desktop_", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<BrokerOperationException>(() =>
            service.CallAsync(session, "desktop_observe", new JsonObject(), CancellationToken.None));
        Assert.Equal("ToolUnavailable", error.Code);
    }

    [Fact]
    public async Task HostDesktopDoesNotRequireAnyProjectAndCannotForwardAnotherHost()
    {
        var config = Configuration();
        config.Workspace.DesktopDriverPath = Environment.ProcessPath;
        Assert.True(File.Exists(config.Workspace.DesktopDriverPath));
        var service = new WorkspaceService(config, new BrokerRegistry(config));
        await using var session = new WorkspaceSession(true, "authenticated-connection", config.Workspace);
        Assert.Empty(config.Projects);
        var tools = await service.ListToolsAsync(session, CancellationToken.None);
        Assert.Contains(tools, tool => tool!["name"]!.GetValue<string>() == "desktop_observe");
        var targetError = await Assert.ThrowsAsync<BrokerOperationException>(() =>
            service.CallAsync(session, "desktop_observe", new JsonObject(), CancellationToken.None));
        Assert.Equal("DesktopTargetRequired", targetError.Code);
        var forwardingError = await Assert.ThrowsAsync<BrokerOperationException>(() =>
            service.CallAsync(session, "desktop_observe", new JsonObject { ["hostId"] = "another-host" }, CancellationToken.None));
        Assert.Equal("InvalidDesktopTarget", forwardingError.Code);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
