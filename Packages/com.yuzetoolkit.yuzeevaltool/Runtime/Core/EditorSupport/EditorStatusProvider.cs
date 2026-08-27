#nullable enable
#if UNITY_EDITOR
using UnityEditor;
using System.Collections.Generic;

namespace YuzeToolkit
{
    [InitializeOnLoad]
    public static class EditorStatusProvider
    {
        private static bool _isChangingPlayMode;

        static EditorStatusProvider()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static bool IsChangingPlayMode => _isChangingPlayMode;

        public static Dictionary<string, object?> GetStateObject()
        {
            return EvalData.Obj(
                ("environment", ToolUtilities.GetEnvironmentObject()),
                ("isPlaying", EditorApplication.isPlaying),
                ("isPaused", EditorApplication.isPaused),
                ("isCompiling", EditorApplication.isCompiling),
                ("isUpdating", EditorApplication.isUpdating),
                ("isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode),
                ("isChangingPlayMode", IsChangingPlayMode),
                ("evalBusyReason", GetEvalBusyReason() ?? string.Empty)
            );
        }

        public static string? GetEvalBusyReason()
        {
            if (EditorApplication.isCompiling)
                return "Unity Editor is compiling scripts";
            if (EditorApplication.isUpdating)
                return "Unity Editor is updating assets";
            if (IsChangingPlayMode)
                return "Unity Editor is changing play mode";
            return null;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                case PlayModeStateChange.ExitingPlayMode:
                    EditorApplication.update -= ClearPlayModeTransition;
                    SetChangingPlayMode(true);
                    break;
                case PlayModeStateChange.EnteredEditMode:
                case PlayModeStateChange.EnteredPlayMode:
                    SetChangingPlayMode(true);
                    EditorApplication.update -= ClearPlayModeTransition;
                    EditorApplication.update += ClearPlayModeTransition;
                    EditorApplication.QueuePlayerLoopUpdate();
                    break;
            }
        }

        private static void SetChangingPlayMode(bool value)
        {
            _isChangingPlayMode = value;
        }

        private static void ClearPlayModeTransition()
        {
            EditorApplication.update -= ClearPlayModeTransition;
            SetChangingPlayMode(false);
        }
    }
}
#endif
