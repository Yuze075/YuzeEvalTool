#nullable enable
using UnityEngine;
#if YUZE_USE_UNITY_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UIElements;
using YuzeToolkit.UnityAgent;

namespace YuzeToolkit
{
    [DisallowMultipleComponent]
    public sealed class UnityAgentPanelModule : MonoBehaviour, IDebugPanelModule
    {
        private const float DefaultMargin = 24f;
        private const int GeometryVersion = 1;
        private const string GeometryVersionKey = "UnityAgent.Runtime.GeometryVersion";

#if YUZE_USE_UNITY_INPUT_SYSTEM
        [SerializeField, Tooltip("Keyboard key used with DebugPanel modifiers to show or hide Unity Agent.")]
        private Key toggleKey = Key.F8;
#endif

        [SerializeField] private Vector2 initialSize = new(1100f, 700f);
        [SerializeField] private Vector2 minimumSize = new(520f, 360f);

        private VisualElement? _layer;
        private VisualElement? _window;
        private VisualElement? _content;
        private AgentButton? _collapseButton;
        private VisualElement? _resizeGrip;
        private UnityAgentWorkbenchView? _workbench;
        private bool _collapsed;
        private float _expandedHeight;

        public int SortOrder => 0;
#if YUZE_USE_UNITY_INPUT_SYSTEM
        public Key ToggleKey => toggleKey;
#endif

        public void Initialize(DebugPanelContext context)
        {
            _layer = context.CreateLayer("unity-agent-runtime-layer");
            _layer.style.position = Position.Absolute;
            _layer.style.left = 0;
            _layer.style.right = 0;
            _layer.style.top = 0;
            _layer.style.bottom = 0;
            _layer.pickingMode = PickingMode.Ignore;

            _window = new VisualElement { name = "unity-agent-runtime-window" };
            _window.style.position = Position.Absolute;
            var hasBottomLeftGeometry = PlayerPrefs.GetInt(GeometryVersionKey) == GeometryVersion;
            _window.style.left = hasBottomLeftGeometry
                ? PlayerPrefs.GetFloat("UnityAgent.Runtime.Left", DefaultMargin)
                : DefaultMargin;
            _window.style.bottom = hasBottomLeftGeometry
                ? PlayerPrefs.GetFloat("UnityAgent.Runtime.Bottom", DefaultMargin)
                : DefaultMargin;
            _window.style.width = PlayerPrefs.GetFloat("UnityAgent.Runtime.Width", initialSize.x);
            _window.style.height = PlayerPrefs.GetFloat("UnityAgent.Runtime.Height", initialSize.y);
            _window.style.minWidth = minimumSize.x;
            _window.style.minHeight = minimumSize.y;
            _window.style.backgroundColor = AgentUi.Background;
            _window.style.borderTopLeftRadius = 14;
            _window.style.borderTopRightRadius = 14;
            _window.style.borderBottomLeftRadius = 14;
            _window.style.borderBottomRightRadius = 14;
            _window.style.overflow = Overflow.Hidden;
            _window.pickingMode = PickingMode.Position;
            AgentUi.SetBorder(_window, AgentUi.BorderStrong, 1);
            _layer.Add(_window);

            var header = new VisualElement { name = "unity-agent-runtime-header" };
            header.style.height = 44;
            header.style.flexShrink = 0;
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.paddingLeft = 14;
            header.style.paddingRight = 8;
            header.style.backgroundColor = AgentUi.Sidebar;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = AgentUi.Border;
            var title = new Label("Unity Agent");
            title.style.flexGrow = 1;
            AgentUi.ApplyTypography(title, AgentTypography.BodyStrong);
            header.Add(title);
            _collapseButton = AgentUi.IconButton(AgentIconKind.ChevronDown,
                "Collapse or expand Unity Agent without affecting other debug overlays.", ToggleCollapsed,
                30, AgentUi.Transparent, AgentUi.TextSecondary);
            _collapseButton.name = "unity-agent-runtime-collapse";
            header.Add(_collapseButton);
            _resizeGrip = AgentUi.IconButton(AgentIconKind.Sliders,
                "Drag to resize Unity Agent from the upper-right corner.", () => { }, 30,
                AgentUi.Transparent, AgentUi.TextSecondary);
            _resizeGrip.name = "unity-agent-runtime-resize";
            header.Add(_resizeGrip);
            _window.Add(header);

            _content = new VisualElement { style = { flexGrow = 1, minWidth = 0, minHeight = 0 } };
            _workbench = new UnityAgentWorkbenchView(UnityAgentHost.Default);
            _content.Add(_workbench);
            _window.Add(_content);

            header.AddManipulator(new WindowDragManipulator(_window, _layer, PersistGeometry));
            _resizeGrip.AddManipulator(new UpperRightResizeManipulator(
                _window, _layer, minimumSize, PersistGeometry));
            _layer.RegisterCallback<GeometryChangedEvent>(_ => ClampToLayer());
            _window.schedule.Execute(_ => ClampToLayer());
        }

