#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using YuzeToolkit;

namespace YuzeToolkit.UnityAgent
{
    internal sealed class AgentEvalConnectionSettingsView : VisualElement
    {
        private const string Endpoint = "http://127.0.0.1:2347/mcp";
        private readonly Dictionary<string, Label> _values = new(StringComparer.Ordinal);
        private readonly AgentToggle _broker;

        public AgentEvalConnectionSettingsView()
        {
            style.minWidth = 0;
            style.width = new Length(100, LengthUnit.Percent);
            Add(AgentUi.PageHeading("Unity Eval Tool · Connection",
                "The same live connection, compilation and process identity data shown by the standalone Yuze Eval Tool Editor window."));

            var connection = AgentUi.Card("Connection", "Live registration and evaluation availability");
            AddValue(connection, "Broker connection", "connection");
            AddValue(connection, "Unity phase", "phase");
            AddValue(connection, "Evaluation", "evaluation");
            AddValue(connection, "Busy reason", "busy");
            AddValue(connection, "Runtime state", "runtime");
            var actions = AgentUi.WrapRow();
            _broker = new AgentToggle("Editor Broker enabled");
            _broker.SetEnabled(UnityAgentEvalSettingsBridge.IsBrokerControlAvailable);
            _broker.RegisterValueChangedCallback(evt => UnityAgentEvalSettingsBridge.SetBrokerEnabled(evt.newValue));
            actions.Add(_broker);
            var reconnect = AgentUi.Button("Reconnect", "Reconnect this Unity process to the Broker.",
                UnityAgentEvalSettingsBridge.Reconnect, 104, AgentUi.Surface3,
                AgentUi.TextSecondary, AgentIconKind.Refresh);
            reconnect.SetEnabled(UnityAgentEvalSettingsBridge.CanReconnect);
            actions.Add(reconnect);
            actions.Add(AgentUi.Button("Copy MCP endpoint", "Copy the computer-level MCP endpoint.",
                () => GUIUtility.systemCopyBuffer = Endpoint, 156));
            var openFolder = AgentUi.Button("Open Broker folder", "Reveal ~/.unityevaltool.",
                UnityAgentEvalSettingsBridge.OpenBrokerFolder, 148);
            openFolder.SetEnabled(UnityAgentEvalSettingsBridge.CanOpenBrokerFolder);
            actions.Add(openFolder);
            connection.Add(actions);
            Add(connection);

            var compilation = AgentUi.Card("Compilation", "Latest compilation cycle published to the Broker");
            AddValue(compilation, "Result", "compilation");
            AddValue(compilation, "Cycle ID", "cycle");
            AddValue(compilation, "Last cycle", "lastCompilation");
            Add(compilation);

            var identity = AgentUi.Card("Unity identity", "Stable process identity and reload generations");
            AddValue(identity, "Instance ID", "instance");
            AddValue(identity, "Connection epoch", "epoch");
            AddValue(identity, "VM generation", "vm");
            AddValue(identity, "Main thread heartbeat", "heartbeat");
            Add(identity);

            var environment = AgentUi.Card("Environment", "External entry points and fixed local storage");
            AddStaticValue(environment, "MCP endpoint", Endpoint);
            AddStaticValue(environment, "Settings file",
                System.IO.Path.Combine(AgentPaths.SettingsRoot, AgentPaths.SettingsFileName));
            AddStaticValue(environment, "Provider settings",
                System.IO.Path.Combine(AgentPaths.SettingsRoot, AgentPaths.ProviderSettingsFileName));
            AddStaticValue(environment, "Agent conversations",
                System.IO.Path.Combine(AgentPaths.SettingsRoot, AgentPaths.AgentConversationsFolderName));
            AddStaticValue(environment, "Command Line history",
                System.IO.Path.Combine(AgentPaths.SettingsRoot, AgentPaths.CommandLineHistoryFolderName));
            AddValue(environment, "CLI installation", "installation");
            Add(environment);
            Tick();
        }

