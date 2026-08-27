#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using YuzeToolkit.Eval;

namespace YuzeToolkit.Agent
{
    public sealed class AgentUnityEvalService : IDisposable
    {
        private readonly object _syncRoot = new();
        private readonly Dictionary<string, EvalSession> _sessions = new(StringComparer.Ordinal);
        private readonly EvalExecutor _executor;
        private bool _disposed;

        public AgentUnityEvalService(int defaultTimeoutSeconds = 30)
        {
            _executor = new EvalExecutor(new EvalOptions
            {
                DefaultEvalTimeoutSeconds = Math.Min(600, Math.Max(1, defaultTimeoutSeconds))
            });
        }

        public async Task<AgentToolResult> ExecuteAsync(
            string agentSessionId,
            string code,
            int timeoutSeconds,
            bool resetSession,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(agentSessionId))
                throw new ArgumentException("Agent session id is required.", nameof(agentSessionId));
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("Eval code is required.", nameof(code));
            cancellationToken.ThrowIfCancellationRequested();
            EvalSession session;
            lock (_syncRoot)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AgentUnityEvalService));
                if (!_sessions.TryGetValue(agentSessionId, out session!))
                {
                    session = new EvalSession("agent-" + agentSessionId, "agent-1", "UnityAgentTool");
                    _sessions.Add(agentSessionId, session);
                }
            }

            var response = await _executor.ExecuteAsync(
                session,
                Guid.NewGuid().ToString("N"),
                EvalData.Obj(
                    ("code", code),
                    ("timeout", Math.Min(600, Math.Max(1, timeoutSeconds))),
                    ("resetSession", resetSession)),
                cancellationToken).ConfigureAwait(false);
            var isError = EvalData.GetBool(response, "isError");
            if (isError && cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            var content = EvalData.AsArray(response.TryGetValue("content", out var raw) ? raw : null);
            if (content != null)
            {
                foreach (var value in content)
                {
                    var item = EvalData.AsObject(value);
                    if (item == null) continue;
                    if (AgentJson.GetString(item, "type") == "text")
                    {
                        if (text.Length > 0) text.AppendLine();
                        text.Append(AgentJson.GetString(item, "text"));
                    }
                    else
                    {
                        if (text.Length > 0) text.AppendLine();
                        text.Append(AgentJson.Stringify(item));
                    }
                }
            }

            var resultText = text.Length == 0 ? AgentJson.Stringify(response) : text.ToString();
            return isError ? AgentToolResult.Error(resultText) : AgentToolResult.Success(resultText);
        }

        public void ReleaseSession(string agentSessionId)
        {
            if (string.IsNullOrWhiteSpace(agentSessionId)) return;
            EvalSession? session = null;
            lock (_syncRoot)
            {
                if (_sessions.TryGetValue(agentSessionId, out session))
                    _sessions.Remove(agentSessionId);
            }
            session?.Dispose();
        }

        public void Dispose()
        {
            List<EvalSession> sessions;
            lock (_syncRoot)
            {
                if (_disposed) return;
                _disposed = true;
                sessions = new List<EvalSession>(_sessions.Values);
                _sessions.Clear();
            }
            foreach (var session in sessions) session.Dispose();
        }
    }

    internal sealed class UnityEvalJsAgentTool : IAgentTool
    {
        private readonly AgentUnityEvalService _service;

        public UnityEvalJsAgentTool(AgentUnityEvalService service)
        {
            _service = service;
            Descriptor = new AgentToolDescriptor(
                "unity_eval_js",
                "Run JavaScript directly in the current Unity process. Define async function execute() and return concise " +
                "serializable data. The PuerTS VM persists for this conversation unless resetSession is true. For unfamiliar " +
                "Unity work, import tools:// to discover root modules and details, then import only the relevant module; generated " +
                "tool methods use positional parameters. Prefer those modules and use CS.* only for uncovered APIs. If an Editor " +
                "action schedules compilation, return immediately; the Agent host will resume this conversation afterward. " +
                "This direct tool does not use Broker, MCP, or CLI.",
                AgentToolAccess.Write,
                AgentToolRisk.FullTrust,
                AgentToolSurface.All,
                false,
                AgentToolArguments.ObjectSchema(AgentJson.Object(
                        ("code", AgentToolArguments.StringProperty(
                            "JavaScript containing async function execute() { ... }.")),
                        ("timeoutSeconds", AgentToolArguments.IntegerProperty("Cooperative timeout in seconds.", 1)),
                        ("resetSession", AgentToolArguments.BooleanProperty(
                            "Reset this conversation's persistent JavaScript VM before execution."))),
                    "code"));
        }

        public AgentToolDescriptor Descriptor { get; }

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var code = AgentToolArguments.RequiredString(arguments, "code");
            var timeout = Math.Min(600,
                Math.Max(1, AgentToolArguments.OptionalInt(arguments, "timeoutSeconds",
                    context.DefaultTimeoutSeconds)));
            var reset = AgentToolArguments.OptionalBool(arguments, "resetSession");
            return _service.ExecuteAsync(context.SessionId, code, timeout, reset, cancellationToken);
        }
    }

    internal sealed class UnitySnapshotAgentTool : IAgentTool
    {
        public UnitySnapshotAgentTool()
        {
            Descriptor = new AgentToolDescriptor(
                "unity_snapshot",
                "Read a bounded snapshot of the current Unity process and loaded scenes without mutating state.",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.ReadOnly,
                AgentToolSurface.All,
                true,
                AgentToolArguments.ObjectSchema(new Dictionary<string, object?>()));
        }

        public AgentToolDescriptor Descriptor { get; }

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MainThreadDispatcher.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scenes = new List<object?>();
                for (var index = 0; index < SceneManager.sceneCount; index++)
                {
                    var scene = SceneManager.GetSceneAt(index);
                    scenes.Add(AgentJson.Object(
                        ("name", scene.name),
                        ("path", scene.path),
                        ("buildIndex", scene.buildIndex),
                        ("isLoaded", scene.isLoaded),
                        ("isDirty", scene.isDirty),
                        ("rootCount", scene.rootCount)));
                }
                var activeScene = SceneManager.GetActiveScene();
                return AgentToolResult.Success(AgentJson.Stringify(AgentJson.Object(
                    ("unityVersion", Application.unityVersion),
                    ("platform", Application.platform.ToString()),
                    ("isEditor", Application.isEditor),
                    ("isPlaying", Application.isPlaying),
                    ("isFocused", Application.isFocused),
                    ("activeScene", activeScene.IsValid() ? activeScene.name : string.Empty),
                    ("loadedScenes", scenes))));
            });
        }
    }

    internal sealed class UnitySceneQueryAgentTool : IAgentTool
    {
        private const int MaximumResults = 200;

        public UnitySceneQueryAgentTool()
        {
            Descriptor = new AgentToolDescriptor(
                "unity_scene_query",
                "Find loaded-scene GameObjects by optional name, tag, or attached component type without mutating Unity state.",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.ReadOnly,
                AgentToolSurface.All,
                true,
                AgentToolArguments.ObjectSchema(AgentJson.Object(
                    ("nameContains", AgentToolArguments.StringProperty("Optional case-insensitive name fragment.")),
                    ("tag", AgentToolArguments.StringProperty("Optional exact Unity tag.")),
                    ("componentType", AgentToolArguments.StringProperty(
                        "Optional component simple or full type name.")),
                    ("maxResults", AgentToolArguments.IntegerProperty(
                        "Maximum returned objects, up to 200.", 1)))));
        }

        public AgentToolDescriptor Descriptor { get; }

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var nameContains = AgentToolArguments.OptionalString(arguments, "nameContains");
            var tag = AgentToolArguments.OptionalString(arguments, "tag");
            var componentType = AgentToolArguments.OptionalString(arguments, "componentType");
            var maxResults = Math.Min(MaximumResults,
                Math.Max(1, AgentToolArguments.OptionalInt(arguments, "maxResults", 50)));
            cancellationToken.ThrowIfCancellationRequested();
            return MainThreadDispatcher.RunAsync(() => Query(
                nameContains, tag, componentType, maxResults, cancellationToken));
        }

        private static AgentToolResult Query(
            string nameContains,
            string tag,
            string componentType,
            int maxResults,
            CancellationToken cancellationToken)
        {
            var matches = new List<object?>(maxResults);
            var totalMatches = 0;
            foreach (var gameObject in Resources.FindObjectsOfTypeAll<GameObject>()
                         .OrderBy(value => value.scene.path, StringComparer.Ordinal)
                         .ThenBy(BuildHierarchyPath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!gameObject.scene.IsValid() || !gameObject.scene.isLoaded) continue;
                if (nameContains.Length > 0 &&
                    gameObject.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (tag.Length > 0 && !string.Equals(gameObject.tag, tag, StringComparison.Ordinal)) continue;
                var components = gameObject.GetComponents<Component>()
                    .Where(value => value != null)
                    .Select(value => value.GetType())
                    .ToList();
                if (componentType.Length > 0 && components.All(type =>
                        !string.Equals(type.Name, componentType, StringComparison.Ordinal) &&
                        !string.Equals(type.FullName, componentType, StringComparison.Ordinal))) continue;

                totalMatches++;
                if (matches.Count >= maxResults) continue;
                matches.Add(AgentJson.Object(
                    ("instanceId", gameObject.GetInstanceID()),
                    ("name", gameObject.name),
                    ("path", BuildHierarchyPath(gameObject)),
                    ("scene", gameObject.scene.name),
                    ("activeSelf", gameObject.activeSelf),
                    ("activeInHierarchy", gameObject.activeInHierarchy),
                    ("tag", gameObject.tag),
                    ("layer", gameObject.layer),
                    ("components", components.Select(type => (object?)(type.FullName ?? type.Name)).ToList())));
            }
            return AgentToolResult.Success(AgentJson.Stringify(AgentJson.Object(
                ("totalMatches", totalMatches),
                ("truncated", totalMatches > matches.Count),
                ("objects", matches))));
        }

        private static string BuildHierarchyPath(GameObject gameObject)
        {
            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names);
        }
    }
}
