#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit.Eval
{
    public interface IUnityEvalToolWorkbenchHost
    {
        bool IsEnabled { get; }
        bool IncludeEditorOnlyTools { get; }
        bool CanOpenBrokerFolder { get; }
        string RuntimeStateLabel { get; }
        void SetEnabled(bool enabled);
        void Reconnect();
        void OpenBrokerFolder();
        void SetToolEnabled(string path, bool enabled);
    }

    public sealed class RuntimeUnityEvalToolWorkbenchHost : IUnityEvalToolWorkbenchHost
    {
        public bool IsEnabled => UnityBrokerClient.Shared.IsRunning;
        public bool IncludeEditorOnlyTools => false;
        public bool CanOpenBrokerFolder => false;
        public string RuntimeStateLabel => Application.isPlaying ? "Player" : "Runtime";

        public void SetEnabled(bool enabled)
        {
            if (enabled) UnityBrokerClient.Shared.Start();
            else UnityBrokerClient.Shared.Stop();
        }

        public void Reconnect() => UnityBrokerClient.Shared.Reconnect();

        public void OpenBrokerFolder() =>
            throw new InvalidOperationException("Opening the Broker folder is only available in the Unity Editor.");

        public void SetToolEnabled(string path, bool enabled) => EvalToolRegistry.SetEnabled(path, enabled);
    }

    public sealed class UnityEvalToolWorkbenchView : VisualElement, IDisposable
    {
        private const string Endpoint = "http://127.0.0.1:2347/mcp";
        private readonly IUnityEvalToolWorkbenchHost _host;
        private readonly HashSet<string> _expandedTools = new(StringComparer.Ordinal);
        private readonly VisualElement _overview = new();
        private readonly VisualElement _tools = new();
        private readonly VisualElement _toolsList = new();
        private Button _overviewTab = null!;
        private Button _toolsTab = null!;
        private Button _featureSwitch = null!;
        private Button _reconnect = null!;
        private Label _connectionBadge = null!;
        private Label _phaseBadge = null!;
        private Label _connection = null!;
        private Label _authorization = null!;
        private Label _phase = null!;
        private Label _canEval = null!;
        private Label _busyReason = null!;
        private Label _runtimeState = null!;
        private Label _compilation = null!;
        private Label _compilationCycle = null!;
        private Label _lastCompilation = null!;
        private Label _instance = null!;
        private Label _connectionEpoch = null!;
        private Label _vmGeneration = null!;
        private Label _mainThread = null!;
        private Label _installation = null!;
        private TextField _toolSearch = null!;
        private Label _toolSearchPlaceholder = null!;
        private bool _toolsDirty = true;
        private bool _toolsPageActive;
        private bool _disposed;

        public UnityEvalToolWorkbenchView(IUnityEvalToolWorkbenchHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            AddToClassList("uet-workbench");
            Build();
            EvalToolRegistry.Changed += MarkToolsDirty;
            Refresh();
        }

        public void Tick()
        {
            Refresh();
            if (_toolsPageActive && _toolsDirty) RefreshTools(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EvalToolRegistry.Changed -= MarkToolsDirty;
        }

        private void Build()
        {
            var header = new VisualElement();
            header.AddToClassList("uet-workbench-header");
            var heading = new VisualElement();
            heading.AddToClassList("uet-workbench-heading");
            header.Add(heading);
            var title = new Label("Yuze Eval Tool") { enableRichText = false };
            title.AddToClassList("uet-workbench-title");
            heading.Add(title);
            var subtitle = new Label("One Broker connection for MCP, CLI and Unity automation") { enableRichText = false };
            subtitle.AddToClassList("uet-workbench-subtitle");
            heading.Add(subtitle);
            _connectionBadge = CreateBadge();
            header.Add(_connectionBadge);
            _phaseBadge = CreateBadge();
            header.Add(_phaseBadge);
            _featureSwitch = CreateButton(string.Empty, ToggleFeature, "uet-workbench-switch");
            header.Add(_featureSwitch);
            Add(header);

            var tabs = new VisualElement();
            tabs.AddToClassList("uet-workbench-tabs");
            _overviewTab = CreateButton("Overview", () => SetActivePage(false), "uet-workbench-tab");
            _toolsTab = CreateButton("Tools", () => SetActivePage(true), "uet-workbench-tab");
            tabs.Add(_overviewTab);
            tabs.Add(_toolsTab);
            Add(tabs);

            var scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Auto
            };
            scroll.AddToClassList("uet-workbench-scroll");
            scroll.Add(_overview);
            scroll.Add(_tools);
            Add(scroll);
            BuildOverview();
            BuildTools();
            SetActivePage(false);
        }

        private void BuildOverview()
        {
            _overview.Add(CreateNotice("Connection model",
                "Unity connects outward to the computer-level Broker. MCP and CLI share this registration and do not open separate Unity ports."));

            var connectionCard = CreateCard("Connection", "Live registration and evaluation availability");
            _connection = AddField(connectionCard, "Broker connection");
            _authorization = AddField(connectionCard, "Authorization");
            _phase = AddField(connectionCard, "Unity phase");
            _canEval = AddField(connectionCard, "Evaluation");
            _busyReason = AddField(connectionCard, "Busy reason");
            _runtimeState = AddField(connectionCard, "Runtime state");
            var controls = new VisualElement();
            controls.AddToClassList("uet-workbench-toolbar");
            _reconnect = CreateButton("Reconnect", _host.Reconnect, "uet-workbench-button");
            controls.Add(_reconnect);
            controls.Add(CreateButton("Copy MCP endpoint", () => GUIUtility.systemCopyBuffer = Endpoint, "uet-workbench-button"));
            if (_host.CanOpenBrokerFolder)
                controls.Add(CreateButton("Open Broker folder", _host.OpenBrokerFolder, "uet-workbench-button"));
            connectionCard.Add(controls);
            _overview.Add(connectionCard);

            var compilationCard = CreateCard("Compilation", "Latest compilation cycle published to the Broker");
            _compilation = AddField(compilationCard, "Result");
            _compilationCycle = AddField(compilationCard, "Cycle ID");
            _lastCompilation = AddField(compilationCard, "Last cycle");
            _overview.Add(compilationCard);

            var identityCard = CreateCard("Unity identity", "Stable process identity and reload generations");
            _instance = AddField(identityCard, "Instance ID");
            _connectionEpoch = AddField(identityCard, "Connection epoch");
            _vmGeneration = AddField(identityCard, "VM generation");
            _mainThread = AddField(identityCard, "Main thread heartbeat");
            _overview.Add(identityCard);

            var environmentCard = CreateCard("Environment", "External entry points used by agents and terminals");
            AddField(environmentCard, "MCP endpoint").text = Endpoint;
            _installation = AddField(environmentCard, "CLI installation");
            _overview.Add(environmentCard);
            _overview.Add(CreateNotice("Agent workflow",
                "unity_status → unity_connect → reuse handle → eval. Wait for compilation through unity_status; CompilationFailed remains executable for repair."));
        }

        private void BuildTools()
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("uet-workbench-toolbar");
            _toolSearch = new TextField { tabIndex = -1 };
            _toolSearch.AddToClassList("uet-workbench-search");
            _toolSearchPlaceholder = new Label("Filter tools…")
            {
                pickingMode = PickingMode.Ignore,
                enableRichText = false
            };
            _toolSearchPlaceholder.AddToClassList("uet-workbench-placeholder");
            _toolSearch.Add(_toolSearchPlaceholder);
            _toolSearch.RegisterValueChangedCallback(evt =>
            {
                _toolSearchPlaceholder.style.display = string.IsNullOrEmpty(evt.newValue)
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
                RefreshTools(false);
            });
            toolbar.Add(_toolSearch);
            toolbar.Add(CreateButton("Refresh registry", () => RefreshTools(true), "uet-workbench-button"));
            _tools.Add(toolbar);
            _tools.Add(_toolsList);
        }

        private void SetActivePage(bool tools)
        {
            _toolsPageActive = tools;
            _overview.style.display = tools ? DisplayStyle.None : DisplayStyle.Flex;
            _tools.style.display = tools ? DisplayStyle.Flex : DisplayStyle.None;
            _overviewTab.EnableInClassList("uet-workbench-tab-active", !tools);
            _toolsTab.EnableInClassList("uet-workbench-tab-active", tools);
            if (tools && _toolsDirty) RefreshTools(false);
        }

        private void Refresh()
        {
            if (_disposed || _connection == null) return;
            var client = UnityBrokerClient.Shared;
            var enabled = _host.IsEnabled;
            var connected = client.IsConnected;
            var running = client.IsRunning;
            var status = client.LatestStatus;
            var identity = client.Identity;
            var connectionText = !enabled ? "Disabled" : connected ? "Connected" : running ? "Reconnecting" : "Stopped";
            _connection.text = connectionText;
            ApplyBadge(_connectionBadge, connectionText, connected ? "success" : enabled ? "warning" : "muted");
            _authorization.text = client.AuthorizationState;
            _authorization.EnableInClassList("uet-workbench-value-success",
                string.Equals(client.AuthorizationState, "Authorized", StringComparison.Ordinal) ||
                string.Equals(client.AuthorizationState, "NotRequired", StringComparison.Ordinal));
            _phase.text = status.Phase;
            ApplyBadge(_phaseBadge, status.Phase, status.CanEval ? "success" : "warning");
            var authorized = string.Equals(client.AuthorizationState, "Authorized", StringComparison.Ordinal) ||
                             string.Equals(client.AuthorizationState, "NotRequired", StringComparison.Ordinal);
            _canEval.text = status.CanEval && connected && authorized
                ? string.Equals(status.Phase, "CompilationFailed", StringComparison.Ordinal) ? "Repair" : "Ready"
                : "Unavailable";
            _canEval.EnableInClassList("uet-workbench-value-success", status.CanEval && connected && authorized);
            _busyReason.text = string.IsNullOrWhiteSpace(status.BusyReason) ? "—" : status.BusyReason;
            _runtimeState.text = status.IsPlaying
                ? status.IsPaused ? _host.RuntimeStateLabel + " / Paused" : _host.RuntimeStateLabel + " / Playing"
                : status.IsUpdating ? _host.RuntimeStateLabel + " / Importing" : _host.RuntimeStateLabel;
            _compilation.text = $"{status.CompilerErrorCount} errors / {status.CompilerWarningCount} warnings";
            _compilation.EnableInClassList("uet-workbench-value-error", status.CompilerErrorCount > 0);
            _compilationCycle.text = ShortId(status.CompilationCycleId);
            _lastCompilation.text = FormatCompilationTimes(status.LastCompilationStartedAtUtc, status.LastCompilationFinishedAtUtc);
            _instance.text = identity.InstanceId;
            _connectionEpoch.text = identity.ConnectionEpoch.ToString(CultureInfo.InvariantCulture);
            _vmGeneration.text = status.VmGeneration.ToString(CultureInfo.InvariantCulture);
            _mainThread.text = status.MainThreadTickAtUtc == default
                ? "No heartbeat"
                : $"tick {status.MainThreadTick} · {status.MainThreadTickAtUtc.ToLocalTime():HH:mm:ss}";
            _installation.text = GetInstallationStatus();
            _reconnect.SetEnabled(enabled);
            _featureSwitch.text = enabled ? "Enabled" : "Disabled";
            _featureSwitch.EnableInClassList("uet-workbench-switch-on", enabled);
        }

        private void RefreshTools(bool refreshMetadata)
        {
            _toolsDirty = false;
            if (refreshMetadata) _ = EvalToolRegistry.GetIndex(true);
            _toolsList.Clear();
            var filter = _toolSearch?.value?.Trim() ?? string.Empty;
            var tools = EvalToolRegistry.ListTools(false)
                .Where(tool => (_host.IncludeEditorOnlyTools || !tool.EditorOnly) && MatchesFilter(tool, filter))
                .OrderBy(tool => tool.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(tool => tool.Path, StringComparer.Ordinal)
                .ToList();
            if (tools.Count == 0)
            {
                _toolsList.Add(CreateNotice("No results", "No tools match the current filter."));
                return;
            }

            AddToolSection("C# Tools", tools.Where(tool => tool.Source.Equals("csharp", StringComparison.OrdinalIgnoreCase)).ToList());
            AddToolSection("JavaScript Tools", tools.Where(tool => tool.Source.Equals("js", StringComparison.OrdinalIgnoreCase)).ToList());
        }

        private void AddToolSection(string title, IReadOnlyList<EvalToolDescriptor> tools)
        {
            if (tools.Count == 0) return;
            var heading = new Label($"{title} · {tools.Count}") { enableRichText = false };
            heading.AddToClassList("uet-workbench-section-title");
            _toolsList.Add(heading);
            foreach (var tool in tools) _toolsList.Add(CreateToolCard(tool));
        }

        private VisualElement CreateToolCard(EvalToolDescriptor tool)
        {
            var card = CreateCard(null, null);
            var header = new VisualElement();
            header.AddToClassList("uet-workbench-tool-header");
            var expanded = _expandedTools.Contains(tool.Path);
            var expand = CreateButton(tool.Name, () =>
            {
                if (!_expandedTools.Add(tool.Path)) _expandedTools.Remove(tool.Path);
                RefreshTools(false);
            }, "uet-workbench-tool-expand");
            expand.EnableInClassList("uet-workbench-tool-expand-open", expanded);
            var disclosure = new VisualElement { pickingMode = PickingMode.Ignore };
            disclosure.AddToClassList("uet-workbench-disclosure");
            expand.Insert(0, disclosure);
            header.Add(expand);
            header.Add(CreatePill(tool.Source.ToUpperInvariant()));
            header.Add(CreatePill(tool.EditorOnly ? "EDITOR" : "RUNTIME"));
            var state = CreateButton(tool.Enabled ? "Enabled" : "Disabled", () =>
            {
                _host.SetToolEnabled(tool.Path, !tool.Enabled);
                RefreshTools(false);
            }, "uet-workbench-tool-state");
            state.EnableInClassList("uet-workbench-switch-on", tool.Enabled);
            header.Add(state);
            card.Add(header);
            var description = new Label(string.IsNullOrWhiteSpace(tool.Description) ? "No description." : tool.Description)
            {
                enableRichText = false
            };
            description.AddToClassList("uet-workbench-tool-description");
            card.Add(description);
            if (!expanded) return card;

            var detail = new VisualElement();
            detail.AddToClassList("uet-workbench-tool-detail");
            AddField(detail, "Import path").text = "tools://" + tool.Path;
            AddField(detail, "Source").text = tool.Source;
            AddField(detail, "Availability").text = tool.EditorOnly ? "Unity Editor only" : "Editor and Player";
            AddField(detail, "Contents").text = $"{tool.Functions.Count} functions / {tool.SubTools.Count} sub tools";
            AddFunctions(detail, tool.Functions);
            card.Add(detail);
            return card;
        }

        private static void AddFunctions(VisualElement parent, IReadOnlyList<EvalToolFunctionDescriptor> functions)
        {
            var title = new Label($"Functions · {functions.Count}") { enableRichText = false };
            title.AddToClassList("uet-workbench-detail-title");
            parent.Add(title);
            if (functions.Count == 0) return;
            foreach (var function in functions)
            {
                var row = new VisualElement();
                row.AddToClassList("uet-workbench-function");
                var signature = new Label(function.MethodName + "(" + string.Join(", ", function.Parameters.Select(FormatParameter)) + ")")
                {
                    enableRichText = false
                };
                signature.AddToClassList("uet-workbench-function-signature");
                row.Add(signature);
                if (!string.IsNullOrWhiteSpace(function.Description))
                    row.Add(CreateMutedLabel(function.Description));
                if (function.Safety != EvalToolSafety.Unspecified)
                    row.Add(CreateMutedLabel("Risk: " + function.RiskLevel +
                                             (function.RequiresConfirmation ? " · confirmation required" : string.Empty)));
                parent.Add(row);
            }
        }

        private static VisualElement CreateCard(string? title, string? subtitle)
        {
            var card = new VisualElement();
            card.AddToClassList("uet-workbench-card");
            if (!string.IsNullOrWhiteSpace(title))
            {
                var heading = new Label(title!) { enableRichText = false };
                heading.AddToClassList("uet-workbench-card-title");
                card.Add(heading);
            }
            if (!string.IsNullOrWhiteSpace(subtitle)) card.Add(CreateMutedLabel(subtitle!));
            return card;
        }

        private static Label AddField(VisualElement parent, string name)
        {
            var row = new VisualElement();
            row.AddToClassList("uet-workbench-field");
            var key = new Label(name) { enableRichText = false };
            key.AddToClassList("uet-workbench-field-key");
            row.Add(key);
            var value = new Label("—") { enableRichText = false };
            value.AddToClassList("uet-workbench-field-value");
            value.selection.isSelectable = true;
            row.Add(value);
            parent.Add(row);
            return value;
        }

        private static VisualElement CreateNotice(string title, string message)
        {
            var notice = new VisualElement();
            notice.AddToClassList("uet-workbench-notice");
            var heading = new Label(title) { enableRichText = false };
            heading.AddToClassList("uet-workbench-notice-title");
            notice.Add(heading);
            var body = new Label(message) { enableRichText = false };
            body.AddToClassList("uet-workbench-notice-body");
            notice.Add(body);
            return notice;
        }

        private static Button CreateButton(string text, Action action, string className)
        {
            var button = new Button(action) { text = text, focusable = false, tabIndex = -1 };
            button.AddToClassList("uet-workbench-owned-button");
            button.AddToClassList(className);
            return button;
        }

        private static Label CreateBadge()
        {
            var badge = new Label { enableRichText = false };
            badge.AddToClassList("uet-workbench-badge");
            return badge;
        }

        private static void ApplyBadge(Label badge, string text, string tone)
        {
            badge.text = text;
            badge.EnableInClassList("uet-workbench-badge-success", tone == "success");
            badge.EnableInClassList("uet-workbench-badge-warning", tone == "warning");
            badge.EnableInClassList("uet-workbench-badge-muted", tone == "muted");
        }

        private static Label CreatePill(string text)
        {
            var pill = new Label(text) { enableRichText = false };
            pill.AddToClassList("uet-workbench-pill");
            return pill;
        }

        private static Label CreateMutedLabel(string text)
        {
            var label = new Label(text) { enableRichText = false };
            label.AddToClassList("uet-workbench-muted");
            return label;
        }

        private void ToggleFeature()
        {
            _host.SetEnabled(!_host.IsEnabled);
            Refresh();
        }

        private void MarkToolsDirty() => _toolsDirty = true;

        private static bool MatchesFilter(EvalToolDescriptor tool, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            return tool.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tool.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tool.Source.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tool.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tool.SubTools.Any(subTool =>
                       subTool.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       subTool.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       subTool.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FormatParameter(EvalToolParameterDescriptor parameter)
        {
            var optional = parameter.Optional ? "?" : string.Empty;
            var defaultValue = parameter.Optional
                ? " = " + (parameter.DefaultValue == null ? "null" : Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture))
                : string.Empty;
            return $"{parameter.Name}{optional}: {parameter.Type}{defaultValue}";
        }

        private static string ShortId(string value) => string.IsNullOrWhiteSpace(value)
            ? "—"
            : value.Length <= 12 ? value : value.Substring(0, 12) + "…";

        private static string FormatCompilationTimes(string startedAt, string finishedAt)
        {
            var started = ParseLocalTime(startedAt);
            var finished = ParseLocalTime(finishedAt);
            return started == "—" && finished == "—" ? "No compilation recorded" : $"started {started} / finished {finished}";
        }

        private static string ParseLocalTime(string value) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime)
                ? dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "—";

        private static string GetInstallationStatus()
        {
            var metadata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unityevaltool", "install.json");
            if (!File.Exists(metadata)) return "Not installed · run the npm-installed `unity` command once";
            try
            {
                var root = EvalData.AsObject(EvalJson.Parse(File.ReadAllText(metadata)));
                var executable = root == null ? string.Empty : EvalData.GetString(root, "executablePath") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(executable)) return "Invalid install metadata · executablePath is missing";
                return File.Exists(executable) ? executable : "Installed executable is missing · " + executable;
            }
            catch (Exception exception)
            {
                return "Invalid install metadata · " + exception.Message;
            }
        }
    }
}
