#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.UIElements;
using YuzeToolkit.Eval;

namespace YuzeToolkit.Agent
{
    internal sealed class UnityAgentWindow : EditorWindow
    {
        private UnityAgentWorkbenchView? _view;
        private IVisualElementScheduledItem? _tickItem;
        private static UnityAgentWorkbenchPage _requestedPage = UnityAgentWorkbenchPage.Chat;

        [MenuItem("YuzeToolkit/Agent Tool")]
        public static void OpenChat()
        {
            Open(UnityAgentWorkbenchPage.Chat);
        }

        public static void OpenSettings()
        {
            Open(UnityAgentWorkbenchPage.Settings);
        }

        private static void Open(UnityAgentWorkbenchPage page)
        {
            _requestedPage = page;
            var window = GetWindow<UnityAgentWindow>("Yuze Agent Tool");
            window.minSize = new Vector2(480, 480);
            window.Show();
            window._view?.ShowPage(page);
        }

        private void CreateGUI()
        {
            _tickItem?.Pause();
            _view?.Dispose();
            rootVisualElement.Clear();
            _view = new UnityAgentWorkbenchView(UnityAgentHost.Default, initialPage: _requestedPage);
            rootVisualElement.Add(_view);
            _tickItem = rootVisualElement.schedule.Execute(() => _view?.Tick()).Every(100);
        }

        private void OnDisable()
        {
            _tickItem?.Pause();
            _tickItem = null;
            _view?.Dispose();
            _view = null;
        }
    }

    [InitializeOnLoad]
    internal static class UnityAgentEditorLifetime
    {
        private static bool _runtimeDataAvailable;

        static UnityAgentEditorLifetime()
        {
            _runtimeDataAvailable = EditorApplication.isPlaying;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            UnityAgentRuntimeDataBridge.Configure(() => _runtimeDataAvailable);
            UnityAgentEvalSettingsBridge.ConfigureBrokerControl(
                () => EditorBrokerBootstrap.IsEnabled,
                EditorBrokerBootstrap.SetEnabled,
                CaptureEvalConnectionSnapshot);
            UnityAgentEvalSettingsBridge.ConfigureEditorActions(
                EditorBrokerBootstrap.Reconnect,
                OpenBrokerFolder,
                UnityAgentProjectSettingsProvider.Open,
                UnityAgentProjectSettingsProvider.OverwriteFromMachineSettings);
            AssemblyReloadEvents.beforeAssemblyReload -= UnityAgentHost.DisposeDefault;
            AssemblyReloadEvents.beforeAssemblyReload += UnityAgentHost.DisposeDefault;
            AssemblyReloadEvents.beforeAssemblyReload -= AgentUi.DisposeEditorFontResources;
            AssemblyReloadEvents.beforeAssemblyReload += AgentUi.DisposeEditorFontResources;
            EditorApplication.quitting -= UnityAgentHost.DisposeDefault;
            EditorApplication.quitting += UnityAgentHost.DisposeDefault;
            EditorApplication.quitting -= AgentUi.DisposeEditorFontResources;
            EditorApplication.quitting += AgentUi.DisposeEditorFontResources;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            _runtimeDataAvailable = state == PlayModeStateChange.EnteredPlayMode;
        }

        private static void OpenBrokerFolder()
        {
            var path = Path.Combine(System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.UserProfile), ".unityevaltool");
            Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        private static UnityAgentEvalConnectionSnapshot CaptureEvalConnectionSnapshot()
        {
            var client = UnityBrokerClient.Shared;
            var status = client.LatestStatus;
            var identity = client.Identity;
            return new UnityAgentEvalConnectionSnapshot
            {
                IsRunning = client.IsRunning,
                IsConnected = client.IsConnected,
                Phase = status.Phase,
                CanEval = status.CanEval,
                BusyReason = status.BusyReason,
                IsPlaying = status.IsPlaying,
                IsPaused = status.IsPaused,
                IsUpdating = status.IsUpdating,
                CompilerErrorCount = status.CompilerErrorCount,
                CompilerWarningCount = status.CompilerWarningCount,
                CompilationCycleId = status.CompilationCycleId,
                LastCompilationStartedAtUtc = status.LastCompilationStartedAtUtc,
                LastCompilationFinishedAtUtc = status.LastCompilationFinishedAtUtc,
                InstanceId = identity.InstanceId,
                ConnectionEpoch = identity.ConnectionEpoch,
                VmGeneration = status.VmGeneration,
                MainThreadTick = status.MainThreadTick,
                MainThreadTickAtUtc = status.MainThreadTickAtUtc
            };
        }

    }

