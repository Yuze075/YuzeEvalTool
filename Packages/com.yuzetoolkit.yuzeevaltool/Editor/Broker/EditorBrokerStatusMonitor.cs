#nullable enable
using System;
using UnityEditor;
using UnityEditor.Compilation;

namespace YuzeToolkit
{
    internal static class EditorBrokerStatusMonitor
    {
        private const string Prefix = nameof(YuzeToolkit) + ".BrokerCompilation.";
        private const string KeyPhase = Prefix + "Phase";
        private const string KeyCycleId = Prefix + "CycleId";
        private const string KeyErrorCount = Prefix + "ErrorCount";
        private const string KeyWarningCount = Prefix + "WarningCount";
        private const string KeyStartedAt = Prefix + "StartedAtUtc";
        private const string KeyFinishedAt = Prefix + "FinishedAtUtc";
        private static bool _readyAfterReloadPending = true;

        public static void Initialize()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload -= MarkReloading;
            AssemblyReloadEvents.beforeAssemblyReload += MarkReloading;
        }

        public static void MarkReloading()
        {
            SessionState.SetString(KeyPhase, "Reloading");
            _readyAfterReloadPending = true;
        }

        public static BrokerUnityStatusSnapshot Capture(long vmGeneration)
        {
            var phase = ResolvePhase();
            var busyReason = phase switch
            {
                "Importing" => "Unity Editor is importing or updating assets.",
                "Compiling" => "Unity Editor is compiling scripts.",
                "CompilationFailed" => $"Unity script compilation failed with {SessionState.GetInt(KeyErrorCount, 0)} error(s). " +
                                       "Eval and CLI are available in repair mode through the last successfully loaded assemblies.",
                "Reloading" => "Unity Editor is reloading assemblies.",
                "PlayModeTransition" => "Unity Editor is changing PlayMode.",
                _ => string.Empty
            };
            return new BrokerUnityStatusSnapshot
            {
                Phase = phase,
                CanEval = string.Equals(phase, "Ready", StringComparison.Ordinal) ||
                          string.Equals(phase, "CompilationFailed", StringComparison.Ordinal),
                BusyReason = busyReason,
                IsPlaying = EditorApplication.isPlaying,
                IsPaused = EditorApplication.isPaused,
                IsUpdating = EditorApplication.isUpdating,
                CompilationCycleId = SessionState.GetString(KeyCycleId, string.Empty),
                CompilerErrorCount = SessionState.GetInt(KeyErrorCount, 0),
                CompilerWarningCount = SessionState.GetInt(KeyWarningCount, 0),
                LastCompilationStartedAtUtc = SessionState.GetString(KeyStartedAt, string.Empty),
                LastCompilationFinishedAtUtc = SessionState.GetString(KeyFinishedAt, string.Empty),
                VmGeneration = vmGeneration
            };
        }

        private static string ResolvePhase()
        {
            if (EditorApplication.isCompiling) return "Compiling";
            if (EditorApplication.isUpdating) return "Importing";
            if (EditorStatusProvider.IsChangingPlayMode || EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                return "PlayModeTransition";
            var persisted = SessionState.GetString(KeyPhase, string.Empty);
            if (string.Equals(persisted, "CompilationFailed", StringComparison.Ordinal)) return persisted;
            if (_readyAfterReloadPending || string.Equals(persisted, "Reloading", StringComparison.Ordinal))
            {
                _readyAfterReloadPending = false;
                SessionState.SetString(KeyPhase, "Ready");
            }
            return "Ready";
        }

        private static void OnCompilationStarted(object context)
        {
            SessionState.SetString(KeyCycleId, Guid.NewGuid().ToString("N"));
            SessionState.SetInt(KeyErrorCount, 0);
            SessionState.SetInt(KeyWarningCount, 0);
            SessionState.SetString(KeyStartedAt, DateTime.UtcNow.ToString("O"));
            SessionState.SetString(KeyPhase, "Compiling");
            _readyAfterReloadPending = false;
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            var errors = SessionState.GetInt(KeyErrorCount, 0);
            var warnings = SessionState.GetInt(KeyWarningCount, 0);
            foreach (var message in messages)
            {
                if (message.type == CompilerMessageType.Error) errors++;
                else if (message.type == CompilerMessageType.Warning) warnings++;
            }
            SessionState.SetInt(KeyErrorCount, errors);
            SessionState.SetInt(KeyWarningCount, warnings);
        }

        private static void OnCompilationFinished(object context)
        {
            SessionState.SetString(KeyFinishedAt, DateTime.UtcNow.ToString("O"));
            if (SessionState.GetInt(KeyErrorCount, 0) > 0)
            {
                SessionState.SetString(KeyPhase, "CompilationFailed");
                _readyAfterReloadPending = false;
                return;
            }
            MarkReloading();
        }
    }
}