        public void SetVisible(bool visible)
        {
            if (_layer == null) return;
            _layer.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) ReleaseFocus();
        }

        public void Tick() => _workbench?.Tick();

        public void Shutdown()
        {
            PersistGeometry();
            ReleaseFocus();
            _workbench?.Dispose();
            _workbench = null;
            _layer?.RemoveFromHierarchy();
            _layer = null;
            _window = null;
            _content = null;
            _collapseButton = null;
            _resizeGrip = null;
            _collapsed = false;
        }

        private void ToggleCollapsed()
        {
            if (_window == null || _content == null || _collapseButton == null || _resizeGrip == null) return;
            _collapsed = !_collapsed;
            _collapseButton.SetIcon(_collapsed ? AgentIconKind.ChevronUp : AgentIconKind.ChevronDown);
            if (_collapsed)
            {
                _expandedHeight = _window.resolvedStyle.height;
                _content.style.display = DisplayStyle.None;
                _resizeGrip.style.display = DisplayStyle.None;
                _window.style.minHeight = 44;
                _window.style.height = 44;
                ReleaseFocus();
                ClampToLayer(44);
            }
            else
            {
                var targetHeight = Mathf.Max(minimumSize.y,
                    _expandedHeight > 44 ? _expandedHeight : initialSize.y);
                _window.style.minHeight = minimumSize.y;
                _window.style.height = targetHeight;
                _content.style.display = DisplayStyle.Flex;
                _resizeGrip.style.display = DisplayStyle.Flex;
                ClampToLayer(targetHeight);
            }
            PersistGeometry();
        }

        private void ClampToLayer(float? requestedHeight = null)
        {
            if (_layer == null || _window == null) return;
            var bounds = _layer.contentRect;
            if (bounds.width <= 0 || bounds.height <= 0) return;
            var minWidth = Mathf.Min(minimumSize.x, bounds.width);
            var minHeight = Mathf.Min(_collapsed ? 44f : minimumSize.y, bounds.height);
            var width = Mathf.Clamp(Resolved(_window.resolvedStyle.width, initialSize.x), minWidth, bounds.width);
            var height = Mathf.Clamp(requestedHeight ?? Resolved(_window.resolvedStyle.height,
                _collapsed ? 44f : initialSize.y), minHeight, bounds.height);
            var left = Mathf.Clamp(_window.layout.x, 0, Mathf.Max(0, bounds.width - width));
            var bottom = Mathf.Clamp(bounds.height - (_window.layout.y + height), 0,
                Mathf.Max(0, bounds.height - height));
            _window.style.width = width;
            _window.style.height = height;
            _window.style.left = left;
            _window.style.top = StyleKeyword.Auto;
            _window.style.bottom = bottom;
            if (!_collapsed) _expandedHeight = height;
        }

        private void PersistGeometry()
        {
            if (_window == null || _collapsed) return;
            PlayerPrefs.SetFloat("UnityAgent.Runtime.Left", _window.layout.x);
            var layerHeight = _layer?.contentRect.height ?? 0;
            PlayerPrefs.SetFloat("UnityAgent.Runtime.Bottom",
                Mathf.Max(0, layerHeight - (_window.layout.y + _window.resolvedStyle.height)));
            PlayerPrefs.SetFloat("UnityAgent.Runtime.Width", _window.resolvedStyle.width);
            PlayerPrefs.SetFloat("UnityAgent.Runtime.Height", _window.resolvedStyle.height);
            PlayerPrefs.SetInt(GeometryVersionKey, GeometryVersion);
        }

        private void ReleaseFocus()
        {
            if (_window?.panel?.focusController.focusedElement is VisualElement focused &&
                IsDescendant(focused, _window)) focused.Blur();
            DebugPanel.ReleaseEventSystemSelection(gameObject);
        }

        private static float Resolved(float value, float fallback) =>
            float.IsNaN(value) || value <= 0 ? fallback : value;

