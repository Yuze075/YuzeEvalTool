#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
#endif
using UnityEngine.UIElements;

namespace YuzeToolkit.UnityAgent
{
    public enum UnityAgentWorkbenchPage
    {
        Chat,
        Settings
    }

    public sealed class AgentScrollContainer
    {
        private readonly Action _scrollToEnd;
        private readonly Action<VisualElement> _scrollTo;

        public AgentScrollContainer(VisualElement root, VisualElement content, Action scrollToEnd,
            Action<VisualElement>? scrollTo = null)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            _scrollToEnd = scrollToEnd ?? throw new ArgumentNullException(nameof(scrollToEnd));
            _scrollTo = scrollTo ?? (_ => { });
        }

        public VisualElement Root { get; }
        public VisualElement Content { get; }
        public void ScrollToEnd() => _scrollToEnd();
        public void ScrollTo(VisualElement element) => _scrollTo(element);

        public static AgentScrollContainer CreateDefault()
        {
            var scroll = AgentUi.Scroll(ScrollViewMode.Vertical);
            return new AgentScrollContainer(scroll, scroll.contentContainer,
                () => scroll.schedule.Execute(() => scroll.scrollOffset =
                    new Vector2(scroll.scrollOffset.x, scroll.contentContainer.layout.height)),
                element => scroll.schedule.Execute(() => scroll.ScrollTo(element)));
        }
    }

    public sealed class UnityAgentWorkbenchView : VisualElement, IDisposable
    {
        private readonly UnityAgentHost _host;
        private readonly Func<AgentScrollContainer> _scrollFactory;
        private readonly VisualElement _pageHost;
        private readonly AgentModalLayer _modal;
        private IDisposable? _page;
        private UnityAgentWorkbenchPage _pageKind;
        private bool _disposed;

        public UnityAgentWorkbenchView(
            UnityAgentHost host,
            Func<AgentScrollContainer>? scrollFactory = null,
            UnityAgentWorkbenchPage initialPage = UnityAgentWorkbenchPage.Chat)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _scrollFactory = scrollFactory ?? AgentScrollContainer.CreateDefault;
            style.flexGrow = 1;
            style.minWidth = 0;
            style.minHeight = 0;
            style.backgroundColor = AgentUi.Background;
            style.color = AgentUi.Text;
            AgentUi.ApplyRoot(this);

            _pageHost = new VisualElement { name = "unity-agent-page-host" };
            _pageHost.style.flexGrow = 1;
            _pageHost.style.minWidth = 0;
            _pageHost.style.minHeight = 0;
            Add(_pageHost);

            _modal = new AgentModalLayer();
            Add(_modal);
            ShowPage(initialPage);
        }

        public void ShowPage(UnityAgentWorkbenchPage page)
        {
            if (_disposed || _page != null && _pageKind == page) return;
            _page?.Dispose();
            _pageHost.Clear();
            _pageKind = page;
            if (page == UnityAgentWorkbenchPage.Chat)
            {
                var chat = new AgentChatView(_host, _scrollFactory(),
                    () => ShowPage(UnityAgentWorkbenchPage.Settings), ShowError, ShowConfirmation);
                _page = chat;
                _pageHost.Add(chat);
            }
            else
            {
                var settings = new AgentSettingsView(_host, _scrollFactory(),
                    () => ShowPage(UnityAgentWorkbenchPage.Chat), ShowError, ShowConfirmation);
                _page = settings;
                _pageHost.Add(settings);
            }
        }

        public void Tick()
        {
            if (_disposed) return;
            if (_page is AgentChatView chat) chat.Tick();
            else if (_page is AgentSettingsView settings) settings.Tick();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _page?.Dispose();
            _page = null;
        }

        private void ShowError(string title, string message) => _modal.ShowError(title, message);

        private void ShowConfirmation(string title, string message, Action confirmed) =>
            _modal.ShowConfirmation(title, message, confirmed);
    }

    public sealed class AgentChatView : VisualElement, IDisposable
    {
        // Unity 2022.3 starts truncating one text mesh at 49,152 vertices. Keeping each
        // plain-text Label well below 12,288 glyphs leaves room for shaping expansion.
        private const int MaximumToolDetailCharactersPerTextElement = 6_000;
        private const float MaximumToolDetailHeight = 360f;

        private readonly UnityAgentHost _host;
        private readonly Action _openSettings;
        private readonly Action<string, string> _showError;
        private readonly Action<string, string, Action> _showConfirmation;
        private VisualElement _sessionList = new();
        private VisualElement _commandSessionList = new();
        private readonly VisualElement _workspaceHost;
        private readonly VisualElement _conversationPage;
        private readonly VisualElement _messageList;
        private readonly AgentScrollContainer _messageScroll;
        private readonly AgentChoiceField _provider;
        private readonly AgentChoiceField _model;
        private readonly AgentButton _refreshModels;
        private readonly Label _modelSource;
        private readonly AgentChoiceField _effort;
        private readonly AgentChoiceField _permission;
        private readonly Label _status;
        private readonly Label _conversationTitle;
        private readonly AgentTextField _composer;
        private readonly AgentButton _action;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Dictionary<string, IReadOnlyList<string>> _modelChoices = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _modelDisplayNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _discoveryStartedProfiles = new(StringComparer.Ordinal);
        private readonly HashSet<string> _expandedToolCalls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _profileIdsByLabel = new(StringComparer.Ordinal);
        private long _lastRevision = -1;
        private string _selectedSessionId = string.Empty;
        private string _shownSessionError = string.Empty;
        private string _modelCatalogDetail = string.Empty;
        private string _newConversationDraft = string.Empty;
        private string _composerSessionId = string.Empty;
        private bool _loadingComposerDraft;
        private IVisualElementScheduledItem? _draftSaveItem;
        private AgentWorkspacePage _workspacePage;
        private AgentCommandLineWorkspaceView? _commandLineView;
        private AgentDebugWorkspaceView? _debugPanelView;
        private AgentLogWorkspaceView? _logView;
        private AgentSystemInfoWorkspaceView? _systemInfoView;
        private bool _initialized;
        private bool _disposed;

        public AgentChatView(
            UnityAgentHost host,
            AgentScrollContainer? messageScroll = null,
            Action? openSettings = null,
            Action<string, string>? showError = null,
            Action<string, string, Action>? showConfirmation = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _openSettings = openSettings ?? (() => { });
            _showError = showError ?? ((_, message) => LogSys.LogError(message));
            _showConfirmation = showConfirmation ?? ((_, _, confirmed) => confirmed());
            name = "unity-agent-chat-view";
            style.flexGrow = 1;
            style.minWidth = 0;
            style.minHeight = 0;
            style.flexDirection = FlexDirection.Row;

            var sidebar = CreateSidebar();
            Add(sidebar);

            _workspaceHost = new VisualElement { name = "unity-agent-workspace-host" };
            _workspaceHost.style.flexGrow = 1;
            _workspaceHost.style.minWidth = 0;
            _workspaceHost.style.minHeight = 0;
            Add(_workspaceHost);

            _conversationPage = new VisualElement { name = "unity-agent-chat-main" };
            var main = _conversationPage;
            main.style.flexGrow = 1;
            main.style.minWidth = 0;
            main.style.minHeight = 0;
            main.style.alignItems = Align.Stretch;
            _workspaceHost.Add(main);

            var header = new VisualElement();
            header.style.height = 54;
            header.style.flexShrink = 0;
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.paddingLeft = 20;
            header.style.paddingRight = 20;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = AgentUi.Border;
            _conversationTitle = new Label("Conversation") { style = { flexGrow = 1, minWidth = 0 } };
            _conversationTitle.style.overflow = Overflow.Hidden;
            _conversationTitle.style.textOverflow = TextOverflow.Ellipsis;
            AgentUi.ApplyTypography(_conversationTitle, AgentTypography.PageTitle);
            header.Add(_conversationTitle);
            _status = new Label("Loading…");
            AgentUi.ApplyTypography(_status, AgentTypography.Caption);
            _status.style.unityTextAlign = TextAnchor.MiddleRight;
            _status.style.color = AgentUi.Muted;
            header.Add(_status);
            main.Add(header);

            _messageScroll = messageScroll ?? AgentScrollContainer.CreateDefault();
            _messageScroll.Root.style.flexGrow = 1;
            _messageScroll.Root.style.minHeight = 0;
            _messageScroll.Content.style.paddingLeft = 24;
            _messageScroll.Content.style.paddingRight = 24;
            _messageScroll.Content.style.paddingTop = 18;
            _messageScroll.Content.style.paddingBottom = 16;
            _messageScroll.Content.style.minHeight = new Length(100, LengthUnit.Percent);
            _messageList = _messageScroll.Content;
            _messageList.style.flexGrow = 1;
            main.Add(_messageScroll.Root);

            var composerDock = new VisualElement();
            composerDock.style.width = new Length(100, LengthUnit.Percent);
            composerDock.style.maxWidth = 780;
            composerDock.style.alignSelf = Align.Center;
            composerDock.style.paddingLeft = 16;
            composerDock.style.paddingRight = 16;
            composerDock.style.paddingBottom = 8;
            composerDock.style.flexShrink = 0;
            main.Add(composerDock);

            var composer = AgentUi.RoundedPanel(22);
            composer.style.width = new Length(100, LengthUnit.Percent);
            composer.style.flexShrink = 0;
            composer.style.minHeight = 104;
            composer.style.paddingLeft = 16;
            composer.style.paddingRight = 12;
            composer.style.paddingTop = 10;
            composer.style.paddingBottom = 6;
            composer.style.backgroundColor = AgentUi.Composer;
            AgentUi.SetBorder(composer, AgentUi.Border1, 1);
            composerDock.Add(composer);

            _composer = new AgentTextField(surface: false)
            {
                multiline = true,
                Placeholder = "Describe a task or ask a question…"
            };
            AgentTooltip.Attach(_composer, "Ctrl/Cmd+Enter to send.");
            AgentUi.ApplyTypography(_composer, AgentTypography.Composer, false);
            _composer.style.minHeight = 24;
            _composer.style.maxHeight = 336;
            _composer.style.whiteSpace = WhiteSpace.Normal;
            _composer.RegisterValueChangedCallback(_ => OnComposerChanged());
            _composer.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return || !evt.ctrlKey && !evt.commandKey) return;
                RunUiTask(ActAsync);
                evt.StopPropagation();
            });
            composer.Add(_composer);

            var controls = new VisualElement();
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.flexWrap = Wrap.NoWrap;
            controls.style.alignItems = Align.Center;
            controls.style.minWidth = 0;
            controls.style.paddingTop = 2;
            controls.style.paddingRight = 8;
            controls.style.paddingBottom = 0;
            controls.style.paddingLeft = 0;
            controls.style.marginTop = 10;
            composer.Add(controls);

            var attach = AgentUi.IconButton(AgentIconKind.Add, "Attachments are not available in this build.",
                () => _showError("Attachments unavailable",
                    "This build does not currently support attaching files to a conversation."),
                28, AgentUi.Surface3, AgentUi.TextSecondary);
            controls.Add(attach);

            _permission = AgentUi.CompactDropdown(new[]
            {
                AgentPermissionMode.ObserveOnly.ToString(), AgentPermissionMode.ConfirmWrites.ToString(),
                AgentPermissionMode.FullAccess.ToString()
            }, "Execution permission");
            _permission.style.width = 146;
            _permission.ValueFormatter = value => value == AgentPermissionMode.ObserveOnly.ToString()
                ? "Observe only"
                : value == AgentPermissionMode.ConfirmWrites.ToString()
                    ? "Confirm writes"
                    : "Full access";
            _permission.RegisterValueChangedCallback(_ => SaveConversationSelection());
            controls.Add(_permission);
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            spacer.style.minWidth = 14;
            controls.Add(spacer);
            _provider = AgentUi.CompactDropdown(Array.Empty<string>(), "API provider profile");
            _provider.style.width = 148;
            _provider.ValueFormatter = value => HumanProviderLabel(value);
            _provider.RegisterValueChangedCallback(_ =>
            {
                RefreshCuratedModels();
                RunUiTask(RefreshModelsAsync);
                SaveConversationSelection();
            });
            controls.Add(_provider);
            _model = AgentUi.CompactDropdown(Array.Empty<string>(),
                "Choose a remotely discovered model or a curated offline fallback.");
            _model.style.width = 190;
            _model.style.flexGrow = 0;
            _model.style.minWidth = 80;
            _model.OpenUpward = true;
            _model.RegisterValueChangedCallback(evt =>
            {
                ApplyChatModelOptions(evt.newValue);
                SaveConversationSelection();
            });
            controls.Add(_model);
            _refreshModels = AgentUi.IconButton(AgentIconKind.Refresh, "Refresh the model catalog",
                () => RunUiTask(RefreshModelsAsync), 28, AgentUi.Surface3, AgentUi.TextSecondary);
            controls.Add(_refreshModels);
            _effort = AgentUi.CompactDropdown(new[] { "default", "none", "low", "medium", "high", "xhigh" },
                "Reasoning effort");
            _effort.style.width = 132;
            _effort.style.minWidth = 132;
            _effort.style.maxWidth = 132;
            _effort.style.flexShrink = 0;
            _effort.ValueFormatter = value => "Effort: " + HumanEffort(value);
            _effort.OptionFormatter = HumanEffort;
            _effort.RegisterValueChangedCallback(_ => SaveConversationSelection());
            controls.Add(_effort);
            _action = AgentUi.IconButton(AgentIconKind.Send, "Send", () => RunUiTask(ActAsync), 34,
                AgentUi.Send, AgentUi.SendForeground);
            _action.style.marginLeft = 7;
            controls.Add(_action);

            _modelSource = new Label("MODEL CATALOG · WAITING");
            AgentUi.ApplyTypography(_modelSource, AgentTypography.Caption);
            _modelSource.style.color = AgentUi.Muted;
            _modelSource.style.marginBottom = 4;
            _modelSource.style.marginLeft = 16;
            AgentTooltip.Attach(_modelSource, () => _modelCatalogDetail);

            RegisterCallback<GeometryChangedEvent>(evt => ApplyResponsiveLayout(sidebar, evt.newRect.width));

            ShowWorkspace(AgentWorkspacePage.Conversation);
            RunUiTask(InitializeAsync);
        }

        private VisualElement CreateSidebar()
        {
            var sidebar = new VisualElement { name = "unity-agent-sidebar" };
            sidebar.style.width = 280;
            sidebar.style.minWidth = 264;
            sidebar.style.maxWidth = 420;
            sidebar.style.flexShrink = 0;
            sidebar.style.paddingLeft = 12;
            sidebar.style.paddingRight = 12;
            sidebar.style.paddingTop = 6;
            sidebar.style.paddingBottom = 6;
            sidebar.style.borderRightWidth = 1;
            sidebar.style.borderRightColor = AgentUi.Border;
            sidebar.style.backgroundColor = AgentUi.Sidebar;

            var brandRow = new VisualElement { name = "unity-agent-sidebar-logo" };
            brandRow.style.flexDirection = FlexDirection.Row;
            brandRow.style.alignItems = Align.Center;
            brandRow.style.height = 60;
            brandRow.style.paddingLeft = 7;
            var mark = AgentUi.RoundedPanel(10);
            mark.style.width = 34;
            mark.style.height = 34;
            mark.style.alignItems = Align.Center;
            mark.style.justifyContent = Justify.Center;
            mark.style.backgroundColor = AgentUi.Accent;
            mark.Add(new AgentIcon(AgentIconKind.Chat, 18) { Tint = AgentUi.AccentForeground });
            brandRow.Add(mark);
            var brandCopy = new VisualElement { name = "unity-agent-sidebar-brand-copy", style = { marginLeft = 10 } };
            var brand = new Label("Yuze Agent Tool");
            AgentUi.ApplyTypography(brand, AgentTypography.PageTitle);
            brandCopy.Add(brand);
            var brandMeta = new Label("Unified workspace");
            AgentUi.ApplyTypography(brandMeta, AgentTypography.Caption);
            brandMeta.style.color = AgentUi.Muted;
            brandCopy.Add(brandMeta);
            brandRow.Add(brandCopy);
            sidebar.Add(brandRow);

            sidebar.Add(CreateWorkspaceNavigation("New conversation", AgentIconKind.Add,
                BeginNewConversation, showTooltipWhenCollapsed: false));
            sidebar.Add(CreateWorkspaceNavigation("New command line", AgentIconKind.Sliders,
                () => RunUiTask(CreateCommandLineSessionAsync), showTooltipWhenCollapsed: false));
            sidebar.Add(CreateWorkspaceNavigation("Debug Panel", AgentIconKind.Provider,
                () => ShowWorkspace(AgentWorkspacePage.DebugPanel)));
            sidebar.Add(CreateWorkspaceNavigation("Log", AgentIconKind.History,
                () => ShowWorkspace(AgentWorkspacePage.Log)));
            sidebar.Add(CreateWorkspaceNavigation("System Info", AgentIconKind.Folder,
                () => ShowWorkspace(AgentWorkspacePage.SystemInfo)));

            var listScroll = AgentUi.Scroll(ScrollViewMode.Vertical);
            listScroll.name = "unity-agent-sidebar-list";
            listScroll.style.flexGrow = 1;
            listScroll.style.minHeight = 0;
            listScroll.style.marginTop = 8;
            _sessionList = new VisualElement();
            listScroll.Add(_sessionList);
            var commandHeading = CreateSidebarGroupHeading("COMMAND LINE SESSIONS");
            commandHeading.style.marginTop = 12;
            listScroll.Add(commandHeading);
            _commandSessionList = new VisualElement();
            listScroll.Add(_commandSessionList);
            sidebar.Add(listScroll);

            var settings = AgentUi.Button("Settings", "Open provider, Agent, history and Eval settings.", _openSettings, 0,
                AgentUi.Transparent, icon: AgentIconKind.Settings);
            settings.name = "unity-agent-sidebar-settings";
            settings.style.flexGrow = 0;
            settings.style.justifyContent = Justify.FlexStart;
            settings.style.marginTop = 8;
            sidebar.Add(settings);
            return sidebar;
        }

        private static AgentButton CreateWorkspaceNavigation(
            string text,
            AgentIconKind icon,
            Action clicked,
            bool showTooltipWhenCollapsed = true)
        {
            var button = AgentUi.Button(text, showTooltipWhenCollapsed ? text : string.Empty, clicked, 0,
                AgentUi.Transparent,
                AgentUi.TextSecondary, icon);
            button.style.height = 36;
            button.style.flexGrow = 0;
            button.style.justifyContent = Justify.FlexStart;
            button.style.marginBottom = 2;
            return button;
        }

        private static Label CreateSidebarGroupHeading(string text)
        {
            var label = new Label(text);
            AgentUi.ApplyTypography(label, AgentTypography.Caption);
            label.style.color = AgentUi.Muted;
            label.style.marginLeft = 8;
            label.style.marginBottom = 5;
            return label;
        }

        public void Tick()
        {
            if (_disposed) return;
            _commandLineView?.Tick();
            _debugPanelView?.Tick();
            _logView?.Tick();
            _systemInfoView?.Tick();
            var revision = _host.Revision;
            if (!_initialized || revision == _lastRevision) return;
            _lastRevision = revision;
            Refresh();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SaveVisibleDraft();
            _draftSaveItem?.Pause();
            _lifetime.Cancel();
            _lifetime.Dispose();
            _commandLineView?.Dispose();
            _debugPanelView?.Dispose();
            _logView?.Dispose();
            _systemInfoView?.Dispose();
        }

        private async Task CreateCommandLineSessionAsync()
        {
            await _host.EnsureInitializedAsync(_lifetime.Token);
            ShowWorkspace(AgentWorkspacePage.CommandLine);
            _commandLineView?.CreateSession();
        }

        private void ShowWorkspace(AgentWorkspacePage page)
        {
            if (_disposed) return;
            if (_workspacePage == AgentWorkspacePage.Conversation && page != AgentWorkspacePage.Conversation)
                SaveVisibleDraft();
            _workspacePage = page;
            _conversationPage.style.display = page == AgentWorkspacePage.Conversation
                ? DisplayStyle.Flex : DisplayStyle.None;
            if (page == AgentWorkspacePage.CommandLine) EnsureCommandLineView();
            if (page == AgentWorkspacePage.DebugPanel && _debugPanelView == null)
            {
                _debugPanelView = new AgentDebugWorkspaceView();
                _workspaceHost.Add(_debugPanelView);
            }
            if (page == AgentWorkspacePage.Log && _logView == null)
            {
                _logView = new AgentLogWorkspaceView();
                _workspaceHost.Add(_logView);
            }
            if (page == AgentWorkspacePage.SystemInfo && _systemInfoView == null)
            {
                _systemInfoView = new AgentSystemInfoWorkspaceView();
                _workspaceHost.Add(_systemInfoView);
            }
            if (_commandLineView != null)
                _commandLineView.style.display = page == AgentWorkspacePage.CommandLine
                    ? DisplayStyle.Flex : DisplayStyle.None;
            if (_debugPanelView != null)
                _debugPanelView.style.display = page == AgentWorkspacePage.DebugPanel
                    ? DisplayStyle.Flex : DisplayStyle.None;
            if (_logView != null)
                _logView.style.display = page == AgentWorkspacePage.Log
                    ? DisplayStyle.Flex : DisplayStyle.None;
            if (_systemInfoView != null)
                _systemInfoView.style.display = page == AgentWorkspacePage.SystemInfo
                    ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void EnsureCommandLineView()
        {
            if (_commandLineView != null) return;
            _commandLineView = new AgentCommandLineWorkspaceView(_host, _commandSessionList,
                () => ShowWorkspace(AgentWorkspacePage.CommandLine));
            _workspaceHost.Add(_commandLineView);
            _commandLineView.style.display = DisplayStyle.None;
        }

        private async Task InitializeAsync()
        {
            await _host.EnsureInitializedAsync(_lifetime.Token);
            EnsureCommandLineView();
            var sessions = _host.GetSessions();
            _selectedSessionId = sessions.Where(value => !value.IsArchived)
                .OrderByDescending(value => value.IsPinned)
                .ThenByDescending(value => value.UpdatedAtUtc)
                .Select(value => value.Id).FirstOrDefault() ?? string.Empty;
            _initialized = true;
            _lastRevision = -1;
            var current = CurrentSession();
            if (current != null) await DiscoverSessionModelsOnceAsync(current.ProviderProfileId);
        }

        private void BeginNewConversation()
        {
            SaveVisibleDraft();
            ShowWorkspace(AgentWorkspacePage.Conversation);
            _selectedSessionId = string.Empty;
            _shownSessionError = string.Empty;
            LoadVisibleDraft(null);
            _lastRevision = -1;
        }

        private async Task DeleteSessionAsync(string sessionId)
        {
            var session = _host.GetSession(sessionId);
            if (session == null) return;
            if (session.State is AgentSessionState.Running or AgentSessionState.AwaitingApproval)
                throw new InvalidOperationException("Stop the active conversation before deleting it.");
            await _host.DeleteSessionAsync(sessionId, _lifetime.Token);
            var sessions = _host.GetSessions();
            _selectedSessionId = sessions.Where(value => !value.IsArchived)
                .OrderByDescending(value => value.IsPinned)
                .ThenByDescending(value => value.UpdatedAtUtc)
                .Select(value => value.Id).FirstOrDefault() ?? string.Empty;
            LoadVisibleDraft(CurrentSession());
        }

        private Task ActAsync()
        {
            var current = CurrentSession();
            var hasText = !string.IsNullOrWhiteSpace(_composer.value);
            if (hasText)
            {
                if (current != null && IsActive(current))
                    return InterruptAndSendAsync(current.Id, _composer.value.Trim());
                return SendAsync();
            }
            if (current != null && IsActive(current)) _host.StopSession(current.Id);
            return Task.CompletedTask;
        }

        private async Task InterruptAndSendAsync(string sessionId, string text)
        {
            _host.StopSession(sessionId);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var session = _host.GetSession(sessionId);
                if (session == null) throw new InvalidOperationException("The conversation no longer exists.");
                if (!IsActive(session))
                {
                    if (_selectedSessionId != sessionId)
                        throw new InvalidOperationException("The active conversation changed before the message could be sent.");
                    await SaveConversationSelectionAsync(sessionId);
                    if (string.Equals(_composer.value.Trim(), text, StringComparison.Ordinal))
                        _composer.value = string.Empty;
                    await _host.SendMessageAsync(sessionId, text, _lifetime.Token);
                    return;
                }
                await Task.Delay(50, _lifetime.Token);
            }
            throw new TimeoutException("The active Agent turn did not stop within 10 seconds.");
        }

        private async Task SendAsync()
        {
            var text = _composer.value.Trim();
            if (text.Length == 0) return;
            if (string.IsNullOrEmpty(_selectedSessionId))
            {
                var created = await _host.CreateSessionAsync(_lifetime.Token);
                _selectedSessionId = created.Id;
                _composerSessionId = created.Id;
                _newConversationDraft = string.Empty;
            }
            var sessionId = _selectedSessionId;
            await SaveConversationSelectionAsync(sessionId);
            _loadingComposerDraft = true;
            _composer.SetValueWithoutNotify(string.Empty);
            _loadingComposerDraft = false;
            await _host.SendMessageAsync(sessionId, text, _lifetime.Token);
        }

        private void SaveConversationSelection()
        {
            if (_initialized) RunUiTask(() => SaveConversationSelectionAsync(_selectedSessionId));
        }

        private async Task SaveConversationSelectionAsync(string sessionId)
        {
            var settings = _host.Settings;
            var profile = _profileIdsByLabel.TryGetValue(_provider.value, out var profileId)
                ? settings.ProviderProfiles.FirstOrDefault(value => value.Id == profileId)
                : null;
            if (profile == null || string.IsNullOrWhiteSpace(sessionId)) return;
            var permission = Enum.TryParse<AgentPermissionMode>(_permission.value, out var parsed)
                ? parsed
                : AgentPermissionMode.ObserveOnly;
            var effort = _effort.value == "default" ? string.Empty : _effort.value;
            if (string.IsNullOrWhiteSpace(_model.value))
                throw new InvalidOperationException("Select a model before sending a message.");
            await _host.UpdateSessionAsync(sessionId, profile.Id, _model.value, effort, permission,
                _lifetime.Token);
        }

        private async Task RefreshModelsAsync()
        {
            var profile = ResolveSelectedProfile();
            if (profile == null) return;
            _refreshModels.SetEnabled(false);
            SetModelCatalogState("MODEL CATALOG · REFRESHING", AgentUi.Muted);
            try
            {
                var discovery = await _host.DiscoverModelsAsync(profile, _lifetime.Token);
                var models = discovery.Models.Select(value => value.Id).Distinct(StringComparer.Ordinal).ToList();
                foreach (var option in discovery.Models)
                    _modelDisplayNames[option.Id] = string.IsNullOrWhiteSpace(option.DisplayName)
                        ? option.Id
                        : option.DisplayName;
                _modelChoices[profile.Id] = models;
                ApplyModelCatalog(profile, models, _model.value);
                SetDiscoveryState(discovery);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ApplyCuratedCatalog(profile, _model.value);
                SetModelCatalogState("MODEL CATALOG · FALLBACK — REFRESH AVAILABLE", AgentUi.Warning,
                    exception.Message);
            }
            finally
            {
                _refreshModels.SetEnabled(true);
            }
        }

        private async Task DiscoverSessionModelsOnceAsync(string profileId)
        {
            if (!_discoveryStartedProfiles.Add(profileId)) return;
            var profile = _host.Settings.ProviderProfiles.FirstOrDefault(value => value.Id == profileId);
            if (profile == null) return;
            try
            {
                var discovery = await _host.DiscoverModelsAsync(profile, _lifetime.Token);
                var models = discovery.Models.Select(value => value.Id).ToList();
                foreach (var option in discovery.Models)
                    _modelDisplayNames[option.Id] = string.IsNullOrWhiteSpace(option.DisplayName)
                        ? option.Id
                        : option.DisplayName;
                _modelChoices[profile.Id] = models;
                var current = CurrentSession();
                if (current?.ProviderProfileId == profile.Id)
                {
                    var selectedModel = !string.IsNullOrWhiteSpace(current.Model)
                        ? current.Model
                        : !string.IsNullOrWhiteSpace(profile.Model)
                            ? profile.Model
                            : models.FirstOrDefault() ?? string.Empty;
                    ApplyModelCatalog(profile, models, selectedModel);
                    selectedModel = _model.value;
                    SetDiscoveryState(discovery);
                    if (string.IsNullOrWhiteSpace(current.Model) && !string.IsNullOrWhiteSpace(selectedModel))
                    {
                        await _host.UpdateSessionAsync(current.Id, profile.Id, selectedModel,
                            string.IsNullOrWhiteSpace(current.ReasoningEffort)
                                ? profile.ReasoningEffort
                                : current.ReasoningEffort,
                            current.PermissionMode, _lifetime.Token);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (_selectedSessionId.Length > 0 && CurrentSession()?.ProviderProfileId == profile.Id)
                {
                    ApplyCuratedCatalog(profile, CurrentSession()?.Model ?? string.Empty);
                    SetModelCatalogState("MODEL CATALOG · FALLBACK — REFRESH AVAILABLE", AgentUi.Warning,
                        exception.Message);
                }
            }
        }

        private void Refresh()
        {
            var sessions = _host.GetSessions();
            if (!string.IsNullOrEmpty(_selectedSessionId) && sessions.All(value => value.Id != _selectedSessionId))
                _selectedSessionId = sessions.Where(value => !value.IsArchived)
                    .OrderByDescending(value => value.IsPinned)
                    .ThenByDescending(value => value.UpdatedAtUtc)
                    .Select(value => value.Id).FirstOrDefault() ?? string.Empty;
            RefreshSessionList(sessions);

            var current = CurrentSession();
            LoadVisibleDraft(current);
            _conversationTitle.text = current == null
                ? "New conversation"
                : string.IsNullOrWhiteSpace(current.Title) ? "Conversation" : current.Title;

            var settings = _host.Settings;
            var labels = settings.ProviderProfiles.Select(ProfileLabel).ToList();
            _profileIdsByLabel.Clear();
            foreach (var value in settings.ProviderProfiles) _profileIdsByLabel[ProfileLabel(value)] = value.Id;
            _provider.choices = labels;
            var profile = settings.ProviderProfiles.FirstOrDefault(value => value.Id == current?.ProviderProfileId)
                          ?? settings.ProviderProfiles[0];
            _provider.SetValueWithoutNotify(ProfileLabel(profile));
            if (!_discoveryStartedProfiles.Contains(profile.Id))
                RunUiTask(() => DiscoverSessionModelsOnceAsync(profile.Id));
            var active = current != null && IsActive(current);
            ApplyModelCatalog(profile,
                _modelChoices.TryGetValue(profile.Id, out var discovered)
                    ? discovered
                    : AgentProviderCatalog.GetModels(profile.ProviderPresetId).Select(value => value.Id),
                string.IsNullOrWhiteSpace(current?.Model) ? profile.Model : current.Model);
            _effort.SetValueWithoutNotify(string.IsNullOrWhiteSpace(current?.ReasoningEffort)
                ? "default"
                : current.ReasoningEffort);
            if (!_effort.choices.Contains(_effort.value))
            {
                var choices = _effort.choices.ToList();
                choices.Add(_effort.value);
                _effort.choices = choices;
            }
            var defaultPermission = AgentPaths.IsEditor
                ? settings.PermissionMode
                : AgentPermissionMode.ObserveOnly;
            _permission.SetValueWithoutNotify((current?.PermissionMode ?? defaultPermission).ToString());

            _status.text = current == null
                ? "Draft · created on first send"
                : $"{current.State}  ·  {current.Usage.TotalTokens:N0} tokens";
            _status.style.color = current?.State == AgentSessionState.Failed ? AgentUi.Error : AgentUi.Muted;
            _provider.SetEnabled(!active);
            _model.SetEnabled(!active);
            _refreshModels.SetEnabled(!active);
            _effort.SetEnabled(!active);
            _permission.SetEnabled(!active);
            RefreshActionButton();

            if (current == null)
            {
                _shownSessionError = string.Empty;
                _messageList.Clear();
                _messageList.Add(CreateEmptyState());
                return;
            }

            if (string.IsNullOrWhiteSpace(current.LastError))
                _shownSessionError = string.Empty;
            else if (current.State == AgentSessionState.Failed &&
                     current.LastError.IndexOf("stopped", StringComparison.OrdinalIgnoreCase) < 0 &&
                     current.LastError.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) < 0 &&
                     current.LastError != _shownSessionError)
            {
                _shownSessionError = current.LastError;
                _showError("Agent turn failed", current.LastError);
            }

            _messageList.Clear();
            var toolResults = current.Messages
                .Where(message => message.Role == AgentMessageRole.Tool &&
                                  !string.IsNullOrWhiteSpace(message.ToolCallId))
                .GroupBy(message => message.ToolCallId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new Queue<AgentMessage>(group), StringComparer.Ordinal);
            var consumedToolResults = new HashSet<string>(StringComparer.Ordinal);
            var renderedItems = 0;
            foreach (var message in current.Messages)
            {
                if (message.Role is AgentMessageRole.User or AgentMessageRole.Assistant &&
                    !string.IsNullOrWhiteSpace(message.Text))
                {
                    _messageList.Add(CreateMessage(message));
                    renderedItems++;
                }

                if (message.Role == AgentMessageRole.Assistant)
                {
                    foreach (var call in message.ToolCalls)
                    {
                        AgentMessage? result = null;
                        if (toolResults.TryGetValue(call.Id, out var candidates) && candidates.Count > 0)
                        {
                            result = candidates.Dequeue();
                            consumedToolResults.Add(result.Id);
                        }
                        _messageList.Add(CreateToolCall(current.Id, message.Id, call, result));
                        renderedItems++;
                    }
                }
                else if (message.Role == AgentMessageRole.Tool && !consumedToolResults.Contains(message.Id))
                {
                    var call = new AgentToolCall
                    {
                        Id = message.ToolCallId,
                        Name = string.IsNullOrWhiteSpace(message.ToolName) ? "Unknown Tool" : message.ToolName,
                        ArgumentsJson = "{}"
                    };
                    _messageList.Add(CreateToolCall(current.Id, message.Id, call, message));
                    renderedItems++;
                }
            }
            if (renderedItems == 0 && !_host.Approvals.Pending.Any(value => value.SessionId == current.Id))
                _messageList.Add(CreateEmptyState());
            foreach (var approval in _host.Approvals.Pending.Where(value => value.SessionId == current.Id))
                _messageList.Add(CreateApproval(approval));
            _messageScroll.ScrollToEnd();
        }

        private void RefreshSessionList(IReadOnlyList<AgentSessionDocument> sessions)
        {
            _sessionList.Clear();
            var active = sessions.Where(value => !value.IsArchived).ToList();
            AddSessionSection("PINNED", active.Where(value => value.IsPinned));
            AddSessionSection("CONVERSATIONS", active.Where(value => !value.IsPinned));
        }

        private void AddSessionSection(string title, IEnumerable<AgentSessionDocument> source)
        {
            var sessions = source.OrderBy(value => value.SortOrder).ThenByDescending(value => value.UpdatedAtUtc).ToList();
            if (sessions.Count == 0 && title != "CONVERSATIONS") return;
            var header = new Label(title);
            AgentUi.ApplyTypography(header, AgentTypography.Caption);
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.color = AgentUi.Muted;
            header.style.marginLeft = 6;
            header.style.marginTop = 10;
            header.style.marginBottom = 4;
            header.style.paddingTop = 4;
            header.style.paddingBottom = 4;
            header.style.paddingLeft = 4;
            header.style.paddingRight = 4;
            header.style.borderTopLeftRadius = 5;
            header.style.borderTopRightRadius = 5;
            header.style.borderBottomLeftRadius = 5;
            header.style.borderBottomRightRadius = 5;
            header.RegisterCallback<PointerEnterEvent>(_ => header.style.backgroundColor = AgentUi.Hover);
            header.RegisterCallback<PointerLeaveEvent>(_ => header.style.backgroundColor = AgentUi.Transparent);
            _sessionList.Add(header);
            foreach (var session in sessions) _sessionList.Add(CreateSessionItem(session));
        }

        private VisualElement CreateSessionItem(AgentSessionDocument session)
        {
            var item = new VisualElement();
            item.style.flexShrink = 0;
            item.style.minHeight = 38;
            item.style.marginBottom = 3;
            item.style.paddingLeft = 9;
            item.style.paddingRight = 7;
            item.style.paddingTop = 7;
            item.style.paddingBottom = 7;
            item.style.borderTopLeftRadius = 7;
            item.style.borderTopRightRadius = 7;
            item.style.borderBottomLeftRadius = 7;
            item.style.borderBottomRightRadius = 7;
            item.style.backgroundColor = session.Id == _selectedSessionId ? AgentUi.Selected : AgentUi.Transparent;
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart } };
            item.Add(row);
            var label = new Label(session.Title);
            label.style.flexGrow = 1;
            label.style.minWidth = 0;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(label);
            var actions = new VisualElement();
            actions.style.position = Position.Absolute;
            actions.style.right = 4;
            actions.style.top = 4;
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.alignItems = Align.Center;
            actions.style.backgroundColor = session.Id == _selectedSessionId ? AgentUi.Selected : AgentUi.Sidebar;
            actions.style.borderTopLeftRadius = 8;
            actions.style.borderTopRightRadius = 8;
            actions.style.borderBottomLeftRadius = 8;
            actions.style.borderBottomRightRadius = 8;
            actions.style.opacity = 0.18f;
            item.Add(actions);
            var pin = AgentUi.IconButton(AgentIconKind.Pin, session.IsPinned ? "Unpin" : "Pin",
                () => RunUiTask(() => UpdateOrganizationAsync(session, !session.IsPinned, false)),
                24, AgentUi.Transparent);
            actions.Add(pin);
            var archive = AgentUi.IconButton(AgentIconKind.Archive, "Archive",
                () => RunUiTask(() => UpdateOrganizationAsync(session, session.IsPinned, true)), 24,
                AgentUi.Transparent);
            actions.Add(archive);
            var meta = new Label(SessionMeta(session));
            AgentUi.ApplyTypography(meta, AgentTypography.Caption);
            meta.style.color = AgentUi.Muted;
            meta.style.marginTop = 3;
            item.Add(meta);
            item.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.button != 0) return;
                ShowWorkspace(AgentWorkspacePage.Conversation);
                SaveVisibleDraft();
                _selectedSessionId = session.Id;
                LoadVisibleDraft(session);
                _shownSessionError = string.Empty;
                _lastRevision = -1;
            });
            item.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (evt.button != 1) return;
                ShowSessionMenu(session, item);
                evt.StopPropagation();
            });
            item.RegisterCallback<PointerEnterEvent>(_ =>
            {
                actions.style.opacity = 1;
                if (session.Id != _selectedSessionId) item.style.backgroundColor = AgentUi.Hover;
            });
            item.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (!IsFocusedWithin(actions)) actions.style.opacity = 0.18f;
                item.style.backgroundColor = session.Id == _selectedSessionId
                    ? AgentUi.Selected
                    : AgentUi.Transparent;
            });
            actions.RegisterCallback<FocusInEvent>(_ => actions.style.opacity = 1);
            actions.RegisterCallback<FocusOutEvent>(_ =>
                actions.schedule.Execute(() =>
                {
                    if (!IsFocusedWithin(actions)) actions.style.opacity = 0.18f;
                }));
            return item;
        }

        private static bool IsFocusedWithin(VisualElement root)
        {
            var focused = root.panel?.focusController?.focusedElement as VisualElement;
            while (focused != null)
            {
                if (focused == root) return true;
                focused = focused.parent;
            }
            return false;
        }

        private void ShowSessionMenu(AgentSessionDocument session, VisualElement anchor)
        {
            var items = new List<AgentMenuItem>
            {
                new(session.IsPinned ? "Unpin" : "Pin",
                    () => RunUiTask(() => UpdateOrganizationAsync(session, !session.IsPinned, false))),
                new("Archive", () => RunUiTask(() => UpdateOrganizationAsync(session, session.IsPinned, true)))
            };
            items.Add(new AgentMenuItem("Delete conversation…", () => _showConfirmation("Delete conversation?",
                    $"Delete “{session.Title}” and its persisted transcript? This cannot be undone.",
                    () => RunUiTask(() => DeleteSessionAsync(session.Id))), dangerous: true,
                separatorBefore: true));
            AgentPopupMenu.Show(anchor, items, 230);
        }

        private async Task UpdateOrganizationAsync(AgentSessionDocument session, bool pinned, bool archived)
        {
            await _host.UpdateSessionOrganizationAsync(session.Id, pinned, archived,
                Math.Max(0, session.SortOrder), _lifetime.Token);
            if (archived && string.Equals(_selectedSessionId, session.Id, StringComparison.Ordinal))
            {
                _selectedSessionId = _host.GetSessions().Where(value => !value.IsArchived)
                    .OrderByDescending(value => value.IsPinned)
                    .ThenByDescending(value => value.UpdatedAtUtc)
                    .Select(value => value.Id).FirstOrDefault() ?? string.Empty;
            }
            _lastRevision = -1;
        }

        private void OnComposerChanged()
        {
            RefreshActionButton();
            if (_loadingComposerDraft) return;
            if (string.IsNullOrEmpty(_composerSessionId))
            {
                _newConversationDraft = _composer.value;
                return;
            }
            var sessionId = _composerSessionId;
            var draft = _composer.value;
            _draftSaveItem?.Pause();
            _draftSaveItem = schedule.Execute(() =>
                RunUiTask(() => _host.UpdateSessionDraftAsync(sessionId, draft, _lifetime.Token)))
                .StartingIn(350);
        }

        private void SaveVisibleDraft()
        {
            if (_loadingComposerDraft) return;
            if (string.IsNullOrEmpty(_composerSessionId))
            {
                _newConversationDraft = _composer.value;
                return;
            }
            var sessionId = _composerSessionId;
            var draft = _composer.value;
            _draftSaveItem?.Pause();
            RunUiTask(() => _host.UpdateSessionDraftAsync(sessionId, draft, _lifetime.Token));
        }

        private void LoadVisibleDraft(AgentSessionDocument? session)
        {
            var targetId = session?.Id ?? string.Empty;
            if (string.Equals(_composerSessionId, targetId, StringComparison.Ordinal)) return;
            _loadingComposerDraft = true;
            _composerSessionId = targetId;
            _composer.SetValueWithoutNotify(session?.Draft ?? _newConversationDraft);
            _loadingComposerDraft = false;
            RefreshActionButton();
        }

        private void RefreshActionButton()
        {
            var current = CurrentSession();
            var active = current != null && IsActive(current);
            var hasText = !string.IsNullOrWhiteSpace(_composer.value);
            _action.SetIcon(hasText || !active ? AgentIconKind.Send : AgentIconKind.Stop);
            _action.HelpText = hasText
                ? active ? "Stop the active turn and send this message" : "Send message"
                : active ? "Stop the active turn" : "Type a message to send";
            _action.SetPalette(active && !hasText ? AgentUi.Danger : AgentUi.Send,
                active && !hasText ? AgentUi.Text : AgentUi.SendForeground);
            _action.SetEnabled(active || !string.IsNullOrWhiteSpace(_composer.value));
        }

        private void ApplyChatModelOptions(string modelId)
        {
            var profile = ResolveSelectedProfile();
            if (profile == null) return;
            var efforts = AgentProviderCatalog.GetReasoningEfforts(profile.ProviderPresetId, modelId);
            var current = _effort.value;
            _effort.choices = new[] { "default" }.Concat(efforts).Distinct(StringComparer.Ordinal).ToList();
            _effort.SetValueWithoutNotify(_effort.choices.Contains(current) ? current : "default");
        }

        private void RefreshCuratedModels()
        {
            var profile = ResolveSelectedProfile();
            if (profile == null) return;
            var models = AgentProviderCatalog.GetModels(profile.ProviderPresetId).Select(value => value.Id).ToList();
            ApplyModelCatalog(profile, models, profile.Model);
            SetModelCatalogState(models.Count == 0
                    ? "MODEL CATALOG · UNAVAILABLE — OPEN SETTINGS"
                    : "MODEL CATALOG · CURATED FALLBACK — REFRESH AVAILABLE",
                models.Count == 0 ? AgentUi.Error : AgentUi.Warning);
            var effort = string.IsNullOrWhiteSpace(profile.ReasoningEffort) ? "default" : profile.ReasoningEffort;
            if (!_effort.choices.Contains(effort))
            {
                var choices = _effort.choices.ToList();
                choices.Add(effort);
                _effort.choices = choices;
            }
            _effort.SetValueWithoutNotify(effort);
        }

        private void ApplyModelCatalog(AgentProviderProfile profile, IEnumerable<string> source, string preferred)
        {
            var choices = source.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).ToList();
            _model.OptionFormatter = id =>
            {
                if (_modelDisplayNames.TryGetValue(id, out var remoteName)) return remoteName;
                return AgentProviderCatalog.GetModel(profile.ProviderPresetId, id)?.DisplayName ?? id;
            };
            _model.ValueFormatter = _model.OptionFormatter;
            _model.OptionDescriptionFormatter = id =>
                string.Equals(_model.OptionFormatter(id), id, StringComparison.Ordinal) ? string.Empty : id;
            _model.choices = choices;
            var selected = choices.Contains(preferred)
                ? preferred
                : choices.Contains(profile.Model)
                    ? profile.Model
                    : choices.FirstOrDefault() ?? string.Empty;
            _model.SetValueWithoutNotify(selected);
            _model.SetEnabled(choices.Count > 0 && !(CurrentSession() is { } session && IsActive(session)));
            ApplyChatModelOptions(selected);
        }

        private void ApplyCuratedCatalog(AgentProviderProfile profile, string preferred)
        {
            ApplyModelCatalog(profile,
                AgentProviderCatalog.GetModels(profile.ProviderPresetId).Select(value => value.Id), preferred);
        }

        private void SetDiscoveryState(AgentModelDiscoveryResult discovery)
        {
            if (discovery.Models.Count == 0)
            {
                SetModelCatalogState("MODEL CATALOG · NO MODELS — OPEN SETTINGS OR REFRESH", AgentUi.Error,
                    discovery.Warning);
                return;
            }
            var fallback = discovery.Source != AgentModelDiscoverySource.Remote;
            SetModelCatalogState(fallback
                    ? "MODEL CATALOG · CURATED FALLBACK — REFRESH AVAILABLE"
                    : $"MODEL CATALOG · REMOTE · {discovery.Models.Count}",
                fallback ? AgentUi.Warning : AgentUi.Success, discovery.Warning);
        }

        private void SetModelCatalogState(string text, Color color, string tooltip = "")
        {
            _modelSource.text = text;
            _modelSource.style.color = color;
            _modelCatalogDetail = tooltip;
            var state = CatalogMenuState(text);
            _model.SetMenuStatus(state, HumanCatalogMessage(state),
                () => RunUiTask(RefreshModelsAsync));
        }

        private static string HumanCatalogMessage(AgentChoiceMenuState state) => state switch
        {
            AgentChoiceMenuState.Loading => "Loading the model catalog...",
            AgentChoiceMenuState.Empty => "No models are available. Check Settings, then refresh.",
            AgentChoiceMenuState.Error => "No models are available. Check Settings, then refresh.",
            AgentChoiceMenuState.Warning => "Using curated fallback models.",
            _ => string.Empty
        };

        private static AgentChoiceMenuState CatalogMenuState(string text)
        {
            if (text.IndexOf("WAIT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("REFRESHING", StringComparison.OrdinalIgnoreCase) >= 0)
                return AgentChoiceMenuState.Loading;
            if (text.IndexOf("NO MODELS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                return AgentChoiceMenuState.Error;
            return text.IndexOf("FALLBACK", StringComparison.OrdinalIgnoreCase) >= 0
                ? AgentChoiceMenuState.Warning
                : AgentChoiceMenuState.Ready;
        }

        private void ApplyResponsiveLayout(VisualElement sidebar, float width)
        {
            var rail = width < 1024f;
            style.flexDirection = FlexDirection.Row;
            sidebar.style.width = rail ? 56 : 280;
            sidebar.style.minWidth = rail ? 56 : 264;
            sidebar.style.maxWidth = rail ? 56 : 420;
            sidebar.style.height = StyleKeyword.Auto;
            sidebar.style.paddingLeft = rail ? 10 : 12;
            sidebar.style.paddingRight = rail ? 10 : 12;
            sidebar.style.paddingTop = rail ? 18 : 6;
            sidebar.style.borderRightWidth = 1;
            sidebar.style.borderBottomWidth = 0;
            var brandCopy = sidebar.Q<VisualElement>("unity-agent-sidebar-brand-copy");
            if (brandCopy != null) brandCopy.style.display = rail ? DisplayStyle.None : DisplayStyle.Flex;
            var list = sidebar.Q<ScrollView>("unity-agent-sidebar-list");
            if (list != null) list.style.display = rail ? DisplayStyle.None : DisplayStyle.Flex;
            var logo = sidebar.Q<VisualElement>("unity-agent-sidebar-logo");
            if (logo != null)
            {
                logo.style.height = rail ? 36 : 60;
                logo.style.paddingLeft = rail ? 0 : 4;
                logo.style.paddingTop = rail ? 0 : 8;
                logo.style.paddingBottom = rail ? 0 : 8;
            }
            var newConversation = sidebar.Q<AgentButton>("unity-agent-sidebar-new");
            if (newConversation != null)
            {
                newConversation.ShowLabel(!rail);
                newConversation.style.width = rail ? 36 : StyleKeyword.Auto;
                newConversation.style.height = rail ? 36 : 38;
            }
            var settings = sidebar.Q<AgentButton>("unity-agent-sidebar-settings");
            if (settings != null)
            {
                settings.ShowLabel(!rail);
                settings.style.width = rail ? 36 : StyleKeyword.Auto;
                settings.style.height = rail ? 36 : 38;
            }
        }

        private AgentProviderProfile? ResolveSelectedProfile()
        {
            if (!_profileIdsByLabel.TryGetValue(_provider.value, out var profileId)) return null;
            return _host.Settings.ProviderProfiles.FirstOrDefault(value => value.Id == profileId);
        }

        private AgentSessionDocument? CurrentSession() => string.IsNullOrWhiteSpace(_selectedSessionId)
            ? null
            : _host.GetSession(_selectedSessionId);

        private static bool IsActive(AgentSessionDocument session) =>
            session.State is AgentSessionState.Running or AgentSessionState.AwaitingApproval;

        private static string SessionMeta(AgentSessionDocument session)
        {
            if (IsActive(session)) return "Running";
            var local = session.UpdatedAtUtc.ToLocalTime();
            return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("MMM d");
        }

        private static VisualElement CreateEmptyState()
        {
            var empty = new VisualElement();
            empty.style.flexGrow = 1;
            empty.style.alignItems = Align.Center;
            empty.style.justifyContent = Justify.Center;
            empty.style.minHeight = 160;
            var title = new Label("What would you like to build?");
            AgentUi.ApplyTypography(title, AgentTypography.EmptyHero);
            empty.Add(title);
            var hint = new Label("The workspace is bound to this Unity project. Agent instructions and tools are ready.");
            hint.style.color = AgentUi.Muted;
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginTop = 7;
            AgentUi.ApplyTypography(hint, AgentTypography.Caption, false);
            hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            empty.Add(hint);
            return empty;
        }

        private static VisualElement CreateMessage(AgentMessage message)
        {
            var box = new VisualElement();
            box.style.maxWidth = new Length(88, LengthUnit.Percent);
            box.style.alignSelf = message.Role == AgentMessageRole.User ? Align.FlexEnd : Align.FlexStart;
            box.style.flexShrink = 0;
            box.style.marginBottom = 11;
            box.style.paddingLeft = 12;
            box.style.paddingRight = 12;
            box.style.paddingTop = 9;
            box.style.paddingBottom = 9;
            box.style.borderTopLeftRadius = 12;
            box.style.borderTopRightRadius = 12;
            box.style.borderBottomLeftRadius = 12;
            box.style.borderBottomRightRadius = 12;
            box.style.backgroundColor = message.Role == AgentMessageRole.User
                ? AgentUi.UserMessage
                : AgentUi.AssistantMessage;
            box.style.borderLeftWidth = 2;
            box.style.borderLeftColor = message.Role == AgentMessageRole.User
                ? AgentUi.Accent
                : AgentUi.BorderStrong;
            var role = new Label(message.Role.ToString().ToUpperInvariant());
            AgentUi.ApplyTypography(role, AgentTypography.Caption);
            role.style.unityFontStyleAndWeight = FontStyle.Bold;
            role.style.letterSpacing = 0.7f;
            role.style.color = message.Role == AgentMessageRole.User ? AgentUi.Accent : AgentUi.Muted;
            box.Add(role);
            var text = new Label(message.Text);
            text.style.whiteSpace = WhiteSpace.Normal;
            text.style.marginTop = 4;
            box.Add(text);
            return box;
        }

        private VisualElement CreateToolCall(
            string sessionId,
            string messageId,
            AgentToolCall call,
            AgentMessage? result)
        {
            var expansionKey = sessionId + ":" + messageId + ":" + call.Id;
            var expanded = _expandedToolCalls.Contains(expansionKey);
            var status = result == null ? "Running" : result.IsError ? "Failed" : "Completed";
            var statusColor = result == null ? AgentUi.Warning : result.IsError ? AgentUi.Error : AgentUi.Success;
            var card = AgentUi.RoundedPanel(10);
            card.style.maxWidth = new Length(92, LengthUnit.Percent);
            card.style.alignSelf = Align.FlexStart;
            card.style.flexShrink = 0;
            card.style.marginBottom = 9;
            card.style.backgroundColor = AgentUi.ToolMessage;
            AgentUi.SetBorder(card, result?.IsError == true ? AgentUi.Error : AgentUi.Border2, 1);

            var details = new VisualElement();
            details.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            details.style.minWidth = 0;
            details.style.paddingLeft = 12;
            details.style.paddingRight = 12;
            details.style.paddingBottom = 10;

            AgentButton? toggle = null;
            toggle = AgentUi.Button(call.Name + "  ·  " + status,
                "Expand or collapse this Tool call's arguments and result.",
                () =>
                {
                    expanded = !expanded;
                    if (expanded) _expandedToolCalls.Add(expansionKey);
                    else _expandedToolCalls.Remove(expansionKey);
                    details.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                    toggle?.SetIcon(expanded ? AgentIconKind.ChevronDown : AgentIconKind.ChevronRight);
                }, 0, AgentUi.Transparent, AgentUi.TextSecondary,
                expanded ? AgentIconKind.ChevronDown : AgentIconKind.ChevronRight);
            toggle.style.width = new Length(100, LengthUnit.Percent);
            toggle.style.height = 38;
            toggle.style.justifyContent = Justify.FlexStart;
            toggle.style.borderTopWidth = 0;
            toggle.style.borderRightWidth = 0;
            toggle.style.borderBottomWidth = 0;
            toggle.style.borderLeftWidth = 3;
            toggle.style.borderLeftColor = statusColor;
            card.Add(toggle);

            details.Add(CreateToolDetail("ARGUMENTS", string.IsNullOrWhiteSpace(call.ArgumentsJson)
                ? "{}"
                : call.ArgumentsJson));
            details.Add(CreateToolDetail(result == null ? "RESULT · RUNNING" :
                result.IsError ? "RESULT · FAILED" : "RESULT · COMPLETED",
                result == null ? "Waiting for the Tool result…" : result.Text));
            card.Add(details);
            return card;
        }

        private static VisualElement CreateToolDetail(string heading, string value)
        {
            var section = new VisualElement { style = { minWidth = 0, marginTop = 8 } };
            var title = new Label(heading);
            AgentUi.ApplyTypography(title, AgentTypography.Caption);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = AgentUi.TextCaption;
            section.Add(title);

            var content = value ?? string.Empty;
            VisualElement contentHost = section;
            if (content.Length > MaximumToolDetailCharactersPerTextElement)
            {
                var scroll = AgentUi.Scroll(ScrollViewMode.Vertical);
                scroll.style.minWidth = 0;
                scroll.style.maxHeight = MaximumToolDetailHeight;
                scroll.style.marginTop = 3;
                scroll.contentContainer.style.minWidth = 0;
                section.Add(scroll);
                contentHost = scroll;
            }

            if (content.Length == 0)
            {
                contentHost.Add(CreateToolDetailTextElement(string.Empty));
                return section;
            }

            var offset = 0;
            while (offset < content.Length)
            {
                var length = Math.Min(MaximumToolDetailCharactersPerTextElement, content.Length - offset);
                if (offset + length < content.Length &&
                    char.IsHighSurrogate(content[offset + length - 1]) &&
                    char.IsLowSurrogate(content[offset + length]))
                    length--;
                contentHost.Add(CreateToolDetailTextElement(content.Substring(offset, length)));
                offset += length;
            }
            return section;
        }

        private static Label CreateToolDetailTextElement(string value)
        {
            var text = new Label(value) { enableRichText = false };
            text.style.minWidth = 0;
            text.style.whiteSpace = WhiteSpace.Normal;
            text.style.color = AgentUi.TextSecondary;
            AgentUi.ApplyTypography(text, AgentTypography.Caption, false);
            return text;
        }

        private VisualElement CreateApproval(AgentApprovalRequest approval)
        {
            var card = AgentUi.RoundedPanel(10);
            card.style.marginBottom = 12;
            card.style.borderLeftWidth = 3;
            card.style.borderLeftColor = AgentUi.Warning;
            card.style.backgroundColor = AgentUi.WarningPanel;
            card.style.paddingLeft = 12;
            card.style.paddingRight = 12;
            card.style.paddingTop = 10;
            card.style.paddingBottom = 10;
            card.Add(new Label("Confirmation required · " + approval.ToolName));
            var arguments = new Label(approval.ArgumentsJson);
            arguments.style.whiteSpace = WhiteSpace.Normal;
            arguments.style.marginTop = 5;
            card.Add(arguments);
            var actions = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            actions.Add(AgentUi.Button("Approve", "Execute this non-read operation.",
                () => _host.ResolveApproval(approval.Id, true), 84, AgentUi.Accent));
            actions.Add(AgentUi.Button("Decline", "Return a declined result to the model.",
                () => _host.ResolveApproval(approval.Id, false), 84));
            card.Add(actions);
            return card;
        }

        private async void RunUiTask(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _showError("Yuze Agent Tool", exception.Message);
            }
        }

        private static string ProfileLabel(AgentProviderProfile profile) =>
            profile.Name + "  ·  " + profile.Protocol + "  ·  " + ShortId(profile.Id);

        private static string HumanProviderLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "Provider";
            var separator = label.IndexOf("  ·  ", StringComparison.Ordinal);
            return separator < 0 ? label : label.Substring(0, separator);
        }

        private static string HumanEffort(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "default") return "Default";
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private static string ShortId(string id) => id.Length <= 6 ? id : id.Substring(0, 6);
    }

    public sealed class AgentSettingsView : VisualElement, IDisposable
    {
        private readonly UnityAgentHost _host;
        private readonly Action _back;
        private readonly Action<string, string> _showError;
        private readonly Action<string, string, Action> _showConfirmation;
        private readonly AgentScrollContainer _scroll;
        private readonly AgentChoiceField _profiles;
        private readonly AgentChoiceField _providerPreset;
        private readonly AgentTextField _name;
        private readonly AgentChoiceField _protocol;
        private readonly AgentTextField _baseUrl;
        private readonly AgentChoiceField _model;
        private readonly AgentButton _refreshModels;
        private readonly Label _modelSource;
        private readonly Label _modelCatalogMessage;
        private readonly AgentChoiceField _effort;
        private readonly AgentIntegerField _maxTokens;
        private readonly AgentIntegerField _contextWindow;
        private readonly AgentTextField _localSecret;
        private readonly AgentChoiceField _permission;
        private readonly AgentIntegerField _toolTimeout;
        private readonly AgentIntegerField _maximumAgentSteps;
        private readonly AgentTextField _editorSystemPrompt;
        private readonly AgentTextField _runtimeSystemPrompt;
        private readonly AgentPathListEditor _agentsRoots;
        private readonly AgentPathListEditor _skillRoots;
        private readonly AgentEvalConnectionSettingsView _evalConnection;
        private readonly AgentEvalToolsSettingsView _evalTools;
        private readonly VisualElement _archivedConversations;
        private readonly VisualElement _archivedCommandLines;
        private readonly CommandLineStore _commandLineStore = new();
        private readonly Label _status;
        private readonly CancellationTokenSource _lifetime = new();
        private AgentSettingsDocument _editing = new();
        private string _selectedProfileId = string.Empty;
        private long _lastRevision = -1;
        private readonly HashSet<string> _discoveryStartedProfiles = new(StringComparer.Ordinal);
        private bool _initialized;
        private bool _disposed;

        public AgentSettingsView(
            UnityAgentHost host,
            AgentScrollContainer? scrollContainer = null,
            Action? back = null,
            Action<string, string>? showError = null,
            Action<string, string, Action>? showConfirmation = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _back = back ?? (() => { });
            _showError = showError ?? ((_, message) => LogSys.LogError(message));
            _showConfirmation = showConfirmation ?? ((_, _, confirmed) => confirmed());
            style.flexGrow = 1;
            style.minWidth = 0;
            style.minHeight = 0;
            AgentUi.ApplyRoot(this);

            var header = new VisualElement();
            header.style.height = 54;
            header.style.flexShrink = 0;
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.paddingLeft = 18;
            header.style.paddingRight = 20;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = AgentUi.Border;
            header.Add(AgentUi.IconButton(AgentIconKind.Back, "Back to conversations", _back, 28));
            var heading = new VisualElement { style = { marginLeft = 10, flexGrow = 1, minWidth = 0 } };
            var title = new Label("Workspace settings");
            AgentUi.ApplyTypography(title, AgentTypography.PageTitle);
            heading.Add(title);
            header.Add(heading);
            _status = new Label("Loading…");
            _status.style.color = AgentUi.Muted;
            _status.style.marginRight = 10;
            header.Add(_status);
            header.Add(AgentUi.Button("Save", "Persist all settings.", () => RunUiTask(SaveAsync), 78, AgentUi.Accent));
            Add(header);
            RegisterCallback<GeometryChangedEvent>(evt =>
            {
                var narrow = evt.newRect.width < 620f;
                _status.style.display = narrow ? DisplayStyle.None : DisplayStyle.Flex;
                header.style.paddingLeft = narrow ? 10 : 18;
                header.style.paddingRight = narrow ? 10 : 20;
            });

            var workspace = new VisualElement { name = "unity-agent-settings-workspace" };
            workspace.style.flexGrow = 1;
            workspace.style.minWidth = 0;
            workspace.style.minHeight = 0;
            workspace.style.flexDirection = FlexDirection.Row;
            workspace.style.width = new Length(100, LengthUnit.Percent);
            workspace.style.height = new Length(100, LengthUnit.Percent);
            workspace.style.backgroundColor = AgentUi.Surface1;
            workspace.style.overflow = Overflow.Hidden;
            Add(workspace);

            var navigation = new VisualElement { name = "unity-agent-settings-navigation" };
            navigation.style.width = 240;
            navigation.style.minWidth = 240;
            navigation.style.flexShrink = 0;
            navigation.style.paddingTop = 12;
            navigation.style.paddingRight = 8;
            navigation.style.paddingBottom = 12;
            navigation.style.paddingLeft = 8;
            navigation.style.borderRightWidth = 1;
            navigation.style.borderRightColor = AgentUi.Border1;
            workspace.Add(navigation);
            RegisterCallback<GeometryChangedEvent>(evt =>
            {
                var compact = evt.newRect.width < 1024f;
                workspace.style.flexDirection = FlexDirection.Row;
                navigation.style.width = compact ? 56 : 240;
                navigation.style.minWidth = compact ? 56 : 240;
                navigation.style.height = new Length(100, LengthUnit.Percent);
                navigation.style.minHeight = 0;
                navigation.style.flexDirection = FlexDirection.Column;
                navigation.style.paddingTop = 12;
                navigation.style.paddingRight = compact ? 6 : 8;
                navigation.style.paddingBottom = 12;
                navigation.style.paddingLeft = compact ? 6 : 8;
                navigation.style.borderRightWidth = 1;
                navigation.style.borderBottomWidth = 0;
                foreach (var cell in navigation.Children().OfType<AgentButton>())
                {
                    cell.ShowLabel(!compact);
                    cell.style.width = new Length(100, LengthUnit.Percent);
                    cell.style.flexGrow = 0;
                    cell.style.marginRight = 0;
                    cell.style.justifyContent = compact ? Justify.Center : Justify.FlexStart;
                }
            });

            _scroll = scrollContainer ?? AgentScrollContainer.CreateDefault();
            _scroll.Root.style.flexGrow = 1;
            _scroll.Root.style.minHeight = 0;
            _scroll.Root.style.marginRight = 4;
            _scroll.Content.style.paddingLeft = 24;
            _scroll.Content.style.paddingRight = 24;
            _scroll.Content.style.paddingTop = 16;
            _scroll.Content.style.paddingBottom = 24;
            _scroll.Content.style.minWidth = 0;
            _scroll.Content.style.maxWidth = new Length(100, LengthUnit.Percent);
            _scroll.Content.style.alignItems = Align.Stretch;
            workspace.Add(_scroll.Root);

            var providerCard = AgentUi.Card("Provider studio",
                "Configure endpoints and select only verified remote models or maintained offline fallbacks.");
            FlattenSettingsCard(providerCard);
            providerCard.style.maxWidth = StyleKeyword.None;
            providerCard.style.alignSelf = Align.Stretch;
            _scroll.Content.Add(providerCard);
            var profileBar = AgentUi.WrapRow();
            providerCard.Add(profileBar);
            _profiles = AgentUi.Dropdown("Profile", Array.Empty<string>());
            _profiles.style.minWidth = 240;
            _profiles.style.flexGrow = 1;
            _profiles.ValueFormatter = HumanProfileLabel;
            _profiles.OptionFormatter = HumanProfileLabel;
            _profiles.OptionDescriptionFormatter = label => label;
            _profiles.RegisterValueChangedCallback(_ => SelectProfileByLabel(_profiles.value));
            profileBar.Add(_profiles);
            profileBar.Add(AgentUi.Button("Add", "Add a provider profile.", AddProfile, 76,
                icon: AgentIconKind.Add));
            profileBar.Add(AgentUi.Button("Remove", "Remove the selected provider profile.", RemoveProfile, 112,
                AgentUi.Danger, AgentUi.Text, AgentIconKind.Delete));

            providerCard.Add(CreateSettingsGroupLabel("Endpoint"));
            _providerPreset = AgentUi.Dropdown("Provider preset",
                new[] { "Custom" }.Concat(AgentProviderCatalog.Providers.Select(PresetLabel)));
            _providerPreset.RegisterValueChangedCallback(_ => ApplyProviderPreset());
            providerCard.Add(_providerPreset);
            _name = AgentUi.Field("Display name", string.Empty, "Name shown in the chat composer.");
            providerCard.Add(_name);
            _protocol = AgentUi.Dropdown("API protocol", AgentProtocolIds.All);
            _protocol.RegisterValueChangedCallback(_ => ApplyProtocolDefaults());
            providerCard.Add(_protocol);
            _baseUrl = AgentUi.Field("Base URL", string.Empty, "HTTP(S) API root URL.");
            providerCard.Add(_baseUrl);
            providerCard.Add(CreateSettingsGroupLabel("Model"));
            _model = AgentUi.Dropdown("Default model", Array.Empty<string>());
            _model.style.minWidth = 0;
            _model.RegisterValueChangedCallback(evt =>
            {
                var profile = SelectedProfile();
                var preset = AgentProviderCatalog.FindProvider(profile);
                if (preset == null) return;
                ApplyModelPreset(AgentProviderCatalog.GetModel(preset.Id, evt.newValue));
            });
            providerCard.Add(_model);
            var modelCatalogRow = AgentUi.WrapRow();
            modelCatalogRow.style.marginTop = 4;
            _modelSource = new Label("MODEL CATALOG · WAITING");
            _modelSource.style.flexGrow = 1;
            _modelSource.style.flexShrink = 1;
            _modelSource.style.minWidth = 0;
            AgentUi.ApplyTypography(_modelSource, AgentTypography.Caption);
            _modelSource.style.color = AgentUi.TextSecondary;
            modelCatalogRow.Add(_modelSource);
            _refreshModels = AgentUi.Button("Refresh", "Refresh this provider's model catalog.",
                () => RunUiTask(DiscoverModelsAsync), 96, AgentUi.Surface3, AgentUi.TextSecondary,
                AgentIconKind.Refresh);
            _refreshModels.style.flexShrink = 0;
            modelCatalogRow.Add(_refreshModels);
            providerCard.Add(modelCatalogRow);
            _modelCatalogMessage = new Label();
            _modelCatalogMessage.style.display = DisplayStyle.None;
            _modelCatalogMessage.style.whiteSpace = WhiteSpace.Normal;
            _modelCatalogMessage.style.marginTop = 6;
            _modelCatalogMessage.style.marginBottom = 4;
            _modelCatalogMessage.style.paddingLeft = 10;
            _modelCatalogMessage.style.paddingRight = 10;
            _modelCatalogMessage.style.paddingTop = 8;
            _modelCatalogMessage.style.paddingBottom = 8;
            _modelCatalogMessage.style.borderLeftWidth = 3;
            AgentUi.ApplyTypography(_modelCatalogMessage, AgentTypography.Caption, false);
            providerCard.Add(_modelCatalogMessage);
            _effort = AgentUi.Dropdown("Default reasoning effort",
                new[] { "default", "none", "low", "medium", "high", "xhigh" });
            providerCard.Add(_effort);
            _maxTokens = new AgentIntegerField("Max output tokens") { value = 4096 };
            providerCard.Add(_maxTokens);
            _contextWindow = new AgentIntegerField("Context window tokens") { value = 128_000 };
            AgentTooltip.Attach(_contextWindow,
                "Used to reserve output space and compact HTTP conversation context before the provider limit.");
            providerCard.Add(_contextWindow);
            providerCard.Add(CreateSettingsGroupLabel("Credentials"));
            _localSecret = AgentUi.Field("Local API key", string.Empty,
                "Stored directly in this machine's providers.json. The password field displays asterisks matching the key length.", true);
            providerCard.Add(_localSecret);

            var defaults = AgentUi.Card("Agent defaults", "Applied to new conversations. The workspace is always this Unity project.");
            FlattenSettingsCard(defaults);
            _scroll.Content.Add(defaults);
            _permission = AgentUi.Dropdown("Default permission", new[]
            {
                AgentPermissionMode.ObserveOnly.ToString(), AgentPermissionMode.ConfirmWrites.ToString(),
                AgentPermissionMode.FullAccess.ToString()
            });
            defaults.Add(_permission);
            _toolTimeout = new AgentIntegerField("Default tool timeout (seconds)");
            AgentTooltip.Attach(_toolTimeout,
                "Used when a process, shell, or Unity eval tool call does not specify its own timeout.");
            defaults.Add(_toolTimeout);
            _maximumAgentSteps = new AgentIntegerField("Maximum model steps per turn");
            AgentTooltip.Attach(_maximumAgentSteps,
                "Stops a looping Agent turn with an explicit StepLimitReached result.");
            defaults.Add(_maximumAgentSteps);
            defaults.Add(CreateSettingsGroupLabel("System prompts"));
            _editorSystemPrompt = AgentUi.Field("Editor system prompt", string.Empty,
                "Instructions used while the Agent runs inside Unity Editor.");
            _editorSystemPrompt.multiline = true;
            _editorSystemPrompt.style.minHeight = 150;
            _editorSystemPrompt.style.whiteSpace = WhiteSpace.Normal;
            defaults.Add(_editorSystemPrompt);
            _runtimeSystemPrompt = AgentUi.Field("Runtime system prompt", string.Empty,
                "Instructions used in a built Player where UnityEditor and project files may be unavailable.");
            _runtimeSystemPrompt.multiline = true;
            _runtimeSystemPrompt.style.minHeight = 150;
            _runtimeSystemPrompt.style.whiteSpace = WhiteSpace.Normal;
            defaults.Add(_runtimeSystemPrompt);

            var agentsCard = AgentUi.Card("AGENTS.md discovery",
                "Ordered highest priority first. Availability and embedded Player snapshots are independent.");
            FlattenSettingsCard(agentsCard);
            _scroll.Content.Add(agentsCard);
            _agentsRoots = new AgentPathListEditor("AGENTS.md roots", "Add AGENTS.md root", false, ShowPathError);
            agentsCard.Add(_agentsRoots);

            var skillsCard = AgentUi.Card("Skills discovery",
                $"Each root may insert {AgentPaths.SettingsDirectoryName}, then always adds " +
                $"{AgentPaths.SkillDirectoryName}. Availability controls direct discovery; embedding independently " +
                "copies a build-time snapshot into Player.");
            FlattenSettingsCard(skillsCard);
            _scroll.Content.Add(skillsCard);
            _skillRoots = new AgentPathListEditor("Skill roots", "Add Skill root", true, ShowPathError);
            skillsCard.Add(_skillRoots);

            var fileCard = AgentUi.Card("Storage", "All machine-local user configuration has one fixed root. Histories use two fixed child folders.");
            FlattenSettingsCard(fileCard);
            _scroll.Content.Add(fileCard);
            var settingsPath = Path.Combine(AgentPaths.SettingsRoot, AgentPaths.SettingsFileName);
            var providerSettingsPath = Path.Combine(AgentPaths.SettingsRoot, AgentPaths.ProviderSettingsFileName);
            AddStoragePath(fileCard, "Machine settings", settingsPath);
            AddStoragePath(fileCard, "Provider settings", providerSettingsPath);
            fileCard.Add(new Label("Agent conversations: " +
                                   Path.Combine(AgentPaths.SettingsRoot, AgentPaths.AgentConversationsFolderName)));
            fileCard.Add(new Label("Command Line history: " +
                                   Path.Combine(AgentPaths.SettingsRoot, AgentPaths.CommandLineHistoryFolderName)));
            fileCard.Add(AgentUi.Button("Reload from disk", "Discard unsaved UI edits and reload settings.json and providers.json.",
                () => _showConfirmation("Reload settings?", "Discard unsaved changes in this page and reload settings.json and providers.json?",
                    () => RunUiTask(ReloadAsync)), 128));

            var projectCard = AgentUi.Card("Project Settings",
                "Package JSON defaults with an optional versioned project Resources override.");
            FlattenSettingsCard(projectCard);
            var projectActions = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            var openProject = AgentUi.Button("Open Unity Project Settings",
                "Open Project Settings > YuzeToolkit > Yuze Agent Tool to edit versioned project defaults.",
                UnityAgentEvalSettingsBridge.OpenProjectSettings, 220, AgentUi.Accent,
                AgentUi.AccentForeground, AgentIconKind.Settings);
            openProject.SetEnabled(UnityAgentEvalSettingsBridge.CanOpenProjectSettings);
            projectActions.Add(openProject);
            var overwriteProject = AgentUi.Button("Overwrite Project Settings",
                "Write the current provider-free configuration to the project Resources override.",
                () => _showConfirmation("Overwrite Project Settings?",
                    "Replace the project Resources defaults with the permission, prompts, Tool limits, and roots currently shown here?",
                    () => RunUiTask(OverwriteProjectSettingsAsync)), 220, AgentUi.Surface3,
                AgentUi.TextSecondary, AgentIconKind.Check);
            overwriteProject.SetEnabled(UnityAgentEvalSettingsBridge.CanOverwriteProjectSettings);
            projectActions.Add(overwriteProject);
            projectCard.Add(projectActions);
            var projectHint = new Label(UnityAgentEvalSettingsBridge.CanOpenProjectSettings
                ? "Valid machine settings stay unchanged; missing or invalid settings are rebuilt from the effective defaults."
                : "Project defaults are read-only in Player builds.");
            projectHint.style.color = AgentUi.Muted;
            projectHint.style.whiteSpace = WhiteSpace.Normal;
            projectCard.Add(projectHint);

            _evalConnection = new AgentEvalConnectionSettingsView();
            _evalTools = new AgentEvalToolsSettingsView();

            var archivedConversationCard = AgentUi.Card("Archived conversations",
                "Archived Agent conversations stay out of the main sidebar. Restore or permanently delete them here.");
            FlattenSettingsCard(archivedConversationCard);
            _archivedConversations = new VisualElement();
            archivedConversationCard.Add(_archivedConversations);

            var archivedCommandCard = AgentUi.Card("Archived command lines",
                "Archived Command Line transcripts are managed separately from Agent conversations.");
            FlattenSettingsCard(archivedCommandCard);
            _archivedCommandLines = new VisualElement();
            archivedCommandCard.Add(_archivedCommandLines);

            VisualElement CreatePage(params VisualElement[] cards)
            {
                var page = new VisualElement();
                page.style.minWidth = 0;
                page.style.width = new Length(100, LengthUnit.Percent);
                foreach (var card in cards) page.Add(card);
                return page;
            }
            _scroll.Content.Clear();
            var providersPage = CreatePage(providerCard);
            var configurationPage = CreatePage(defaults, agentsCard, skillsCard, fileCard, projectCard);
            var evalConnectionPage = CreatePage(_evalConnection);
            var evalToolsPage = CreatePage(_evalTools);
            var archivedConversationsPage = CreatePage(archivedConversationCard);
            var archivedCommandLinesPage = CreatePage(archivedCommandCard);
            var settingsPages = new[]
            {
                providersPage, configurationPage, evalConnectionPage, evalToolsPage,
                archivedConversationsPage, archivedCommandLinesPage
            };
            foreach (var page in settingsPages) _scroll.Content.Add(page);

            var navigationButtons = new List<AgentButton>();
            void AddNavigation(string label, AgentIconKind icon, VisualElement target, bool selected = false)
            {
                AgentButton? button = null;
                button = CreateSettingsNavigation(label, icon, () =>
                {
                    foreach (var candidate in navigationButtons)
                        SetSettingsNavigationSelected(candidate, candidate == button);
                    foreach (var page in settingsPages)
                        page.style.display = ReferenceEquals(page, target) ? DisplayStyle.Flex : DisplayStyle.None;
                    if (_scroll.Root is ScrollView view)
                        view.schedule.Execute(() => view.scrollOffset = Vector2.zero);
                });
                navigationButtons.Add(button);
                navigation.Add(button);
                SetSettingsNavigationSelected(button, selected);
            }
            for (var index = 1; index < settingsPages.Length; index++)
                settingsPages[index].style.display = DisplayStyle.None;
            AddNavigation("Model providers", AgentIconKind.Provider, providersPage, true);
            AddNavigation("Configuration", AgentIconKind.Sliders, configurationPage);
            AddNavigation("Eval connection", AgentIconKind.Folder, evalConnectionPage);
            AddNavigation("Eval Tools", AgentIconKind.Provider, evalToolsPage);
            AddNavigation("Archived conversations", AgentIconKind.Archive, archivedConversationsPage);
            AddNavigation("Archived command lines", AgentIconKind.History, archivedCommandLinesPage);

            RunUiTask(InitializeAsync);
        }

        private static AgentButton CreateSettingsNavigation(string text, AgentIconKind icon, Action clicked)
        {
            var button = AgentUi.Button(text, text, clicked, 0, AgentUi.Transparent,
                AgentUi.TextSecondary, icon);
            button.style.height = 40;
            button.style.flexGrow = 0;
            button.style.justifyContent = Justify.FlexStart;
            button.style.marginBottom = 4;
            button.style.borderTopLeftRadius = 12;
            button.style.borderTopRightRadius = 12;
            button.style.borderBottomLeftRadius = 12;
            button.style.borderBottomRightRadius = 12;
            button.AddToClassList("unity-agent-settings-navigation-cell");
            return button;
        }

        private static void SetSettingsNavigationSelected(AgentButton button, bool selected)
        {
            button.SetPalette(selected ? AgentUi.Active : AgentUi.Transparent,
                selected ? AgentUi.Accent : AgentUi.TextSecondary);
        }

        private static Label CreateSettingsGroupLabel(string text)
        {
            var label = new Label(text);
            AgentUi.ApplyTypography(label, AgentTypography.Caption);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = AgentUi.TextSecondary;
            label.style.marginTop = 12;
            label.style.marginBottom = 2;
            return label;
        }

        private static void FlattenSettingsCard(VisualElement card)
        {
            card.style.backgroundColor = AgentUi.Transparent;
            AgentUi.SetBorder(card, AgentUi.Transparent, 0);
        }

        private static void AddStoragePath(VisualElement parent, string label, string path)
        {
            var value = new Label(label + ": " + path);
            value.style.whiteSpace = WhiteSpace.Normal;
            value.style.color = AgentUi.Muted;
            AgentTooltip.Attach(value, path);
            parent.Add(value);
        }

        public void Tick()
        {
            if (_disposed || !_initialized) return;
            // Settings edits are deliberately not overwritten by background Host revisions.
            var revision = _host.Revision;
            if (revision != _lastRevision)
            {
                _lastRevision = revision;
                RefreshArchives();
            }
            _evalConnection.Tick();
            _evalTools.Tick();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _evalTools.Dispose();
        }

        private async Task InitializeAsync()
        {
            await _host.EnsureInitializedAsync(_lifetime.Token);
            _editing = _host.Settings;
            _selectedProfileId = _editing.DefaultProviderProfileId;
            RefreshProfileChoices();
            LoadSelectedProfile();
            _permission.SetValueWithoutNotify(_editing.PermissionMode.ToString());
            _toolTimeout.SetValueWithoutNotify(_editing.DefaultToolTimeoutSeconds);
            _maximumAgentSteps.SetValueWithoutNotify(_editing.MaximumAgentSteps);
            _editorSystemPrompt.SetValueWithoutNotify(_editing.EditorSystemPrompt);
            _runtimeSystemPrompt.SetValueWithoutNotify(_editing.RuntimeSystemPrompt);
            _agentsRoots.SetItems(_editing.AgentsRoots);
            _skillRoots.SetItems(_editing.SkillRoots);
            RefreshArchives();
            _initialized = true;
            _lastRevision = _host.Revision;
            _status.text = "Ready";
            await DiscoverSettingsModelsOnceAsync(_selectedProfileId);
        }

        private async Task SaveAsync()
        {
            SaveSelectedProfileFields();
            var missingModel = _editing.ProviderProfiles.FirstOrDefault(value => string.IsNullOrWhiteSpace(value.Model));
            if (missingModel != null)
                throw new InvalidOperationException($"Provider “{missingModel.Name}” has no selected model. Refresh its catalog or choose a curated fallback before saving.");
            CollectConfigurationFields();
            _editing.DefaultProviderProfileId = _selectedProfileId;
            await _host.SaveSettingsAsync(_editing, _lifetime.Token);
            _editing = _host.Settings;
            _status.text = "Saved  ·  " + DateTime.Now.ToString("HH:mm:ss");
        }

        private Task OverwriteProjectSettingsAsync()
        {
            CollectConfigurationFields();
            UnityAgentEvalSettingsBridge.OverwriteProjectSettings(_editing);
            _status.text = "Project Settings overwritten  ·  " + DateTime.Now.ToString("HH:mm:ss");
            return Task.CompletedTask;
        }

        private void CollectConfigurationFields()
        {
            if (!Enum.TryParse<AgentPermissionMode>(_permission.value, out var permission))
                throw new InvalidOperationException("Permission mode is invalid.");
            if (string.IsNullOrWhiteSpace(_editorSystemPrompt.value))
                throw new InvalidOperationException("Editor system prompt is required.");
            if (string.IsNullOrWhiteSpace(_runtimeSystemPrompt.value))
                throw new InvalidOperationException("Runtime system prompt is required.");
            if (_toolTimeout.value < 1)
                throw new InvalidOperationException("Default Tool timeout must be positive.");
            if (_maximumAgentSteps.value < 1)
                throw new InvalidOperationException("Maximum Agent steps must be positive.");
            _editing.PermissionMode = permission;
            _editing.DefaultToolTimeoutSeconds = _toolTimeout.value;
            _editing.MaximumAgentSteps = _maximumAgentSteps.value;
            _editing.EditorSystemPrompt = _editorSystemPrompt.value;
            _editing.RuntimeSystemPrompt = _runtimeSystemPrompt.value;
            _editing.AgentsRoots = _agentsRoots.GetItems();
            _editing.SkillRoots = _skillRoots.GetItems();
        }

        private async Task DiscoverModelsAsync()
        {
            SaveSelectedProfileFields();
            _refreshModels.SetEnabled(false);
            SetSettingsCatalogState("MODEL CATALOG · REFRESHING", AgentUi.Muted);
            try
            {
                var result = await _host.DiscoverModelsAsync(SelectedProfile(), _lifetime.Token);
                ApplySettingsModelCatalog(result.Models, _model.value);
                SetSettingsDiscoveryState(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ApplySettingsCuratedCatalog(SelectedProfile(), _model.value);
                SetSettingsCatalogState("MODEL CATALOG · FALLBACK — REFRESH AVAILABLE", AgentUi.Warning,
                    exception.Message);
            }
            finally
            {
                _refreshModels.SetEnabled(true);
            }
        }

        private Task DiscoverSettingsModelsOnceAsync(string profileId)
        {
            if (!_discoveryStartedProfiles.Add(profileId)) return Task.CompletedTask;
            return DiscoverSettingsModelsForProfileAsync(profileId);
        }

        private async Task DiscoverSettingsModelsForProfileAsync(string profileId)
        {
            var profile = _editing.ProviderProfiles.FirstOrDefault(value => value.Id == profileId);
            if (profile == null) return;
            try
            {
                var result = await _host.DiscoverModelsAsync(profile, _lifetime.Token);
                if (_selectedProfileId != profileId) return;
                ApplySettingsModelCatalog(result.Models, _model.value);
                SetSettingsDiscoveryState(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (_selectedProfileId != profileId) return;
                ApplySettingsCuratedCatalog(profile, _model.value);
                SetSettingsCatalogState("MODEL CATALOG · FALLBACK — REFRESH AVAILABLE", AgentUi.Warning,
                    exception.Message);
            }
        }

        private async Task ReloadAsync()
        {
            await _host.ReloadSettingsFromDiskAsync(_lifetime.Token);
            _editing = _host.Settings;
            _selectedProfileId = _editing.DefaultProviderProfileId;
            RefreshProfileChoices();
            LoadSelectedProfile();
            _permission.SetValueWithoutNotify(_editing.PermissionMode.ToString());
            _toolTimeout.SetValueWithoutNotify(_editing.DefaultToolTimeoutSeconds);
            _maximumAgentSteps.SetValueWithoutNotify(_editing.MaximumAgentSteps);
            _editorSystemPrompt.SetValueWithoutNotify(_editing.EditorSystemPrompt);
            _runtimeSystemPrompt.SetValueWithoutNotify(_editing.RuntimeSystemPrompt);
            _agentsRoots.SetItems(_editing.AgentsRoots);
            _skillRoots.SetItems(_editing.SkillRoots);
            RefreshArchives();
            _status.text = "Reloaded from disk";
        }

        private void AddProfile()
        {
            SaveSelectedProfileFields();
            var profile = new AgentProviderProfile { Name = "New provider", ProviderPresetId = "custom" };
            _editing.ProviderProfiles.Add(profile);
            _selectedProfileId = profile.Id;
            RefreshProfileChoices();
            LoadSelectedProfile();
            RunUiTask(() => DiscoverSettingsModelsOnceAsync(profile.Id));
        }

        private void RemoveProfile()
        {
            if (_editing.ProviderProfiles.Count <= 1)
            {
                _showError("Provider required", "At least one provider profile is required.");
                return;
            }
            var profile = SelectedProfile();
            _showConfirmation("Remove provider?", $"Remove provider profile “{profile.Name}”?", () =>
            {
                _editing.ProviderProfiles.RemoveAll(value => value.Id == _selectedProfileId);
                _selectedProfileId = _editing.ProviderProfiles[0].Id;
                RefreshProfileChoices();
                LoadSelectedProfile();
            });
        }

        private void SelectProfileByLabel(string label)
        {
            SaveSelectedProfileFields();
            var profile = _editing.ProviderProfiles.FirstOrDefault(value => ProfileLabel(value) == label);
            if (profile == null) return;
            _selectedProfileId = profile.Id;
            LoadSelectedProfile();
            RunUiTask(() => DiscoverSettingsModelsOnceAsync(profile.Id));
        }

        private void SaveSelectedProfileFields()
        {
            var profile = _editing.ProviderProfiles.FirstOrDefault(value => value.Id == _selectedProfileId);
            if (profile == null) return;
            profile.Name = string.IsNullOrWhiteSpace(_name.value) ? "Provider" : _name.value.Trim();
            var selectedPreset = AgentProviderCatalog.Providers.FirstOrDefault(value =>
                PresetLabel(value) == _providerPreset.value);
            profile.ProviderPresetId = selectedPreset?.Id ?? "custom";
            profile.Protocol = _protocol.value;
            profile.BaseUrl = _baseUrl.value.Trim();
            profile.Model = _model.value;
            profile.ReasoningEffort = _effort.value == "default" ? string.Empty : _effort.value;
            profile.MaxOutputTokens = Math.Max(1, _maxTokens.value);
            profile.ContextWindowTokens = Math.Max(8_192, _contextWindow.value);
            profile.ApiKey = _localSecret.value;
            RefreshProfileChoices(false);
        }

        private void LoadSelectedProfile()
        {
            var profile = SelectedProfile();
            _profiles.SetValueWithoutNotify(ProfileLabel(profile));
            var preset = AgentProviderCatalog.FindProvider(profile.ProviderPresetId);
            _providerPreset.SetValueWithoutNotify(preset == null ? "Custom" : PresetLabel(preset));
            _name.SetValueWithoutNotify(profile.Name);
            _protocol.SetValueWithoutNotify(profile.Protocol);
            _baseUrl.SetValueWithoutNotify(profile.BaseUrl);
            ApplySettingsCuratedCatalog(profile, profile.Model);
            _effort.SetValueWithoutNotify(string.IsNullOrWhiteSpace(profile.ReasoningEffort)
                ? "default"
                : profile.ReasoningEffort);
            EnsureChoice(_effort, _effort.value);
            _maxTokens.SetValueWithoutNotify(profile.MaxOutputTokens);
            _contextWindow.SetValueWithoutNotify(profile.ContextWindowTokens);
            _localSecret.SetValueWithoutNotify(profile.ApiKey);
        }

        private void ApplySettingsModelCatalog(IEnumerable<AgentModelOption> source, string preferred)
        {
            var options = source.GroupBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => value.First()).ToList();
            var displayNames = options.ToDictionary(value => value.Id,
                value => string.IsNullOrWhiteSpace(value.DisplayName) ? value.Id : value.DisplayName,
                StringComparer.Ordinal);
            _model.OptionFormatter = id => displayNames.TryGetValue(id, out var name) ? name : id;
            _model.ValueFormatter = _model.OptionFormatter;
            _model.OptionDescriptionFormatter = id =>
                displayNames.TryGetValue(id, out var name) && !string.Equals(name, id, StringComparison.Ordinal)
                    ? id
                    : string.Empty;
            _model.choices = options.Select(value => value.Id).ToList();
            var selected = _model.choices.Contains(preferred)
                ? preferred
                : _model.choices.FirstOrDefault() ?? string.Empty;
            _model.SetValueWithoutNotify(selected);
            _model.SetEnabled(_model.choices.Count > 0);
            ApplyModelOption(options.FirstOrDefault(value => value.Id == selected));
        }

        private void ApplySettingsCuratedCatalog(AgentProviderProfile profile, string preferred)
        {
            var models = AgentProviderCatalog.GetModels(profile.ProviderPresetId);
            var displayNames = models.ToDictionary(value => value.Id,
                value => string.IsNullOrWhiteSpace(value.DisplayName) ? value.Id : value.DisplayName,
                StringComparer.Ordinal);
            _model.OptionFormatter = id => displayNames.TryGetValue(id, out var name) ? name : id;
            _model.ValueFormatter = _model.OptionFormatter;
            _model.OptionDescriptionFormatter = id =>
                displayNames.TryGetValue(id, out var name) && !string.Equals(name, id, StringComparison.Ordinal)
                    ? id
                    : string.Empty;
            _model.choices = models.Select(value => value.Id).Distinct(StringComparer.Ordinal).ToList();
            var selected = _model.choices.Contains(preferred)
                ? preferred
                : _model.choices.Contains(profile.Model)
                    ? profile.Model
                    : _model.choices.FirstOrDefault() ?? string.Empty;
            _model.SetValueWithoutNotify(selected);
            _model.SetEnabled(_model.choices.Count > 0);
            ApplyModelPreset(models.FirstOrDefault(value => value.Id == selected));
            SetSettingsCatalogState(_model.choices.Count == 0
                    ? "MODEL CATALOG · UNAVAILABLE — REFRESH REQUIRED"
                    : "MODEL CATALOG · CURATED FALLBACK — REFRESH AVAILABLE",
                _model.choices.Count == 0 ? AgentUi.Error : AgentUi.Warning);
        }

        private void SetSettingsDiscoveryState(AgentModelDiscoveryResult discovery)
        {
            if (discovery.Models.Count == 0)
            {
                SetSettingsCatalogState("MODEL CATALOG · NO MODELS — REFRESH REQUIRED", AgentUi.Error,
                    discovery.Warning);
                return;
            }
            var fallback = discovery.Source != AgentModelDiscoverySource.Remote;
            SetSettingsCatalogState(fallback
                    ? "MODEL CATALOG · CURATED FALLBACK — REFRESH AVAILABLE"
                    : $"MODEL CATALOG · REMOTE · {discovery.Models.Count}",
                fallback ? AgentUi.Warning : AgentUi.Success, discovery.Warning);
        }

        private void SetSettingsCatalogState(string text, Color color, string tooltip = "")
        {
            var state = CatalogMenuState(text);
            _modelSource.text = state switch
            {
                AgentChoiceMenuState.Loading => "Loading models...",
                AgentChoiceMenuState.Error => "Models unavailable",
                AgentChoiceMenuState.Warning => "Using curated models",
                _ => "Models ready"
            };
            _modelSource.style.color = color;
            var detail = string.IsNullOrWhiteSpace(tooltip) ? HumanCatalogMessage(state) : tooltip;
            var showDetail = state is AgentChoiceMenuState.Error or AgentChoiceMenuState.Warning;
            _modelCatalogMessage.text = detail;
            _modelCatalogMessage.style.display = showDetail && !string.IsNullOrWhiteSpace(detail)
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            _modelCatalogMessage.style.color = state == AgentChoiceMenuState.Error
                ? AgentUi.Error
                : AgentUi.Warning;
            _modelCatalogMessage.style.backgroundColor = state == AgentChoiceMenuState.Error
                ? AgentUi.ErrorPanel
                : AgentUi.WarningPanel;
            _modelCatalogMessage.style.borderLeftColor = state == AgentChoiceMenuState.Error
                ? AgentUi.Error
                : AgentUi.Warning;
            _model.SetMenuStatus(state, HumanCatalogMessage(state),
                () => RunUiTask(DiscoverModelsAsync));
        }

        private static string HumanCatalogMessage(AgentChoiceMenuState state) => state switch
        {
            AgentChoiceMenuState.Loading => "Loading the model catalog...",
            AgentChoiceMenuState.Empty => "No models are available. Check the provider settings, then refresh.",
            AgentChoiceMenuState.Error => "No models are available. Check the provider settings, then refresh.",
            AgentChoiceMenuState.Warning => "Using curated fallback models.",
            _ => string.Empty
        };

        private static AgentChoiceMenuState CatalogMenuState(string text)
        {
            if (text.IndexOf("WAIT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("REFRESHING", StringComparison.OrdinalIgnoreCase) >= 0)
                return AgentChoiceMenuState.Loading;
            if (text.IndexOf("NO MODELS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                return AgentChoiceMenuState.Error;
            return text.IndexOf("FALLBACK", StringComparison.OrdinalIgnoreCase) >= 0
                ? AgentChoiceMenuState.Warning
                : AgentChoiceMenuState.Ready;
        }

        private void ApplyProviderPreset()
        {
            if (!_initialized) return;
            if (_providerPreset.value == "Custom") return;
            var preset = AgentProviderCatalog.Providers.FirstOrDefault(value => PresetLabel(value) == _providerPreset.value);
            if (preset == null) return;
            var profile = SelectedProfile();
            AgentProviderCatalog.ApplyPreset(profile, preset.Id);
            LoadSelectedProfile();
            _status.text = preset.DisplayName + " defaults applied";
        }

        private void ApplyModelOption(AgentModelOption? option)
        {
            if (option == null) return;
            _model.value = option.Id;
            var efforts = option.ReasoningEfforts.Count == 0
                ? new[] { "default" }
                : new[] { "default" }.Concat(option.ReasoningEfforts).Distinct(StringComparer.Ordinal).ToArray();
            _effort.choices = efforts.ToList();
            _effort.SetValueWithoutNotify(string.IsNullOrWhiteSpace(option.DefaultReasoningEffort)
                ? "default"
                : option.DefaultReasoningEffort);
            if (option.RecommendedOutputTokens > 0) _maxTokens.value = option.RecommendedOutputTokens;
            if (option.ContextTokens > 0) _contextWindow.value = option.ContextTokens;
        }

        private void ApplyModelPreset(AgentModelPreset? model)
        {
            if (model == null) return;
            var efforts = model.ReasoningEfforts.Count == 0
                ? new[] { "default" }
                : new[] { "default" }.Concat(model.ReasoningEfforts).Distinct(StringComparer.Ordinal).ToArray();
            _effort.choices = efforts.ToList();
            _effort.SetValueWithoutNotify(string.IsNullOrWhiteSpace(model.DefaultReasoningEffort)
                ? "default"
                : model.DefaultReasoningEffort);
            if (model.RecommendedOutputTokens > 0) _maxTokens.value = model.RecommendedOutputTokens;
            if (model.ContextTokens > 0) _contextWindow.value = model.ContextTokens;
        }

        private void ApplyProtocolDefaults()
        {
            if (_protocol.value == AgentProtocolIds.AnthropicMessages)
            {
                if (string.IsNullOrWhiteSpace(_baseUrl.value))
                    _baseUrl.value = "https://api.anthropic.com/v1/";
            }
            else if (string.IsNullOrWhiteSpace(_baseUrl.value))
            {
                _baseUrl.value = "https://api.openai.com/v1/";
            }
        }

        private void RefreshArchives()
        {
            _archivedConversations.Clear();
            var conversations = _host.GetSessions().Where(value => value.IsArchived)
                .OrderByDescending(value => value.UpdatedAtUtc).ToList();
            if (conversations.Count == 0)
                _archivedConversations.Add(ArchiveEmpty("No archived Agent conversations."));
            foreach (var session in conversations)
                _archivedConversations.Add(CreateArchivedConversationRow(session));

            _archivedCommandLines.Clear();
            var commands = _commandLineStore.Load().Where(value => value.IsArchived)
                .OrderByDescending(value => value.UpdatedAtUtc).ToList();
            if (commands.Count == 0)
                _archivedCommandLines.Add(ArchiveEmpty("No archived Command Line transcripts."));
            foreach (var session in commands)
                _archivedCommandLines.Add(CreateArchivedCommandLineRow(session));
        }

        private VisualElement CreateArchivedConversationRow(AgentSessionDocument session)
        {
            var row = AgentUi.Inset();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            var title = new Label(session.Title) { style = { flexGrow = 1, minWidth = 0 } };
            title.style.overflow = Overflow.Hidden;
            title.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(title);
            row.Add(AgentUi.Button("Restore", "Move this conversation back to the main sidebar.",
                () => RunUiTask(async () =>
                {
                    await _host.UpdateSessionOrganizationAsync(session.Id, session.IsPinned, false,
                        Math.Max(0, session.SortOrder), _lifetime.Token);
                    RefreshArchives();
                }), 82));
            row.Add(AgentUi.Button("Delete", "Permanently delete this archived conversation.",
                () => _showConfirmation("Delete archived conversation?",
                    $"Delete “{session.Title}” and its persisted transcript? This cannot be undone.",
                    () => RunUiTask(async () =>
                    {
                        await _host.DeleteSessionAsync(session.Id, _lifetime.Token);
                        RefreshArchives();
                    })), 82, AgentUi.Danger));
            return row;
        }

        private VisualElement CreateArchivedCommandLineRow(CommandLineSessionDocument session)
        {
            var row = AgentUi.Inset();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            var title = new Label(session.Title) { style = { flexGrow = 1, minWidth = 0 } };
            title.style.overflow = Overflow.Hidden;
            title.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(title);
            row.Add(AgentUi.Button("Restore", "Move this Command Line transcript back to the main sidebar.",
                () =>
                {
                    session.IsArchived = false;
                    _commandLineStore.Save(session);
                    RefreshArchives();
                }, 82));
            row.Add(AgentUi.Button("Delete", "Permanently delete this archived Command Line transcript.",
                () => _showConfirmation("Delete archived command line?",
                    $"Delete “{session.Title}” and its persisted transcript? This cannot be undone.", () =>
                    {
                        _commandLineStore.Delete(session.Id);
                        RefreshArchives();
                    }), 82, AgentUi.Danger));
            return row;
        }

        private static Label ArchiveEmpty(string message)
        {
            var label = new Label(message);
            label.style.color = AgentUi.Muted;
            label.style.marginTop = 8;
            return label;
        }

        private void ShowPathError(string message) => _showError("Invalid path", message);

        private AgentProviderProfile SelectedProfile() =>
            _editing.ProviderProfiles.First(value => value.Id == _selectedProfileId);

        private void RefreshProfileChoices(bool update = true)
        {
            _profiles.choices = _editing.ProviderProfiles.Select(ProfileLabel).ToList();
            if (update) _profiles.SetValueWithoutNotify(ProfileLabel(SelectedProfile()));
        }

        private async void RunUiTask(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _showError("Yuze Agent Tool settings", exception.Message);
            }
        }

        private static void EnsureChoice(AgentChoiceField field, string value)
        {
            if (field.choices.Contains(value)) return;
            var choices = field.choices.ToList();
            choices.Add(value);
            field.choices = choices;
        }

        private static string PresetLabel(AgentProviderPreset preset) => preset.DisplayName + "  ·  " + preset.Id;
        private static string ProfileLabel(AgentProviderProfile profile) =>
            profile.Name + "  ·  " + profile.Protocol + "  ·  " +
            (profile.Id.Length <= 6 ? profile.Id : profile.Id.Substring(0, 6));

        private static string HumanProfileLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "Provider profile";
            var separator = label.IndexOf("  ·  ", StringComparison.Ordinal);
            return separator < 0 ? label : label.Substring(0, separator);
        }
    }

    internal sealed class AgentPathListEditor : VisualElement
    {
        private readonly string _addLabel;
        private readonly bool _isSkillRoot;
        private readonly Action<string> _showError;
        private readonly VisualElement _list;
        private readonly List<AgentPathLocation> _items = new();
        private readonly List<AgentPathLocationEditor> _editors = new();

        public AgentPathListEditor(string label, string addLabel, bool isSkillRoot, Action<string> showError)
        {
            _addLabel = addLabel;
            _isSkillRoot = isSkillRoot;
            _showError = showError;
            var heading = new Label(label);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.marginBottom = 5;
            Add(heading);
            _list = new VisualElement();
            Add(_list);
            Add(AgentUi.Button(_addLabel, "Add a lower-priority root.", AddItem, 160,
                icon: AgentIconKind.Add));
        }

        public event Action? Changed;

        public void SetItems(IEnumerable<AgentPathLocation> items)
        {
            _items.Clear();
            _items.AddRange(items.Select(Clone));
            Refresh();
        }

        public List<AgentPathLocation> GetItems()
        {
            var drafts = _editors.Select(value => value.GetValue()).ToList();
            var duplicate = drafts.GroupBy(value => value.Id, StringComparer.Ordinal)
                .FirstOrDefault(value => value.Count() > 1);
            if (duplicate != null) throw new InvalidOperationException("Path root ids must be unique.");
            return drafts;
        }

        private void AddItem()
        {
            _items.Add(new AgentPathLocation
            {
                BasePath = AgentPathBase.ProjectRoot,
                UseUnityAgentToolDirectory = true,
                RelativePath = string.Empty,
                Scope = AgentPathScope.All,
                EmbedInPlayerBuild = false
            });
            Refresh();
            Changed?.Invoke();
        }

        private void Refresh()
        {
            _list.Clear();
            _editors.Clear();
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var index = i;
                var card = AgentUi.Inset();
                card.style.marginBottom = 7;
                var top = AgentUi.WrapRow();
                var priority = new Label("Priority " + (i + 1));
                priority.style.flexGrow = 1;
                priority.style.unityFontStyleAndWeight = FontStyle.Bold;
                top.Add(priority);
                top.Add(AgentUi.IconButton(AgentIconKind.ChevronUp, "Raise priority", () => Move(index, -1), 30));
                top.Add(AgentUi.IconButton(AgentIconKind.ChevronDown, "Lower priority", () => Move(index, 1), 30));
                top.Add(AgentUi.IconButton(AgentIconKind.Delete, "Remove root", () => Remove(index), 30, AgentUi.Danger));
                card.Add(top);
                var editor = new AgentPathLocationEditor(true, _isSkillRoot, _showError);
                editor.SetValue(item);
                _editors.Add(editor);
                editor.Changed += value =>
                {
                    value.Id = item.Id;
                    _items[index] = value;
                    Changed?.Invoke();
                };
                card.Add(editor);
                _list.Add(card);
            }
            if (_items.Count == 0)
            {
                var empty = new Label("No roots configured. Add one to enable discovery.");
                empty.style.color = AgentUi.Muted;
                empty.style.marginBottom = 8;
                _list.Add(empty);
            }
        }

        private void Move(int index, int delta)
        {
            var target = Mathf.Clamp(index + delta, 0, _items.Count - 1);
            if (target == index) return;
            var item = _items[index];
            _items.RemoveAt(index);
            _items.Insert(target, item);
            Refresh();
            Changed?.Invoke();
        }

        private void Remove(int index)
        {
            _items.RemoveAt(index);
            Refresh();
            Changed?.Invoke();
        }

        private static AgentPathLocation Clone(AgentPathLocation value) => new()
        {
            Id = value.Id,
            BasePath = value.BasePath,
            UseUnityAgentToolDirectory = value.UseUnityAgentToolDirectory,
            RelativePath = value.RelativePath,
            Scope = value.Scope,
            EmbedInPlayerBuild = value.EmbedInPlayerBuild
        };
    }

    internal sealed class AgentPathLocationEditor : VisualElement
    {
        private static readonly string[] ScopeOptions = { "None", "Editor only", "Player only", "All" };
        private readonly AgentChoiceField _basePath;
        private readonly AgentChoiceField _scope;
        private readonly AgentTextField _relativePath;
        private readonly AgentToggle _useUnityAgentToolDirectory;
        private readonly AgentToggle? _embedInPlayerBuild;
        private readonly Label _preview;
        private readonly bool _isSkillRoot;
        private readonly Action<string> _showError;
        private string _id = Guid.NewGuid().ToString("N");

        public AgentPathLocationEditor(bool showBuildToggle, bool isSkillRoot, Action<string> showError)
        {
            _isSkillRoot = isSkillRoot;
            _showError = showError;
            style.minWidth = 0;
            style.width = new Length(100, LengthUnit.Percent);

            // Keep each path field on its own full-width line. The Project Settings host owns
            // the available content width, so fixed columns make the page look split and force
            // the fields to compete with the host's navigation pane at smaller sizes.
            var fieldsRow = new VisualElement();
            fieldsRow.name = "unity-agent-path-fields";
            fieldsRow.style.minWidth = 0;
            fieldsRow.style.width = new Length(100, LengthUnit.Percent);
            fieldsRow.style.flexDirection = FlexDirection.Column;
            fieldsRow.style.flexWrap = Wrap.NoWrap;
            fieldsRow.style.alignItems = Align.Stretch;
            fieldsRow.style.marginBottom = 2;
            Add(fieldsRow);
            _basePath = AgentUi.Dropdown("Base", Enum.GetNames(typeof(AgentPathBase)));
            _basePath.style.width = new Length(100, LengthUnit.Percent);
            _basePath.style.minWidth = 0;
            _basePath.style.flexGrow = 0;
            _basePath.style.flexShrink = 0;
            _basePath.style.marginRight = 0;
            _basePath.RegisterValueChangedCallback(_ => ChangedByUser());
            fieldsRow.Add(_basePath);
            _scope = AgentUi.Dropdown("Available in", ScopeOptions);
            _scope.style.width = new Length(100, LengthUnit.Percent);
            _scope.style.minWidth = 0;
            _scope.style.flexGrow = 0;
            _scope.style.flexShrink = 0;
            _scope.style.marginRight = 0;
            AgentTooltip.Attach(_scope,
                "Controls direct path discovery. Embedded Player content is independent of this scope.");
            _scope.RegisterValueChangedCallback(_ => ChangedByUser());
            fieldsRow.Add(_scope);
            _relativePath = AgentUi.Field("Relative path", string.Empty,
                isSkillRoot
                    ? "Optional child path after the selected base, optional .unityagenttool, and fixed .agents/skills folders."
                    : "Optional path after the selected base and optional .unityagenttool folder. Absolute paths are rejected.");
            _relativePath.style.width = new Length(100, LengthUnit.Percent);
            _relativePath.style.minWidth = 0;
            _relativePath.style.flexGrow = 0;
            _relativePath.style.flexShrink = 0;
            _relativePath.style.flexBasis = StyleKeyword.Auto;
            _relativePath.RegisterValueChangedCallback(_ => ChangedByUser());
            fieldsRow.Add(_relativePath);

            var optionsRow = AgentUi.WrapRow();
            optionsRow.name = "unity-agent-path-options";
            optionsRow.style.width = new Length(100, LengthUnit.Percent);
            optionsRow.style.minWidth = 0;
            optionsRow.style.marginTop = 2;
            optionsRow.style.marginBottom = 2;
            optionsRow.style.alignItems = Align.Center;
            Add(optionsRow);
            _useUnityAgentToolDirectory = new AgentToggle("Use .unityagenttool");
            _useUnityAgentToolDirectory.style.marginRight = 12;
            AgentTooltip.Attach(_useUnityAgentToolDirectory,
                "Insert the .unityagenttool folder below the selected base before resolving this root.");
            _useUnityAgentToolDirectory.RegisterValueChangedCallback(_ => ChangedByUser());
            optionsRow.Add(_useUnityAgentToolDirectory);
            if (showBuildToggle)
            {
                _embedInPlayerBuild = new AgentToggle("Embed in Player build");
                AgentTooltip.Attach(_embedInPlayerBuild,
                    "Copy a build-time snapshot into Player content regardless of the availability scope. " +
                    "Review external files before enabling.");
                _embedInPlayerBuild.RegisterValueChangedCallback(_ => ChangedByUser());
                optionsRow.Add(_embedInPlayerBuild);
            }
            _preview = new Label();
            AgentUi.ApplyTypography(_preview, AgentTypography.Caption, false);
            _preview.style.color = AgentUi.Muted;
            _preview.style.whiteSpace = WhiteSpace.Normal;
            _preview.style.minWidth = 0;
            _preview.style.width = new Length(100, LengthUnit.Percent);
            _preview.style.maxWidth = new Length(100, LengthUnit.Percent);
            _preview.style.marginTop = 4;
            _preview.style.paddingLeft = 1;
            _preview.style.paddingRight = 1;
            Add(_preview);
        }

        public event Action<AgentPathLocation>? Changed;

        public void SetValue(AgentPathLocation value)
        {
            _id = value.Id;
            _basePath.SetValueWithoutNotify(value.BasePath.ToString());
            _scope.SetValueWithoutNotify(GetScopeLabel(value.Scope));
            _relativePath.SetValueWithoutNotify(value.RelativePath);
            _useUnityAgentToolDirectory.SetValueWithoutNotify(value.UseUnityAgentToolDirectory);
            _embedInPlayerBuild?.SetValueWithoutNotify(value.EmbedInPlayerBuild);
            RefreshPreview();
        }

        public AgentPathLocation GetValue()
        {
            var value = CreateValue();
            AgentPaths.Validate(value);
            return value;
        }

        private AgentPathLocation CreateValue()
        {
            var basePath = Enum.TryParse<AgentPathBase>(_basePath.value, out var parsed)
                ? parsed
                : AgentPathBase.ProjectRoot;
            var scope = ParseScope(_scope.value);
            return new AgentPathLocation
            {
                Id = _id,
                BasePath = basePath,
                UseUnityAgentToolDirectory = _useUnityAgentToolDirectory.value,
                RelativePath = _relativePath.value.Trim(),
                Scope = scope,
                EmbedInPlayerBuild = _embedInPlayerBuild?.value ?? false
            };
        }

        private static AgentPathScope ParseScope(string value) => value switch
        {
            "None" => AgentPathScope.None,
            "Editor only" => AgentPathScope.EditorOnly,
            "Player only" => AgentPathScope.PlayerOnly,
            "All" => AgentPathScope.All,
            _ => throw new InvalidOperationException($"Unknown Agent path scope '{value}'.")
        };

        private static string GetScopeLabel(AgentPathScope value) => value switch
        {
            AgentPathScope.None => "None",
            AgentPathScope.EditorOnly => "Editor only",
            AgentPathScope.PlayerOnly => "Player only",
            AgentPathScope.All => "All",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Agent path scope.")
        };

        private void ChangedByUser()
        {
            try
            {
                var value = GetValue();
                RefreshPreview();
                Changed?.Invoke(value);
            }
            catch (Exception exception)
            {
                _preview.text = "Invalid path";
                _preview.style.color = AgentUi.Error;
                _showError(exception.Message);
            }
        }

        private void RefreshPreview()
        {
            try
            {
                var value = CreateValue();
                _preview.text = _isSkillRoot ? AgentPaths.ResolveSkill(value) : AgentPaths.Resolve(value);
                _preview.style.color = AgentUi.Muted;
            }
            catch (Exception exception)
            {
                _preview.text = exception.Message;
                _preview.style.color = AgentUi.Error;
            }
        }
    }

    internal sealed class AgentModalLayer : VisualElement
    {
        private readonly Label _title;
        private readonly Label _message;
        private readonly AgentButton _cancel;
        private readonly AgentButton _confirm;
        private Action? _confirmed;

        public AgentModalLayer()
        {
            focusable = true;
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            style.backgroundColor = AgentUi.Mask;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            style.display = DisplayStyle.None;

            style.paddingTop = 24;
            style.paddingRight = 24;
            style.paddingBottom = 24;
            style.paddingLeft = 24;
            var dialog = AgentUi.RoundedPanel(24);
            dialog.style.width = new Length(100, LengthUnit.Percent);
            dialog.style.maxWidth = 380;
            dialog.style.maxHeight = new Length(100, LengthUnit.Percent);
            dialog.style.minWidth = 0;
            dialog.style.paddingLeft = 22;
            dialog.style.paddingRight = 22;
            dialog.style.paddingTop = 20;
            dialog.style.paddingBottom = 18;
            dialog.style.backgroundColor = AgentUi.PanelRaised;
            dialog.style.borderTopWidth = 1;
            dialog.style.borderBottomWidth = 1;
            dialog.style.borderLeftWidth = 1;
            dialog.style.borderRightWidth = 1;
            dialog.style.borderTopColor = AgentUi.BorderStrong;
            dialog.style.borderBottomColor = AgentUi.BorderStrong;
            dialog.style.borderLeftColor = AgentUi.BorderStrong;
            dialog.style.borderRightColor = AgentUi.BorderStrong;
            Add(dialog);
            _title = new Label();
            AgentUi.ApplyTypography(_title, AgentTypography.PageTitle);
            dialog.Add(_title);
            var body = AgentUi.Scroll(ScrollViewMode.Vertical);
            body.style.flexShrink = 1;
            body.style.minHeight = 0;
            body.style.marginTop = 9;
            body.style.marginBottom = 14;
            dialog.Add(body);
            _message = new Label();
            _message.style.whiteSpace = WhiteSpace.Normal;
            AgentUi.ApplyTypography(_message, AgentTypography.Body, false);
            body.Add(_message);
            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.FlexEnd;
            dialog.Add(buttons);
            _cancel = AgentUi.Button("Cancel", "Close this dialog.", Hide, 78);
            buttons.Add(_cancel);
            _confirm = AgentUi.Button("OK", "Confirm.", Confirm, 78, AgentUi.Accent);
            buttons.Add(_confirm);
            RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape) return;
                Hide();
                evt.StopPropagation();
            });
        }

        public void ShowError(string title, string message)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message)) return;
            _title.text = title;
            _message.text = message;
            _confirmed = null;
            _cancel.style.display = DisplayStyle.None;
            _confirm.text = "Close";
            _confirm.HelpText = "Close this dialog.";
            style.display = DisplayStyle.Flex;
            BringToFront();
            schedule.Execute(() => _confirm.Focus());
        }

        public void ShowConfirmation(string title, string message, Action confirmed)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message)) return;
            _title.text = title;
            _message.text = message;
            _confirmed = confirmed;
            _cancel.style.display = DisplayStyle.Flex;
            _confirm.text = "Confirm";
            _confirm.HelpText = "Confirm this action.";
            style.display = DisplayStyle.Flex;
            BringToFront();
            schedule.Execute(() => _confirm.Focus());
        }

        private void Confirm()
        {
            var callback = _confirmed;
            Hide();
            callback?.Invoke();
        }

        private void Hide()
        {
            _confirmed = null;
            style.display = DisplayStyle.None;
        }
    }

    internal enum AgentTypography
    {
        Caption,
        Control,
        Body,
        BodyStrong,
        Composer,
        PageTitle,
        EmptyHero
    }

    internal static class AgentUi
    {
#if UNITY_EDITOR
        private const string EditorFontValidationText = "通用命令调用器设置调试面板gypqj ÅÉ";
        private static readonly (string Family, string Style)[] EditorFontCandidates =
        {
            ("Heiti SC", "Medium"),
            ("Heiti SC", "Light"),
            ("Arial Unicode MS", "Regular"),
            ("Microsoft YaHei UI", "Regular"),
            ("Microsoft YaHei", "Regular"),
            ("Noto Sans CJK SC", "Regular"),
            ("Noto Sans SC", "Regular"),
            ("WenQuanYi Micro Hei", "Regular"),
            ("Songti SC", "Regular"),
            ("Hiragino Sans GB", "W3")
        };

        private static FontAsset? _editorFontAsset;
        private static bool _editorFontResolutionAttempted;
#endif

        // Source-pinned DeepSeek Harness dark tokens. Editor and Runtime use this single table.
        public static readonly Color Background = new Color32(21, 21, 23, 255);
        public static readonly Color Sidebar = new Color32(27, 27, 28, 255);
        public static readonly Color Surface1 = new Color32(35, 35, 36, 255);
        public static readonly Color Surface2 = new Color32(44, 44, 46, 255);
        public static readonly Color Surface3 = new Color32(53, 54, 56, 255);
        public static readonly Color Panel = Surface1;
        public static readonly Color PanelRaised = Surface2;
        public static readonly Color PanelInset = Surface1;
        public static readonly Color Composer = Surface2;
        public static readonly Color Input = Surface1;
        public static readonly Color InputHover = new(1f, 1f, 1f, 0.08f);
        public static readonly Color Popup = Surface3;
        public static readonly Color Hover = new(1f, 1f, 1f, 0.08f);
        public static readonly Color Active = new(1f, 1f, 1f, 0.14f);
        public static readonly Color Border1 = new(1f, 1f, 1f, 0.06f);
        public static readonly Color Border2 = new(1f, 1f, 1f, 0.12f);
        public static readonly Color Border3 = new(1f, 1f, 1f, 0.16f);
        public static readonly Color Border = Border1;
        public static readonly Color BorderStrong = Border2;
        public static readonly Color Text = new Color32(249, 250, 251, 255);
        public static readonly Color TextSecondary = new Color32(207, 211, 214, 255);
        public static readonly Color TextTertiary = new Color32(173, 178, 184, 255);
        public static readonly Color TextCaption = new Color32(129, 133, 140, 255);
        public static readonly Color TextDimmed = new Color32(67, 69, 74, 255);
        public static readonly Color Muted = TextTertiary;
        public static readonly Color Placeholder = TextCaption;
        public static readonly Color Accent = new Color32(96, 165, 250, 255);
        public static readonly Color AccentForeground = Text;
        public static readonly Color Focus = Accent;
        public static readonly Color Success = new Color32(34, 197, 94, 255);
        public static readonly Color Send = Accent;
        public static readonly Color SendForeground = Text;
        public static readonly Color Danger = new Color32(87, 12, 12, 255);
        public static readonly Color Selected = Active;
        public static readonly Color Transparent = new(0f, 0f, 0f, 0f);
        public static readonly Color UserMessage = Surface2;
        public static readonly Color AssistantMessage = Surface1;
        public static readonly Color ToolMessage = Surface1;
        public static readonly Color ErrorPanel = new(0.34f, 0.047f, 0.047f, 0.72f);
        public static readonly Color WarningPanel = new(0.153f, 0.141f, 0.122f, 1f);
        public static readonly Color Warning = new Color32(221, 134, 41, 255);
        public static readonly Color Error = new Color32(242, 90, 90, 255);
        public static readonly Color Scrollbar1 = new Color32(60, 60, 61, 255);
        public static readonly Color Scrollbar1Hover = new Color32(84, 85, 87, 255);
        public static readonly Color Scrollbar2 = new Color32(84, 85, 87, 255);
        public static readonly Color Scrollbar2Hover = new Color32(101, 103, 107, 255);
        public static readonly Color Mask = new(0f, 0f, 0f, 0.5f);
        public static readonly Color Selection = new(0.376f, 0.647f, 0.980f, 0.42f);

        public static void ApplyRoot(VisualElement root)
        {
            ApplyFont(root);
            root.style.color = Text;
            ApplyTypography(root, AgentTypography.Body);
        }

        public static void ApplyFont(VisualElement root)
        {
#if UNITY_EDITOR
            var fontAsset = ResolveEditorFontAsset();
            if (fontAsset != null)
                root.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromSDFFont(fontAsset));
#endif
        }

