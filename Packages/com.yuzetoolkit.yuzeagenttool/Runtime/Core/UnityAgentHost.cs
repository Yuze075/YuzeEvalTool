#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit.Agent
{
    public sealed class UnityAgentHost : IDisposable
    {
        private static readonly object StaticSyncRoot = new();
        private static UnityAgentHost? _default;

        private readonly object _syncRoot = new();
        private readonly Dictionary<string, AgentSessionRuntime> _sessions = new(StringComparer.Ordinal);
        private readonly IAgentStore _store;
        private readonly bool _ownsStore;
        private readonly IAgentModelProvider _provider;
        private readonly bool _ownsProvider;
        private readonly AgentToolRegistry _tools = new();
        private readonly AgentApprovalService _approvals = new();
        private readonly AgentInstructionService _instructions = new();
        private readonly AgentUnityEvalService _evalService = new();
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private readonly SemaphoreSlim _initializeGate = new(1, 1);
        private readonly SemaphoreSlim _settingsMutationGate = new(1, 1);
        private readonly object _operationSyncRoot = new();
        private readonly object _turnAdmissionSyncRoot = new();
        private AgentSettingsDocument _settings = new();
        private AgentLoop? _loop;
        private bool _initialized;
        private bool _disposed;
        private bool _turnAdmissionClosed;
        private int _activeOperationCount;
        private int _activeTurnCount;
        private TaskCompletionSource<bool> _noActiveOperations = CompletedSignal();
        private TaskCompletionSource<bool> _noActiveTurns = CompletedSignal();
        private TaskCompletionSource<bool> _turnAdmissionOpened = CompletedSignal();
        private long _revision;

        public UnityAgentHost()
        {
            AgentPaths.CaptureUnityPathSnapshot();
            _store = new FileAgentStore(FileAgentStore.GetDefaultRootPath());
            _ownsStore = true;
            RegisterBuiltInTools();
            _provider = new HttpAgentModelProvider();
            _ownsProvider = true;
            _approvals.Changed += MarkChanged;
        }

        public UnityAgentHost(
            IAgentStore store,
            IAgentModelProvider provider)
        {
            AgentPaths.CaptureUnityPathSnapshot();
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            RegisterBuiltInTools();
            _approvals.Changed += MarkChanged;
        }

        public static UnityAgentHost Default
        {
            get
            {
                lock (StaticSyncRoot)
                    return _default ??= new UnityAgentHost();
            }
        }

        public long Revision
        {
            get
            {
                ThrowIfDisposed();
                return Interlocked.Read(ref _revision);
            }
        }

        public event Action<AgentHostStreamEvent>? StreamEvent;

        public AgentApprovalService Approvals
        {
            get
            {
                ThrowIfDisposed();
                return _approvals;
            }
        }

        /// <summary>
        /// Registry used by the built-in Yuze Agent Tool loop.
        /// Custom debug tools may be registered at runtime; duplicate names fail explicitly.
        /// </summary>
        public AgentToolRegistry Tools
        {
            get
            {
                ThrowIfDisposed();
                return _tools;
            }
        }

        public AgentSettingsDocument Settings
        {
            get
            {
                ThrowIfDisposed();
                lock (_syncRoot) return AgentDocumentCodec.Clone(_settings);
            }
        }

        public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
        {
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            await EnsureInitializedCoreAsync(linkedCancellation.Token).ConfigureAwait(false);
        }

        private async Task EnsureInitializedCoreAsync(CancellationToken cancellationToken)
        {
            lock (_syncRoot)
            {
                if (_initialized) return;
            }
            await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_syncRoot)
                {
                    if (_initialized) return;
                }
                await _settingsMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (_syncRoot)
                    {
                        if (_initialized) return;
                    }
                    var settings = await _store.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
                    ValidateSettings(settings);
                    var sessions = await _store.LoadSessionsAsync(cancellationToken).ConfigureAwait(false);
                    var repairedSessions = new List<AgentSessionDocument>();
                    var profileIds = new HashSet<string>(settings.ProviderProfiles.Select(profile => profile.Id),
                        StringComparer.Ordinal);
                    var projectRoot = AgentPaths.ProjectRoot;
                    foreach (var document in sessions)
                    {
                        var repaired = false;
                        var closedCalls = AgentConversationIntegrity.CloseIncompleteToolCalls(document,
                            "Tool call was not completed because the previous Unity domain or process ended.");
                        if (document.State is AgentSessionState.Running or AgentSessionState.AwaitingApproval ||
                            closedCalls > 0)
                        {
                            document.State = AgentSessionState.Interrupted;
                            document.PendingApproval = null;
                            document.LastError =
                                "The previous Unity domain or process ended while this conversation was active.";
                            repaired = true;
                        }
                        if (!profileIds.Contains(document.ProviderProfileId))
                        {
                            var profile = settings.ProviderProfiles.First(value =>
                                value.Id == settings.DefaultProviderProfileId);
                            document.ProviderProfileId = profile.Id;
                            document.Model = profile.Model;
                            document.ReasoningEffort = profile.ReasoningEffort;
                            document.ProviderThreadId = string.Empty;
                            repaired = true;
                        }
                        if (document.SortOrder < 0)
                        {
                            document.SortOrder = 0;
                            repaired = true;
                        }
                        if (!string.IsNullOrEmpty(document.SystemPrompt))
                        {
                            document.SystemPrompt = string.Empty;
                            document.ProviderThreadId = string.Empty;
                            repaired = true;
                        }
                        if (!AgentPaths.PathsEqual(
                                string.IsNullOrWhiteSpace(document.WorkingDirectory)
                                    ? projectRoot
                                    : document.WorkingDirectory,
                                projectRoot))
                        {
                            document.WorkingDirectory = projectRoot;
                            document.ProviderThreadId = string.Empty;
                            repaired = true;
                        }
                        else if (!string.Equals(document.WorkingDirectory, projectRoot, StringComparison.Ordinal))
                        {
                            document.WorkingDirectory = projectRoot;
                            repaired = true;
                        }
                        if (repaired) repairedSessions.Add(document);
                    }
                    var createdRuntimes = new List<AgentSessionRuntime>();
                    try
                    {
                        foreach (var document in repairedSessions)
                            await _store.SaveSessionAsync(document, cancellationToken).ConfigureAwait(false);
                        lock (_syncRoot)
                        {
                            _settings = settings;
                            foreach (var document in sessions)
                            {
                                var runtime = new AgentSessionRuntime(document);
                                _sessions[document.Id] = runtime;
                                createdRuntimes.Add(runtime);
                            }
                            _loop = new AgentLoop(_tools, _approvals, _instructions, _provider);
                            _initialized = true;
                        }
                    }
                    catch
                    {
                        lock (_syncRoot)
                        {
                            foreach (var runtime in createdRuntimes)
                                _sessions.Remove(runtime.Document.Id);
                        }
                        foreach (var runtime in createdRuntimes) runtime.Dispose();
                        throw;
                    }
                    MarkChanged();
                }
                finally
                {
                    _settingsMutationGate.Release();
                }
            }
            finally
            {
                _initializeGate.Release();
            }
        }

        public IReadOnlyList<AgentSessionDocument> GetSessions()
        {
            using var operation = EnterOperation();
            lock (_syncRoot)
            {
                return _sessions.Values.Select(runtime =>
                    {
                        lock (runtime.SyncRoot) return AgentDocumentCodec.Clone(runtime.Document);
                    })
                    .OrderBy(session => session.IsArchived)
                    .ThenByDescending(session => session.IsPinned)
                    .ThenBy(session => session.SortOrder)
                    .ThenByDescending(session => session.UpdatedAtUtc)
                    .ToList();
            }
        }

        public AgentSessionDocument? GetSession(string sessionId)
        {
            using var operation = EnterOperation();
            AgentSessionRuntime? runtime;
            lock (_syncRoot) _sessions.TryGetValue(sessionId, out runtime);
            if (runtime == null) return null;
            lock (runtime.SyncRoot)
            {
                var clone = AgentDocumentCodec.Clone(runtime.Document);
                if (!string.IsNullOrEmpty(runtime.LiveText))
                {
                    clone.Messages.Add(new AgentMessage
                    {
                        Role = AgentMessageRole.Assistant,
                        Text = runtime.LiveText
                    });
                }
                return clone;
            }
        }

        public async Task<AgentSessionDocument> CreateSessionAsync(CancellationToken cancellationToken = default)
        {
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            var token = linkedCancellation.Token;
            await EnsureInitializedCoreAsync(token).ConfigureAwait(false);
            await _settingsMutationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                AgentSettingsDocument settings;
                int sortOrder;
                lock (_syncRoot)
                {
                    settings = AgentDocumentCodec.Clone(_settings);
                    sortOrder = _sessions.Values
                        .Select(runtime => runtime.Document.SortOrder)
                        .DefaultIfEmpty(-1)
                        .Max() + 1;
                }
                var profile = settings.ProviderProfiles.First(profile => profile.Id == settings.DefaultProviderProfileId);
                var document = new AgentSessionDocument
                {
                    ProviderProfileId = profile.Id,
                    Model = profile.Model,
                    ReasoningEffort = profile.ReasoningEffort,
                    PermissionMode = AgentPaths.IsEditor
                        ? settings.PermissionMode
                        : AgentPermissionMode.ObserveOnly,
                    WorkingDirectory = AgentPaths.IsEditor
                        ? AgentPaths.ProjectRoot
                        : AgentPaths.GetBasePath(AgentPathBase.PersistentData),
                    SortOrder = sortOrder
                };
                var runtime = new AgentSessionRuntime(document);
                lock (_syncRoot) _sessions.Add(document.Id, runtime);
                try
                {
                    await _store.SaveSessionAsync(document, token).ConfigureAwait(false);
                }
                catch
                {
                    lock (_syncRoot)
                    {
                        if (_sessions.TryGetValue(document.Id, out var current) && ReferenceEquals(current, runtime))
                            _sessions.Remove(document.Id);
                    }
                    runtime.Dispose();
                    throw;
                }
                MarkChanged();
                return AgentDocumentCodec.Clone(document);
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        public async Task<AgentTurnResult> SendMessageAsync(
            string sessionId,
            string text,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Message text is required.", nameof(text));
            if (text.Length > 1_000_000)
                throw new ArgumentException("Message text cannot exceed 1,000,000 characters.", nameof(text));
            using var operation = EnterOperation();
            CancellationTokenSource? turnCancellation = null;
            AgentSessionRuntime runtime;
            var token = cancellationToken;
            using (var admissionCancellation = CreateOperationCancellation(cancellationToken))
            {
                token = admissionCancellation.Token;
                await EnsureInitializedCoreAsync(token).ConfigureAwait(false);
                runtime = GetRuntime(sessionId);
            }
            // Do not retain a token whose linked source was just disposed. Turn admission creates
            // its own lifetime-linked source below; this zero-timeout gate check only needs to
            // observe the caller cancellation in the intervening synchronous section.
            token = cancellationToken;
            lock (runtime.SyncRoot)
            {
                if (runtime.IsDeleting)
                    throw new InvalidOperationException("This conversation is being deleted.");
            }
            if (!await runtime.TurnGate.WaitAsync(0, token).ConfigureAwait(false))
                throw new InvalidOperationException("This conversation already has an active turn.");
            long startingInputTokens;
            long startingOutputTokens;
            lock (runtime.SyncRoot)
            {
                startingInputTokens = runtime.Document.Usage.InputTokens;
                startingOutputTokens = runtime.Document.Usage.OutputTokens;
            }
            var turnAdmitted = false;
            try
            {
                using (var admissionCancellation = CreateOperationCancellation(cancellationToken))
                {
                    await EnterTurnAdmissionAsync(admissionCancellation.Token).ConfigureAwait(false);
                    turnAdmitted = true;
                    turnCancellation = CreateOperationCancellation(cancellationToken);
                    token = turnCancellation.Token;
                }
                lock (runtime.SyncRoot)
                {
                    if (runtime.IsDeleting)
                        throw new InvalidOperationException("This conversation is being deleted.");
                    runtime.ActiveCancellation = turnCancellation;
                    runtime.Document.Messages.Add(new AgentMessage
                    {
                        Role = AgentMessageRole.User,
                        Text = text.Trim()
                    });
                    runtime.Document.Draft = string.Empty;
                    if (runtime.Document.Messages.Count == 1)
                        runtime.Document.Title = CreateTitle(text);
                    runtime.Document.State = AgentSessionState.Running;
                    runtime.Document.LastError = string.Empty;
                    runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                }
                MarkChanged();
                await SaveRuntimeAsync(runtime, token).ConfigureAwait(false);
                AgentSettingsDocument settings;
                AgentProviderProfile profile;
                lock (_syncRoot)
                {
                    settings = AgentDocumentCodec.Clone(_settings);
                    profile = settings.ProviderProfiles.FirstOrDefault(value => value.Id == runtime.Document.ProviderProfileId)
                              ?? throw new InvalidOperationException(
                                  $"Provider profile '{runtime.Document.ProviderProfileId}' no longer exists.");
                }
                var loop = _loop ?? throw new InvalidOperationException("Agent loop is not initialized.");
                await loop.RunAsync(runtime, settings, profile,
                    () => SaveRuntimeAsync(runtime, token),
                    MarkChanged,
                    value => PublishStreamEvent(runtime.Document.Id, value),
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var deleting = false;
                lock (runtime.SyncRoot)
                {
                    deleting = runtime.IsDeleting;
                    if (!deleting)
                    {
                        var interruption = string.IsNullOrWhiteSpace(runtime.InterruptionMessage)
                            ? "Agent turn was stopped."
                            : runtime.InterruptionMessage;
                        AgentConversationIntegrity.CloseIncompleteToolCalls(runtime.Document,
                            "Tool call was not completed because " + interruption.TrimEnd('.') + ".");
                        runtime.Document.State = AgentSessionState.Interrupted;
                        runtime.Document.PendingApproval = null;
                        runtime.Document.LastError = interruption;
                        runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }
                if (!deleting)
                {
                    MarkChanged();
                    await SaveRuntimeAsync(runtime, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    throw;
                }
            }
            catch (Exception exception)
            {
                var deleting = false;
                lock (runtime.SyncRoot)
                {
                    deleting = runtime.IsDeleting;
                    if (!deleting)
                    {
                        AgentConversationIntegrity.CloseIncompleteToolCalls(runtime.Document,
                            "Tool call was not completed because the Agent turn failed: " + exception.Message);
                        runtime.Document.State = AgentSessionState.Failed;
                        runtime.Document.PendingApproval = null;
                        runtime.Document.LastError = exception.Message;
                        runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }
                if (!deleting)
                {
                    MarkChanged();
                    await SaveRuntimeAsync(runtime, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    throw;
                }
                PublishStreamEvent(runtime.Document.Id,
                    new AgentStreamEvent(AgentStreamEventKind.RunFailed, exception.Message, isError: true));
            }
            finally
            {
                lock (runtime.SyncRoot)
                {
                    if (ReferenceEquals(runtime.ActiveCancellation, turnCancellation))
                        runtime.ActiveCancellation = null;
                    runtime.InterruptionMessage = string.Empty;
                }
                turnCancellation?.Dispose();
                if (turnAdmitted) ExitTurnAdmission();
                runtime.TurnGate.Release();
            }
            return CompleteTurn(runtime, startingInputTokens, startingOutputTokens);
        }

        public void StopSession(string sessionId)
        {
            using var operation = EnterOperation();
            var runtime = GetRuntime(sessionId);
            lock (runtime.SyncRoot) runtime.ActiveCancellation?.Cancel();
            _approvals.CancelSession(sessionId);
        }

        /// <summary>
        /// Captures active Editor conversations before a compilation marker is written. The caller
        /// must persist that marker before requesting interruption so Domain Reload cannot lose intent.
        /// </summary>
        public IReadOnlyList<string> GetActiveSessionIdsForEditorCompilation()
        {
            using var operation = EnterOperation();
            if (!AgentPaths.IsEditor) return Array.Empty<string>();
            lock (_syncRoot)
            {
                if (!_initialized) return Array.Empty<string>();
                var result = new List<string>();
                foreach (var runtime in _sessions.Values)
                {
                    lock (runtime.SyncRoot)
                    {
                        if (runtime.ActiveCancellation != null &&
                            runtime.Document.State is AgentSessionState.Running or AgentSessionState.AwaitingApproval)
                            result.Add(runtime.Document.Id);
                    }
                }
                return result;
            }
        }

        /// <summary>Interrupts only the previously captured conversations that are still active.</summary>
        public IReadOnlyList<string> InterruptSessionsForEditorCompilation(
            IReadOnlyList<string> sessionIds,
            string interruptionMessage)
        {
            if (sessionIds == null) throw new ArgumentNullException(nameof(sessionIds));
            if (string.IsNullOrWhiteSpace(interruptionMessage))
                throw new ArgumentException("Compilation interruption message is required.",
                    nameof(interruptionMessage));
            using var operation = EnterOperation();
            if (!AgentPaths.IsEditor)
                throw new InvalidOperationException("Editor compilation recovery is unavailable in a Player.");
            var cancellations = new List<(string Id, CancellationTokenSource Cancellation)>();
            lock (_syncRoot)
            {
                foreach (var id in sessionIds.Distinct(StringComparer.Ordinal))
                {
                    if (!_sessions.TryGetValue(id, out var runtime)) continue;
                    lock (runtime.SyncRoot)
                    {
                        if (runtime.ActiveCancellation == null ||
                            runtime.Document.State is not (AgentSessionState.Running or
                                AgentSessionState.AwaitingApproval)) continue;
                        runtime.InterruptionMessage = interruptionMessage;
                        cancellations.Add((id, runtime.ActiveCancellation));
                    }
                }
            }
            foreach (var value in cancellations)
            {
                value.Cancellation.Cancel();
                _approvals.CancelSession(value.Id);
            }
            return cancellations.Select(value => value.Id).ToList();
        }

        public async Task WaitForSessionIdleAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            await EnsureInitializedCoreAsync(linkedCancellation.Token).ConfigureAwait(false);
            var runtime = GetRuntime(sessionId);
            await runtime.TurnGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            runtime.TurnGate.Release();
        }

        public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            var token = linkedCancellation.Token;
            await EnsureInitializedCoreAsync(token).ConfigureAwait(false);
            await _settingsMutationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                AgentSessionRuntime? runtime;
                lock (_syncRoot) _sessions.TryGetValue(sessionId, out runtime);
                if (runtime == null) return;

                lock (runtime.SyncRoot)
                {
                    if (runtime.IsDeleting) return;
                    runtime.IsDeleting = true;
                    runtime.ActiveCancellation?.Cancel();
                }
                _approvals.CancelSession(sessionId);
                var gateHeld = false;
                var deleted = false;
                try
                {
                    await runtime.TurnGate.WaitAsync(token).ConfigureAwait(false);
                    gateHeld = true;
                    lock (_syncRoot)
                    {
                        if (!_sessions.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, runtime))
                            return;
                    }
                    await _store.DeleteSessionAsync(sessionId, token).ConfigureAwait(false);
                    lock (_syncRoot)
                    {
                        if (_sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, runtime))
                            _sessions.Remove(sessionId);
                    }
                    deleted = true;
                }
                finally
                {
                    if (gateHeld) runtime.TurnGate.Release();
                    if (!deleted)
                    {
                        lock (runtime.SyncRoot) runtime.IsDeleting = false;
                    }
                }
                if (!deleted) return;
                _evalService.ReleaseSession(sessionId);
                runtime.Dispose();
                MarkChanged();
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        public async Task UpdateSessionAsync(
            string sessionId,
            string providerProfileId,
            string model,
            string reasoningEffort,
            AgentPermissionMode permissionMode,
            CancellationToken cancellationToken = default)
        {
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            var token = linkedCancellation.Token;
            await EnsureInitializedCoreAsync(token).ConfigureAwait(false);
            if (!Enum.IsDefined(typeof(AgentPermissionMode), permissionMode))
                throw new ArgumentOutOfRangeException(nameof(permissionMode), permissionMode,
                    "Unknown Agent permission mode.");
            if (string.IsNullOrWhiteSpace(providerProfileId))
                throw new ArgumentException("Provider profile id is required.", nameof(providerProfileId));
            await _settingsMutationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (_syncRoot)
                {
                    if (_settings.ProviderProfiles.All(profile =>
                            !string.Equals(profile.Id, providerProfileId, StringComparison.Ordinal)))
                        throw new KeyNotFoundException($"Provider profile '{providerProfileId}' does not exist.");
                }
                var runtime = GetRuntime(sessionId);
                if (!await runtime.TurnGate.WaitAsync(0, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Stop the active turn before changing conversation settings.");
                try
                {
                    var projectRoot = AgentPaths.ProjectRoot;
                    lock (runtime.SyncRoot)
                    {
                        if (runtime.IsDeleting)
                            throw new InvalidOperationException("This conversation is being deleted.");
                        if (!string.Equals(runtime.Document.ProviderProfileId, providerProfileId, StringComparison.Ordinal) ||
                            !string.Equals(runtime.Document.Model, model ?? string.Empty, StringComparison.Ordinal) ||
                            !string.Equals(runtime.Document.ReasoningEffort, reasoningEffort ?? string.Empty,
                                StringComparison.Ordinal))
                            runtime.Document.ProviderThreadId = string.Empty;
                        runtime.Document.ProviderProfileId = providerProfileId;
                        runtime.Document.Model = model ?? string.Empty;
                        runtime.Document.ReasoningEffort = reasoningEffort ?? string.Empty;
                        runtime.Document.PermissionMode = permissionMode;
                        if (!string.Equals(runtime.Document.WorkingDirectory, projectRoot, StringComparison.Ordinal))
                        {
                            runtime.Document.WorkingDirectory = projectRoot;
                            runtime.Document.ProviderThreadId = string.Empty;
                        }
                        runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                    }
                    await SaveRuntimeAsync(runtime, token).ConfigureAwait(false);
                    MarkChanged();
                }
                finally
                {
                    runtime.TurnGate.Release();
                }
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        [Obsolete("Conversation workspaces are fixed to the current Unity project root.")]
        public Task UpdateSessionAsync(
            string sessionId,
            string providerProfileId,
            string model,
            string reasoningEffort,
            AgentPermissionMode permissionMode,
            CancellationToken cancellationToken,
            string? workingDirectory)
        {
            ThrowIfDisposed();
            if (!string.IsNullOrWhiteSpace(workingDirectory) &&
                !AgentPaths.PathsEqual(workingDirectory, AgentPaths.ProjectRoot))
                throw new InvalidOperationException(
                    "Conversation workspaces are fixed to the current Unity project root.");
            return UpdateSessionAsync(sessionId, providerProfileId, model, reasoningEffort, permissionMode,
                cancellationToken);
        }

        [Obsolete("The system prompt is global and can only be changed through Agent settings.")]
        public Task UpdateSessionSystemPromptAsync(
            string sessionId,
            string systemPrompt,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            throw new NotSupportedException(
                "Conversation-specific system prompts are disabled. Change the global prompt in Agent settings.");
        }

        public async Task UpdateSessionOrganizationAsync(
            string sessionId,
            bool isPinned,
            bool isArchived,
            int sortOrder,
            CancellationToken cancellationToken = default)
        {
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            var token = linkedCancellation.Token;
            await EnsureInitializedCoreAsync(token).ConfigureAwait(false);
            if (sortOrder < 0) throw new ArgumentOutOfRangeException(nameof(sortOrder));
            await _settingsMutationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var runtime = GetRuntime(sessionId);
                if (!await runtime.TurnGate.WaitAsync(0, token).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Stop the active turn before changing conversation organization.");
                try
                {
                    lock (runtime.SyncRoot)
                    {
                        if (runtime.IsDeleting)
                            throw new InvalidOperationException("This conversation is being deleted.");
                        runtime.Document.IsPinned = isPinned;
                        runtime.Document.IsArchived = isArchived;
                        runtime.Document.SortOrder = sortOrder;
                        runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                    }
                    await SaveRuntimeAsync(runtime, token).ConfigureAwait(false);
                    MarkChanged();
                }
                finally
                {
                    runtime.TurnGate.Release();
                }
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        public async Task UpdateSessionDraftAsync(
            string sessionId,
            string draft,
            CancellationToken cancellationToken = default)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (draft.Length > 1_000_000)
                throw new ArgumentException("Conversation draft cannot exceed 1,000,000 characters.", nameof(draft));
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            var token = linkedCancellation.Token;
            await EnsureInitializedCoreAsync(token).ConfigureAwait(false);
            var runtime = GetRuntime(sessionId);
            await _settingsMutationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (runtime.SyncRoot)
                {
                    if (runtime.IsDeleting)
                        throw new InvalidOperationException("This conversation is being deleted.");
                    if (string.Equals(runtime.Document.Draft, draft, StringComparison.Ordinal)) return;
                    runtime.Document.Draft = draft;
                }
                await SaveRuntimeAsync(runtime, token).ConfigureAwait(false);
                MarkChanged();
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        public async Task SaveSettingsAsync(
            AgentSettingsDocument settings,
            CancellationToken cancellationToken = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            var token = linkedCancellation.Token;
            await EnsureInitializedCoreAsync(token).ConfigureAwait(false);
            ValidateSettings(settings);
            var normalized = AgentDocumentCodec.Clone(settings);
            normalized.DefaultToolTimeoutSeconds = Math.Max(1, normalized.DefaultToolTimeoutSeconds);
            normalized.MaximumAgentSteps = Math.Max(1, normalized.MaximumAgentSteps);
            foreach (var profile in normalized.ProviderProfiles)
            {
                profile.MaxOutputTokens = Math.Max(1, profile.MaxOutputTokens);
                profile.ContextWindowTokens = Math.Max(8_192, profile.ContextWindowTokens);
            }
            ValidateSettings(normalized);
            await _settingsMutationGate.WaitAsync(token).ConfigureAwait(false);
            var admissionClosed = false;
            try
            {
                var noActiveTurns = CloseTurnAdmission();
                admissionClosed = true;
                await WaitWithCancellationAsync(noActiveTurns, token).ConfigureAwait(false);
                AgentSettingsDocument previous;
                lock (_syncRoot) previous = AgentDocumentCodec.Clone(_settings);
                var resetProviderThreads = SettingsAffectConversationContext(previous, normalized);
                await _store.SaveSettingsAsync(normalized, token).ConfigureAwait(false);
                var validProfiles = new HashSet<string>(normalized.ProviderProfiles.Select(profile => profile.Id),
                    StringComparer.Ordinal);
                var defaultProfile = normalized.ProviderProfiles.First(profile =>
                    profile.Id == normalized.DefaultProviderProfileId);
                var changedSessions = new List<AgentSessionRuntime>();
                lock (_syncRoot)
                {
                    _settings = AgentDocumentCodec.Clone(normalized);
                    foreach (var runtime in _sessions.Values)
                    {
                        var changed = false;
                        lock (runtime.SyncRoot)
                        {
                            if (!validProfiles.Contains(runtime.Document.ProviderProfileId))
                            {
                                runtime.Document.ProviderProfileId = defaultProfile.Id;
                                runtime.Document.Model = defaultProfile.Model;
                                runtime.Document.ReasoningEffort = defaultProfile.ReasoningEffort;
                                runtime.Document.ProviderThreadId = string.Empty;
                                changed = true;
                            }
                            if (!string.IsNullOrEmpty(runtime.Document.SystemPrompt))
                            {
                                runtime.Document.SystemPrompt = string.Empty;
                                changed = true;
                            }
                            if (!string.Equals(runtime.Document.WorkingDirectory, AgentPaths.ProjectRoot,
                                    StringComparison.Ordinal))
                            {
                                runtime.Document.WorkingDirectory = AgentPaths.ProjectRoot;
                                runtime.Document.ProviderThreadId = string.Empty;
                                changed = true;
                            }
                            if (resetProviderThreads && !string.IsNullOrEmpty(runtime.Document.ProviderThreadId))
                            {
                                runtime.Document.ProviderThreadId = string.Empty;
                                changed = true;
                            }
                        }
                        if (changed) changedSessions.Add(runtime);
                    }
                }
                foreach (var runtime in changedSessions)
                    await SaveRuntimeAsync(runtime, token).ConfigureAwait(false);
                await MergeStoredSessionsAsync(normalized, resetProviderThreads, token)
                    .ConfigureAwait(false);
                MarkChanged();
            }
            finally
            {
                if (admissionClosed) OpenTurnAdmission();
                _settingsMutationGate.Release();
            }
        }

        public async Task ReloadSettingsFromDiskAsync(CancellationToken cancellationToken = default)
        {
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            var token = linkedCancellation.Token;
            await EnsureInitializedCoreAsync(token).ConfigureAwait(false);
            await _settingsMutationGate.WaitAsync(token).ConfigureAwait(false);
            var admissionClosed = false;
            try
            {
                var noActiveTurns = CloseTurnAdmission();
                admissionClosed = true;
                await WaitWithCancellationAsync(noActiveTurns, token).ConfigureAwait(false);

                var reloaded = await _store.LoadSettingsAsync(token).ConfigureAwait(false);
                ValidateSettings(reloaded);
                var normalized = AgentDocumentCodec.Clone(reloaded);
                normalized.DefaultToolTimeoutSeconds = Math.Max(1, normalized.DefaultToolTimeoutSeconds);
                normalized.MaximumAgentSteps = Math.Max(1, normalized.MaximumAgentSteps);
                foreach (var profile in normalized.ProviderProfiles)
                {
                    profile.MaxOutputTokens = Math.Max(1, profile.MaxOutputTokens);
                    profile.ContextWindowTokens = Math.Max(8_192, profile.ContextWindowTokens);
                }
                ValidateSettings(normalized);

                AgentSettingsDocument previous;
                lock (_syncRoot) previous = AgentDocumentCodec.Clone(_settings);
                var resetProviderThreads = SettingsAffectConversationContext(previous, normalized);
                var validProfiles = new HashSet<string>(normalized.ProviderProfiles.Select(profile => profile.Id),
                    StringComparer.Ordinal);
                var defaultProfile = normalized.ProviderProfiles.First(profile =>
                    profile.Id == normalized.DefaultProviderProfileId);
                var changedSessions = new List<AgentSessionRuntime>();
                lock (_syncRoot)
                {
                    _settings = AgentDocumentCodec.Clone(normalized);
                    foreach (var runtime in _sessions.Values)
                    {
                        var changed = false;
                        lock (runtime.SyncRoot)
                        {
                            if (!validProfiles.Contains(runtime.Document.ProviderProfileId))
                            {
                                runtime.Document.ProviderProfileId = defaultProfile.Id;
                                runtime.Document.Model = defaultProfile.Model;
                                runtime.Document.ReasoningEffort = defaultProfile.ReasoningEffort;
                                runtime.Document.ProviderThreadId = string.Empty;
                                changed = true;
                            }
                            if (!string.IsNullOrEmpty(runtime.Document.SystemPrompt))
                            {
                                runtime.Document.SystemPrompt = string.Empty;
                                changed = true;
                            }
                            if (!string.Equals(runtime.Document.WorkingDirectory, AgentPaths.ProjectRoot,
                                    StringComparison.Ordinal))
                            {
                                runtime.Document.WorkingDirectory = AgentPaths.ProjectRoot;
                                runtime.Document.ProviderThreadId = string.Empty;
                                changed = true;
                            }
                            if (resetProviderThreads && !string.IsNullOrEmpty(runtime.Document.ProviderThreadId))
                            {
                                runtime.Document.ProviderThreadId = string.Empty;
                                changed = true;
                            }
                        }
                        if (changed) changedSessions.Add(runtime);
                    }
                }
                foreach (var runtime in changedSessions)
                    await SaveRuntimeAsync(runtime, token).ConfigureAwait(false);
                await MergeStoredSessionsAsync(normalized, resetProviderThreads, token)
                    .ConfigureAwait(false);
                MarkChanged();
            }
            finally
            {
                if (admissionClosed) OpenTurnAdmission();
                _settingsMutationGate.Release();
            }
        }

        /// <summary>
        /// A history location may already contain conversations that are not present in the
        /// currently open workspace. Switching or externally reloading that location must expose
        /// them immediately, while preserving the in-memory copy of conversations already open.
        /// </summary>
        private async Task MergeStoredSessionsAsync(
            AgentSettingsDocument settings,
            bool resetProviderThreads,
            CancellationToken cancellationToken)
        {
            var storedSessions = await _store.LoadSessionsAsync(cancellationToken).ConfigureAwait(false);
            var profileIds = new HashSet<string>(settings.ProviderProfiles.Select(profile => profile.Id),
                StringComparer.Ordinal);
            var defaultProfile = settings.ProviderProfiles.First(profile =>
                profile.Id == settings.DefaultProviderProfileId);
            var addedSessions = new List<(string Id, AgentSessionRuntime Runtime)>();

            try
            {
                foreach (var document in storedSessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_syncRoot)
                    {
                        if (_sessions.ContainsKey(document.Id)) continue;
                    }

                    var repaired = false;
                    var closedCalls = AgentConversationIntegrity.CloseIncompleteToolCalls(document,
                        "Tool call was not completed because the previous Unity domain or process ended.");
                    if (document.State is AgentSessionState.Running or AgentSessionState.AwaitingApproval ||
                        closedCalls > 0)
                    {
                        document.State = AgentSessionState.Interrupted;
                        document.PendingApproval = null;
                        document.LastError =
                            "The previous Unity domain or process ended while this conversation was active.";
                        repaired = true;
                    }
                    if (!profileIds.Contains(document.ProviderProfileId))
                    {
                        document.ProviderProfileId = defaultProfile.Id;
                        document.Model = defaultProfile.Model;
                        document.ReasoningEffort = defaultProfile.ReasoningEffort;
                        document.ProviderThreadId = string.Empty;
                        repaired = true;
                    }
                    if (document.SortOrder < 0)
                    {
                        document.SortOrder = 0;
                        repaired = true;
                    }
                    if (!string.IsNullOrEmpty(document.SystemPrompt))
                    {
                        document.SystemPrompt = string.Empty;
                        document.ProviderThreadId = string.Empty;
                        repaired = true;
                    }
                    if (!string.Equals(document.WorkingDirectory, AgentPaths.ProjectRoot, StringComparison.Ordinal))
                    {
                        document.WorkingDirectory = AgentPaths.ProjectRoot;
                        document.ProviderThreadId = string.Empty;
                        repaired = true;
                    }
                    if (resetProviderThreads && !string.IsNullOrEmpty(document.ProviderThreadId))
                    {
                        document.ProviderThreadId = string.Empty;
                        repaired = true;
                    }
                    if (repaired)
                        await _store.SaveSessionAsync(document, cancellationToken).ConfigureAwait(false);

                    lock (_syncRoot)
                    {
                        if (_sessions.ContainsKey(document.Id)) continue;
                        var runtime = new AgentSessionRuntime(document);
                        _sessions.Add(document.Id, runtime);
                        addedSessions.Add((document.Id, runtime));
                    }
                }
            }
            catch
            {
                foreach (var added in addedSessions)
                {
                    lock (_syncRoot)
                    {
                        if (_sessions.TryGetValue(added.Id, out var current) && ReferenceEquals(current, added.Runtime))
                            _sessions.Remove(added.Id);
                    }
                    added.Runtime.Dispose();
                }
                throw;
            }
        }

        public async Task<IReadOnlyList<string>> ListModelsAsync(
            AgentProviderProfile profile,
            CancellationToken cancellationToken = default)
        {
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            return await _provider.ListModelsAsync(profile, linkedCancellation.Token).ConfigureAwait(false);
        }

        public async Task<AgentModelDiscoveryResult> DiscoverModelsAsync(
            AgentProviderProfile profile,
            CancellationToken cancellationToken = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            using var operation = EnterOperation();
            using var linkedCancellation = CreateOperationCancellation(cancellationToken);
            if (_provider is HttpAgentModelProvider http)
                return await http.DiscoverModelsAsync(profile, linkedCancellation.Token).ConfigureAwait(false);
            return await DiscoverCustomProviderModelsAsync(profile, linkedCancellation.Token).ConfigureAwait(false);
        }

        private async Task<AgentModelDiscoveryResult> DiscoverCustomProviderModelsAsync(
            AgentProviderProfile profile,
            CancellationToken cancellationToken)
        {
            try
            {
                var remote = await _provider.ListModelsAsync(profile, cancellationToken).ConfigureAwait(false);
                return AgentProviderCatalog.MergeRemoteModels(profile, remote);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return AgentProviderCatalog.CuratedResult(profile,
                    AgentModelDiscoverySource.CuratedFallback, exception.Message);
            }
        }

        public bool ResolveApproval(string approvalId, bool approved)
        {
            using var operation = EnterOperation();
            return _approvals.Resolve(approvalId, approved);
        }

        public void Dispose()
        {
            Task operationsCompleted;
            lock (_operationSyncRoot)
            {
                if (_disposed) return;
                _disposed = true;
                operationsCompleted = _noActiveOperations.Task;
            }
            _lifetimeCancellation.Cancel();
            List<AgentSessionRuntime> sessions;
            lock (_syncRoot)
                sessions = _sessions.Values.ToList();
            foreach (var runtime in sessions)
            {
                lock (runtime.SyncRoot) runtime.ActiveCancellation?.Cancel();
                _approvals.CancelSession(runtime.Document.Id);
            }
            _ = DisposeWhenOperationsCompleteAsync(operationsCompleted);
        }

        private async Task DisposeWhenOperationsCompleteAsync(Task operationsCompleted)
        {
            await operationsCompleted.ConfigureAwait(false);
            List<AgentSessionRuntime> sessions;
            lock (_syncRoot)
            {
                // An operation which entered before Dispose may finish creating a runtime after
                // the initial cancellation snapshot. Capture again only after every operation has
                // drained so those late runtimes are released as well.
                sessions = _sessions.Values.ToList();
                _sessions.Clear();
            }
            foreach (var runtime in sessions)
            {
                _evalService.ReleaseSession(runtime.Document.Id);
                runtime.Dispose();
            }
            DisposeOwnedResources();
        }

        private void DisposeOwnedResources()
        {
            _approvals.Changed -= MarkChanged;
            _evalService.Dispose();
            if (_ownsProvider && _provider is IDisposable providerDisposable) providerDisposable.Dispose();
            if (_ownsStore && _store is IDisposable storeDisposable) storeDisposable.Dispose();
            _initializeGate.Dispose();
            _settingsMutationGate.Dispose();
            _lifetimeCancellation.Dispose();
        }

        public static void DisposeDefault()
        {
            lock (StaticSyncRoot)
            {
                _default?.Dispose();
                _default = null;
            }
        }

        private void RegisterBuiltInTools()
        {
            _tools.Register(new ReadFileAgentTool());
            _tools.Register(new ListDirectoryAgentTool());
            _tools.Register(new FileInfoAgentTool());
            _tools.Register(new WriteFileAgentTool());
            _tools.Register(new ApplyPatchAgentTool());
            _tools.Register(new CreateDirectoryAgentTool());
            _tools.Register(new DeletePathAgentTool());
            _tools.Register(new CopyPathAgentTool());
            _tools.Register(new MovePathAgentTool());
            var processRunner = new AgentProcessRunner();
            _tools.Register(new ProcessAgentTool(processRunner));
            _tools.Register(new ShellAgentTool(processRunner));
            _tools.Register(new UnitySnapshotAgentTool());
            _tools.Register(new UnitySceneQueryAgentTool());
            _tools.Register(new UnityEvalJsAgentTool(_evalService));
            _tools.Register(new SkillListAgentTool(_instructions, () => Settings));
            _tools.Register(new SkillReadAgentTool(_instructions, () => Settings));
        }

        private AgentSessionRuntime GetRuntime(string sessionId)
        {
            lock (_syncRoot)
            {
                if (_sessions.TryGetValue(sessionId, out var runtime)) return runtime;
            }
            throw new KeyNotFoundException($"Agent conversation '{sessionId}' was not found.");
        }

        private HostOperation EnterOperation()
        {
            lock (_operationSyncRoot)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(UnityAgentHost));
                if (_activeOperationCount == 0) _noActiveOperations = PendingSignal();
                _activeOperationCount++;
                return new HostOperation(this);
            }
        }

        private void ExitOperation()
        {
            TaskCompletionSource<bool>? completed = null;
            lock (_operationSyncRoot)
            {
                if (_activeOperationCount <= 0)
                    throw new InvalidOperationException("UnityAgentHost operation count is unbalanced.");
                _activeOperationCount--;
                if (_activeOperationCount == 0) completed = _noActiveOperations;
            }
            completed?.TrySetResult(true);
        }

        private void ThrowIfDisposed()
        {
            lock (_operationSyncRoot)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(UnityAgentHost));
            }
        }

        private CancellationTokenSource CreateOperationCancellation(CancellationToken cancellationToken) =>
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);

        private async Task EnterTurnAdmissionAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task admissionOpened;
                lock (_turnAdmissionSyncRoot)
                {
                    if (!_turnAdmissionClosed)
                    {
                        if (_activeTurnCount == 0) _noActiveTurns = PendingSignal();
                        _activeTurnCount++;
                        return;
                    }
                    admissionOpened = _turnAdmissionOpened.Task;
                }
                await WaitWithCancellationAsync(admissionOpened, cancellationToken).ConfigureAwait(false);
            }
        }

        private void ExitTurnAdmission()
        {
            TaskCompletionSource<bool>? completed = null;
            lock (_turnAdmissionSyncRoot)
            {
                if (_activeTurnCount <= 0)
                    throw new InvalidOperationException("UnityAgentHost active turn count is unbalanced.");
                _activeTurnCount--;
                if (_activeTurnCount == 0) completed = _noActiveTurns;
            }
            completed?.TrySetResult(true);
        }

        private Task CloseTurnAdmission()
        {
            lock (_turnAdmissionSyncRoot)
            {
                if (_turnAdmissionClosed)
                    throw new InvalidOperationException("UnityAgentHost turn admission is already closed.");
                _turnAdmissionClosed = true;
                _turnAdmissionOpened = PendingSignal();
                return _noActiveTurns.Task;
            }
        }

        private void OpenTurnAdmission()
        {
            TaskCompletionSource<bool>? opened = null;
            lock (_turnAdmissionSyncRoot)
            {
                if (!_turnAdmissionClosed) return;
                _turnAdmissionClosed = false;
                opened = _turnAdmissionOpened;
            }
            opened.TrySetResult(true);
        }

        private static async Task WaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }
            var canceled = PendingSignal();
            using var registration = cancellationToken.Register(() => canceled.TrySetCanceled(cancellationToken));
            var completed = await Task.WhenAny(task, canceled.Task).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }

        private static TaskCompletionSource<bool> PendingSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource<bool> CompletedSignal()
        {
            var signal = PendingSignal();
            signal.TrySetResult(true);
            return signal;
        }

        private sealed class HostOperation : IDisposable
        {
            private UnityAgentHost? _owner;

            public HostOperation(UnityAgentHost owner) => _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitOperation();
        }

        private async Task SaveRuntimeAsync(AgentSessionRuntime runtime, CancellationToken cancellationToken)
        {
            AgentSessionDocument snapshot;
            lock (runtime.SyncRoot)
            {
                if (runtime.IsDeleting) return;
                snapshot = AgentDocumentCodec.Clone(runtime.Document);
            }
            await _store.SaveSessionAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        private void MarkChanged()
        {
            Interlocked.Increment(ref _revision);
        }

        private void PublishStreamEvent(string sessionId, AgentStreamEvent value)
        {
            var handlers = StreamEvent;
            if (handlers == null) return;
            var hostEvent = new AgentHostStreamEvent(sessionId, value);
            foreach (Action<AgentHostStreamEvent> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(hostEvent);
                }
                catch (Exception exception)
                {
                    LogSys.LogException(exception);
                }
            }
        }

        private static AgentTurnResult CreateTurnResult(
            AgentSessionRuntime runtime,
            long startingInputTokens,
            long startingOutputTokens)
        {
            lock (runtime.SyncRoot)
            {
                return new AgentTurnResult(runtime.Document.Id, runtime.Document.State,
                    runtime.Document.LastError, new AgentUsage
                    {
                        InputTokens = runtime.Document.Usage.InputTokens >= startingInputTokens
                            ? runtime.Document.Usage.InputTokens - startingInputTokens
                            : 0,
                        OutputTokens = runtime.Document.Usage.OutputTokens >= startingOutputTokens
                            ? runtime.Document.Usage.OutputTokens - startingOutputTokens
                            : 0
                    });
            }
        }

        private AgentTurnResult CompleteTurn(
            AgentSessionRuntime runtime,
            long startingInputTokens,
            long startingOutputTokens)
        {
            var result = CreateTurnResult(runtime, startingInputTokens, startingOutputTokens);
            PublishStreamEvent(result.SessionId, new AgentStreamEvent(
                AgentStreamEventKind.TurnCompleted, result.State.ToString(), isError: !result.IsSuccess));
            return result;
        }

        private static string CreateTitle(string text)
        {
            var title = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return title.Length <= 42 ? title : title.Substring(0, 42) + "…";
        }

        internal static void ValidateSettings(AgentSettingsDocument settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            ValidateProviderSettings(AgentProviderSettingsDocument.FromSettings(settings));
            ValidateMachineSettings(settings);
        }

        internal static void ValidateProviderSettings(AgentProviderSettingsDocument settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (settings.ProviderProfiles == null || settings.ProviderProfiles.Count == 0)
                throw new ArgumentException("At least one Provider profile is required.", nameof(settings));

            var profileIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in settings.ProviderProfiles)
            {
                if (profile == null) throw new ArgumentException("Provider profiles cannot contain null values.", nameof(settings));
                if (string.IsNullOrWhiteSpace(profile.Id))
                    throw new ArgumentException("Every Provider profile requires an id.", nameof(settings));
                if (!profileIds.Add(profile.Id))
                    throw new ArgumentException($"Duplicate Provider profile id '{profile.Id}'.", nameof(settings));
                if (!AgentProtocolIds.All.Contains(profile.Protocol))
                    throw new ArgumentException($"Provider profile '{profile.Id}' uses unknown protocol '{profile.Protocol}'.", nameof(settings));
                if (string.IsNullOrWhiteSpace(profile.BaseUrl))
                    throw new ArgumentException($"Provider profile '{profile.Id}' requires a base URL.", nameof(settings));
                if (profile.ApiKey.Length > 131_072)
                    throw new ArgumentException(
                        $"Provider profile '{profile.Id}' API key exceeds the 131,072 character limit.",
                        nameof(settings));
                if (profile.ContextWindowTokens < 8_192)
                    throw new ArgumentException(
                        $"Provider profile '{profile.Id}' requires a context window of at least 8,192 tokens.",
                        nameof(settings));

                if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var baseUri) ||
                         (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ArgumentException(
                        $"Provider profile '{profile.Id}' requires an absolute HTTP(S) base URL.", nameof(settings));
                }
            }

            if (string.IsNullOrWhiteSpace(settings.DefaultProviderProfileId) ||
                !profileIds.Contains(settings.DefaultProviderProfileId))
                throw new ArgumentException("Default Provider profile does not exist.", nameof(settings));
        }

        internal static void ValidateMachineSettings(AgentSettingsDocument settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (!Enum.IsDefined(typeof(AgentPermissionMode), settings.PermissionMode))
                throw new ArgumentException("Settings contain an unknown Agent permission mode.", nameof(settings));

            ValidatePathLocations(settings.AgentsRoots, "AGENTS.md", nameof(settings));
            ValidatePathLocations(settings.SkillRoots, "Skill", nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.EditorSystemPrompt))
                throw new ArgumentException("Editor system prompt is required.", nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.RuntimeSystemPrompt))
                throw new ArgumentException("Runtime system prompt is required.", nameof(settings));
            if (settings.DefaultToolTimeoutSeconds < 1)
                throw new ArgumentException("Default Tool timeout must be positive.", nameof(settings));
            if (settings.MaximumAgentSteps < 1)
                throw new ArgumentException("Maximum Agent steps must be positive.", nameof(settings));
        }

        private static void ValidatePathLocations(
            IReadOnlyList<AgentPathLocation>? locations,
            string label,
            string parameterName)
        {
            if (locations == null)
                throw new ArgumentException($"{label} roots collection cannot be null.", parameterName);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var location in locations)
            {
                if (location == null)
                    throw new ArgumentException($"{label} roots cannot contain null values.", parameterName);
                AgentPaths.Validate(location, parameterName);
                if (!ids.Add(location.Id))
                    throw new ArgumentException($"Duplicate {label} root id '{location.Id}'.", parameterName);
            }
        }

        private static bool SettingsAffectConversationContext(
            AgentSettingsDocument previous,
            AgentSettingsDocument current)
        {
            if (!string.Equals(previous.EditorSystemPrompt, current.EditorSystemPrompt, StringComparison.Ordinal) ||
                !string.Equals(previous.RuntimeSystemPrompt, current.RuntimeSystemPrompt, StringComparison.Ordinal))
                return true;
            if (!PathLocationsEqual(previous.AgentsRoots, current.AgentsRoots) ||
                !PathLocationsEqual(previous.SkillRoots, current.SkillRoots)) return true;

            var previousProfiles = previous.ProviderProfiles.OrderBy(profile => profile.Id, StringComparer.Ordinal)
                .ToList();
            var currentProfiles = current.ProviderProfiles.OrderBy(profile => profile.Id, StringComparer.Ordinal)
                .ToList();
            if (previousProfiles.Count != currentProfiles.Count) return true;
            for (var index = 0; index < previousProfiles.Count; index++)
            {
                var left = previousProfiles[index];
                var right = currentProfiles[index];
                if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal) ||
                    !string.Equals(left.ProviderPresetId, right.ProviderPresetId, StringComparison.Ordinal) ||
                    !string.Equals(left.Protocol, right.Protocol, StringComparison.Ordinal) ||
                    !string.Equals(left.BaseUrl, right.BaseUrl, StringComparison.Ordinal) ||
                    !string.Equals(left.Model, right.Model, StringComparison.Ordinal) ||
                    !string.Equals(left.ReasoningEffort, right.ReasoningEffort, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool PathLocationsEqual(
            IReadOnlyList<AgentPathLocation> left,
            IReadOnlyList<AgentPathLocation> right)
        {
            if (left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index].Id, right[index].Id, StringComparison.Ordinal) ||
                    left[index].BasePath != right[index].BasePath ||
                    left[index].UseUnityAgentToolDirectory != right[index].UseUnityAgentToolDirectory ||
                    !string.Equals(left[index].RelativePath, right[index].RelativePath, StringComparison.Ordinal) ||
                    left[index].Scope != right[index].Scope ||
                    left[index].EmbedInPlayerBuild != right[index].EmbedInPlayerBuild)
                    return false;
            }
            return true;
        }
    }
}
