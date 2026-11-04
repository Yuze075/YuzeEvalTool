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
        var result = await service.CallAsync(true, "workspace_list", new JsonObject(), CancellationToken.None);
        var projects = result["structuredContent"]!["result"]!["projects"]!.AsArray();
        Assert.Equal(2, projects.Count);
        Assert.Contains(projects, p => p!["workspaceId"]!.GetValue<string>() == manual.Id);
        Assert.Contains(projects, p => p!["workspaceId"]!.GetValue<string>() == automatic.Id);
        foreach (var project in projects)
            Assert.False(project!["desktopAvailable"]!.GetValue<bool>());
        using var document = JsonDocument.Parse(File.ReadAllText(config.FilePath));
        Assert.DoesNotContain("CredentialRef", document.RootElement.GetProperty("workspace").GetRawText());
        Assert.Equal(automatic.Id, BrokerConfiguration.Load(config.FilePath).RegisterProject(second).Id);
    }

    [Fact]
    public async Task MissingOptionalDriverRejectsDesktopTools()
    {
        var config = Configuration();
        config.Workspace.DesktopDriverPath = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var service = new WorkspaceService(config, new BrokerRegistry(config));
        var error = await Assert.ThrowsAsync<BrokerOperationException>(() =>
            service.CallAsync(true, "desktop_observe", new JsonObject(), CancellationToken.None));
        Assert.Equal("ToolUnavailable", error.Code);
    }

    [Fact]
    public async Task HostDesktopDoesNotRequireAnyProjectAndCannotForwardAnotherHost()
    {
        var config = Configuration();
        config.Workspace.DesktopDriverPath = Environment.ProcessPath;
        Assert.True(File.Exists(config.Workspace.DesktopDriverPath));
        var service = new WorkspaceService(config, new BrokerRegistry(config));
        Assert.Empty(config.Projects);
        var targetError = await Assert.ThrowsAsync<BrokerOperationException>(() =>
            service.CallAsync(true, "desktop_observe", new JsonObject(), CancellationToken.None));
        Assert.Equal("DesktopTargetRequired", targetError.Code);
        var forwardingError = await Assert.ThrowsAsync<BrokerOperationException>(() =>
            service.CallAsync(true, "desktop_observe", new JsonObject { ["hostId"] = "another-host" }, CancellationToken.None));
        Assert.Equal("InvalidDesktopTarget", forwardingError.Code);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