#if UNITY_EDITOR
        internal static void DisposeEditorFontResources()
        {
            if (_editorFontAsset != null) DestroyEditorFontAsset(_editorFontAsset);
            _editorFontAsset = null;
            _editorFontResolutionAttempted = false;
        }

        private static FontAsset? ResolveEditorFontAsset()
        {
            if (_editorFontAsset != null) return _editorFontAsset;
            if (_editorFontResolutionAttempted) return null;
            _editorFontResolutionAttempted = true;

            var installedFontNames = FontEngine.GetSystemFontNames();
            if (installedFontNames == null)
            {
                LogSys.LogError("Yuze Agent Tool could not enumerate system fonts for Editor UI text rendering.");
                return null;
            }

            var installed = new HashSet<string>(installedFontNames, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in EditorFontCandidates)
            {
                if (!installed.Contains(candidate.Family + " - " + candidate.Style)) continue;

                var fontAsset = FontAsset.CreateFontAsset(candidate.Family, candidate.Style, 90);
                if (fontAsset == null) continue;
                fontAsset.TryAddCharacters(EditorFontValidationText, true);
                if (EditorFontValidationText.Any(character => !fontAsset.HasCharacter(character)))
                {
                    DestroyEditorFontAsset(fontAsset);
                    continue;
                }

                fontAsset.name = "Yuze Agent Tool Editor System Font";
                fontAsset.hideFlags = HideFlags.HideAndDontSave;
                if (fontAsset.material != null) fontAsset.material.hideFlags = HideFlags.HideAndDontSave;
                foreach (var texture in fontAsset.atlasTextures)
                    if (texture != null)
                        texture.hideFlags = HideFlags.HideAndDontSave;
                _editorFontAsset = fontAsset;
                return _editorFontAsset;
            }

            LogSys.LogError(
                "Yuze Agent Tool could not find an installed system font that covers its Editor UI text. " +
                "Install a CJK-capable system font or extend AgentUi.EditorFontCandidates for this platform.");
            return null;
        }

        private static void DestroyEditorFontAsset(FontAsset fontAsset)
        {
            var material = fontAsset.material;
            var textures = fontAsset.atlasTextures;
            UnityEngine.Object.DestroyImmediate(fontAsset);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            foreach (var texture in textures)
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
        }
