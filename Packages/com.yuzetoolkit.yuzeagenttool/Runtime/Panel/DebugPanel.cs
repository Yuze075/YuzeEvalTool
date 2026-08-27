#nullable enable
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if YUZE_USE_UNITY_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UIElements;

namespace YuzeToolkit
{
    [RequireComponent(typeof(UIDocument))]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-9000)]
    public sealed class DebugPanel : MonoBehaviour
    {
        private static DebugPanel? _instance;

        [SerializeField, Tooltip("Whether the debug panel is visible immediately after it is created.")]
        private bool showOnStartup;

#if YUZE_USE_UNITY_INPUT_SYSTEM
        [SerializeField, Tooltip("Whether Ctrl must be held when pressing the toggle key.")]
        private bool toggleCtrl;

        [SerializeField, Tooltip("Whether Alt must be held when pressing the toggle key.")]
        private bool toggleAlt;
#endif

        private readonly List<IDebugPanelModule> _modules = new();
        private readonly Dictionary<IDebugPanelModule, bool> _moduleVisibility = new();
#if YUZE_USE_UNITY_INPUT_SYSTEM
        private readonly HashSet<Key> _pressedToggleKeys = new();
#endif
        private UIDocument? _uiDocument;
        private VisualElement? _root;
        private DebugPanelContext? _context;
        private bool _modulesInitialized;
        private bool _moduleInitializationFailed;
        private bool _startupVisibilityApplied;
        private bool _visible;

        public static DebugPanel? ActiveInstance => _instance;

        public static bool IsActive => _instance != null;

        public bool IsVisible
        {
            get => _visible;
            set => SetVisible(value);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                // DisallowMultipleComponent prevents new duplicates, but an already serialized
                // duplicate on the singleton GameObject must not destroy the valid panel and all
                // of its modules. A duplicate hosted elsewhere still owns a disposable clone GO.
                if (_instance.gameObject == gameObject)
                    Destroy(this);
                else
                    Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            _uiDocument ??= GetComponent<UIDocument>();
            InitializeDocument();
        }

        private void OnEnable()
        {
            if (_instance != this) return;
            InitializeDocument();
        }

        private void OnDisable()
        {
            if (_instance != this) return;
            ShutdownModules();
            _moduleInitializationFailed = false;
        }

        private void Update()
        {
            if (_instance != this) return;
            if (_root == null || (!_modulesInitialized && !_moduleInitializationFailed))
                InitializeDocument();
            if (_root == null) return;

#if YUZE_USE_UNITY_INPUT_SYSTEM
            HandleToggleInput();
#endif

            if (!_visible) return;

            for (var i = 0; i < _modules.Count; i++)
            {
                var module = _modules[i];
                if (IsModuleVisible(module))
                    module.Tick();
            }
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            ShutdownModules();
            _instance = null;
        }

        private void InitializeDocument()
        {
            _uiDocument ??= GetComponent<UIDocument>();
            if (_uiDocument == null)
            {
                this.LogError($"{nameof(DebugPanel)} requires a {nameof(UIDocument)} component.");
                enabled = false;
                return;
            }

            if (_uiDocument.panelSettings == null)
            {
                this.LogError($"{nameof(DebugPanel)} requires configured {nameof(PanelSettings)} on its {nameof(UIDocument)}.");
                enabled = false;
                return;
            }

            if (_root == null)
            {
                _root = _uiDocument.rootVisualElement;
                if (_root == null) return;

                _root.Clear();
                PrepareRoot(_root);
                InstallInteractionPolicy(_root);
                _context = new DebugPanelContext(_root);
            }

            if (!_modulesInitialized && !_moduleInitializationFailed)
                InitializeModules();

            if (!_startupVisibilityApplied)
            {
                _startupVisibilityApplied = true;
                SetAllModulesVisible(showOnStartup);
            }
            else
            {
                UpdateRootVisibility();
            }
        }

        private static void PrepareRoot(VisualElement root)
        {
            root.style.position = Position.Absolute;
            root.style.left = 0;
            root.style.right = 0;
            root.style.top = 0;
            root.style.bottom = 0;
            root.style.flexGrow = 1;
        }

        private void InstallInteractionPolicy(VisualElement root)
        {
            root.RegisterCallback<PointerDownEvent>(evt =>
            {
                ReleaseEventSystemSelection();
                if (evt.button == 0 && FindTextField(evt.target as VisualElement, root) is { enabledInHierarchy: true })
                    return;

                if (root.panel?.focusController.focusedElement is VisualElement focused)
                    focused.Blur();
            }, TrickleDown.TrickleDown);
        }

        private static TextField? FindTextField(VisualElement? target, VisualElement root)
        {
            for (var current = target; current != null && current != root; current = current.parent)
                if (current is TextField textField)
                    return textField;
            return null;
        }

        private void InitializeModules()
        {
            if (_context == null) return;

            ShutdownModules();
            _modules.Clear();
            _moduleVisibility.Clear();
            try
            {
                foreach (var behaviour in GetComponents<MonoBehaviour>()
                             .Where(behaviour => behaviour.isActiveAndEnabled)
                             .OrderBy(behaviour => (behaviour as IDebugPanelModule)?.SortOrder ?? int.MaxValue))
                {
                    if (behaviour is not IDebugPanelModule module) continue;

                    // Add before Initialize so a partially initialized module participates in rollback.
                    _modules.Add(module);
                    _moduleVisibility[module] = false;
                    module.Initialize(_context);
                }

                _modulesInitialized = true;
                _moduleInitializationFailed = false;
            }
            catch (System.Exception exception)
            {
                this.LogException(exception);
                RollbackInitializedModules();
                _moduleInitializationFailed = true;
            }
        }

        private void ShutdownModules()
        {
            ReleaseInteractionFocus();
            RollbackInitializedModules();

            _modulesInitialized = false;
            _visible = false;
            if (_root != null)
                _root.style.display = DisplayStyle.None;
        }

        private void RollbackInitializedModules()
        {
            for (var i = _modules.Count - 1; i >= 0; i--)
            {
                try
                {
                    _modules[i].Shutdown();
                }
                catch (System.Exception exception)
                {
                    this.LogException(exception);
                }
            }

            _modules.Clear();
            _moduleVisibility.Clear();
#if YUZE_USE_UNITY_INPUT_SYSTEM
            _pressedToggleKeys.Clear();
#endif
        }

        private void SetVisible(bool visible)
        {
            SetAllModulesVisible(visible);
        }

        private void SetAllModulesVisible(bool visible)
        {
            for (var i = 0; i < _modules.Count; i++)
                SetModuleVisible(_modules[i], visible);

            UpdateRootVisibility();
        }

#if YUZE_USE_UNITY_INPUT_SYSTEM
        private void HandleToggleInput()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || IsEditingText()) return;

            _pressedToggleKeys.Clear();
            for (var i = 0; i < _modules.Count; i++)
            {
                var toggleKey = _modules[i].ToggleKey;
                if (!_pressedToggleKeys.Add(toggleKey)) continue;
                if (IsTogglePressed(keyboard, toggleKey))
                    ToggleModulesByKey(toggleKey);
            }
        }

        private bool IsEditingText()
        {
            if (_root?.panel?.focusController.focusedElement is not VisualElement focused)
                return false;

            for (var current = focused; current != null; current = current.parent)
                if (current is TextField)
                    return true;

            return false;
        }

        private void ToggleModulesByKey(Key toggleKey)
        {
            var anyVisible = false;
            for (var i = 0; i < _modules.Count; i++)
            {
                var module = _modules[i];
                if (module.ToggleKey == toggleKey && IsModuleVisible(module))
                {
                    anyVisible = true;
                    break;
                }
            }

            var visible = !anyVisible;
            for (var i = 0; i < _modules.Count; i++)
            {
                var module = _modules[i];
                if (module.ToggleKey == toggleKey)
                    SetModuleVisible(module, visible);
            }

            UpdateRootVisibility();
        }
