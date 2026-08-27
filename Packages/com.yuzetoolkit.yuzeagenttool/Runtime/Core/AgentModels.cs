#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit.UnityAgent
{
    public enum AgentPermissionMode
    {
        FullAccess = 0,
        ConfirmWrites = 1,
        ObserveOnly = 2
    }

    public enum AgentToolAccess
    {
        Write = 0,
        ReadOnly = 1
    }

    public enum AgentToolRisk
    {
        ReadOnly = 0,
        WorkspaceWrite = 1,
        UnityMutation = 2,
        Process = 3,
        Destructive = 4,
        FullTrust = 5
    }

    [Flags]
    public enum AgentToolSurface
    {
        None = 0,
        Editor = 1,
        Player = 2,
        All = Editor | Player
    }

    public enum AgentMessageRole
    {
        User,
        Assistant,
        Tool
    }

    public enum AgentSessionState
    {
        Idle,
        Running,
        AwaitingApproval,
        Completed,
        Interrupted,
        Failed,
        StepLimitReached
    }

    public enum AgentStreamEventKind
    {
        RunStarted,
        TextDelta,
        ReasoningDelta,
        ToolCallStarted,
        ToolCallArgumentsDelta,
        ToolExecutionStarted,
        ToolExecutionCompleted,
        UsageUpdated,
        RunCompleted,
        RunFailed,
        TurnCompleted
    }

    public sealed class AgentProviderProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string ProviderPresetId { get; set; } = "openai";

        public string Name { get; set; } = "OpenAI";

        public string Protocol { get; set; } = AgentProtocolIds.OpenAiResponses;

        public string BaseUrl { get; set; } = "https://api.openai.com/v1/";

        public string Model { get; set; } = string.Empty;

        public string ReasoningEffort { get; set; } = string.Empty;

        /// <summary>API key used by this machine-local Provider profile.</summary>
        public string ApiKey { get; set; } = string.Empty;

        public int MaxOutputTokens { get; set; } = 4096;

        /// <summary>
        /// Complete model context window. Curated presets populate their published value; custom
        /// providers use this explicit profile value so context compaction never relies on a model-name guess.
        /// </summary>
        public int ContextWindowTokens { get; set; } = 128_000;

        public bool StrictTools { get; set; } = true;
    }

    public static class AgentProtocolIds
    {
        public const string OpenAiResponses = "openai-responses";
        public const string OpenAiChat = "openai-chat";
        public const string AnthropicMessages = "anthropic-messages";
        public const string GoogleGeminiInteractions = "google-gemini-interactions";

        public static readonly IReadOnlyList<string> All = new[]
        {
            OpenAiResponses,
            OpenAiChat,
            AnthropicMessages,
            GoogleGeminiInteractions
        };
    }

    /// <summary>
    /// Stable, machine-independent anchors used by every configurable Agent path.
    /// The value is persisted by name; do not reorder or rename existing members.
    /// </summary>
    public enum AgentPathBase
    {
        ProjectRoot = 0,
        PersistentData = 1,
        UserProfile = 2,
        Documents = 3,
        LocalApplicationData = 4,
        RoamingApplicationData = 5,
        TemporaryCache = 6,
        StreamingAssets = 7
    }

    /// <summary>
    /// Selects the environments in which a configured path is discovered directly. Embedded Player
    /// content is an independent source and is not restricted by this scope.
    /// </summary>
    public enum AgentPathScope
    {
        None = 0,
        EditorOnly = 1,
        PlayerOnly = 2,
        All = 3
    }

    /// <summary>
    /// A portable path made from a stable base and an optional relative path.
    /// RelativePath may contain parent segments, but it must never be absolute.
    /// </summary>
    public sealed class AgentPathLocation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public AgentPathBase BasePath { get; set; } = AgentPathBase.ProjectRoot;

        /// <summary>
        /// When true, path resolution inserts the package-owned .unityagenttool namespace below
        /// BasePath. Disable it for roots such as a project-level AGENTS.md or .agents/skills folder.
        /// </summary>
        public bool UseUnityAgentToolDirectory { get; set; } = true;

        public string RelativePath { get; set; } = string.Empty;

        /// <summary>Controls direct path discovery in Editor and Player environments.</summary>
        public AgentPathScope Scope { get; set; } = AgentPathScope.All;

        /// <summary>
        /// When true, the build-time contents of this root are copied into Player StreamingAssets.
        /// Embedded content is loaded in Player regardless of Scope.
        /// </summary>
        public bool EmbedInPlayerBuild { get; set; }

    }

    /// <summary>
    /// Machine-local Provider profiles are user-owned configuration. They are persisted separately
    /// from settings that are seeded from Package or Project defaults.
    /// </summary>
    public sealed class AgentProviderSettingsDocument
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string DefaultProviderProfileId { get; set; } = string.Empty;

        public List<AgentProviderProfile> ProviderProfiles { get; set; } = new();

        public static AgentProviderSettingsDocument CreateDefault()
        {
            var profile = new AgentProviderProfile();
            if (!AgentProviderCatalog.ApplyPreset(profile, "openai"))
                throw new InvalidOperationException("The built-in OpenAI Provider preset is missing.");
            return new AgentProviderSettingsDocument
            {
                DefaultProviderProfileId = profile.Id,
                ProviderProfiles = new List<AgentProviderProfile> { profile }
            };
        }

        public static AgentProviderSettingsDocument FromSettings(AgentSettingsDocument settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new AgentProviderSettingsDocument
            {
                DefaultProviderProfileId = settings.DefaultProviderProfileId,
                ProviderProfiles = settings.ProviderProfiles
            };
        }

        public void ApplyTo(AgentSettingsDocument settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.DefaultProviderProfileId = DefaultProviderProfileId;
            settings.ProviderProfiles = ProviderProfiles;
        }
    }

    public sealed class AgentSettingsDocument
    {
        public const int CurrentSchemaVersion = 13;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string DefaultProviderProfileId { get; set; } = string.Empty;

        public AgentPermissionMode PermissionMode { get; set; } = AgentPermissionMode.ConfirmWrites;

        public string EditorSystemPrompt { get; set; } = string.Empty;

        public string RuntimeSystemPrompt { get; set; } = string.Empty;

        public int DefaultToolTimeoutSeconds { get; set; }

        public int MaximumAgentSteps { get; set; }

        public List<AgentProviderProfile> ProviderProfiles { get; set; } = new();

        /// <summary>Ordered, highest-priority-first AGENTS.md discovery roots.</summary>
        public List<AgentPathLocation> AgentsRoots { get; set; } = new();

        /// <summary>Ordered, highest-priority-first directories containing Skills.</summary>
        public List<AgentPathLocation> SkillRoots { get; set; } = new();

        public static AgentSettingsDocument CreateDefault(AgentProjectSettingsDocument projectDefaults)
        {
            if (projectDefaults == null) throw new ArgumentNullException(nameof(projectDefaults));
            var settings = new AgentSettingsDocument();
            projectDefaults.ApplyTo(settings);
            AgentProviderSettingsDocument.CreateDefault().ApplyTo(settings);
            return settings;
        }
    }

    /// <summary>
    /// Provider-free defaults stored with the Unity project and included in Player builds.
    /// Machine credentials and Provider endpoints are intentionally absent.
    /// </summary>
    public sealed class AgentProjectSettingsDocument
    {
        public const int CurrentSchemaVersion = 6;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public AgentPermissionMode PermissionMode { get; set; } = AgentPermissionMode.ConfirmWrites;
        public string EditorSystemPrompt { get; set; } = string.Empty;
        public string RuntimeSystemPrompt { get; set; } = string.Empty;
        public int DefaultToolTimeoutSeconds { get; set; }
        public int MaximumAgentSteps { get; set; }
        public List<AgentPathLocation> AgentsRoots { get; set; } = new();
        public List<AgentPathLocation> SkillRoots { get; set; } = new();

        public static AgentProjectSettingsDocument FromSettings(AgentSettingsDocument settings) => new()
        {
            PermissionMode = settings.PermissionMode,
            EditorSystemPrompt = settings.EditorSystemPrompt,
            RuntimeSystemPrompt = settings.RuntimeSystemPrompt,
            DefaultToolTimeoutSeconds = settings.DefaultToolTimeoutSeconds,
            MaximumAgentSteps = settings.MaximumAgentSteps,
            AgentsRoots = settings.AgentsRoots.Select(ClonePath).ToList(),
            SkillRoots = settings.SkillRoots.Select(ClonePath).ToList()
        };

        public void ApplyTo(AgentSettingsDocument settings)
        {
            settings.PermissionMode = PermissionMode;
            settings.EditorSystemPrompt = EditorSystemPrompt;
            settings.RuntimeSystemPrompt = RuntimeSystemPrompt;
            settings.DefaultToolTimeoutSeconds = DefaultToolTimeoutSeconds;
            settings.MaximumAgentSteps = MaximumAgentSteps;
            settings.AgentsRoots = AgentsRoots.Select(ClonePath).ToList();
            settings.SkillRoots = SkillRoots.Select(ClonePath).ToList();
        }

        private static AgentPathLocation ClonePath(AgentPathLocation value) => new()
        {
            Id = value.Id,
            BasePath = value.BasePath,
            UseUnityAgentToolDirectory = value.UseUnityAgentToolDirectory,
            RelativePath = value.RelativePath,
            Scope = value.Scope,
            EmbedInPlayerBuild = value.EmbedInPlayerBuild
        };
    }

    public sealed class AgentToolCall
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string ArgumentsJson { get; set; } = "{}";

        public string ProviderItemId { get; set; } = string.Empty;
    }

    public sealed class AgentMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public AgentMessageRole Role { get; set; }

        public string Text { get; set; } = string.Empty;

        public List<AgentToolCall> ToolCalls { get; set; } = new();

        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public bool IsError { get; set; }

        /// <summary>
        /// Provider-owned JSON that must round-trip across tool turns. The host persists but
        /// never interprets this value.
        /// </summary>
        public string ProviderDataJson { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class AgentUsage
    {
        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long TotalTokens => InputTokens + OutputTokens;
    }

    public sealed class AgentSessionDocument
    {
        public const int CurrentSchemaVersion = 5;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Title { get; set; } = "New conversation";

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public string ProviderProfileId { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string ReasoningEffort { get; set; } = string.Empty;

        public AgentPermissionMode PermissionMode { get; set; } = AgentPermissionMode.ObserveOnly;

        public string SystemPrompt { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Opaque conversation identifier owned by a stateful HTTP model protocol.
        /// Stateless protocols leave this empty.
        /// </summary>
        public string ProviderThreadId { get; set; } = string.Empty;

        public AgentSessionState State { get; set; } = AgentSessionState.Idle;

        public List<AgentMessage> Messages { get; set; } = new();

        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Total number of messages physically removed from Messages and represented by Summary.
        /// Schema V3 never keeps the summarized prefix in Messages.
        /// </summary>
        public int SummarizedMessageCount { get; set; }

        /// <summary>
        /// Number of messages at the start of Messages represented by Summary for model requests.
        /// Unlike legacy SummarizedMessageCount, these messages remain in durable history.
        /// </summary>
        public int ContextSummaryMessageCount { get; set; }

        public int CompletedSteps { get; set; }

        public AgentUsage Usage { get; set; } = new();

        public string LastError { get; set; } = string.Empty;

        public AgentApprovalRequest? PendingApproval { get; set; }

        public bool IsPinned { get; set; }

        public bool IsArchived { get; set; }

        public int SortOrder { get; set; }

        /// <summary>Independent unsent composer text for this persisted conversation.</summary>
        public string Draft { get; set; } = string.Empty;
    }

    public sealed class AgentToolDescriptor
    {
        public AgentToolDescriptor(
            string name,
            string description,
            AgentToolAccess access,
            Dictionary<string, object?> parameters)
            : this(name, description, access,
                access == AgentToolAccess.ReadOnly ? AgentToolRisk.ReadOnly : AgentToolRisk.WorkspaceWrite,
                AgentToolSurface.All, false, parameters)
        {
        }

        public AgentToolDescriptor(
            string name,
            string description,
            AgentToolAccess access,
            AgentToolRisk risk,
            AgentToolSurface surfaces,
            bool parallelSafe,
            Dictionary<string, object?> parameters)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name is required.", nameof(name));
            if (!Enum.IsDefined(typeof(AgentToolAccess), access))
                throw new ArgumentOutOfRangeException(nameof(access), access, "Unknown Agent Tool access.");
            if (!Enum.IsDefined(typeof(AgentToolRisk), risk))
                throw new ArgumentOutOfRangeException(nameof(risk), risk, "Unknown Agent Tool risk.");
            if (surfaces == AgentToolSurface.None || (surfaces & ~AgentToolSurface.All) != 0)
                throw new ArgumentOutOfRangeException(nameof(surfaces), surfaces,
                    "Agent Tool must target Editor, Player, or both surfaces.");
            if (access == AgentToolAccess.ReadOnly && risk != AgentToolRisk.ReadOnly)
                throw new ArgumentException("A read-only Agent Tool must use ReadOnly risk.", nameof(risk));
            if (access != AgentToolAccess.ReadOnly && risk == AgentToolRisk.ReadOnly)
                throw new ArgumentException("A mutating Agent Tool cannot use ReadOnly risk.", nameof(risk));
            Name = name;
            Description = description ?? string.Empty;
            Access = access;
            Risk = risk;
            Surfaces = surfaces;
            ParallelSafe = parallelSafe;
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        }

        public string Name { get; }

        public string Description { get; }

        public AgentToolAccess Access { get; }

        public AgentToolRisk Risk { get; }

        public AgentToolSurface Surfaces { get; }

        public bool ParallelSafe { get; }

        public Dictionary<string, object?> Parameters { get; }
    }

    public sealed class AgentToolResult
    {
        public bool IsError { get; set; }

        public string Text { get; set; } = string.Empty;

        public static AgentToolResult Success(string text) => new() { Text = text ?? string.Empty };

        public static AgentToolResult Error(string text) => new() { IsError = true, Text = text ?? string.Empty };
    }

    public sealed class AgentToolContext
    {
        internal AgentToolContext(
            string sessionId,
            string workingDirectory,
            int defaultTimeoutSeconds,
            AgentPermissionMode permissionMode,
            AgentToolSurface surface)
        {
            SessionId = sessionId;
            WorkingDirectory = workingDirectory;
            DefaultTimeoutSeconds = Math.Max(1, defaultTimeoutSeconds);
            PermissionMode = permissionMode;
            Surface = surface;
        }

        public string SessionId { get; }

        public string WorkingDirectory { get; }

        public int DefaultTimeoutSeconds { get; }

        public AgentPermissionMode PermissionMode { get; }

        public AgentToolSurface Surface { get; }
    }

    public interface IAgentTool
    {
        AgentToolDescriptor Descriptor { get; }

        Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken);
    }

    public sealed class AgentModelRequest
    {
        public string SessionId { get; set; } = string.Empty;

        public string ProviderThreadId { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; } = string.Empty;

        public AgentPermissionMode PermissionMode { get; set; }

        public int DefaultToolTimeoutSeconds { get; set; }

        public string SystemPrompt { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string ReasoningEffort { get; set; } = string.Empty;

        public int MaxOutputTokens { get; set; }

        public IReadOnlyList<AgentMessage> Messages { get; set; } = Array.Empty<AgentMessage>();

        public IReadOnlyList<AgentToolDescriptor> Tools { get; set; } = Array.Empty<AgentToolDescriptor>();
    }

    public sealed class AgentModelResponse
    {
        public string Text { get; set; } = string.Empty;

        public string ProviderThreadId { get; set; } = string.Empty;

        public string ProviderDataJson { get; set; } = string.Empty;

        public List<AgentToolCall> ToolCalls { get; set; } = new();

        public AgentUsage Usage { get; set; } = new();

        public string FinishReason { get; set; } = string.Empty;
    }

    public sealed class AgentStreamEvent
    {
        public AgentStreamEvent(
            AgentStreamEventKind kind,
            string text = "",
            string callId = "",
            bool isError = false)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            CallId = callId ?? string.Empty;
            IsError = isError;
        }

        public AgentStreamEventKind Kind { get; }

        public string Text { get; }

        public string CallId { get; }

        public bool IsError { get; }
    }

    public sealed class AgentHostStreamEvent
    {
        public AgentHostStreamEvent(string sessionId, AgentStreamEvent streamEvent)
        {
            SessionId = string.IsNullOrWhiteSpace(sessionId)
                ? throw new ArgumentException("Session id is required.", nameof(sessionId))
                : sessionId;
            StreamEvent = streamEvent ?? throw new ArgumentNullException(nameof(streamEvent));
        }

        public string SessionId { get; }

        public AgentStreamEvent StreamEvent { get; }
    }

    public sealed class AgentTurnResult
    {
        public AgentTurnResult(string sessionId, AgentSessionState state, string error, AgentUsage usage)
        {
            SessionId = sessionId ?? string.Empty;
            State = state;
            Error = error ?? string.Empty;
            Usage = usage ?? throw new ArgumentNullException(nameof(usage));
        }

        public string SessionId { get; }

        public AgentSessionState State { get; }

        public string Error { get; }

        public AgentUsage Usage { get; }

        public bool IsSuccess => State == AgentSessionState.Completed;
    }

    public interface IAgentModelProvider
    {
        Task<AgentModelResponse> CompleteAsync(
            AgentProviderProfile profile,
            AgentModelRequest request,
            Action<AgentStreamEvent>? onEvent,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<string>> ListModelsAsync(
            AgentProviderProfile profile,
            CancellationToken cancellationToken);
    }

}