#endif

        public static void ApplyTypography(VisualElement element, AgentTypography role, bool singleLine = true)
        {
            var size = 14;
            var seat = 22;
            var weight = FontStyle.Normal;
            switch (role)
            {
                case AgentTypography.Caption:
                    size = 12;
                    seat = 18;
                    break;
                case AgentTypography.Control:
                    size = 13;
                    seat = 20;
                    weight = FontStyle.Bold;
                    break;
                case AgentTypography.BodyStrong:
                    weight = FontStyle.Bold;
                    break;
                case AgentTypography.Composer:
                    size = 16;
                    seat = 24;
                    break;
                case AgentTypography.PageTitle:
                    size = 16;
                    seat = 24;
                    weight = FontStyle.Bold;
                    break;
                case AgentTypography.EmptyHero:
                    size = 26;
                    seat = 32;
                    weight = FontStyle.Bold;
                    break;
            }
            element.style.fontSize = size;
            element.style.minHeight = seat;
            element.style.unityFontStyleAndWeight = weight;
            element.style.whiteSpace = singleLine ? WhiteSpace.NoWrap : WhiteSpace.Normal;
            element.style.unityTextAlign = TextAnchor.MiddleLeft;
        }

        public static AgentButton Button(string text, string tooltip, Action clicked, int width,
            Color? background = null, Color? foreground = null, AgentIconKind icon = AgentIconKind.None)
        {
            var surface = background ?? PanelRaised;
            var content = foreground ?? (surface == Accent || surface == Send ? AccentForeground : Text);
            var button = new AgentButton(text, tooltip, clicked, surface, content, icon);
            button.style.height = 36;
            button.style.flexShrink = 0;
            if (width > 0) button.style.width = width; else button.style.flexGrow = 1;
            button.style.marginLeft = 3;
            button.style.marginRight = 3;
            button.style.borderTopLeftRadius = 18;
            button.style.borderTopRightRadius = 18;
            button.style.borderBottomLeftRadius = 18;
            button.style.borderBottomRightRadius = 18;
            return button;
        }

        public static AgentButton IconButton(AgentIconKind icon, string tooltip, Action clicked, int size,
            Color? background = null, Color? foreground = null)
        {
            var button = Button(string.Empty, tooltip, clicked, size, background, foreground, icon);
            button.style.height = size;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            return button;
        }

        public static AgentTextField Field(string label, string value, string tooltip, bool password = false)
        {
            var field = new AgentTextField(label)
            {
                value = value,
                isPasswordField = password
            };
            ApplyTypography(field, AgentTypography.Body);
            field.style.marginTop = 4;
            field.style.marginBottom = 4;
            field.style.minWidth = 0;
            field.style.maxWidth = new Length(100, LengthUnit.Percent);
            field.style.flexShrink = 1;
            return field;
        }

        public static AgentChoiceField Dropdown(string label, IEnumerable<string> choices)
        {
            var list = choices.ToList();
            var field = new AgentChoiceField(label, list);
            field.style.marginTop = 4;
            field.style.marginBottom = 4;
            field.style.minWidth = 0;
            field.style.maxWidth = new Length(100, LengthUnit.Percent);
            field.style.flexShrink = 1;
            if (list.Count > 0) field.SetValueWithoutNotify(list[0]);
            return field;
        }

        public static AgentChoiceField CompactDropdown(IEnumerable<string> choices, string tooltip)
        {
            var list = choices.ToList();
            var field = new AgentChoiceField(string.Empty, list, true);
            if (list.Count > 0) field.SetValueWithoutNotify(list[0]);
            field.style.width = 120;
            field.style.flexGrow = 0;
            field.style.flexShrink = 1;
            field.style.minWidth = 52;
            field.style.maxWidth = 220;
            field.style.marginLeft = 3;
            field.style.marginRight = 3;
            return field;
        }

        public static ScrollView Scroll(ScrollViewMode mode)
        {
            var scroll = new ScrollView(mode);
            scroll.style.backgroundImage = StyleKeyword.None;
            scroll.style.backgroundColor = Transparent;
            scroll.style.borderTopWidth = 0;
            scroll.style.borderRightWidth = 0;
            scroll.style.borderBottomWidth = 0;
            scroll.style.borderLeftWidth = 0;
            scroll.contentContainer.style.backgroundImage = StyleKeyword.None;
            scroll.contentContainer.style.backgroundColor = Transparent;
            scroll.contentViewport.style.backgroundImage = StyleKeyword.None;
            scroll.contentViewport.style.backgroundColor = Transparent;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            scroll.schedule.Execute(() => StyleScroller(scroll));
            return scroll;
        }

        public static void StyleScroller(ScrollView scroll)
        {
            var scroller = scroll.verticalScroller;
            ResetScrollVisuals(scroller);
            scroller.style.width = 8;
            scroller.style.backgroundImage = StyleKeyword.None;
            scroller.style.backgroundColor = Transparent;
            scroller.style.marginTop = 2;
            scroller.style.marginRight = 1;
            scroller.style.marginBottom = 2;
            scroller.lowButton.style.display = DisplayStyle.None;
            scroller.highButton.style.display = DisplayStyle.None;
            scroller.lowButton.style.backgroundImage = StyleKeyword.None;
            scroller.highButton.style.backgroundImage = StyleKeyword.None;
            scroller.slider.style.backgroundImage = StyleKeyword.None;
            scroller.slider.style.backgroundColor = Transparent;
            scroller.slider.style.borderTopWidth = 0;
            scroller.slider.style.borderRightWidth = 0;
            scroller.slider.style.borderBottomWidth = 0;
            scroller.slider.style.borderLeftWidth = 0;
            var tracker = scroller.slider.Q<VisualElement>(className: "unity-base-slider__tracker");
            if (tracker != null)
            {
                tracker.style.backgroundImage = StyleKeyword.None;
                tracker.style.backgroundColor = Transparent;
                tracker.style.borderTopWidth = 0;
                tracker.style.borderRightWidth = 0;
                tracker.style.borderBottomWidth = 0;
                tracker.style.borderLeftWidth = 0;
            }
            var dragger = scroller.slider.Q<VisualElement>(className: "unity-base-slider__dragger");
            if (dragger != null)
            {
                dragger.style.backgroundImage = StyleKeyword.None;
                dragger.style.backgroundColor = Scrollbar1;
                dragger.style.borderTopWidth = 0;
                dragger.style.borderRightWidth = 0;
                dragger.style.borderBottomWidth = 0;
                dragger.style.borderLeftWidth = 0;
                dragger.style.borderTopLeftRadius = 4;
                dragger.style.borderTopRightRadius = 4;
                dragger.style.borderBottomLeftRadius = 4;
                dragger.style.borderBottomRightRadius = 4;
                dragger.RegisterCallback<PointerEnterEvent>(_ => dragger.style.backgroundColor = Scrollbar1Hover);
                dragger.RegisterCallback<PointerLeaveEvent>(_ => dragger.style.backgroundColor = Scrollbar1);
                dragger.RegisterCallback<PointerDownEvent>(_ => dragger.style.backgroundColor = Focus);
                dragger.RegisterCallback<PointerUpEvent>(_ => dragger.style.backgroundColor = Scrollbar1Hover);
            }
            var draggerBorder = scroller.slider.Q<VisualElement>(className: "unity-base-slider__dragger-border");
            if (draggerBorder != null)
            {
                draggerBorder.style.backgroundImage = StyleKeyword.None;
                draggerBorder.style.backgroundColor = Transparent;
                draggerBorder.style.borderTopWidth = 0;
                draggerBorder.style.borderRightWidth = 0;
                draggerBorder.style.borderBottomWidth = 0;
                draggerBorder.style.borderLeftWidth = 0;
            }
        }

        private static void ResetScrollVisuals(VisualElement root)
        {
            root.style.backgroundImage = StyleKeyword.None;
            root.style.backgroundColor = Transparent;
            root.style.borderTopWidth = 0;
            root.style.borderRightWidth = 0;
            root.style.borderBottomWidth = 0;
            root.style.borderLeftWidth = 0;
            foreach (var child in root.Children()) ResetScrollVisuals(child);
        }

        public static VisualElement PageHeading(string title, string subtitle)
        {
            var root = new VisualElement();
            root.style.minWidth = 0;
            root.style.marginBottom = 14;
            var heading = new Label(title);
            ApplyTypography(heading, AgentTypography.PageTitle);
            root.Add(heading);
            var help = new Label(subtitle);
            ApplyTypography(help, AgentTypography.Caption, false);
            help.style.color = Muted;
            help.style.whiteSpace = WhiteSpace.Normal;
            help.style.marginTop = 4;
            root.Add(help);
            return root;
        }

        public static VisualElement Card(string title, string subtitle)
        {
            var card = RoundedPanel(12);
            card.style.minWidth = 0;
            card.style.width = new Length(100, LengthUnit.Percent);
            card.style.maxWidth = 800;
            card.style.alignSelf = Align.Center;
            card.style.marginBottom = 14;
            card.style.paddingLeft = 18;
            card.style.paddingRight = 18;
            card.style.paddingTop = 16;
            card.style.paddingBottom = 18;
            card.style.backgroundColor = Panel;
            SetBorder(card, Border, 1);
            var heading = new Label(title);
            ApplyTypography(heading, AgentTypography.BodyStrong);
            card.Add(heading);
            var help = new Label(subtitle);
            help.style.color = Muted;
            help.style.whiteSpace = WhiteSpace.Normal;
            ApplyTypography(help, AgentTypography.Caption, false);
            help.style.marginTop = 2;
            help.style.marginBottom = 8;
            card.Add(help);
            return card;
        }

        public static VisualElement RoundedPanel(float radius)
        {
            var panel = new VisualElement();
            panel.style.backgroundColor = Panel;
            panel.style.borderTopLeftRadius = radius;
            panel.style.borderTopRightRadius = radius;
            panel.style.borderBottomLeftRadius = radius;
            panel.style.borderBottomRightRadius = radius;
            return panel;
        }

        public static void SetBorder(VisualElement element, Color color, float width)
        {
            element.style.borderTopWidth = width;
            element.style.borderRightWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderTopColor = color;
            element.style.borderRightColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
        }

        public static VisualElement Inset()
        {
            var inset = RoundedPanel(7);
            inset.style.minWidth = 0;
            inset.style.maxWidth = new Length(100, LengthUnit.Percent);
            inset.style.backgroundColor = PanelInset;
            inset.style.paddingLeft = 10;
            inset.style.paddingRight = 10;
            inset.style.paddingTop = 8;
            inset.style.paddingBottom = 8;
            return inset;
        }

        public static VisualElement WrapRow()
        {
            var row = new VisualElement();
            row.style.minWidth = 0;
            row.style.maxWidth = new Length(100, LengthUnit.Percent);
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.alignItems = Align.Center;
            return row;
        }
    }
}
