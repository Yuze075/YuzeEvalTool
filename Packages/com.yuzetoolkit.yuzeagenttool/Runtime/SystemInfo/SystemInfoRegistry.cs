#nullable enable
using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace YuzeToolkit.Agent
{
    public static class SystemInfoRegistry
    {
        private static readonly object SyncRoot = new();
        private static readonly List<SystemInfoRegistration> Registrations = new();

        static SystemInfoRegistry()
        {
            RegisterDefaultProviders();
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        public static bool Register(string key, Func<string> valueProvider)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("System info key cannot be empty.", nameof(key));
            if (valueProvider == null)
                throw new ArgumentNullException(nameof(valueProvider));

            lock (SyncRoot)
            {
                for (var i = 0; i < Registrations.Count; i++)
                {
                    if (!string.Equals(Registrations[i].Key, key, StringComparison.Ordinal))
                        continue;

                    Registrations[i] = new SystemInfoRegistration(key, valueProvider);
                    return false;
                }

                Registrations.Add(new SystemInfoRegistration(key, valueProvider));
                return true;
            }
        }

        public static bool Unregister(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            lock (SyncRoot)
            {
                for (var i = 0; i < Registrations.Count; i++)
                {
                    if (!string.Equals(Registrations[i].Key, key, StringComparison.Ordinal))
                        continue;

                    Registrations.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        internal static SystemInfoSnapshot CaptureSnapshot()
        {
            SystemInfoRegistration[] registrations;
            lock (SyncRoot)
                registrations = Registrations.ToArray();

            var lines = new SystemInfoLine[registrations.Length];
            for (var i = 0; i < registrations.Length; i++)
                lines[i] = new SystemInfoLine(registrations[i].Key, ResolveValue(registrations[i].ValueProvider));

            return new SystemInfoSnapshot(lines);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeState()
        {
            ResetToDefaultProviders();
        }

#if UNITY_EDITOR
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state is PlayModeStateChange.ExitingEditMode or PlayModeStateChange.ExitingPlayMode)
                ResetToDefaultProviders();
        }
#endif

        private static void ResetToDefaultProviders()
        {
            lock (SyncRoot)
                Registrations.Clear();

            RegisterDefaultProviders();
        }

        private static void RegisterDefaultProviders()
        {
            Register("Screen", FormatCurrentResolution);
            Register("Window", () => $"{Screen.width}x{Screen.height}@{GetRefreshRate()}Hz[{(int)Screen.dpi}dpi]");
            Register("Graphics API", () => $"{UnityEngine.SystemInfo.graphicsDeviceType}");
            Register("GPU", () => UnityEngine.SystemInfo.graphicsDeviceName);
            Register("VRAM", () => $"{UnityEngine.SystemInfo.graphicsMemorySize} MB");
            Register("Max Texture Size", () => $"{UnityEngine.SystemInfo.maxTextureSize}px");
            Register("Shader Level", () => $"{UnityEngine.SystemInfo.graphicsShaderLevel}");
            Register("CPU", () => $"{UnityEngine.SystemInfo.processorType} [{UnityEngine.SystemInfo.processorCount} cores]");
            Register("RAM", () => $"{UnityEngine.SystemInfo.systemMemorySize} MB");
            Register("OS", () => $"{UnityEngine.SystemInfo.operatingSystem} [{UnityEngine.SystemInfo.deviceType}]");
        }

        private static string ResolveValue(Func<string> valueProvider)
        {
            try
            {
                return valueProvider() ?? string.Empty;
            }
            catch (Exception exception)
            {
                return $"Error: {exception.GetType().Name}";
            }
        }

        private static string FormatCurrentResolution()
        {
            var resolution = Screen.currentResolution;
            return $"{resolution.width}x{resolution.height}@{GetRefreshRate()}Hz";
        }

        private static int GetRefreshRate()
        {
            var resolution = Screen.currentResolution;
#if UNITY_2022_2_OR_NEWER
            return Mathf.RoundToInt((float)resolution.refreshRateRatio.value);
#else
            return resolution.refreshRate;
#endif
        }
    }
}