    /// <summary>
    /// Persists active Agent turns across script compilation and resumes them once the Editor is stable.
    /// </summary>
    [InitializeOnLoad]
    internal static class UnityAgentCompilationRecovery
    {
        private const string MarkerFileName = "UnityAgentEditorCompilationRecovery.json";
        private const string Interruption =
            "Unity Editor started script compilation; this turn was paused for automatic continuation after compilation.";
        private static int _errors;
        private static int _warnings;
        private static double _fallbackAt;
        private static bool _resumeRunning;
        private static bool _resumePending;

        static UnityAgentCompilationRecovery()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            MigrateLegacyMarker();
            _resumePending = File.Exists(MarkerPath);
            EditorApplication.delayCall += TryResume;
        }

        private static void OnCompilationStarted(object context)
        {
            _errors = 0;
            _warnings = 0;
            _fallbackAt = 0;
            var host = UnityAgentHost.Default;
            var active = host.GetActiveSessionIdsForEditorCompilation();
            if (active.Count == 0) return;
            var marker = new RecoveryMarker
            {
                editorProcessId = Process.GetCurrentProcess().Id,
                projectRoot = AgentPaths.ProjectRoot,
                startedAtUtc = DateTime.UtcNow.ToString("O"),
                sessionIds = active.Distinct(StringComparer.Ordinal).ToList()
            };
            try
            {
                SaveMarker(marker);
            }
            catch (Exception exception)
            {
                LogSys.LogError("Yuze Agent Tool could not persist compilation recovery intent; active turns were not " +
                               "interrupted automatically. " + exception.Message);
                return;
            }
            marker.sessionIds = host.InterruptSessionsForEditorCompilation(marker.sessionIds, Interruption)
                .Distinct(StringComparer.Ordinal).ToList();
            if (marker.sessionIds.Count == 0) DeleteMarker();
            else SaveMarker(marker);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            foreach (var message in messages)
            {
                if (message.type == CompilerMessageType.Error) _errors++;
                else if (message.type == CompilerMessageType.Warning) _warnings++;
            }
        }

        private static void OnCompilationFinished(object context)
        {
            if (!TryLoadMarker(out var marker)) return;
            marker.compilationFinished = true;
            marker.compilerErrorCount = _errors;
            marker.compilerWarningCount = _warnings;
            marker.finishedAtUtc = DateTime.UtcNow.ToString("O");
            SaveMarker(marker);
            if (_errors > 0)
            {
                _resumePending = true;
                EditorApplication.delayCall += TryResume;
            }
            else
            {
                // Successful compilation normally reloads immediately; this covers configurations
                // which compile without a Domain Reload.
                _fallbackAt = EditorApplication.timeSinceStartup + 5d;
            }
        }

        private static void BeforeAssemblyReload()
        {
            _fallbackAt = 0;
            if (!TryLoadMarker(out var marker)) return;
            marker.awaitingDomainReload = true;
            SaveMarker(marker);
        }

        private static void Update()
        {
            if (_fallbackAt > 0 && EditorApplication.timeSinceStartup >= _fallbackAt)
            {
                _fallbackAt = 0;
                _resumePending = true;
            }
            if (_resumePending) TryResume();
        }

        private static void TryResume()
        {
            if (_resumeRunning || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!TryLoadMarker(out var marker))
            {
                _resumePending = false;
                return;
            }
            if (!marker.compilationFinished && !marker.awaitingDomainReload)
            {
                _resumePending = false;
                return;
            }
            _resumePending = false;
            _resumeRunning = true;
            _ = ResumeAsync(marker);
        }

