#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using YuzeToolkit.UnityAgent;

namespace YuzeToolkit
{
    /// <summary>
    /// Runtime-owned string selector. It uses the same package-owned popup and vector icons as enum selectors,
    /// while allowing project data such as Resources-backed template names to provide the options.
    /// </summary>
    internal sealed class DebugChoiceDropdown : VisualElement
    {
        private readonly Label _label;
        private readonly AgentButton _button;
        private readonly List<string> _options = new();
        private VisualElement? _popup;
        private VisualElement? _popupHost;
        private int _index;

        public DebugChoiceDropdown(string label, IReadOnlyList<string> options, int index)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
                _options.Add(options[optionIndex] ?? string.Empty);
            _index = _options.Count == 0 ? -1 : Mathf.Clamp(index, 0, _options.Count - 1);

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
                name = "unity-debug-tool-choice-button"
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

        public event Action<int>? ValueChanged;

        public void SetOptionsWithoutNotify(IReadOnlyList<string> options, int index)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var nextIndex = options.Count == 0 ? -1 : Mathf.Clamp(index, 0, options.Count - 1);
            var optionsChanged = _options.Count != options.Count;
            if (!optionsChanged)
            {
                for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
                {
                    if (string.Equals(_options[optionIndex], options[optionIndex], StringComparison.Ordinal)) continue;
                    optionsChanged = true;
                    break;
                }
            }

            if (!optionsChanged && _index == nextIndex) return;

            ClosePopup();
            _options.Clear();
            for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
                _options.Add(options[optionIndex] ?? string.Empty);
            _index = nextIndex;
            RefreshButton();
        }

        private void TogglePopup()
        {
            if (_popup != null)
            {
                ClosePopup();
                return;
            }

            if (!enabledInHierarchy || _options.Count == 0) return;
            var host = AgentPopupMenu.ResolvePopupHost(this);
            if (host == null) return;

            var popup = new VisualElement { name = "unity-debug-tool-choice-popup" };
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

            for (var optionIndex = 0; optionIndex < _options.Count; optionIndex++)
            {
                var capturedIndex = optionIndex;
                var selected = capturedIndex == _index;
                var item = new AgentButton(
                    _options[capturedIndex],
                    string.Empty,
                    () => Select(capturedIndex),
                    selected ? AgentUi.Active : AgentUi.Transparent,
                    selected ? AgentUi.Accent : AgentUi.Text,
                    selected ? AgentIconKind.Check : AgentIconKind.None);
                item.EnableContentWrapping();
                item.focusable = false;
                item.tabIndex = -1;
                item.AddToClassList(DebugWindowUss.EnumPopupItemClass);
                item.EnableInClassList(DebugWindowUss.EnumPopupItemSelectedClass, selected);
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
            PositionPopup(host, popup, _options.Count);
            host.RegisterCallback<PointerDownEvent>(OnHostPointerDown, TrickleDown.TrickleDown);
            host.RegisterCallback<KeyDownEvent>(OnHostKeyDown, TrickleDown.TrickleDown);
            host.RegisterCallback<GeometryChangedEvent>(OnHostGeometryChanged);
            _button.AddToClassList(DebugWindowUss.EnumButtonOpenClass);
        }

        private void Select(int index)
        {
            if (index != _index)
            {
                _index = index;
                RefreshButton();
                ValueChanged?.Invoke(index);
            }

            ClosePopup();
        }

        private void RefreshButton() => _button.text = _index >= 0 && _index < _options.Count
            ? _options[_index]
            : "No options";

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
