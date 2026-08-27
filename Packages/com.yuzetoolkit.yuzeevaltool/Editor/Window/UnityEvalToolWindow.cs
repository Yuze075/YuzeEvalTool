#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit
{
    internal sealed class UnityEvalToolWindow : EditorWindow
    {
        private const string StyleSheetPath =
            "Packages/com.yuzetoolkit.yuzeevaltool/Editor/Window/UnityEvalToolWindow.uss";
        private const int RefreshIntervalMilliseconds = 500;
        private UnityEvalToolWorkbenchView? _view;
        private IVisualElementScheduledItem? _tickItem;

        [MenuItem(nameof(YuzeToolkit) + "/Yuze Eval Tool")]
        public static void Open()
        {
            var window = GetWindow<UnityEvalToolWindow>("Yuze Eval Tool");
            window.minSize = new Vector2(480, 360);
            window.Show();
        }

        private void CreateGUI()
        {
            _tickItem?.Pause();
            _view?.Dispose();
            rootVisualElement.Clear();
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (styleSheet == null)
                throw new MissingReferenceException($"Yuze Eval Tool workbench stylesheet is missing at '{StyleSheetPath}'.");
            rootVisualElement.styleSheets.Add(styleSheet);
            _view = new UnityEvalToolWorkbenchView(new EditorWorkbenchHost());
            rootVisualElement.Add(_view);
            _tickItem = rootVisualElement.schedule.Execute(() => _view?.Tick()).Every(RefreshIntervalMilliseconds);
        }

        private void OnDisable()
        {
            _tickItem?.Pause();
            _tickItem = null;
            _view?.Dispose();
            _view = null;
        }

        private sealed class EditorWorkbenchHost : IUnityEvalToolWorkbenchHost
        {
            private const string ToolPrefPrefix = nameof(YuzeToolkit) + ".McpTool.Enabled.";

            public bool IsEnabled => EditorBrokerBootstrap.IsEnabled;
            public bool IncludeEditorOnlyTools => true;
            public bool CanOpenBrokerFolder => true;
            public string RuntimeStateLabel => EditorApplication.isPlaying ? "Editor / Play Mode" : "Editor / Edit Mode";

            public void SetEnabled(bool enabled) => EditorBrokerBootstrap.SetEnabled(enabled);

            public void Reconnect() => EditorBrokerBootstrap.Reconnect();

            public void OpenBrokerFolder()
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".unityevaltool");
                Directory.CreateDirectory(directory);
                EditorUtility.RevealInFinder(directory);
            }

            public void SetToolEnabled(string path, bool enabled)
            {
                EditorPrefs.SetBool(ToolPrefPrefix + path, enabled);
                EvalToolRegistry.SetEnabled(path, enabled);
            }
        }
    }
}
