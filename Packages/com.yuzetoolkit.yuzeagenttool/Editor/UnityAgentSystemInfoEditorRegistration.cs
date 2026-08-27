#nullable enable
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using YuzeToolkit.UnityAgent;

namespace YuzeToolkit.UnityAgent
{
    [InitializeOnLoad]
    internal static class UnityAgentSystemInfoEditorRegistration
    {
        private const string SystemInfoRoot =
            "Packages/com.yuzetoolkit.yuzeagenttool/Runtime/SystemInfo/UI/SystemInfo";
        private const string PerformanceRoot =
            "Packages/com.yuzetoolkit.yuzeagenttool/Runtime/Performance/UI/PerformanceMonitor";

        private static IDisposable? _systemInfoRegistration;
        private static IDisposable? _performanceRegistration;

        static UnityAgentSystemInfoEditorRegistration()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.delayCall += RegisterEditModeSections;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    DisposeRegistrations();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    EditorApplication.delayCall += RegisterEditModeSections;
                    break;
            }
        }

        private static void RegisterEditModeSections()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || _systemInfoRegistration != null) return;

            var systemTemplate = LoadRequired<VisualTreeAsset>(SystemInfoRoot + ".uxml");
            var systemStyle = LoadRequired<StyleSheet>(SystemInfoRoot + ".uss");
            var performanceTemplate = LoadRequired<VisualTreeAsset>(PerformanceRoot + ".uxml");
            var performanceStyle = LoadRequired<StyleSheet>(PerformanceRoot + ".uss");

            _systemInfoRegistration = UnityAgentWorkspaceRegistry.RegisterSystemInfoSection(
                "unity-agent-system-info", 10,
                () => SystemInfoModule.CreateWorkspaceSection(systemTemplate, systemStyle));
            try
            {
                _performanceRegistration = UnityAgentWorkspaceRegistry.RegisterSystemInfoSection(
                    "unity-agent-performance", 0,
                    () => PerformanceMonitorModule.CreateWorkspaceSection(performanceTemplate, performanceStyle));
            }
            catch
            {
                DisposeRegistrations();
                throw;
            }
        }

        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            return asset != null
                ? asset
                : throw new MissingReferenceException($"Required UnityAgentTool asset was not found: {path}");
        }

        private static void DisposeRegistrations()
        {
            _performanceRegistration?.Dispose();
            _performanceRegistration = null;
            _systemInfoRegistration?.Dispose();
            _systemInfoRegistration = null;
        }
    }
}
