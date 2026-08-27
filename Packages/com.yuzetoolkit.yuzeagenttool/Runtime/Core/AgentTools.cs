#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YuzeToolkit.Eval;

namespace YuzeToolkit.Agent
{
    public sealed class AgentToolRegistry
    {
        private readonly object _syncRoot = new();
        private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.Ordinal);

        public IDisposable Register(IAgentTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            lock (_syncRoot)
            {
                if (_tools.ContainsKey(tool.Descriptor.Name))
                    throw new InvalidOperationException($"Agent Tool '{tool.Descriptor.Name}' is already registered.");
                _tools.Add(tool.Descriptor.Name, tool);
            }
            return new ToolRegistration(this, tool);
        }

        public bool TryGet(string name, out IAgentTool tool)
        {
            lock (_syncRoot)
                return _tools.TryGetValue(name, out tool!);
        }

        public IReadOnlyList<AgentToolDescriptor> ListDescriptors()
        {
            lock (_syncRoot)
                return _tools.Values.Select(tool => tool.Descriptor)
                    .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal).ToList();
        }

        public IReadOnlyList<AgentToolDescriptor> ListDescriptors(
            AgentPermissionMode permissionMode,
            AgentToolSurface surface)
        {
            lock (_syncRoot)
                return _tools.Values.Select(tool => tool.Descriptor)
                    .Where(descriptor => AgentToolPolicy.IsExposed(descriptor, permissionMode, surface))
                    .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal).ToList();
        }

        private void Unregister(IAgentTool tool)
        {
            lock (_syncRoot)
            {
                if (_tools.TryGetValue(tool.Descriptor.Name, out var current) && ReferenceEquals(current, tool))
                    _tools.Remove(tool.Descriptor.Name);
            }
        }

        private sealed class ToolRegistration : IDisposable
        {
            private AgentToolRegistry? _registry;
            private IAgentTool? _tool;

            public ToolRegistration(AgentToolRegistry registry, IAgentTool tool)
            {
                _registry = registry;
                _tool = tool;
            }

            public void Dispose()
            {
                var registry = Interlocked.Exchange(ref _registry, null);
                var tool = Interlocked.Exchange(ref _tool, null);
                if (registry != null && tool != null) registry.Unregister(tool);
            }
        }
    }

    internal static class AgentToolPolicy
    {
        public static AgentToolSurface CurrentSurface =>
            AgentPaths.IsEditor ? AgentToolSurface.Editor : AgentToolSurface.Player;

        public static bool IsExposed(
            AgentToolDescriptor descriptor,
            AgentPermissionMode permissionMode,
            AgentToolSurface surface)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (!Enum.IsDefined(typeof(AgentPermissionMode), permissionMode))
                throw new ArgumentOutOfRangeException(nameof(permissionMode), permissionMode,
                    "Unknown Agent permission mode.");
            if (surface is not (AgentToolSurface.Editor or AgentToolSurface.Player))
                throw new ArgumentOutOfRangeException(nameof(surface), surface,
                    "Tool policy requires exactly one active surface.");
            if ((descriptor.Surfaces & surface) == 0) return false;
            return permissionMode != AgentPermissionMode.ObserveOnly ||
                   descriptor.Risk == AgentToolRisk.ReadOnly;
        }

        public static bool RequiresApproval(AgentToolDescriptor descriptor, AgentPermissionMode permissionMode)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            return permissionMode == AgentPermissionMode.ConfirmWrites &&
                   descriptor.Risk != AgentToolRisk.ReadOnly;
        }

        public static bool RestrictsFileSystem(AgentPermissionMode permissionMode) =>
            permissionMode != AgentPermissionMode.FullAccess;
    }

    public sealed class AgentApprovalRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string SessionId { get; set; } = string.Empty;

        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public string ArgumentsJson { get; set; } = "{}";

        public string Description { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class AgentApprovalService
    {
        private sealed class PendingApproval
        {
            public PendingApproval(AgentApprovalRequest request)
            {
                Request = request;
                Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public AgentApprovalRequest Request { get; }

            public TaskCompletionSource<bool> Completion { get; }
        }

        private readonly object _syncRoot = new();
        private readonly Dictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);

        public event Action? Changed;

        public IReadOnlyList<AgentApprovalRequest> Pending
        {
            get
            {
                lock (_syncRoot)
                    return _pending.Values.Select(value => value.Request)
                        .OrderBy(value => value.CreatedAtUtc).ToList();
            }
        }

        public async Task<bool> WaitForDecisionAsync(
            AgentApprovalRequest request,
            CancellationToken cancellationToken,
            Func<Task>? afterRegistered = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            PendingApproval pending;
            lock (_syncRoot)
            {
                if (_pending.ContainsKey(request.Id))
                    throw new InvalidOperationException($"Approval '{request.Id}' is already pending.");
                pending = new PendingApproval(request);
                _pending.Add(request.Id, pending);
            }

            Changed?.Invoke();
            using var registration = cancellationToken.Register(() => pending.Completion.TrySetCanceled());
            try
            {
                if (afterRegistered != null) await afterRegistered().ConfigureAwait(false);
                return await pending.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_syncRoot)
                    _pending.Remove(request.Id);
                Changed?.Invoke();
            }
        }

        public bool Resolve(string approvalId, bool approved)
        {
            if (string.IsNullOrWhiteSpace(approvalId)) return false;
            PendingApproval? pending;
            lock (_syncRoot)
                _pending.TryGetValue(approvalId, out pending);
            return pending != null && pending.Completion.TrySetResult(approved);
        }

        public void CancelSession(string sessionId)
        {
            List<PendingApproval> pending;
            lock (_syncRoot)
                pending = _pending.Values.Where(value => value.Request.SessionId == sessionId).ToList();
            foreach (var value in pending)
                value.Completion.TrySetCanceled();
        }
    }

    internal static class AgentToolArguments
    {
        public static Dictionary<string, object?> Parse(string json)
        {
            try
            {
                return AgentJson.ParseObject(json);
            }
            catch (Exception exception) when (exception is FormatException || exception is InvalidOperationException)
            {
                throw new ArgumentException("Tool arguments must be a valid JSON object.", nameof(json), exception);
            }
        }

        public static string RequiredString(Dictionary<string, object?> arguments, string key)
        {
            if (!arguments.TryGetValue(key, out var raw) || raw is not string value ||
                string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Required string argument '{key}' is missing.");
            return value;
        }

        public static string RequiredText(Dictionary<string, object?> arguments, string key)
        {
            if (!arguments.TryGetValue(key, out var raw) || raw is not string value)
                throw new ArgumentException($"Required text argument '{key}' is missing.");
            return value;
        }

        public static string OptionalString(Dictionary<string, object?> arguments, string key, string fallback = "")
        {
            if (!arguments.TryGetValue(key, out var raw) || raw == null) return fallback;
            if (raw is string value) return value;
            throw new ArgumentException($"Optional argument '{key}' must be a string.");
        }

        public static int OptionalInt(Dictionary<string, object?> arguments, string key, int fallback) =>
            EvalData.GetInt(arguments, key, fallback);

        public static bool OptionalBool(Dictionary<string, object?> arguments, string key, bool fallback = false) =>
            EvalData.GetBool(arguments, key, fallback);

        public static List<string> OptionalStrings(Dictionary<string, object?> arguments, string key)
        {
            var values = AgentJson.GetArray(arguments, key);
            if (values == null) return new List<string>();
            var result = new List<string>(values.Count);
            foreach (var value in values)
            {
                if (value is not string text)
                    throw new ArgumentException($"Array argument '{key}' must contain only strings.");
                result.Add(text);
            }
            return result;
        }

        public static List<Dictionary<string, object?>> RequiredObjects(
            Dictionary<string, object?> arguments,
            string key)
        {
            if (!arguments.ContainsKey(key))
                throw new ArgumentException($"Required array argument '{key}' is missing.");
            var values = AgentJson.GetObjectArray(arguments, key);
            if (values.Count == 0)
                throw new ArgumentException($"Required array argument '{key}' cannot be empty.");
            return values;
        }

        public static Dictionary<string, object?> ObjectSchema(
            Dictionary<string, object?> properties,
            params string[] required)
        {
            return AgentJson.Object(
                ("type", "object"),
                ("properties", properties),
                ("required", required.Cast<object?>().ToList()),
                ("additionalProperties", false));
        }

        public static Dictionary<string, object?> StringProperty(string description)
        {
            return AgentJson.Object(("type", "string"), ("description", description));
        }

        public static Dictionary<string, object?> BooleanProperty(string description)
        {
            return AgentJson.Object(("type", "boolean"), ("description", description));
        }

        public static Dictionary<string, object?> IntegerProperty(string description, int minimum = 0)
        {
            return AgentJson.Object(("type", "integer"), ("minimum", minimum), ("description", description));
        }
    }
}