#endif

        private void SetModuleVisible(IDebugPanelModule module, bool visible)
        {
            _moduleVisibility[module] = visible;
            module.SetVisible(visible);
        }

        private bool IsModuleVisible(IDebugPanelModule module)
        {
            return _moduleVisibility.TryGetValue(module, out var visible) && visible;
        }

        private void UpdateRootVisibility()
        {
            _visible = _modules.Any(IsModuleVisible);
            if (_root == null) return;

            if (!_visible)
                ReleaseInteractionFocus();
            _root.style.display = _visible ? DisplayStyle.Flex : DisplayStyle.None;
            _root.pickingMode = PickingMode.Ignore;
        }

        private void ReleaseInteractionFocus()
        {
            if (_root?.panel?.focusController.focusedElement is VisualElement focused)
                focused.Blur();

            ReleaseEventSystemSelection(gameObject);
        }

#if YUZE_USE_UNITY_INPUT_SYSTEM
        private bool IsTogglePressed(Keyboard keyboard, Key toggleKey)
        {
            if (toggleKey == Key.None) return false;

            var ctrlPressed = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            var altPressed = keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed;
            return (!toggleCtrl || ctrlPressed) &&
                   (!toggleAlt || altPressed) &&
                   keyboard[toggleKey].wasPressedThisFrame;
        }
#endif

        internal static void ReleaseEventSystemSelection()
        {
#if YUZE_USE_UNITY_UGUI
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
#endif
        }

        internal static void ReleaseEventSystemSelection(GameObject owner)
        {
#if YUZE_USE_UNITY_UGUI
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem != null && eventSystem.currentSelectedGameObject == owner)
                eventSystem.SetSelectedGameObject(null);
#endif
        }
    }
}