        private static async Task ResumeAsync(RecoveryMarker marker)
        {
            try
            {
                DeleteMarker();
                var host = UnityAgentHost.Default;
                await host.EnsureInitializedAsync().ConfigureAwait(true);
                var outcome = marker.compilerErrorCount > 0
                    ? $"completed with {marker.compilerErrorCount} compiler error(s) and " +
                      $"{marker.compilerWarningCount} warning(s)"
                    : $"completed with no compiler errors and {marker.compilerWarningCount} warning(s)";
                var reload = marker.awaitingDomainReload
                    ? " Unity reloaded the script Domain, so cached Unity objects and the JavaScript VM were reset."
                    : string.Empty;
                var continuation = $"Unity Editor script compilation {outcome}.{reload} Continue the interrupted " +
                                   "task now: re-inspect the current files and Unity state, check the compilation " +
                                   "result, and proceed from the persisted conversation instead of assuming the " +
                                   "interrupted tool call completed.";
                var tasks = new List<Task<AgentTurnResult>>();
                foreach (var id in marker.sessionIds.Distinct(StringComparer.Ordinal))
                {
                    try
                    {
                        await host.WaitForSessionIdleAsync(id).ConfigureAwait(true);
                    }
                    catch (KeyNotFoundException)
                    {
                        continue;
                    }
                    var session = host.GetSession(id);
                    if (session == null || session.State is not (AgentSessionState.Interrupted or
                            AgentSessionState.Failed)) continue;
                    tasks.Add(host.SendMessageAsync(id, continuation));
                }
                var results = await Task.WhenAll(tasks).ConfigureAwait(true);
                var failed = results.Where(result => !result.IsSuccess).ToList();
                if (failed.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"{failed.Count} Agent conversation(s) failed to resume after compilation: " +
                        string.Join("; ", failed.Select(result =>
                            $"{result.SessionId}: {result.State} {result.Error}")));
                }
            }
            catch (Exception exception)
            {
                marker.resumeAttempts++;
                marker.lastResumeError = exception.Message;
                if (marker.resumeAttempts < 3 && !File.Exists(MarkerPath))
                {
                    SaveMarker(marker);
                    _resumePending = true;
                }
                else
                {
                    LogSys.LogError("Yuze Agent Tool could not resume conversations after compilation. " +
                                   exception.Message);
                }
            }
            finally
            {
                _resumeRunning = false;
            }
        }

        private static bool TryLoadMarker(out RecoveryMarker marker)
        {
            marker = null!;
            if (!File.Exists(MarkerPath)) return false;
            try
            {
                marker = JsonUtility.FromJson<RecoveryMarker>(File.ReadAllText(MarkerPath, Encoding.UTF8));
                if (marker == null || marker.schemaVersion != 1 ||
                    marker.editorProcessId != Process.GetCurrentProcess().Id ||
                    !AgentPaths.PathsEqual(marker.projectRoot, AgentPaths.ProjectRoot) ||
                    marker.sessionIds == null || marker.sessionIds.Count == 0)
                {
                    DeleteMarker();
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                LogSys.LogError("Yuze Agent Tool compilation recovery marker is unreadable and was removed. " +
                               exception.Message);
                DeleteMarker();
                return false;
            }
        }

        private static void SaveMarker(RecoveryMarker marker)
        {
            var path = MarkerPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ??
                                      throw new InvalidOperationException("Recovery marker has no parent directory."));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonUtility.ToJson(marker, true), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporary, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(temporary, path, true);
                        File.Delete(temporary);
                    }
                }
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void DeleteMarker()
        {
            try
            {
                if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogSys.LogError("Yuze Agent Tool could not remove its compilation recovery marker. " + exception.Message);
            }
        }

        private static void MigrateLegacyMarker()
        {
            if (File.Exists(MarkerPath) || !File.Exists(LegacyMarkerPath)) return;
            try
            {
                Directory.CreateDirectory(AgentPaths.SettingsRoot);
                File.Move(LegacyMarkerPath, MarkerPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogSys.LogError("Yuze Agent Tool could not migrate its compilation recovery marker. " +
                               exception.Message);
            }
        }

        private static string MarkerPath => Path.Combine(AgentPaths.SettingsRoot, MarkerFileName);

        private static string LegacyMarkerPath => Path.Combine(AgentPaths.LegacySettingsRoot, MarkerFileName);

        [Serializable]
        private sealed class RecoveryMarker
        {
            public int schemaVersion = 1;
            public int editorProcessId;
            public string projectRoot = string.Empty;
            public string startedAtUtc = string.Empty;
            public string finishedAtUtc = string.Empty;
            public bool compilationFinished;
            public bool awaitingDomainReload;
            public int compilerErrorCount;
            public int compilerWarningCount;
            public int resumeAttempts;
            public string lastResumeError = string.Empty;
            public List<string> sessionIds = new();
        }
    }
}