        public void Tick()
        {
            var status = UnityAgentEvalSettingsBridge.ConnectionSnapshot;
            var enabled = UnityAgentEvalSettingsBridge.IsBrokerControlAvailable
                ? UnityAgentEvalSettingsBridge.BrokerEnabled
                : status.IsRunning;
            Set("connection", !enabled ? "Disabled" : status.IsConnected ? "Connected" :
                status.IsRunning ? "Reconnecting" : "Stopped");
            Set("phase", string.IsNullOrWhiteSpace(status.Phase) ? "Unavailable" : status.Phase);
            Set("evaluation", status.CanEval && status.IsConnected
                ? string.Equals(status.Phase, "CompilationFailed", StringComparison.Ordinal) ? "Repair" : "Ready"
                : "Unavailable");
            Set("busy", string.IsNullOrWhiteSpace(status.BusyReason) ? "—" : status.BusyReason);
            Set("runtime", status.IsPlaying
                ? status.IsPaused ? UnityAgentEvalSettingsBridge.RuntimeStateLabel + " / Paused" :
                    UnityAgentEvalSettingsBridge.RuntimeStateLabel + " / Playing"
                : status.IsUpdating ? UnityAgentEvalSettingsBridge.RuntimeStateLabel + " / Importing" :
                    UnityAgentEvalSettingsBridge.RuntimeStateLabel);
            Set("compilation", $"{status.CompilerErrorCount} errors / {status.CompilerWarningCount} warnings");
            Set("cycle", Short(status.CompilationCycleId));
            Set("lastCompilation", FormatTimes(status.LastCompilationStartedAtUtc,
                status.LastCompilationFinishedAtUtc));
            Set("instance", string.IsNullOrWhiteSpace(status.InstanceId) ? "Unavailable" : status.InstanceId);
            Set("epoch", status.ConnectionEpoch.ToString(CultureInfo.InvariantCulture));
            Set("vm", status.VmGeneration.ToString(CultureInfo.InvariantCulture));
            Set("heartbeat", status.MainThreadTickAtUtc == default
                ? "No heartbeat"
                : $"tick {status.MainThreadTick} · {status.MainThreadTickAtUtc.ToLocalTime():HH:mm:ss}");
            Set("installation", GetInstallationStatus());
            if (UnityAgentEvalSettingsBridge.IsBrokerControlAvailable)
                _broker.SetValueWithoutNotify(UnityAgentEvalSettingsBridge.BrokerEnabled);
        }

