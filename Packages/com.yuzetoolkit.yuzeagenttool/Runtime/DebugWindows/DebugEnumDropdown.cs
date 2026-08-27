#nullable enable
using System;
using UnityEngine;
using UnityEngine.UIElements;
using YuzeToolkit.UnityAgent;

namespace YuzeToolkit
{
    /// <summary>
    /// Runtime-owned enum selector. It intentionally does not use EnumField/GenericDropdownMenu,
    /// because those popups are rendered with Unity's editor/runtime theme outside the control tree.
    /// </summary>
    internal sealed class DebugEnumDropdown : VisualElement
    {
        private readonly Label _label;
        private readonly AgentButton _button;
        private readonly Type _enumType;
        private VisualElement? _popup;
        private VisualElement? _popupHost;
        private Enum _value;

        public DebugEnumDropdown(string label, Type enumType, Enum value)
        {
            if (enumType == null) throw new ArgumentNullException(nameof(enumType));
            if (!enumType.IsEnum) throw new ArgumentException("Enum type is required.", nameof(enumType));
            if (value == null) throw new ArgumentNullException(nameof(value));

            _enumType = enumType;
            _value = value;

            AddToClassList(DebugWindowUss.EnumFieldClass);
            style.flexGrow = 1;
            style.minWidth = 0;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;

            _label = new Label(label) { enableRichText = false };
            _label.AddToClassList(DebugWindowUss.EnumLabelClass);
            DebugWindowUss.ApplyControlLabel(_label);
            _label.style.display = string.IsNullOrWhiteSpace(label) ? DisplayStyle.None : DisplayStyle.Flex;
            Add(_label);

            _button = new AgentButton(string.Empty, string.Empty, TogglePopup,
                AgentUi.Input, AgentUi.Text, AgentIconKind.ChevronDown)
            {
                name = "unity-debug-tool-enum-button"
            };
            _button.EnableContentWrapping();
            _button.focusable = false;
            _button.tabIndex = -1;
            _button.AddToClassList(DebugWindowUss.EnumButtonClass);
            _button.style.flexGrow = 1;
            _button.style.minHeight = 32;
            _button.style.height = StyleKeyword.Auto;
            _button.style.minWidth = 0;
            _button.style.paddingLeft = 10;
            _button.style.paddingRight = 8;
            _button.style.backgroundImage = StyleKeyword.None;
            _button.style.backgroundColor = AgentUi.Input;
            _button.style.borderTopLeftRadius = 8;
            _button.style.borderTopRightRadius = 8;
            _button.style.borderBottomLeftRadius = 8;
            _button.style.borderBottomRightRadius = 8;
            AgentUi.SetBorder(_button, AgentUi.Border, 1);
            Add(_button);

            RefreshButton();
            RegisterCallback<DetachFromPanelEvent>(_ => ClosePopup());
        }

        public event Action<Enum>? ValueChanged;

        public void SetValueWithoutNotify(Enum value)
        {
            if (value == null || value.GetType() != _enumType) return;
            _value = value;
            RefreshButton();
        }

        private void TogglePopup()
        {
            if (_popup != null)
            {
                ClosePopup();
                return;
            }

            if (!enabledInHierarchy) return;

            var values = Enum.GetValues(_enumType);
            if (values.Length == 0) return;

            var host = AgentPopupMenu.ResolvePopupHost(this);
            if (host == null) return;

            var popup = new VisualElement { name = "unity-debug-tool-enum-popup" };
            popup.AddToClassList(DebugWindowUss.EnumPopupClass);
            popup.pickingMode = PickingMode.Position;
            popup.style.position = Position.Absolute;
            popup.style.paddingTop = 4;
            popup.style.paddingBottom = 4;
            popup.style.backgroundColor = AgentUi.Popup;
            popup.style.borderTopLeftRadius = 10;
            popup.style.borderTopRightRadius = 10;
            popup.style.borderBottomLeftRadius = 10;
            popup.style.borderBottomRightRadius = 10;
            AgentUi.SetBorder(popup, AgentUi.BorderStrong, 1);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList(DebugWindowUss.EnumPopupScrollClass);
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            scroll.focusable = false;
            scroll.tabIndex = -1;
            popup.Add(scroll);

            for (var index = 0; index < values.Length; index++)
            {
                if (values.GetValue(index) is not Enum option) continue;
                var captured = option;
                var selected = Equals(captured, _value);
                var item = new AgentButton(
                    FormatOption(captured),
                    string.Empty,
                    () => Select(captured),
                    selected ? AgentUi.Active : AgentUi.Transparent,
                    selected ? AgentUi.Accent : AgentUi.Text,
                    selected ? AgentIconKind.Check : AgentIconKind.None);
                item.EnableContentWrapping();
                item.focusable = false;
                item.tabIndex = -1;
                item.AddToClassList(DebugWindowUss.EnumPopupItemClass);
                item.EnableInClassList(DebugWindowUss.EnumPopupItemSelectedClass, Equals(captured, _value));
                item.style.minHeight = 36;
                item.style.height = StyleKeyword.Auto;
                item.style.width = Length.Percent(100);
                item.style.justifyContent = Justify.FlexStart;
                item.style.paddingLeft = 10;
                item.style.paddingRight = 10;
                item.style.backgroundImage = StyleKeyword.None;
                item.style.borderTopWidth = 0;
                item.style.borderRightWidth = 0;
                item.style.borderBottomWidth = 0;
                item.style.borderLeftWidth = 0;
                scroll.Add(item);
            }

            _popup = popup;
            _popupHost = host;
            host.Add(popup);
            PositionPopup(host, popup, values.Length);
            host.RegisterCallback<PointerDownEvent>(OnHostPointerDown, TrickleDown.TrickleDown);
            host.RegisterCallback<PointerMoveEvent>(OnHostPointerMove, TrickleDown.TrickleDown);
            host.RegisterCallback<KeyDownEvent>(OnHostKeyDown, TrickleDown.TrickleDown);
            host.RegisterCallback<GeometryChangedEvent>(OnHostGeometryChanged);
            _button.AddToClassList(DebugWindowUss.EnumButtonOpenClass);
        }

