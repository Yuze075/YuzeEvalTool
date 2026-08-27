#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace YuzeToolkit.Agent
{
    /// <summary>A live section hosted by the workbench System Info page.</summary>
    public interface IUnityAgentWorkspaceSection : IDisposable
    {
        VisualElement Root { get; }
        void Tick();
    }

    /// <summary>
    /// Composition point for the protected System Info and Performance views owned by AgentTool.
    /// </summary>
    public static class UnityAgentWorkspaceRegistry
    {
        private static readonly List<Registration> SystemInfoRegistrations = new();
        private static int _revision;

        internal static int Revision => _revision;

        public static IDisposable RegisterSystemInfoSection(
            string id,
            int order,
            Func<IUnityAgentWorkspaceSection> factory)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Section id is required.", nameof(id));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (SystemInfoRegistrations.Any(value => string.Equals(value.Id, id, StringComparison.Ordinal)))
                throw new InvalidOperationException($"System Info section '{id}' is already registered.");
            var registration = new Registration(id, order, factory);
            SystemInfoRegistrations.Add(registration);
            _revision++;
            return new RegistrationHandle(registration);
        }

        internal static IReadOnlyList<IUnityAgentWorkspaceSection> CreateSystemInfoSections()
        {
            var sections = new List<IUnityAgentWorkspaceSection>();
            try
            {
                foreach (var registration in SystemInfoRegistrations
                             .OrderBy(value => value.Order).ThenBy(value => value.Id, StringComparer.Ordinal))
                    sections.Add(registration.Factory());
                return sections;
            }
            catch
            {
                for (var index = sections.Count - 1; index >= 0; index--)
                    sections[index].Dispose();
                throw;
            }
        }

        private sealed class Registration
        {
            public Registration(string id, int order, Func<IUnityAgentWorkspaceSection> factory)
            {
                Id = id;
                Order = order;
                Factory = factory;
            }

            public string Id { get; }
            public int Order { get; }
            public Func<IUnityAgentWorkspaceSection> Factory { get; }
        }

        private sealed class RegistrationHandle : IDisposable
        {
            private Registration? _registration;

            public RegistrationHandle(Registration registration) => _registration = registration;

            public void Dispose()
            {
                if (_registration == null) return;
                SystemInfoRegistrations.Remove(_registration);
                _registration = null;
                _revision++;
            }
        }
    }

    /// <summary>
    /// Editor bridge for the exact interval in which runtime-owned DebugWindow bindings are safe to render.
    /// Player builds fall back to Application.isPlaying.
    /// </summary>
    public static class UnityAgentRuntimeDataBridge
    {
        private static Func<bool>? _isAvailable;

        public static bool IsAvailable => _isAvailable?.Invoke() ?? UnityEngine.Application.isPlaying;

        public static void Configure(Func<bool> isAvailable) =>
            _isAvailable = isAvailable ?? throw new ArgumentNullException(nameof(isAvailable));
    }

    /// <summary>Editor-only Eval broker controls exposed without making the runtime assembly depend on Editor code.</summary>
    public static class UnityAgentEvalSettingsBridge
    {
        private static Func<bool>? _getBrokerEnabled;
        private static Action<bool>? _setBrokerEnabled;
        private static Func<UnityAgentEvalConnectionSnapshot>? _getConnectionSnapshot;
        private static Action? _reconnect;
        private static Action? _openBrokerFolder;
        private static Action? _openProjectSettings;
        private static Action<AgentSettingsDocument>? _overwriteProjectSettings;

        public static bool IsBrokerControlAvailable => _getBrokerEnabled != null && _setBrokerEnabled != null;
        public static bool CanReconnect => _reconnect != null;
        public static bool CanOpenBrokerFolder => _openBrokerFolder != null;
        public static bool CanOpenProjectSettings => _openProjectSettings != null;
        public static bool CanOverwriteProjectSettings => _overwriteProjectSettings != null;
        public static bool IncludeEditorOnlyTools => IsBrokerControlAvailable;
        public static string RuntimeStateLabel => AgentPaths.IsEditor ? "Editor" : "Player";
        public static bool BrokerEnabled => _getBrokerEnabled?.Invoke() ?? false;
        public static UnityAgentEvalConnectionSnapshot ConnectionSnapshot =>
            _getConnectionSnapshot?.Invoke() ?? new UnityAgentEvalConnectionSnapshot();

        public static void SetBrokerEnabled(bool enabled)
        {
            if (_setBrokerEnabled == null)
                throw new InvalidOperationException("The Eval Broker control is only available in the Unity Editor.");
            _setBrokerEnabled(enabled);
        }

        public static void ConfigureBrokerControl(
            Func<bool> getEnabled,
            Action<bool> setEnabled,
            Func<UnityAgentEvalConnectionSnapshot> getConnectionSnapshot)
        {
            _getBrokerEnabled = getEnabled ?? throw new ArgumentNullException(nameof(getEnabled));
            _setBrokerEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
            _getConnectionSnapshot = getConnectionSnapshot ??
                                     throw new ArgumentNullException(nameof(getConnectionSnapshot));
        }

        public static void ConfigureEditorActions(
            Action reconnect,
            Action openBrokerFolder,
            Action openProjectSettings,
            Action<AgentSettingsDocument> overwriteProjectSettings)
        {
            _reconnect = reconnect ?? throw new ArgumentNullException(nameof(reconnect));
            _openBrokerFolder = openBrokerFolder ?? throw new ArgumentNullException(nameof(openBrokerFolder));
            _openProjectSettings = openProjectSettings ?? throw new ArgumentNullException(nameof(openProjectSettings));
            _overwriteProjectSettings = overwriteProjectSettings ??
                                        throw new ArgumentNullException(nameof(overwriteProjectSettings));
        }

        public static void Reconnect() =>
            (_reconnect ?? throw new InvalidOperationException("Reconnect is only available in the Unity Editor."))();

        public static void OpenBrokerFolder() =>
            (_openBrokerFolder ?? throw new InvalidOperationException(
                "The Broker folder is only available in the Unity Editor."))();

        public static void OpenProjectSettings() =>
            (_openProjectSettings ?? throw new InvalidOperationException(
                "Project Settings are only available in the Unity Editor."))();

        public static void OverwriteProjectSettings(AgentSettingsDocument settings) =>
            (_overwriteProjectSettings ?? throw new InvalidOperationException(
                "Project Settings are only writable in the Unity Editor."))(settings);
    }

    /// <summary>
    /// Optional Editor diagnostics copied across the assembly boundary. The Agent runtime never
    /// references or connects to the Broker client directly.
    /// </summary>
    public sealed class UnityAgentEvalConnectionSnapshot
    {
        public bool IsRunning { get; set; }
        public bool IsConnected { get; set; }
        public string Phase { get; set; } = string.Empty;
        public bool CanEval { get; set; }
        public string BusyReason { get; set; } = string.Empty;
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public bool IsUpdating { get; set; }
        public int CompilerErrorCount { get; set; }
        public int CompilerWarningCount { get; set; }
        public string CompilationCycleId { get; set; } = string.Empty;
        public string LastCompilationStartedAtUtc { get; set; } = string.Empty;
        public string LastCompilationFinishedAtUtc { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public long ConnectionEpoch { get; set; }
        public long VmGeneration { get; set; }
        public long MainThreadTick { get; set; }
        public DateTime MainThreadTickAtUtc { get; set; }
    }
}