        private static string GetInstallationStatus()
        {
            var metadata = System.IO.Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), ".unityevaltool", "install.json");
            if (!System.IO.File.Exists(metadata))
                return "Not installed · run the npm-installed `unity` command once";
            try
            {
                var root = EvalData.AsObject(EvalJson.Parse(System.IO.File.ReadAllText(metadata)));
                var executable = root == null ? string.Empty : EvalData.GetString(root, "executablePath") ?? string.Empty;
                return string.IsNullOrWhiteSpace(executable)
                    ? "Invalid install metadata · executablePath is missing"
                    : executable;
            }
            catch (Exception exception)
            {
                return "Invalid install metadata · " + exception.Message;
            }
        }

        private void AddValue(VisualElement parent, string name, string key)
        {
            var label = AddStaticValue(parent, name, "—");
            _values.Add(key, label);
        }

        private static Label AddStaticValue(VisualElement parent, string name, string value)
        {
            var row = AgentUi.Inset();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.FlexStart;
            var key = new Label(name);
            key.style.width = 178;
            key.style.flexShrink = 0;
            key.style.color = AgentUi.Muted;
            row.Add(key);
            var label = new Label(value) { style = { flexGrow = 1, minWidth = 0 } };
            label.style.whiteSpace = WhiteSpace.Normal;
            row.Add(label);
            parent.Add(row);
            return label;
        }

        private void Set(string key, string value) => _values[key].text = value;
        private static string Short(string value) => string.IsNullOrWhiteSpace(value)
            ? "—" : value.Length <= 12 ? value : value.Substring(0, 12) + "…";
        private static string FormatTimes(string startedAt, string finishedAt)
        {
            var started = ParseLocalTime(startedAt);
            var finished = ParseLocalTime(finishedAt);
            return started == "—" && finished == "—"
                ? "No compilation published"
                : $"started {started} / finished {finished}";
        }

        private static string ParseLocalTime(string value) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var dateTime)
                ? dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "—";
    }

    internal sealed class AgentEvalToolsSettingsView : VisualElement, IDisposable
    {
        private readonly AgentTextField _search;
        private readonly VisualElement _list;
        private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
        private bool _dirty = true;

        public AgentEvalToolsSettingsView()
        {
            style.minWidth = 0;
            style.width = new Length(100, LengthUnit.Percent);
            Add(AgentUi.PageHeading("Unity Eval Tool · Tools",
                "Complete root Tool, function, safety and sub-tool information matching the standalone Yuze Eval Tool Editor window."));
            var toolbar = AgentUi.WrapRow();
            _search = AgentUi.Field(string.Empty, string.Empty, "Filter by Tool name, path or description.");
            _search.Placeholder = "Filter Tools…";
            _search.style.flexGrow = 1;
            _search.RegisterValueChangedCallback(_ => { _dirty = true; Refresh(); });
            toolbar.Add(_search);
            toolbar.Add(AgentUi.Button("Refresh registry", "Refresh Tool metadata and rebuild this list.",
                () => { _ = EvalToolRegistry.GetIndex(true); _dirty = true; Refresh(); },
                146, AgentUi.Surface3, AgentUi.TextSecondary, AgentIconKind.Refresh));
            Add(toolbar);
            _list = new VisualElement { style = { minWidth = 0, marginTop = 10 } };
            Add(_list);
            EvalToolRegistry.Changed += MarkDirty;
            Refresh();
        }

        public void Tick()
        {
            if (_dirty) Refresh();
        }

        public void Dispose() => EvalToolRegistry.Changed -= MarkDirty;
        private void MarkDirty() => _dirty = true;

        private void Refresh()
        {
            _dirty = false;
            _list.Clear();
            var filter = _search.value.Trim();
            var tools = EvalToolRegistry.ListTools(false)
                .Where(value => (UnityAgentEvalSettingsBridge.IncludeEditorOnlyTools || !value.EditorOnly) &&
                                Matches(value, filter))
                .OrderBy(value => value.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Path, StringComparer.Ordinal).ToList();
            if (tools.Count == 0)
            {
                _list.Add(AgentWorkspaceUi.Empty("No Tools match the current filter."));
                return;
            }
            foreach (var group in tools.GroupBy(value => value.Source, StringComparer.OrdinalIgnoreCase))
            {
                var heading = new Label($"{group.Key.ToUpperInvariant()} TOOLS · {group.Count()}");
                AgentUi.ApplyTypography(heading, AgentTypography.Caption);
                heading.style.color = AgentUi.Muted;
                heading.style.marginTop = 10;
                _list.Add(heading);
                foreach (var tool in group) _list.Add(CreateTool(tool));
            }
        }

        private VisualElement CreateTool(EvalToolDescriptor tool)
        {
            var card = AgentUi.Card(tool.Name, string.IsNullOrWhiteSpace(tool.Description)
                ? "No description." : tool.Description);
            var expanded = _expanded.Contains(tool.Path);
            var header = AgentUi.WrapRow();
            header.Add(AgentUi.Button(expanded ? "Hide details" : "Show details", tool.Path, () =>
            {
                if (!_expanded.Add(tool.Path)) _expanded.Remove(tool.Path);
                Refresh();
            }, 112));
            header.Add(Pill(tool.Source.ToUpperInvariant()));
            header.Add(Pill(tool.EditorOnly ? "EDITOR" : "RUNTIME"));
            var toggle = new AgentToggle(tool.Enabled ? "Enabled" : "Disabled");
            toggle.SetValueWithoutNotify(tool.Enabled);
            toggle.RegisterValueChangedCallback(evt =>
            {
                var result = EvalToolRegistry.SetToolEnabled(tool.Path, evt.newValue);
                if (!EvalData.GetBool(result, "ok"))
                    throw new InvalidOperationException(EvalData.GetString(result, "error") ??
                                                        $"Unable to update Tool '{tool.Path}'.");
                _dirty = true;
            });
            header.Add(toggle);
            card.Add(header);
            if (!expanded) return card;

            AddDetail(card, "Import path", "tools://" + tool.Path);
            AddDetail(card, "Source", tool.Source);
            AddDetail(card, "Availability", tool.EditorOnly ? "Unity Editor only" : "Editor and Player");
            AddDetail(card, "Contents", $"{tool.Functions.Count} functions / {tool.SubTools.Count} sub tools");
            foreach (var function in tool.Functions)
            {
                var signature = function.MethodName + "(" + string.Join(", ",
                    function.Parameters.Select(FormatParameter)) + ")";
                var detail = AgentUi.Inset();
                var title = new Label(signature);
                title.style.whiteSpace = WhiteSpace.Normal;
                detail.Add(title);
                if (!string.IsNullOrWhiteSpace(function.Description))
                    detail.Add(Muted(function.Description));
                if (function.Safety != EvalToolSafety.Unspecified)
                    detail.Add(Muted("Risk: " + function.RiskLevel +
                                     (function.RequiresConfirmation ? " · confirmation required" : string.Empty)));
                card.Add(detail);
            }
            foreach (var subTool in tool.SubTools)
                AddDetail(card, "Sub-tool", $"{subTool.Path} · {subTool.FunctionCount} functions · " +
                    (subTool.Enabled ? "enabled" : "disabled"));
            return card;
        }

        private static string FormatParameter(EvalToolParameterDescriptor value) =>
            value.Name + ": " + value.Type + (value.Optional ? "?" : string.Empty);

        private static bool Matches(EvalToolDescriptor tool, string filter) =>
            string.IsNullOrWhiteSpace(filter) ||
            tool.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            tool.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            tool.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            tool.SubTools.Any(value => value.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       value.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

        private static Label Pill(string text)
        {
            var label = new Label(text);
            AgentUi.ApplyTypography(label, AgentTypography.Caption);
            label.style.color = AgentUi.TextSecondary;
            label.style.backgroundColor = AgentUi.Surface3;
            label.style.paddingLeft = 8;
            label.style.paddingRight = 8;
            return label;
        }

        private static void AddDetail(VisualElement parent, string name, string value)
        {
            var row = AgentUi.Inset();
            row.style.flexDirection = FlexDirection.Row;
            var key = new Label(name) { style = { width = 140, flexShrink = 0 } };
            key.style.color = AgentUi.Muted;
            row.Add(key);
            var content = new Label(value) { style = { flexGrow = 1, minWidth = 0 } };
            content.style.whiteSpace = WhiteSpace.Normal;
            row.Add(content);
            parent.Add(row);
        }

        private static Label Muted(string value)
        {
            var label = new Label(value);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = AgentUi.Muted;
            return label;
        }
    }
}