        private void Select(Enum value)
        {
            if (!Equals(_value, value))
            {
                _value = value;
                RefreshButton();
                ValueChanged?.Invoke(value);
            }

            ClosePopup();
        }

        private void RefreshButton()
        {
            _button.text = FormatOption(_value);
        }

        private static string FormatOption(Enum value)
        {
            var text = value.ToString();
            return string.IsNullOrWhiteSpace(text) ? Convert.ToInt64(value).ToString() : text;
        }

        private void PositionPopup(VisualElement host, VisualElement popup, int optionCount)
        {
            var origin = _button.ChangeCoordinatesTo(host, Vector2.zero);
            var availableWidth = Mathf.Max(80f, host.resolvedStyle.width);
            var availableHeight = Mathf.Max(46f, host.resolvedStyle.height);
            var width = Mathf.Min(Mathf.Max(180f, _button.resolvedStyle.width),
                Mathf.Min(320f, Mathf.Max(80f, availableWidth - 16f)));
            var estimatedHeight = Mathf.Min(
                Mathf.Min(320f, Mathf.Max(46f, optionCount * 38f + 8f)),
                Mathf.Max(46f, availableHeight - 16f));
            var left = Mathf.Clamp(origin.x, 8f, Mathf.Max(8f, availableWidth - width - 8f));
            var below = origin.y + _button.resolvedStyle.height + 4f;
            var top = below + estimatedHeight <= availableHeight - 8f
                ? below
                : Mathf.Max(8f, origin.y - estimatedHeight - 4f);

            popup.style.left = left;
            popup.style.top = top;
            popup.style.width = width;
            popup.style.maxHeight = estimatedHeight;
        }

        private void OnHostPointerDown(PointerDownEvent evt)
        {
            var target = evt.target as VisualElement;
            if (target != null && (IsDescendantOf(target, _popup) || IsDescendantOf(target, _button))) return;
            ClosePopup();
        }

        private void OnHostPointerMove(PointerMoveEvent evt)
        {
            if (evt.pressedButtons == 0 || IsDescendantOf(evt.target as VisualElement, _popup)) return;
            ClosePopup();
        }

        private void OnHostKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape) return;
            ClosePopup();
            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private void OnHostGeometryChanged(GeometryChangedEvent evt)
        {
            if (evt.oldRect.size == evt.newRect.size) return;
            ClosePopup();
        }

        private void ClosePopup()
        {
            if (_popupHost != null)
            {
                _popupHost.UnregisterCallback<PointerDownEvent>(OnHostPointerDown, TrickleDown.TrickleDown);
                _popupHost.UnregisterCallback<PointerMoveEvent>(OnHostPointerMove, TrickleDown.TrickleDown);
                _popupHost.UnregisterCallback<KeyDownEvent>(OnHostKeyDown, TrickleDown.TrickleDown);
                _popupHost.UnregisterCallback<GeometryChangedEvent>(OnHostGeometryChanged);
            }

            _popup?.RemoveFromHierarchy();
            _popup = null;
            _popupHost = null;
            _button.RemoveFromClassList(DebugWindowUss.EnumButtonOpenClass);
        }

        private static bool IsDescendantOf(VisualElement? target, VisualElement? ancestor)
        {
            if (ancestor == null) return false;
            for (var current = target; current != null; current = current.parent)
                if (current == ancestor)
                    return true;
            return false;
        }
    }
}