        private static bool IsDescendant(VisualElement current, VisualElement ancestor)
        {
            for (var value = current; value != null; value = value.parent)
                if (value == ancestor) return true;
            return false;
        }

        private sealed class WindowDragManipulator : PointerManipulator
        {
            private readonly VisualElement _window;
            private readonly VisualElement _layer;
            private readonly System.Action _completed;
            private Vector2 _pointer;
            private Vector2 _position;
            private bool _active;

            public WindowDragManipulator(VisualElement window, VisualElement layer, System.Action completed)
            {
                _window = window;
                _layer = layer;
                _completed = completed;
            }

            protected override void RegisterCallbacksOnTarget()
            {
                target.RegisterCallback<PointerDownEvent>(Down);
                target.RegisterCallback<PointerMoveEvent>(Move);
                target.RegisterCallback<PointerUpEvent>(Up);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                target.UnregisterCallback<PointerDownEvent>(Down);
                target.UnregisterCallback<PointerMoveEvent>(Move);
                target.UnregisterCallback<PointerUpEvent>(Up);
            }

            private void Down(PointerDownEvent evt)
            {
                if (evt.button != 0) return;
                _active = true;
                _pointer = evt.position;
                _position = new Vector2(_window.layout.x,
                    _layer.contentRect.height - (_window.layout.y + _window.resolvedStyle.height));
                target.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            }

            private void Move(PointerMoveEvent evt)
            {
                if (!_active || !target.HasPointerCapture(evt.pointerId)) return;
                var delta = (Vector2)evt.position - _pointer;
                var bounds = _layer.contentRect;
                _window.style.left = Mathf.Clamp(_position.x + delta.x, 0,
                    Mathf.Max(0, bounds.width - _window.resolvedStyle.width));
                _window.style.bottom = Mathf.Clamp(_position.y - delta.y, 0,
                    Mathf.Max(0, bounds.height - _window.resolvedStyle.height));
                evt.StopPropagation();
            }

            private void Up(PointerUpEvent evt)
            {
                if (!_active) return;
                _active = false;
                if (target.HasPointerCapture(evt.pointerId)) target.ReleasePointer(evt.pointerId);
                _completed();
                evt.StopPropagation();
            }
        }

        private sealed class UpperRightResizeManipulator : PointerManipulator
        {
            private readonly VisualElement _window;
            private readonly VisualElement _layer;
            private readonly Vector2 _minimum;
            private readonly System.Action _completed;
            private Vector2 _pointer;
            private Vector2 _size;
            private float _bottom;
            private bool _active;

            public UpperRightResizeManipulator(VisualElement window, VisualElement layer, Vector2 minimum,
                System.Action completed)
            {
                _window = window;
                _layer = layer;
                _minimum = minimum;
                _completed = completed;
            }

            protected override void RegisterCallbacksOnTarget()
            {
                target.RegisterCallback<PointerDownEvent>(Down);
                target.RegisterCallback<PointerMoveEvent>(Move);
                target.RegisterCallback<PointerUpEvent>(Up);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                target.UnregisterCallback<PointerDownEvent>(Down);
                target.UnregisterCallback<PointerMoveEvent>(Move);
                target.UnregisterCallback<PointerUpEvent>(Up);
            }

            private void Down(PointerDownEvent evt)
            {
                if (evt.button != 0) return;
                _active = true;
                _pointer = evt.position;
                _size = new Vector2(_window.resolvedStyle.width, _window.resolvedStyle.height);
                _bottom = _layer.contentRect.height - (_window.layout.y + _window.resolvedStyle.height);
                target.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            }

            private void Move(PointerMoveEvent evt)
            {
                if (!_active || !target.HasPointerCapture(evt.pointerId)) return;
                var delta = (Vector2)evt.position - _pointer;
                var bounds = _layer.contentRect;
                var width = Mathf.Clamp(_size.x + delta.x,
                    Mathf.Min(_minimum.x, bounds.width), bounds.width - _window.layout.x);
                var maximumHeight = Mathf.Max(44f, bounds.height - _bottom);
                var height = Mathf.Clamp(_size.y - delta.y,
                    Mathf.Min(_minimum.y, maximumHeight), maximumHeight);
                _window.style.width = width;
                _window.style.height = height;
                evt.StopPropagation();
            }

            private void Up(PointerUpEvent evt)
            {
                if (!_active) return;
                _active = false;
                if (target.HasPointerCapture(evt.pointerId)) target.ReleasePointer(evt.pointerId);
                _completed();
                evt.StopPropagation();
            }
        }
    }
}
