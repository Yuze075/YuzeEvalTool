#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    [InitializeOnLoad]
    internal static class EditorCompilationMonitor
    {
        private const string SessionKeyDomainReloading = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".DomainReloading";
        private const string SessionKeyCompileStarted = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".CompileStartedUtc";
        private const string SessionKeyCompileFinished = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".CompileFinishedUtc";
        private const string SessionKeyLastRequestId = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".LastRequestId";
        private const string SessionKeyLastRequestKind = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".LastRequestKind";
        private const string SessionKeyLastRequestStarted = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".LastRequestStartedUtc";
        private const string SessionKeyLastRequestStatus = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".LastRequestStatus";
        private const string SessionKeyLastRequestError = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".LastRequestError";
        private const string SessionKeyActiveRequestId = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".ActiveRequestId";
        private const string SessionKeyCompilerErrorCount = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".CompilerErrorCount";
        private const string SessionKeyCompilerWarningCount = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".CompilerWarningCount";
        private const string SessionKeyRequestDispatched = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".RequestDispatched";
        private const string SessionKeyCompilationObserved = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".CompilationObserved";
        private const string SessionKeyTargetReloadPending = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".TargetReloadPending";
        private const string SessionKeyPendingRequestId = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".PendingRequestId";
        private const string SessionKeyPendingRequestKind = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".PendingRequestKind";
        private const string SessionKeyPendingRequestReason = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".PendingRequestReason";
        private const string SessionKeyPendingStopPlayModeRequested = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".PendingStopPlayModeRequested";
        private const string SessionKeyLastAssetRefreshStarted = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".LastAssetRefreshStartedUtc";
        private const string SessionKeyLastAssetRefreshFinished = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".LastAssetRefreshFinishedUtc";
        private const string SessionKeyLastScriptCompilationRequested = nameof(YuzeToolkit) + "." + nameof(EditorCompilationMonitor) + ".LastScriptCompilationRequestedUtc";

        static EditorCompilationMonitor()
        {
            if (!EditorProcessGuard.IsPrimaryEditorProcess) return;

            SessionState.SetBool(SessionKeyDomainReloading, false);
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= AfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += AfterAssemblyReload;
            UnityLogBuffer.Start();
        }

        public static Dictionary<string, object?> GetStateObject()
        {
            return EvalData.Obj(
                ("environment", ToolUtilities.GetEnvironmentObject()),
                ("isCompiling", EditorApplication.isCompiling),
                ("isUpdating", EditorApplication.isUpdating),
                ("isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode),
                ("isChangingPlayMode", EditorStatusProvider.IsChangingPlayMode),
                ("evalBusyReason", EditorStatusProvider.GetEvalBusyReason() ?? string.Empty),
                ("isDomainReloading", SessionState.GetBool(SessionKeyDomainReloading, false)),
                ("lastCompilationStartedAtUtc", SessionState.GetString(SessionKeyCompileStarted, string.Empty)),
                ("lastCompilationFinishedAtUtc", SessionState.GetString(SessionKeyCompileFinished, string.Empty)),
                ("lastRequestId", SessionState.GetString(SessionKeyLastRequestId, string.Empty)),
                ("lastRequestKind", SessionState.GetString(SessionKeyLastRequestKind, string.Empty)),
                ("lastRequestStartedAtUtc", SessionState.GetString(SessionKeyLastRequestStarted, string.Empty)),
                ("lastRequestStatus", SessionState.GetString(SessionKeyLastRequestStatus, string.Empty)),
                ("lastRequestError", SessionState.GetString(SessionKeyLastRequestError, string.Empty)),
                ("hasActiveRequest", HasActiveRequest()),
                ("activeRequestId", SessionState.GetString(SessionKeyActiveRequestId, string.Empty)),
                ("compilerErrorCount", SessionState.GetInt(SessionKeyCompilerErrorCount, 0)),
                ("compilerWarningCount", SessionState.GetInt(SessionKeyCompilerWarningCount, 0)),
                ("requestDispatched", IsRequestDispatched()),
                ("compilationObserved", IsCompilationObserved()),
                ("targetReloadPending", IsTargetReloadPending()),
                ("hasPendingRequest", HasPendingRequest()),
                ("pendingRequestId", SessionState.GetString(SessionKeyPendingRequestId, string.Empty)),
                ("pendingRequestKind", SessionState.GetString(SessionKeyPendingRequestKind, string.Empty)),
                ("pendingRequestReason", SessionState.GetString(SessionKeyPendingRequestReason, string.Empty)),
                ("pendingStopPlayModeRequested", SessionState.GetBool(SessionKeyPendingStopPlayModeRequested, false)),
                ("lastAssetRefreshStartedAtUtc", SessionState.GetString(SessionKeyLastAssetRefreshStarted, string.Empty)),
                ("lastAssetRefreshFinishedAtUtc", SessionState.GetString(SessionKeyLastAssetRefreshFinished, string.Empty)),
                ("lastScriptCompilationRequestedAtUtc", SessionState.GetString(SessionKeyLastScriptCompilationRequested, string.Empty))
            );
        }

        public static Dictionary<string, object?> RequestScriptCompilation()
        {
            EditorProcessGuard.EnsurePrimaryEditorProcess("Requesting script compilation");
            var requestId = BeginRequest("scriptCompilation");
            if (TryDeferUntilSafeEditMode(requestId, "scriptCompilation", out var deferredState))
                return deferredState;

            DispatchRequestNow("scriptCompilation");
            var state = GetStateObject();
            state["requestId"] = requestId;
            state["requested"] = "scriptCompilation";
            state["pending"] = false;
            state["message"] = "Script compilation was accepted. Return from this eval and wait through Broker unity_status with waitFor=compilation-complete and the pre-request capturedAtUtc. This Unity-side requestId is diagnostic and is not a Broker compilationCycleId.";
            return state;
        }

        public static Dictionary<string, object?> RefreshAssetDatabaseNow()
        {
            EditorProcessGuard.EnsurePrimaryEditorProcess("Refreshing assets and requesting script compilation");
            var requestId = BeginRequest("assetRefresh");
            if (TryDeferUntilSafeEditMode(requestId, "assetRefresh", out var deferredState))
                return deferredState;

            DispatchRequestNow("assetRefresh");
            var state = GetStateObject();
            state["requestId"] = requestId;
            state["requested"] = "assetRefresh";
            state["pending"] = false;
            state["message"] = "Asset refresh and script compilation were accepted. Return from this eval and wait through Broker unity_status with waitFor=compilation-complete and the pre-request capturedAtUtc. This Unity-side requestId is diagnostic and is not a Broker compilationCycleId.";
            return state;
        }

        private static void RequestScriptCompilationNow()
        {
            SetRequestStatus(EditorApplication.isCompiling ? "Compiling" : "CompilationRequested");
            SessionState.SetString(SessionKeyLastScriptCompilationRequested, DateTime.UtcNow.ToString("O"));
            CompilationPipeline.RequestScriptCompilation();
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void RefreshAssetDatabaseAndRequestCompilation()
        {
            SetRequestStatus("Refreshing");
            SessionState.SetString(SessionKeyLastAssetRefreshStarted, DateTime.UtcNow.ToString("O"));
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            SessionState.SetString(SessionKeyLastAssetRefreshFinished, DateTime.UtcNow.ToString("O"));
            RequestScriptCompilationNow();
        }

        private static void DispatchRequestNow(string kind)
        {
            ClearPendingRequest();
            MarkRequestDispatched();
            try
            {
                if (string.Equals(kind, "scriptCompilation", StringComparison.Ordinal))
                    RequestScriptCompilationNow();
                else if (string.Equals(kind, "assetRefresh", StringComparison.Ordinal))
                    RefreshAssetDatabaseAndRequestCompilation();
                else
                    throw new InvalidOperationException($"Unknown compilation request kind '{kind}'.");
            }
            catch (Exception ex)
            {
                CompleteRequest("Failed", ex.Message);
                throw;
            }
        }

        private static bool TryDeferUntilSafeEditMode(
            string requestId,
            string kind,
            out Dictionary<string, object?> state)
        {
            state = null!;
            if (!ShouldDeferScriptChange(out var reason, out var shouldStopPlayMode))
                return false;

            SetPendingRequest(requestId, kind, reason, shouldStopPlayMode);
            SetRequestStatus("WaitingForSafeEditMode");
            if (shouldStopPlayMode)
                EditorApplication.isPlaying = false;

            var message = shouldStopPlayMode
                ? "Unity is playing or changing play mode. The request is pending; Yuze Eval Tool requested PlayMode exit and will run it after Unity returns to stable EditMode."
                : "Unity is busy. The request is pending and will run after Unity returns to a safe EditMode state.";
            state = GetStateObject();
            state["requestId"] = requestId;
            state["requested"] = kind;
            state["pending"] = true;
            state["message"] = message;
            return true;
        }

        private static bool ShouldDeferScriptChange(out string reason, out bool shouldStopPlayMode)
        {
            shouldStopPlayMode = false;

            if (EditorApplication.isCompiling)
            {
                reason = "Unity Editor is compiling scripts";
                return true;
            }

            if (EditorApplication.isUpdating)
            {
                reason = "Unity Editor is updating assets";
                return true;
            }

            if (EditorStatusProvider.IsChangingPlayMode || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                reason = "Unity Editor is playing or changing play mode";
                shouldStopPlayMode = true;
                return true;
            }

            reason = string.Empty;
            return false;
        }

        private static void SetPendingRequest(string requestId, string kind, string reason, bool stopPlayModeRequested)
        {
            SessionState.SetString(SessionKeyPendingRequestId, requestId);
            SessionState.SetString(SessionKeyPendingRequestKind, kind);
            SessionState.SetString(SessionKeyPendingRequestReason, reason);
            SessionState.SetBool(SessionKeyPendingStopPlayModeRequested, stopPlayModeRequested);
        }

        private static void ClearPendingRequest()
        {
            SessionState.EraseString(SessionKeyPendingRequestId);
            SessionState.EraseString(SessionKeyPendingRequestKind);
            SessionState.EraseString(SessionKeyPendingRequestReason);
            SessionState.SetBool(SessionKeyPendingStopPlayModeRequested, false);
        }

        private static bool HasPendingRequest() =>
            !string.IsNullOrEmpty(SessionState.GetString(SessionKeyPendingRequestId, string.Empty));

        private static bool HasRequestedPlayModeStop() =>
            SessionState.GetBool(SessionKeyPendingStopPlayModeRequested, false);

        private static void TryRunPendingRequest()
        {
            if (!HasPendingRequest()) return;

            if (ShouldDeferScriptChange(out var reason, out var shouldStopPlayMode))
            {
                SessionState.SetString(SessionKeyPendingRequestReason, reason);
                if (shouldStopPlayMode && !HasRequestedPlayModeStop())
                {
                    SessionState.SetBool(SessionKeyPendingStopPlayModeRequested, true);
                    EditorApplication.isPlaying = false;
                }
                return;
            }

            var kind = SessionState.GetString(SessionKeyPendingRequestKind, string.Empty);
            try
            {
                DispatchRequestNow(kind);
            }
            catch (Exception ex)
            {
                LogSys.LogError($"[Yuze Eval Tool] Failed to run pending {kind} request: {ex.Message}");
            }
        }

        private static string BeginRequest(string kind)
        {
            var requestId = Guid.NewGuid().ToString("N");
            SessionState.SetString(SessionKeyLastRequestId, requestId);
            SessionState.SetString(SessionKeyLastRequestKind, kind);
            SessionState.SetString(SessionKeyLastRequestStarted, DateTime.UtcNow.ToString("O"));
            SessionState.SetString(SessionKeyLastRequestStatus, "Accepted");
            SessionState.EraseString(SessionKeyLastRequestError);
            SessionState.SetString(SessionKeyActiveRequestId, requestId);
            SessionState.SetInt(SessionKeyCompilerErrorCount, 0);
            SessionState.SetInt(SessionKeyCompilerWarningCount, 0);
            ClearRequestProgress();
            ClearPendingRequest();
            return requestId;
        }

        private static void Update()
        {
            TryRunPendingRequest();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode)
                TryRunPendingRequest();
        }

        private static void OnCompilationStarted(object context)
        {
            SessionState.SetString(SessionKeyCompileStarted, DateTime.UtcNow.ToString("O"));
            if (!HasDispatchedActiveRequest()) return;

            SessionState.SetBool(SessionKeyCompilationObserved, true);
            SessionState.SetBool(SessionKeyTargetReloadPending, false);
            SetRequestStatus("Compiling");
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (!HasObservedCompilationForActiveRequest()) return;

            var errorCount = SessionState.GetInt(SessionKeyCompilerErrorCount, 0);
            var warningCount = SessionState.GetInt(SessionKeyCompilerWarningCount, 0);
            foreach (var message in messages)
            {
                if (message.type == CompilerMessageType.Error)
                    errorCount++;
                else if (message.type == CompilerMessageType.Warning)
                    warningCount++;
            }

            SessionState.SetInt(SessionKeyCompilerErrorCount, errorCount);
            SessionState.SetInt(SessionKeyCompilerWarningCount, warningCount);
        }

        private static void OnCompilationFinished(object context)
        {
            SessionState.SetString(SessionKeyCompileFinished, DateTime.UtcNow.ToString("O"));
            if (!HasObservedCompilationForActiveRequest()) return;

            var errorCount = SessionState.GetInt(SessionKeyCompilerErrorCount, 0);
            if (errorCount > 0)
            {
                CompleteRequest("Failed", $"Script compilation finished with {errorCount} compiler error(s).");
                return;
            }

            SetRequestStatus("CompilationFinished");
        }

        private static void BeforeAssemblyReload()
        {
            SessionState.SetBool(SessionKeyDomainReloading, true);
            if (!HasObservedCompilationForActiveRequest()) return;

            SessionState.SetBool(SessionKeyTargetReloadPending, true);
            SetRequestStatus("Reloading");
        }

        private static void AfterAssemblyReload()
        {
            SessionState.SetBool(SessionKeyDomainReloading, false);
            if (!HasObservedCompilationForActiveRequest() || !IsTargetReloadPending()) return;

            CompleteRequest("Ready", string.Empty);
        }

        private static bool HasActiveRequest() =>
            !string.IsNullOrEmpty(SessionState.GetString(SessionKeyActiveRequestId, string.Empty));

        private static bool IsRequestDispatched() =>
            SessionState.GetBool(SessionKeyRequestDispatched, false);

        private static bool IsCompilationObserved() =>
            SessionState.GetBool(SessionKeyCompilationObserved, false);

        private static bool IsTargetReloadPending() =>
            SessionState.GetBool(SessionKeyTargetReloadPending, false);

        private static bool HasDispatchedActiveRequest() =>
            HasActiveRequest() && IsRequestDispatched();

        private static bool HasObservedCompilationForActiveRequest() =>
            HasDispatchedActiveRequest() && IsCompilationObserved();

        private static void MarkRequestDispatched()
        {
            if (!HasActiveRequest())
                throw new InvalidOperationException("Cannot dispatch a compilation request without an active request.");

            SessionState.SetBool(SessionKeyRequestDispatched, true);
            SessionState.SetBool(SessionKeyCompilationObserved, false);
            SessionState.SetBool(SessionKeyTargetReloadPending, false);
            SetRequestStatus("Dispatched");
        }

        private static void ClearRequestProgress()
        {
            SessionState.SetBool(SessionKeyRequestDispatched, false);
            SessionState.SetBool(SessionKeyCompilationObserved, false);
            SessionState.SetBool(SessionKeyTargetReloadPending, false);
        }

        private static void SetRequestStatus(string status)
        {
            if (!HasActiveRequest()) return;
            SessionState.SetString(SessionKeyLastRequestStatus, status);
        }

        private static void CompleteRequest(string status, string error)
        {
            SessionState.SetString(SessionKeyLastRequestStatus, status);
            if (string.IsNullOrEmpty(error))
                SessionState.EraseString(SessionKeyLastRequestError);
            else
                SessionState.SetString(SessionKeyLastRequestError, error);
            SessionState.EraseString(SessionKeyActiveRequestId);
            ClearRequestProgress();
            ClearPendingRequest();
        }
    }
}
#endif
