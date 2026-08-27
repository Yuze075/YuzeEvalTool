#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace YuzeToolkit.UnityAgent.Tests
{
    public sealed class AgentCoreTests
    {
        [Test]
        public void ObserveOnly_ExposesOnlyReadToolsForCurrentSurface()
        {
            var registry = new AgentToolRegistry();
            registry.Register(new StubTool("read", AgentToolAccess.ReadOnly, AgentToolRisk.ReadOnly,
                AgentToolSurface.All));
            registry.Register(new StubTool("write", AgentToolAccess.Write, AgentToolRisk.WorkspaceWrite,
                AgentToolSurface.All));
            registry.Register(new StubTool("process", AgentToolAccess.Write, AgentToolRisk.Process,
                AgentToolSurface.Editor));

            var playerTools = registry.ListDescriptors(AgentPermissionMode.ObserveOnly, AgentToolSurface.Player);

            Assert.That(playerTools.Select(value => value.Name), Is.EqualTo(new[] { "read" }));
            Assert.That(registry.ListDescriptors(AgentPermissionMode.ConfirmWrites, AgentToolSurface.Editor)
                .Select(value => value.Name), Is.EqualTo(new[] { "process", "read", "write" }));
        }

        [Test]
        public void Registration_DisposeRemovesOnlyRegisteredTool()
        {
            var registry = new AgentToolRegistry();
            var registration = registry.Register(new StubTool("temporary", AgentToolAccess.ReadOnly,
                AgentToolRisk.ReadOnly, AgentToolSurface.All));
            Assert.That(registry.TryGet("temporary", out _), Is.True);

            registration.Dispose();

            Assert.That(registry.TryGet("temporary", out _), Is.False);
        }

        [Test]
        public void RestrictedPath_RejectsOutsideProjectWhileFullAccessAllowsIt()
        {
            var outside = Path.Combine(Path.GetTempPath(), "unity-agent-outside.txt");
            var restricted = new AgentToolContext("test", AgentPaths.ProjectRoot, 30,
                AgentPermissionMode.ConfirmWrites, AgentToolSurface.Editor);
            var fullAccess = new AgentToolContext("test", AgentPaths.ProjectRoot, 30,
                AgentPermissionMode.FullAccess, AgentToolSurface.Editor);

            Assert.Throws<UnauthorizedAccessException>(() => AgentPath.Resolve(restricted, outside));
            Assert.That(AgentPath.Resolve(fullAccess, outside), Is.EqualTo(Path.GetFullPath(outside)));
        }

        [Test]
        public void ApplyPatch_RequiresUtf8CurrentHashAndExactOccurrenceCount()
        {
            var directory = Path.Combine(Path.GetTempPath(), "unity-agent-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "sample.txt");
            try
            {
                File.WriteAllText(path, "alpha\nbeta\n", new UTF8Encoding(false));
                var hash = ReadFileAgentTool.ComputeSha256(path, CancellationToken.None);
                var edits = new[]
                {
                    AgentJson.Object(("oldText", "beta"), ("newText", "gamma"),
                        ("expectedOccurrences", 1))
                };

                var applied = ApplyPatchAgentTool.Apply(path, hash, edits, CancellationToken.None);

                Assert.That(applied.IsError, Is.False, applied.Text);
                Assert.That(File.ReadAllText(path), Is.EqualTo("alpha\ngamma\n"));
                var afterFirstPatch = File.ReadAllText(path);
                var stale = ApplyPatchAgentTool.Apply(path, hash, edits, CancellationToken.None);
                Assert.That(stale.IsError, Is.True);
                Assert.That(File.ReadAllText(path), Is.EqualTo(afterFirstPatch));

                var currentHash = ReadFileAgentTool.ComputeSha256(path, CancellationToken.None);
                var wrongCount = new[]
                {
                    AgentJson.Object(("oldText", "gamma"), ("newText", "delta"),
                        ("expectedOccurrences", 2))
                };
                var rejected = ApplyPatchAgentTool.Apply(path, currentHash, wrongCount, CancellationToken.None);
                Assert.That(rejected.IsError, Is.True);
                Assert.That(File.ReadAllText(path), Is.EqualTo(afterFirstPatch));

                var invalidUtf8 = new byte[] { 0xff, 0xfe, 0xfd };
                File.WriteAllBytes(path, invalidUtf8);
                var invalidHash = ReadFileAgentTool.ComputeSha256(path, CancellationToken.None);
                rejected = ApplyPatchAgentTool.Apply(path, invalidHash, edits, CancellationToken.None);
                Assert.That(rejected.IsError, Is.True);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(invalidUtf8));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public async Task SendMessage_ReturnsFailedTurnAndPublishesFailureEvent()
        {
            var store = new MemoryStore(CreateSettings());
            using var host = new UnityAgentHost(store, new ThrowingProvider());
            var events = new List<AgentHostStreamEvent>();
            host.StreamEvent += events.Add;
            var session = await host.CreateSessionAsync();

            var result = await host.SendMessageAsync(session.Id, "fail predictably");

            Assert.That(result.State, Is.EqualTo(AgentSessionState.Failed));
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error, Does.Contain("provider failed"));
            Assert.That(events.Any(value => value.SessionId == session.Id &&
                                            value.StreamEvent.Kind == AgentStreamEventKind.RunFailed &&
                                            value.StreamEvent.IsError), Is.True);
        }

        [Test]
        public async Task SendMessage_PublishesToolExecutionLifecycle()
        {
            var store = new MemoryStore(CreateSettings());
            using var host = new UnityAgentHost(store, new ToolThenCompleteProvider());
            host.Tools.Register(new StubTool("test_read", AgentToolAccess.ReadOnly, AgentToolRisk.ReadOnly,
                AgentToolSurface.All));
            var events = new List<AgentHostStreamEvent>();
            host.StreamEvent += events.Add;
            var session = await host.CreateSessionAsync();

            var result = await host.SendMessageAsync(session.Id, "use a tool");

            Assert.That(result.IsSuccess, Is.True, result.Error);
            Assert.That(events.Any(value => value.StreamEvent.Kind == AgentStreamEventKind.ToolExecutionStarted &&
                                            value.StreamEvent.CallId == "call-1"), Is.True);
            Assert.That(events.Any(value => value.StreamEvent.Kind == AgentStreamEventKind.ToolExecutionCompleted &&
                                            value.StreamEvent.CallId == "call-1" &&
                                            !value.StreamEvent.IsError), Is.True);
            Assert.That(events.Any(value => value.StreamEvent.Kind == AgentStreamEventKind.TurnCompleted &&
                                            value.StreamEvent.Text == AgentSessionState.Completed.ToString() &&
                                            !value.StreamEvent.IsError), Is.True);
        }

        private static AgentSettingsDocument CreateSettings()
        {
            var profile = new AgentProviderProfile
            {
                Id = "test-provider",
                ProviderPresetId = "openai",
                Protocol = AgentProtocolIds.OpenAiResponses,
                BaseUrl = "https://example.invalid/v1/",
                Model = "test-model"
            };
            return new AgentSettingsDocument
            {
                DefaultProviderProfileId = profile.Id,
                PermissionMode = AgentPermissionMode.ConfirmWrites,
                EditorSystemPrompt = "Test editor prompt.",
                RuntimeSystemPrompt = "Test runtime prompt.",
                DefaultToolTimeoutSeconds = 30,
                MaximumAgentSteps = 4,
                ProviderProfiles = new List<AgentProviderProfile> { profile }
            };
        }

        private sealed class StubTool : IAgentTool
        {
            public StubTool(string name, AgentToolAccess access, AgentToolRisk risk, AgentToolSurface surfaces)
            {
                Descriptor = new AgentToolDescriptor(name, name, access, risk, surfaces, true,
                    AgentToolArguments.ObjectSchema(new Dictionary<string, object?>()));
            }

            public AgentToolDescriptor Descriptor { get; }

            public Task<AgentToolResult> ExecuteAsync(AgentToolContext context,
                Dictionary<string, object?> arguments, CancellationToken cancellationToken) =>
                Task.FromResult(AgentToolResult.Success("ok"));
        }

        private sealed class ThrowingProvider : IAgentModelProvider
        {
            public Task<AgentModelResponse> CompleteAsync(AgentProviderProfile profile, AgentModelRequest request,
                Action<AgentStreamEvent>? onEvent, CancellationToken cancellationToken) =>
                Task.FromException<AgentModelResponse>(new InvalidOperationException("provider failed"));

            public Task<IReadOnlyList<string>> ListModelsAsync(AgentProviderProfile profile,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<string>>(new[] { "test-model" });
        }

        private sealed class ToolThenCompleteProvider : IAgentModelProvider
        {
            private int _calls;

            public Task<AgentModelResponse> CompleteAsync(AgentProviderProfile profile, AgentModelRequest request,
                Action<AgentStreamEvent>? onEvent, CancellationToken cancellationToken)
            {
                _calls++;
                return Task.FromResult(_calls == 1
                    ? new AgentModelResponse
                    {
                        ToolCalls = new List<AgentToolCall>
                        {
                            new() { Id = "call-1", Name = "test_read", ArgumentsJson = "{}" }
                        }
                    }
                    : new AgentModelResponse { Text = "done" });
            }

            public Task<IReadOnlyList<string>> ListModelsAsync(AgentProviderProfile profile,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<string>>(new[] { "test-model" });
        }

        private sealed class MemoryStore : IAgentStore
        {
            private AgentSettingsDocument _settings;
            private readonly Dictionary<string, AgentSessionDocument> _sessions = new(StringComparer.Ordinal);

            public MemoryStore(AgentSettingsDocument settings)
            {
                _settings = settings;
            }

            public Task<IReadOnlyList<AgentSessionDocument>> LoadSessionsAsync(CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<AgentSessionDocument>>(
                    _sessions.Values.Select(AgentDocumentCodec.Clone).ToList());

            public Task SaveSessionAsync(AgentSessionDocument session, CancellationToken cancellationToken)
            {
                _sessions[session.Id] = AgentDocumentCodec.Clone(session);
                return Task.CompletedTask;
            }

            public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
            {
                _sessions.Remove(sessionId);
                return Task.CompletedTask;
            }

            public Task<AgentSettingsDocument> LoadSettingsAsync(CancellationToken cancellationToken) =>
                Task.FromResult(AgentDocumentCodec.Clone(_settings));

            public Task SaveSettingsAsync(AgentSettingsDocument settings, CancellationToken cancellationToken)
            {
                _settings = AgentDocumentCodec.Clone(settings);
                return Task.CompletedTask;
            }
        }
    }
}
