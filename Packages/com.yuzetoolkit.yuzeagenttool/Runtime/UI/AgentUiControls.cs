#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit.UnityAgent
{
    internal enum AgentIconKind
    {
        None,
        Add,
        Back,
        ChevronDown,
        ChevronRight,
        ChevronUp,
        Refresh,
        Send,
        Stop,
        Check,
        Settings,
        Pin,
        Archive,
        Restore,
        Delete,
        Provider,
        Sliders,
        Folder,
        History,
        Chat
    }

    /// <summary>Package-owned vector icon seat. No glyph or editor skin participates in rendering.</summary>
    internal sealed class AgentIcon : VisualElement
    {
        private AgentIconKind _kind;
        private Color _tint;

        public AgentIcon(AgentIconKind kind, float size = 16)
        {
            _kind = kind;
            _tint = AgentUi.TextSecondary;
            pickingMode = PickingMode.Ignore;
            style.width = size;
            style.height = size;
            style.flexShrink = 0;
            generateVisualContent += Draw;
        }

        public AgentIconKind Kind
        {
            get => _kind;
            set
            {
                if (_kind == value) return;
                _kind = value;
                MarkDirtyRepaint();
            }
        }

        public Color Tint
        {
            get => _tint;
            set
            {
                _tint = value;
                MarkDirtyRepaint();
            }
        }

        private void Draw(MeshGenerationContext context)
        {
            if (_kind == AgentIconKind.None) return;
            var painter = context.painter2D;
            var rect = contentRect;
            var scale = Mathf.Min(rect.width, rect.height) / 16f;
            var origin = rect.center - new Vector2(8f * scale, 8f * scale);
            painter.strokeColor = _tint;
            painter.fillColor = _tint;
            painter.lineWidth = Mathf.Max(1.5f, 1.9f * scale);

            Vector2 P(float x, float y) => origin + new Vector2(x * scale, y * scale);
            void Stroke(params Vector2[] points)
            {
                if (points.Length < 2) return;
                painter.BeginPath();
                painter.MoveTo(points[0]);
                for (var i = 1; i < points.Length; i++) painter.LineTo(points[i]);
                painter.Stroke();
            }
            void Fill(params Vector2[] points)
            {
                if (points.Length < 3) return;
                painter.BeginPath();
                painter.MoveTo(points[0]);
                for (var i = 1; i < points.Length; i++) painter.LineTo(points[i]);
                painter.ClosePath();
                painter.Fill();
            }

            switch (_kind)
            {
                case AgentIconKind.Add:
                    Stroke(P(3, 8), P(13, 8));
                    Stroke(P(8, 3), P(8, 13));
                    break;
                case AgentIconKind.Back:
                    Stroke(P(10.5f, 3), P(5.5f, 8), P(10.5f, 13));
                    break;
                case AgentIconKind.ChevronDown:
                    Stroke(P(3.5f, 6), P(8, 10.5f), P(12.5f, 6));
                    break;
                case AgentIconKind.ChevronRight:
                    Stroke(P(6, 3.5f), P(10.5f, 8), P(6, 12.5f));
                    break;
                case AgentIconKind.ChevronUp:
                    Stroke(P(3.5f, 10), P(8, 5.5f), P(12.5f, 10));
                    break;
                case AgentIconKind.Send:
                    Fill(P(3, 8), P(8, 3), P(13, 8), P(10, 8), P(10, 13), P(6, 13), P(6, 8));
                    break;
                case AgentIconKind.Stop:
                    Fill(P(4, 4), P(12, 4), P(12, 12), P(4, 12));
                    break;
                case AgentIconKind.Check:
                    Stroke(P(3, 8), P(6.5f, 11.5f), P(13, 4.5f));
                    break;
                case AgentIconKind.Refresh:
                    Stroke(P(12.5f, 6.5f), P(12.5f, 3), P(9, 3));
                    Stroke(P(12, 4), P(10.5f, 2.8f), P(8, 2.2f), P(5.4f, 3), P(3.4f, 5), P(2.8f, 7.5f));
                    Stroke(P(3.5f, 9.5f), P(3.5f, 13), P(7, 13));
                    Stroke(P(4, 12), P(5.5f, 13.2f), P(8, 13.8f), P(10.6f, 13), P(12.6f, 11), P(13.2f, 8.5f));
                    break;
                case AgentIconKind.Pin:
                    Stroke(P(5, 3), P(11, 3), P(10, 7), P(12, 9), P(4, 9), P(6, 7), P(5, 3));
                    Stroke(P(8, 9), P(8, 14));
                    break;
                case AgentIconKind.Archive:
                    Stroke(P(3, 5), P(13, 5), P(12, 13), P(4, 13), P(3, 5));
                    Stroke(P(2.5f, 3), P(13.5f, 3), P(13.5f, 5), P(2.5f, 5), P(2.5f, 3));
                    Stroke(P(6, 8), P(10, 8));
                    break;
                case AgentIconKind.Restore:
                    Stroke(P(5.5f, 5), P(2.5f, 5), P(2.5f, 2));
                    Stroke(P(3, 5), P(5, 3), P(8, 2.5f), P(11, 4), P(13, 7), P(12.5f, 10.5f), P(10, 13), P(6, 13));
                    break;
                case AgentIconKind.Delete:
                    Stroke(P(4, 5), P(12, 5), P(11, 13), P(5, 13), P(4, 5));
                    Stroke(P(3, 3.5f), P(13, 3.5f));
                    Stroke(P(6.5f, 2), P(9.5f, 2));
                    break;
                case AgentIconKind.Settings:
                case AgentIconKind.Sliders:
                    Stroke(P(3, 4), P(13, 4));
                    Stroke(P(3, 8), P(13, 8));
                    Stroke(P(3, 12), P(13, 12));
                    Fill(P(5, 2.5f), P(7, 2.5f), P(7, 5.5f), P(5, 5.5f));
                    Fill(P(9, 6.5f), P(11, 6.5f), P(11, 9.5f), P(9, 9.5f));
                    Fill(P(6, 10.5f), P(8, 10.5f), P(8, 13.5f), P(6, 13.5f));
                    break;
                case AgentIconKind.Provider:
                    Stroke(P(4, 3), P(12, 3), P(12, 7), P(4, 7), P(4, 3));
                    Stroke(P(4, 9), P(12, 9), P(12, 13), P(4, 13), P(4, 9));
                    break;
                case AgentIconKind.Folder:
                    Stroke(P(2.5f, 5), P(6.5f, 5), P(8, 6.5f), P(13.5f, 6.5f), P(12.5f, 13), P(3.5f, 13), P(2.5f, 5));
                    break;
                case AgentIconKind.History:
                    Stroke(P(5, 4), P(3, 4), P(3, 2));
                    Stroke(P(3.5f, 4), P(5.5f, 2.5f), P(9, 2.5f), P(12, 5), P(13, 8), P(12, 11), P(9, 13), P(5.5f, 12.5f));
                    Stroke(P(8, 5), P(8, 8), P(10.5f, 9.5f));
                    break;
                case AgentIconKind.Chat:
                    Stroke(P(3, 3), P(13, 3), P(13, 11), P(8, 11), P(5, 14), P(5, 11), P(3, 11), P(3, 3));
                    break;
            }
        }
    }

    /// <summary>
    /// Package-owned button. It intentionally does not derive from UI Toolkit's Button, so no
    /// editor skin, background image, padding, border, or state selector can leak into the Agent UI.
    /// </summary>
    internal sealed class AgentButton : VisualElement
    {
        private readonly Label _label;
        private readonly Label _description;
        private readonly VisualElement _textStack;
        private readonly AgentIcon _icon;
        private readonly Action _clicked;
        private string _helpText;
        private Color _surface;
        private Color _foreground;
        private bool _hovered;
        private bool _pressed;
        private bool _focused;

        public AgentButton(string text, string tooltip, Action clicked, Color surface, Color foreground,
            AgentIconKind icon = AgentIconKind.None)
        {
            _clicked = clicked ?? throw new ArgumentNullException(nameof(clicked));
            _helpText = tooltip ?? string.Empty;
            focusable = true;
            pickingMode = PickingMode.Position;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            style.flexShrink = 0;
            style.backgroundImage = StyleKeyword.None;
            style.borderTopWidth = 1;
            style.borderRightWidth = 1;
            style.borderBottomWidth = 1;
            style.borderLeftWidth = 1;
            style.paddingTop = 0;
            style.paddingRight = 10;
            style.paddingBottom = 0;
            style.paddingLeft = 10;
            style.opacity = 1;
            style.overflow = Overflow.Hidden;

            _icon = new AgentIcon(icon);
            _icon.style.display = icon == AgentIconKind.None ? DisplayStyle.None : DisplayStyle.Flex;
            _icon.style.marginRight = string.IsNullOrEmpty(text) ? 0 : 6;
            Add(_icon);

            _textStack = new VisualElement { pickingMode = PickingMode.Ignore };
            _textStack.style.minWidth = 0;
            _textStack.style.flexShrink = 1;
            _textStack.style.justifyContent = Justify.Center;
            Add(_textStack);

            _label = new Label { pickingMode = PickingMode.Ignore };
            _label.style.flexShrink = 1;
            _label.style.minWidth = 0;
            _label.style.unityTextAlign = TextAnchor.MiddleCenter;
            _label.style.whiteSpace = WhiteSpace.NoWrap;
            _label.style.overflow = Overflow.Hidden;
            _label.style.textOverflow = TextOverflow.Ellipsis;
            _label.style.backgroundImage = StyleKeyword.None;
            _label.style.marginTop = 0;
            _label.style.marginRight = 0;
            _label.style.marginBottom = 0;
            _label.style.marginLeft = 0;
            _label.style.paddingTop = 0;
            _label.style.paddingRight = 0;
            _label.style.paddingBottom = 0;
            _label.style.paddingLeft = 0;
            AgentUi.ApplyTypography(_label, AgentTypography.Control);
            _textStack.Add(_label);
            _description = new Label { pickingMode = PickingMode.Ignore };
            _description.style.display = DisplayStyle.None;
            _description.style.minWidth = 0;
            _description.style.overflow = Overflow.Hidden;
            _description.style.textOverflow = TextOverflow.Ellipsis;
            _description.style.color = AgentUi.TextCaption;
            AgentUi.ApplyTypography(_description, AgentTypography.Caption);
            _textStack.Add(_description);

            SetPalette(surface, foreground);
            this.text = text;
            AgentTooltip.Attach(this, () =>
                string.IsNullOrWhiteSpace(_helpText) ||
                _textStack.resolvedStyle.display != DisplayStyle.None && !string.IsNullOrWhiteSpace(_label.text)
                    ? string.Empty
                    : _helpText);

            RegisterCallback<PointerEnterEvent>(_ =>
            {
                _hovered = true;
                RefreshSurface();
            });
            RegisterCallback<PointerLeaveEvent>(_ =>
            {
                _hovered = false;
                _pressed = false;
                RefreshSurface();
            });
            RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !enabledInHierarchy) return;
                _pressed = true;
                Focus();
                RefreshSurface();
                evt.StopPropagation();
            });
            RegisterCallback<PointerUpEvent>(evt =>
            {
                if (evt.button != 0 || !_pressed) return;
                _pressed = false;
                RefreshSurface();
                if (worldBound.Contains(evt.position) && enabledInHierarchy) _clicked();
                evt.StopPropagation();
            });
            RegisterCallback<KeyDownEvent>(evt =>
            {
                if (!enabledInHierarchy || evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.Space) return;
                _clicked();
                evt.StopPropagation();
            });
            RegisterCallback<FocusInEvent>(_ =>
            {
                _focused = true;
                RefreshSurface();
            });
            RegisterCallback<FocusOutEvent>(_ =>
            {
                _focused = false;
                RefreshSurface();
            });
        }

        public string text
        {
            get => _label.text;
            set
            {
                _label.text = value ?? string.Empty;
                _icon.style.marginRight = _icon.style.display == DisplayStyle.None || string.IsNullOrEmpty(_label.text)
                    ? 0
                    : 6;
            }
        }

        public void EnableContentWrapping()
        {
            _label.style.whiteSpace = WhiteSpace.Normal;
            _label.style.overflow = Overflow.Visible;
            _label.style.textOverflow = TextOverflow.Clip;
            _textStack.style.flexGrow = 0;
            _textStack.style.maxWidth = Length.Percent(100);
        }

        public void SetIcon(AgentIconKind icon)
        {
            _icon.Kind = icon;
            _icon.style.display = icon == AgentIconKind.None ? DisplayStyle.None : DisplayStyle.Flex;
            _icon.style.marginRight = string.IsNullOrEmpty(_label.text) ? 0 : 6;
        }

        public void ShowLabel(bool visible)
        {
            _textStack.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            _icon.style.marginRight = visible && !string.IsNullOrEmpty(_label.text) ? 6 : 0;
        }

        public void SetDescription(string description)
        {
            _description.text = description ?? string.Empty;
            _description.style.display = string.IsNullOrWhiteSpace(_description.text)
                ? DisplayStyle.None
                : DisplayStyle.Flex;
        }

        public string HelpText
        {
            get => _helpText;
            set => _helpText = value ?? string.Empty;
        }

        public void SetPalette(Color surface, Color foreground)
        {
            _surface = surface;
            _foreground = foreground;
            _label.style.color = foreground;
            _icon.Tint = foreground;
            style.color = foreground;
            RefreshSurface();
        }

        public void SetBackground(Color surface)
        {
            _surface = surface;
            RefreshSurface();
        }

        public new void SetEnabled(bool value)
        {
            base.SetEnabled(value);
            style.opacity = value ? 1f : 0.42f;
            RefreshSurface();
        }

        private void RefreshSurface()
        {
            var target = _pressed
                ? Color.Lerp(_surface, Color.black, 0.18f)
                : _hovered
                    ? Color.Lerp(_surface, Color.white, 0.10f)
                    : _surface;
            style.backgroundColor = target;
            var outline = _focused ? AgentUi.Focus : _hovered ? AgentUi.BorderStrong : AgentUi.Transparent;
            style.borderTopColor = outline;
            style.borderRightColor = outline;
            style.borderBottomColor = outline;
            style.borderLeftColor = outline;
            _label.style.color = enabledInHierarchy ? _foreground : Color.Lerp(_foreground, AgentUi.Muted, 0.55f);
            _description.style.color = enabledInHierarchy
                ? AgentUi.TextCaption
                : Color.Lerp(AgentUi.TextCaption, AgentUi.TextDimmed, 0.55f);
            _icon.Tint = enabledInHierarchy ? _foreground : Color.Lerp(_foreground, AgentUi.Muted, 0.55f);
        }
    }

    /// <summary>
    /// TextField with every visual sub-part restyled by the package. Text editing remains native so
    /// IME, selection, clipboard, password fields, and multiline behavior work in Editor and Player.
    /// </summary>
    internal sealed class AgentTextField : TextField
    {
        private readonly VisualElement _input;
        private readonly Label _placeholder;
        private bool _surface;
        private bool _isFocused;
        private bool _invalid;

        public AgentTextField(string label = "", bool surface = true) : base(label)
        {
            _surface = surface;
            style.minWidth = 0;
            style.flexShrink = 1;
            style.flexDirection = FlexDirection.Column;
            style.backgroundImage = StyleKeyword.None;
            style.backgroundColor = AgentUi.Transparent;
            style.borderTopWidth = 0;
            style.borderRightWidth = 0;
            style.borderBottomWidth = 0;
            style.borderLeftWidth = 0;
            style.marginTop = 0;
            style.marginRight = 0;
            style.marginBottom = 0;
            style.marginLeft = 0;
            style.paddingTop = 0;
            style.paddingRight = 0;
            style.paddingBottom = 0;
            style.paddingLeft = 0;
            style.opacity = 1;

            labelElement.style.width = StyleKeyword.Auto;
            labelElement.style.minWidth = 0;
            labelElement.style.flexGrow = 0;
            labelElement.style.flexShrink = 0;
            AgentUi.ApplyTypography(labelElement, AgentTypography.Caption);
            labelElement.style.color = AgentUi.Muted;
            labelElement.style.marginTop = 0;
            labelElement.style.marginRight = 0;
            labelElement.style.marginBottom = string.IsNullOrEmpty(label) ? 0 : 6;
            labelElement.style.marginLeft = 1;
            labelElement.style.paddingTop = 0;
            labelElement.style.paddingRight = 0;
            labelElement.style.paddingBottom = 0;
            labelElement.style.paddingLeft = 0;
            labelElement.style.backgroundImage = StyleKeyword.None;
            labelElement.style.backgroundColor = AgentUi.Transparent;
            labelElement.style.borderTopWidth = 0;
            labelElement.style.borderRightWidth = 0;
            labelElement.style.borderBottomWidth = 0;
            labelElement.style.borderLeftWidth = 0;
            if (string.IsNullOrEmpty(label)) labelElement.style.display = DisplayStyle.None;

            _placeholder = new Label { pickingMode = PickingMode.Ignore };
            _placeholder.style.position = Position.Absolute;
            _placeholder.style.left = _surface ? 8 : 0;
            _placeholder.style.right = _surface ? 8 : 0;
            _placeholder.style.top = 0;
            _placeholder.style.bottom = 0;
            _placeholder.style.unityTextAlign = TextAnchor.MiddleLeft;
            _placeholder.style.color = AgentUi.Placeholder;
            _placeholder.style.whiteSpace = WhiteSpace.NoWrap;
            _placeholder.style.overflow = Overflow.Hidden;
            _placeholder.style.textOverflow = TextOverflow.Ellipsis;
            _placeholder.style.display = DisplayStyle.None;

            // TextField builds its native editing hierarchy in the base constructor. Resolve and
            // compose that hierarchy before this field can enter a Panel: moving the placeholder
            // after attachment leaves Unity 2022.3's render chain with an Undetermined clip method
            // until the next clipping pass and can assert during the current visuals pass.
            _input = this.Q<VisualElement>(className: "unity-base-text-field__input")
                     ?? this.Q<VisualElement>(className: "unity-text-field__input")
                     ?? this.Q<VisualElement>(className: "unity-text-input")
                     ?? throw new InvalidOperationException(
                         "UnityAgentTool could not resolve Unity's native TextField input hierarchy.");
            _input.Add(_placeholder);
            StyleNativeInput();

            this.RegisterValueChangedCallback(_ => RefreshPlaceholder());
            RegisterCallback<PointerEnterEvent>(_ => SetInputBorder(_invalid ? AgentUi.Error : AgentUi.BorderStrong));
            RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (!_isFocused) SetInputBorder(_invalid ? AgentUi.Error : AgentUi.Border);
            });
            RegisterCallback<FocusInEvent>(_ =>
            {
                _isFocused = true;
                SetInputBorder(_invalid ? AgentUi.Error : AgentUi.Focus);
                RefreshPlaceholder();
            });
            RegisterCallback<FocusOutEvent>(_ =>
            {
                _isFocused = false;
                SetInputBorder(_invalid ? AgentUi.Error : AgentUi.Border);
                RefreshPlaceholder();
            });
            RegisterCallback<ContextualMenuPopulateEvent>(evt => evt.StopImmediatePropagation(),
                TrickleDown.TrickleDown);
        }

        public string Placeholder
        {
            get => _placeholder.text;
            set
            {
                _placeholder.text = value ?? string.Empty;
                RefreshPlaceholder();
            }
        }

        public void SetSurface(bool enabled)
        {
            _surface = enabled;
            StyleNativeInput();
        }

        public void SetInvalid(bool invalid)
        {
            _invalid = invalid;
            _input.style.backgroundColor = invalid
                ? AgentUi.ErrorPanel
                : _surface ? AgentUi.Input : AgentUi.Transparent;
            SetInputBorder(invalid ? AgentUi.Error : _isFocused ? AgentUi.Focus : AgentUi.Border);
        }

        public new void SetEnabled(bool value)
        {
            base.SetEnabled(value);
            style.opacity = value ? 1f : 0.42f;
        }

        private void StyleNativeInput()
        {
            var textElement = _input.Q<TextElement>();
            if (textElement != null)
            {
                textElement.style.backgroundImage = StyleKeyword.None;
                textElement.style.backgroundColor = AgentUi.Transparent;
                textElement.style.color = AgentUi.Text;
                StyleTextSelection(textElement);
            }
            _input.style.flexGrow = 1;
            _input.style.minWidth = 0;
            _input.style.minHeight = 32;
            _input.style.backgroundImage = StyleKeyword.None;
            _input.style.backgroundColor = _invalid
                ? AgentUi.ErrorPanel
                : _surface ? AgentUi.Input : AgentUi.Transparent;
            _input.style.color = AgentUi.Text;
            _input.style.marginTop = 0;
            _input.style.marginRight = 0;
            _input.style.marginBottom = 0;
            _input.style.marginLeft = 0;
            _input.style.paddingTop = _surface ? 5 : 4;
            _input.style.paddingRight = _surface ? 8 : 0;
            _input.style.paddingBottom = _surface ? 5 : 4;
            _input.style.paddingLeft = _surface ? 8 : 0;
            _input.style.borderTopLeftRadius = _surface ? 8 : 0;
            _input.style.borderTopRightRadius = _surface ? 8 : 0;
            _input.style.borderBottomLeftRadius = _surface ? 8 : 0;
            _input.style.borderBottomRightRadius = _surface ? 8 : 0;
            if (!_surface)
            {
                _input.style.borderTopWidth = 0;
                _input.style.borderRightWidth = 0;
                _input.style.borderBottomWidth = 0;
                _input.style.borderLeftWidth = 0;
                _input.style.borderTopColor = AgentUi.Transparent;
                _input.style.borderRightColor = AgentUi.Transparent;
                _input.style.borderBottomColor = AgentUi.Transparent;
                _input.style.borderLeftColor = AgentUi.Transparent;
            }
            SetInputBorder(_invalid ? AgentUi.Error : _surface ? AgentUi.Border : AgentUi.Transparent);

            ResetNativeTextVisuals(_input);
            _placeholder.style.left = _surface ? 8 : 0;
            _placeholder.style.right = _surface ? 8 : 0;
            _placeholder.style.top = 0;
            _placeholder.style.bottom = 0;
            _placeholder.style.color = AgentUi.Placeholder;
            _placeholder.style.unityTextAlign = TextAnchor.MiddleLeft;
            RefreshPlaceholder();
        }

        private static void ResetNativeTextVisuals(VisualElement root)
        {
            foreach (var child in root.Children())
            {
                child.style.backgroundImage = StyleKeyword.None;
                child.style.backgroundColor = AgentUi.Transparent;
                child.style.color = AgentUi.Text;
                child.style.borderTopWidth = 0;
                child.style.borderRightWidth = 0;
                child.style.borderBottomWidth = 0;
                child.style.borderLeftWidth = 0;
                child.style.marginTop = 0;
                child.style.marginRight = 0;
                child.style.marginBottom = 0;
                child.style.marginLeft = 0;
                if (child is TextElement textElement) StyleTextSelection(textElement);
                ResetNativeTextVisuals(child);
            }
        }

        private static void StyleTextSelection(TextElement textElement)
        {
            // Unity 2022 exposes these through TextElement's explicit ITextSelection implementation
            // in some player profiles, while newer profiles expose public properties. Reflection
            // keeps this Runtime assembly portable without falling back to Unity's skin colors.
            var interfaceType = typeof(TextElement).Assembly.GetType("UnityEngine.UIElements.ITextSelection");
            if (interfaceType == null || !interfaceType.IsInstanceOfType(textElement)) return;
            interfaceType.GetProperty("cursorColor", BindingFlags.Instance | BindingFlags.Public)?
                .SetValue(textElement, AgentUi.Text, null);
            interfaceType.GetProperty("selectionColor", BindingFlags.Instance | BindingFlags.Public)?
                .SetValue(textElement, AgentUi.Selection, null);
        }

        private void SetInputBorder(Color color)
        {
            if (!_surface) return;
            _input.style.borderTopWidth = 1;
            _input.style.borderRightWidth = 1;
            _input.style.borderBottomWidth = 1;
            _input.style.borderLeftWidth = 1;
            _input.style.borderTopColor = color;
            _input.style.borderRightColor = color;
            _input.style.borderBottomColor = color;
            _input.style.borderLeftColor = color;
        }

        private void RefreshPlaceholder()
        {
            _placeholder.style.display = !string.IsNullOrEmpty(_placeholder.text) &&
                                         string.IsNullOrEmpty(value)
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }
    }

    internal enum AgentChoiceMenuState
    {
        Ready,
        Loading,
        Empty,
        Error,
        Warning
    }

    /// <summary>Package-owned dropdown whose choices are rendered by the owned overlay layer.</summary>
    internal sealed class AgentChoiceField : VisualElement, INotifyValueChanged<string>
    {
        private readonly Label _caption;
        private readonly Label _valueLabel;
        private readonly AgentIcon _arrow;
        private readonly VisualElement _trigger;
        private readonly bool _compact;
        private List<string> _choices = new();
        private string _value = string.Empty;
        private bool _hovered;
        private bool _focused;
        private Color? _foreground;
        private AgentChoiceMenuState _menuState;
        private string _menuMessage = string.Empty;
        private Action? _retry;

        public AgentChoiceField(string label, IEnumerable<string> choices, bool compact = false)
        {
            _compact = compact;
            style.minWidth = 0;
            style.flexShrink = 1;
            style.backgroundImage = StyleKeyword.None;
            style.opacity = 1;
            style.marginTop = compact ? 0 : 4;
            style.marginBottom = compact ? 0 : 4;

            _caption = new Label(label) { pickingMode = PickingMode.Ignore };
            AgentUi.ApplyTypography(_caption, AgentTypography.Caption);
            _caption.style.color = AgentUi.Muted;
            _caption.style.marginLeft = 1;
            _caption.style.marginBottom = 6;
            _caption.style.display = string.IsNullOrEmpty(label) ? DisplayStyle.None : DisplayStyle.Flex;
            Add(_caption);

            _trigger = new VisualElement { focusable = true };
            _trigger.style.height = compact ? 28 : 32;
            _trigger.style.width = new Length(100, LengthUnit.Percent);
            _trigger.style.maxWidth = new Length(100, LengthUnit.Percent);
            _trigger.style.minWidth = 0;
            _trigger.style.flexShrink = 1;
            _trigger.style.flexDirection = FlexDirection.Row;
            _trigger.style.alignItems = Align.Center;
            _trigger.style.backgroundImage = StyleKeyword.None;
            _trigger.style.backgroundColor = compact ? AgentUi.Surface3 : AgentUi.Input;
            _trigger.style.borderTopLeftRadius = compact ? 14 : 8;
            _trigger.style.borderTopRightRadius = compact ? 14 : 8;
            _trigger.style.borderBottomLeftRadius = compact ? 14 : 8;
            _trigger.style.borderBottomRightRadius = compact ? 14 : 8;
            _trigger.style.borderTopWidth = 1;
            _trigger.style.borderRightWidth = 1;
            _trigger.style.borderBottomWidth = 1;
            _trigger.style.borderLeftWidth = 1;
            _trigger.style.borderTopColor = AgentUi.Border;
            _trigger.style.borderRightColor = AgentUi.Border;
            _trigger.style.borderBottomColor = AgentUi.Border;
            _trigger.style.borderLeftColor = AgentUi.Border;
            _trigger.style.paddingLeft = compact ? 8 : 8;
            _trigger.style.paddingRight = compact ? 4 : 8;
            Add(_trigger);

            _valueLabel = new Label { pickingMode = PickingMode.Ignore };
            _valueLabel.style.flexGrow = 1;
            _valueLabel.style.flexShrink = 1;
            _valueLabel.style.minWidth = 0;
            _valueLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _valueLabel.style.overflow = Overflow.Hidden;
            _valueLabel.style.textOverflow = TextOverflow.Ellipsis;
            _valueLabel.style.color = AgentUi.Text;
            AgentUi.ApplyTypography(_valueLabel, compact ? AgentTypography.Control : AgentTypography.Body);
            _trigger.Add(_valueLabel);
            _arrow = new AgentIcon(AgentIconKind.ChevronDown, 16);
            _arrow.style.width = 16;
            _arrow.style.minWidth = 16;
            _arrow.style.maxWidth = 16;
            _arrow.style.flexGrow = 0;
            _arrow.style.flexShrink = 0;
            _arrow.style.marginLeft = 4;
            _arrow.Tint = AgentUi.TextSecondary;
            _trigger.Add(_arrow);

            this.choices = choices.ToList();
            if (_choices.Count > 0) SetValueWithoutNotify(_choices[0]);

            _trigger.RegisterCallback<PointerEnterEvent>(_ =>
            {
                _hovered = true;
                RefreshTrigger();
            });
            _trigger.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                _hovered = false;
                RefreshTrigger();
            });
            _trigger.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !enabledInHierarchy) return;
                _trigger.Focus();
                ShowMenu();
                evt.StopPropagation();
            });
            _trigger.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (!enabledInHierarchy) return;
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.Space)
                {
                    ShowMenu();
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.UpArrow || evt.keyCode == KeyCode.DownArrow)
                {
                    SelectOffset(evt.keyCode == KeyCode.UpArrow ? -1 : 1);
                    evt.StopPropagation();
                }
            });
            _trigger.RegisterCallback<FocusInEvent>(_ =>
            {
                _focused = true;
                RefreshTrigger();
            });
            _trigger.RegisterCallback<FocusOutEvent>(_ =>
            {
                _focused = false;
                RefreshTrigger();
            });
        }

        public string label
        {
            get => _caption.text;
            set
            {
                _caption.text = value ?? string.Empty;
                _caption.style.display = string.IsNullOrEmpty(_caption.text) ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        public List<string> choices
        {
            get => _choices;
            set
            {
                _choices = value?.Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
                RefreshValueLabel();
            }
        }

        public Func<string, string>? ValueFormatter { get; set; }
        public Func<string, string>? OptionFormatter { get; set; }
        public Func<string, string>? OptionDescriptionFormatter { get; set; }
        public bool OpenUpward { get; set; }

        public void SetMenuStatus(AgentChoiceMenuState state, string message = "", Action? retry = null)
        {
            _menuState = state;
            _menuMessage = message ?? string.Empty;
            _retry = retry;
            RefreshValueLabel();
        }

        public string value
        {
            get => _value;
            set
            {
                value ??= string.Empty;
                if (string.Equals(_value, value, StringComparison.Ordinal)) return;
                var previous = _value;
                SetValueWithoutNotify(value);
                using var evt = ChangeEvent<string>.GetPooled(previous, _value);
                evt.target = this;
                SendEvent(evt);
            }
        }

        public void SetValueWithoutNotify(string newValue)
        {
            _value = newValue ?? string.Empty;
            RefreshValueLabel();
        }

        public void SetForeground(Color color)
        {
            _foreground = color;
            _valueLabel.style.color = color;
            _arrow.Tint = color;
        }

        public new void SetEnabled(bool value)
        {
            base.SetEnabled(value);
            style.opacity = value ? 1f : 0.42f;
            _trigger.pickingMode = value ? PickingMode.Position : PickingMode.Ignore;
        }

        private void ShowMenu()
        {
            var options = new List<AgentMenuItem>();
            if (_menuState != AgentChoiceMenuState.Ready)
            {
                var fallback = _menuState switch
                {
                    AgentChoiceMenuState.Loading => "Loading options...",
                    AgentChoiceMenuState.Empty => "No options available",
                    AgentChoiceMenuState.Error => "Options unavailable",
                    AgentChoiceMenuState.Warning => "Using curated fallback",
                    _ => "Options unavailable"
                };
                var text = string.IsNullOrWhiteSpace(_menuMessage) ? fallback : _menuMessage;
                var actionable = _menuState is AgentChoiceMenuState.Error or AgentChoiceMenuState.Warning;
                options.Add(new AgentMenuItem(text,
                    actionable ? _retry : null,
                    dangerous: _menuState == AgentChoiceMenuState.Error,
                    disabled: !actionable || _retry == null,
                    description: actionable && _retry != null ? "Refresh catalog" : string.Empty));
            }
            options.AddRange(_choices.Select(choice => new AgentMenuItem(
                string.IsNullOrEmpty(choice) ? "Default" : OptionFormatter?.Invoke(choice) ?? choice,
                () => value = choice,
                string.Equals(choice, _value, StringComparison.Ordinal),
                description: OptionDescriptionFormatter?.Invoke(choice) ?? string.Empty)));
            if (options.Count == 0)
                options.Add(new AgentMenuItem("No options available", null, false, false, true));
            AgentPopupMenu.Show(_trigger, options, Math.Max(_compact ? 240 : 180,
                Mathf.RoundToInt(worldBound.width)), OpenUpward);
        }

        private void RefreshValueLabel()
        {
            if (_choices.Count == 0)
            {
                _valueLabel.text = _menuState == AgentChoiceMenuState.Loading ? "Loading..." : "No options";
                _valueLabel.style.color = _menuState == AgentChoiceMenuState.Error
                    ? AgentUi.Error
                    : AgentUi.TextCaption;
                return;
            }
            var formatted = ValueFormatter?.Invoke(_value) ?? _value;
            _valueLabel.text = string.IsNullOrEmpty(formatted) ? "Select…" : formatted;
            _valueLabel.style.color = _foreground ?? AgentUi.Text;
            _arrow.Kind = AgentIconKind.ChevronDown;
        }

        private void RefreshTrigger()
        {
            _trigger.style.backgroundColor = _hovered
                ? (_compact ? AgentUi.Active : AgentUi.InputHover)
                : (_compact ? AgentUi.Surface3 : AgentUi.Input);
            SetBorder(_focused ? AgentUi.Focus : _compact ? AgentUi.Border1 : AgentUi.Border2);
        }

        private void SetBorder(Color color)
        {
            _trigger.style.borderTopColor = color;
            _trigger.style.borderRightColor = color;
            _trigger.style.borderBottomColor = color;
            _trigger.style.borderLeftColor = color;
        }

        private void SelectOffset(int direction)
        {
            if (_choices.Count == 0) return;
            var index = _choices.IndexOf(_value);
            if (index < 0) index = direction > 0 ? -1 : 0;
            value = _choices[(index + direction + _choices.Count) % _choices.Count];
        }
    }

    internal sealed class AgentIntegerField : VisualElement, INotifyValueChanged<int>
    {
        private readonly AgentTextField _field;
        private int _value;
        private bool _invalid;

        public AgentIntegerField(string label)
        {
            style.minWidth = 0;
            style.marginTop = 4;
            style.marginBottom = 4;
            _field = new AgentTextField(label);
            _field.style.flexGrow = 1;
            _field.RegisterValueChangedCallback(evt =>
            {
                if (!int.TryParse(evt.newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    _invalid = true;
                    _field.SetInvalid(true);
                    return;
                }
                _invalid = false;
                _field.SetInvalid(false);
                value = parsed;
            });
            _field.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (!_invalid) return;
                _invalid = false;
                _field.SetInvalid(false);
                _field.SetValueWithoutNotify(_value.ToString(CultureInfo.InvariantCulture));
            });
            Add(_field);
        }

        public int value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                var previous = _value;
                SetValueWithoutNotify(value);
                using var evt = ChangeEvent<int>.GetPooled(previous, _value);
                evt.target = this;
                SendEvent(evt);
            }
        }

        public void SetValueWithoutNotify(int newValue)
        {
            _value = newValue;
            _field.SetValueWithoutNotify(newValue.ToString(CultureInfo.InvariantCulture));
        }

        public new void SetEnabled(bool value)
        {
            base.SetEnabled(value);
            style.opacity = value ? 1f : 0.42f;
        }
    }

    internal sealed class AgentToggle : VisualElement, INotifyValueChanged<bool>
    {
        private readonly VisualElement _track;
        private readonly VisualElement _knob;
        private bool _value;
        private bool _hovered;
        private bool _pressed;

        public AgentToggle(string label)
        {
            focusable = true;
            style.height = 32;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.flexShrink = 0;
            style.paddingLeft = 3;
            style.paddingRight = 3;
            var caption = new Label(label) { pickingMode = PickingMode.Ignore };
            caption.style.color = AgentUi.Muted;
            caption.style.marginRight = 8;
            Add(caption);
            _track = new VisualElement { pickingMode = PickingMode.Ignore };
            _track.style.width = 34;
            _track.style.height = 18;
            _track.style.borderTopLeftRadius = 9;
            _track.style.borderTopRightRadius = 9;
            _track.style.borderBottomLeftRadius = 9;
            _track.style.borderBottomRightRadius = 9;
            _track.style.justifyContent = Justify.Center;
            Add(_track);
            _knob = new VisualElement { pickingMode = PickingMode.Ignore };
            _knob.style.position = Position.Absolute;
            _knob.style.top = 3;
            _knob.style.width = 12;
            _knob.style.height = 12;
            _knob.style.borderTopLeftRadius = 6;
            _knob.style.borderTopRightRadius = 6;
            _knob.style.borderBottomLeftRadius = 6;
            _knob.style.borderBottomRightRadius = 6;
            _track.Add(_knob);

            RegisterCallback<PointerEnterEvent>(_ =>
            {
                _hovered = true;
                RefreshVisual();
            });
            RegisterCallback<PointerLeaveEvent>(_ =>
            {
                _hovered = false;
                RefreshVisual();
            });
            RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !enabledInHierarchy) return;
                _pressed = true;
                Focus();
                RefreshVisual();
                evt.StopPropagation();
            });
            RegisterCallback<PointerUpEvent>(evt =>
            {
                if (evt.button != 0 || !_pressed) return;
                _pressed = false;
                if (worldBound.Contains(evt.position) && enabledInHierarchy) value = !value;
                RefreshVisual();
                evt.StopPropagation();
            });
            RegisterCallback<KeyDownEvent>(evt =>
            {
                if (!enabledInHierarchy || evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.Space) return;
                value = !value;
                evt.StopPropagation();
            });
            RegisterCallback<FocusInEvent>(_ =>
            {
                _track.style.borderTopWidth = 1;
                _track.style.borderRightWidth = 1;
                _track.style.borderBottomWidth = 1;
                _track.style.borderLeftWidth = 1;
                _track.style.borderTopColor = AgentUi.Focus;
                _track.style.borderRightColor = AgentUi.Focus;
                _track.style.borderBottomColor = AgentUi.Focus;
                _track.style.borderLeftColor = AgentUi.Focus;
            });
            RegisterCallback<FocusOutEvent>(_ =>
            {
                _pressed = false;
                _track.style.borderTopWidth = 0;
                _track.style.borderRightWidth = 0;
                _track.style.borderBottomWidth = 0;
                _track.style.borderLeftWidth = 0;
                RefreshVisual();
            });
            RefreshVisual();
        }

        public bool value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                var previous = _value;
                SetValueWithoutNotify(value);
                using var evt = ChangeEvent<bool>.GetPooled(previous, _value);
                evt.target = this;
                SendEvent(evt);
            }
        }

        public void SetValueWithoutNotify(bool newValue)
        {
            _value = newValue;
            RefreshVisual();
        }

        public new void SetEnabled(bool value)
        {
            base.SetEnabled(value);
            style.opacity = value ? 1f : 0.42f;
            pickingMode = value ? PickingMode.Position : PickingMode.Ignore;
        }

        private void RefreshVisual()
        {
            var baseColor = _value ? AgentUi.Accent : AgentUi.BorderStrong;
            _track.style.backgroundColor = _pressed
                ? Color.Lerp(baseColor, Color.black, 0.18f)
                : _hovered
                    ? Color.Lerp(baseColor, Color.white, 0.12f)
                    : baseColor;
            _knob.style.backgroundColor = _value ? AgentUi.Text : AgentUi.Muted;
            _knob.style.left = _value ? 19 : 3;
        }
    }

    internal sealed class AgentMenuItem
    {
        public AgentMenuItem(string text, Action? action, bool selected = false, bool dangerous = false,
            bool disabled = false, bool separatorBefore = false, string description = "")
        {
            Text = text;
            Action = action;
            Selected = selected;
            Dangerous = dangerous;
            Disabled = disabled;
            SeparatorBefore = separatorBefore;
            Description = description ?? string.Empty;
        }

        public string Text { get; }
        public Action? Action { get; }
        public bool Selected { get; }
        public bool Dangerous { get; }
        public bool Disabled { get; }
        public bool SeparatorBefore { get; }
        public string Description { get; }
    }

    internal static class AgentPopupMenu
    {
        private const string LayerName = "unity-agent-owned-popup-layer";

        public static void Show(VisualElement anchor, IReadOnlyList<AgentMenuItem> items, int minWidth = 220,
            bool openUpward = false)
        {
            var root = ResolvePopupHost(anchor);
            if (root == null) return;
            root.Q<VisualElement>(LayerName)?.RemoveFromHierarchy();
            AgentTooltip.HideAll(root);

            var rows = items?.Where(item => item != null).ToList() ?? new List<AgentMenuItem>();
            if (rows.Count == 0)
                rows.Add(new AgentMenuItem("No options available", null, disabled: true));

            var layer = new VisualElement { name = LayerName, focusable = true };
            var optionButtons = new List<AgentButton>();
            var focusedIndex = 0;
            EventCallback<GeometryChangedEvent>? geometryChanged = null;
            layer.style.position = Position.Absolute;
            layer.style.left = 0;
            layer.style.right = 0;
            layer.style.top = 0;
            layer.style.bottom = 0;
            layer.style.backgroundColor = AgentUi.Transparent;
            layer.style.backgroundImage = StyleKeyword.None;
            void Close()
            {
                if (geometryChanged != null) root.UnregisterCallback(geometryChanged);
                if (layer.parent != null) layer.RemoveFromHierarchy();
            }
            geometryChanged = _ => Close();
            root.RegisterCallback(geometryChanged);
            layer.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == layer) Close();
            });
            layer.RegisterCallback<DetachFromPanelEvent>(_ => AgentTooltip.HideAll(root));
            anchor.RegisterCallback<DetachFromPanelEvent>(_ => Close());
            layer.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape)
                {
                    Close();
                    anchor.Focus();
                    evt.StopPropagation();
                    return;
                }
                if (optionButtons.Count == 0 || evt.keyCode != KeyCode.UpArrow && evt.keyCode != KeyCode.DownArrow)
                    return;
                focusedIndex = (focusedIndex + (evt.keyCode == KeyCode.UpArrow ? -1 : 1) + optionButtons.Count) %
                               optionButtons.Count;
                optionButtons[focusedIndex].Focus();
                evt.StopPropagation();
            });
            root.Add(layer);
            layer.BringToFront();

            var offset = openUpward ? 8f : 4f;
            var position = root.WorldToLocal(new Vector2(anchor.worldBound.xMin, anchor.worldBound.yMax + offset));
            var anchorTop = root.WorldToLocal(new Vector2(anchor.worldBound.xMin, anchor.worldBound.yMin - offset));
            var menu = AgentUi.RoundedPanel(12);
            menu.style.position = Position.Absolute;
            var availableWidth = Mathf.Max(0, root.resolvedStyle.width - 32f);
            menu.style.left = Mathf.Max(16, position.x);
            var naturalHeight = 14f + rows.Sum(item =>
                (string.IsNullOrWhiteSpace(item.Description) ? 40f : 58f) +
                (item.SeparatorBefore ? 11f : 0f));
            var estimatedHeight = Mathf.Clamp(naturalHeight, 46f, openUpward ? 352f : 312f);
            menu.style.top = openUpward
                ? Mathf.Max(16, anchorTop.y - estimatedHeight)
                : Mathf.Max(16, position.y);
            var widthCap = openUpward ? 240f : 320f;
            menu.style.width = Mathf.Min(Mathf.Min(Mathf.Max(minWidth, anchor.worldBound.width), widthCap),
                availableWidth);
            menu.style.minHeight = 46;
            menu.style.maxHeight = Mathf.Min(openUpward ? 360 : 320,
                Mathf.Max(46, root.resolvedStyle.height - 96));
            menu.style.paddingTop = 4;
            menu.style.paddingRight = 4;
            menu.style.paddingBottom = 4;
            menu.style.paddingLeft = 4;
            menu.style.backgroundColor = AgentUi.Popup;
            AgentUi.SetBorder(menu, AgentUi.Border1, 1);
            layer.Add(menu);

            var maxListHeight = Mathf.Min(openUpward ? 352 : 312,
                Mathf.Max(38, root.resolvedStyle.height - 104));
            var needsScroll = naturalHeight > maxListHeight;
            VisualElement list;
            ScrollView? menuScroll = null;
            if (needsScroll)
            {
                menuScroll = AgentUi.Scroll(ScrollViewMode.Vertical);
                menuScroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
                menuScroll.style.height = maxListHeight;
                menuScroll.style.minHeight = 0;
                list = menuScroll.contentContainer;
                menu.Add(menuScroll);
            }
            else
            {
                list = new VisualElement();
                list.style.flexShrink = 0;
                menu.Add(list);
            }
            foreach (var item in rows)
            {
                if (item.SeparatorBefore)
                {
                    var separator = new VisualElement();
                    separator.style.height = 1;
                    separator.style.marginTop = 5;
                    separator.style.marginRight = 5;
                    separator.style.marginBottom = 5;
                    separator.style.marginLeft = 5;
                    separator.style.backgroundColor = AgentUi.Border;
                    list.Add(separator);
                }

                var action = item.Action;
                var option = AgentUi.Button(item.Text, item.Text, () =>
                {
                    if (item.Disabled) return;
                    Close();
                    action?.Invoke();
                }, 0, AgentUi.Transparent, item.Dangerous ? AgentUi.Error : AgentUi.Text,
                    item.Selected ? AgentIconKind.Check : AgentIconKind.None);
                option.style.minHeight = 38;
                option.style.marginTop = 1;
                option.style.marginRight = 0;
                option.style.marginBottom = 1;
                option.style.marginLeft = 0;
                option.style.justifyContent = Justify.FlexStart;
                option.SetDescription(item.Description);
                if (!string.IsNullOrWhiteSpace(item.Description))
                    option.style.height = 58;
                option.style.opacity = item.Disabled ? 0.45f : 1f;
                option.SetEnabled(!item.Disabled);
                if (!item.Disabled)
                {
                    var navigationIndex = optionButtons.Count;
                    option.RegisterCallback<FocusInEvent>(_ => focusedIndex = navigationIndex);
                    optionButtons.Add(option);
                }
                list.Add(option);
            }

            layer.schedule.Execute(() =>
            {
                var width = menu.resolvedStyle.width;
                var height = menu.resolvedStyle.height;
                var rootWidth = root.resolvedStyle.width;
                var availableHeight = root.resolvedStyle.height;
                if (!float.IsNaN(width) && position.x + width > rootWidth - 16)
                    menu.style.left = Mathf.Max(16, rootWidth - width - 16);
                var shouldOpenAbove = openUpward || !float.IsNaN(height) && position.y + height > availableHeight - 16;
                if (shouldOpenAbove && !float.IsNaN(height))
                {
                    menu.style.top = Mathf.Max(16, anchorTop.y - height);
                }
                else if (!float.IsNaN(height))
                {
                    menu.style.top = Mathf.Clamp(position.y, 16, Mathf.Max(16, availableHeight - height - 16));
                }
                if (optionButtons.Count > 0)
                {
                    var selectedIndex = rows.Where(value => !value.Disabled).ToList()
                        .FindIndex(value => value.Selected);
                    focusedIndex = selectedIndex < 0 ? 0 : selectedIndex;
                    optionButtons[focusedIndex].Focus();
                    menuScroll?.ScrollTo(optionButtons[focusedIndex]);
                }
                else
                {
                    layer.Focus();
                }
            });
        }

        internal static VisualElement? ResolvePopupHost(VisualElement anchor)
        {
            var panelRoot = anchor.panel?.visualTree;
            if (panelRoot == null) return null;
            for (var current = anchor; current != null && current != panelRoot; current = current.parent)
            {
                // EditorWindow panels contain internal chrome outside the user's content coordinates.
                // The workbench is the shared visible viewport in both Editor and Player, so owned
                // popups stay readable and clamp against the same logical rectangle as their anchors.
                if (current is UnityAgentWorkbenchView ||
                    current.ClassListContains(global::YuzeToolkit.DebugWindowUss.LayerClass))
                    return current;
            }
            LogSys.LogError("Yuze Agent Tool popup has no package-owned workbench or Debug Panel layer host.");
            return null;
        }
    }

    /// <summary>One owned tooltip layer per panel. Native UI Toolkit tooltip popups are suppressed.</summary>
    public static class AgentTooltip
    {
        private const string LayerName = "unity-agent-owned-tooltip";
        private const string RootClassName = "unity-agent-owned-tooltip-root";

        /// <summary>
        /// Constrains owned tooltips to a package-controlled visible surface embedded in a larger panel.
        /// </summary>
        public static void UseAsRoot(VisualElement root) => root.AddToClassList(RootClassName);

        public static void Attach(VisualElement target, string text) => Attach(target, () => text);

        public static void Attach(VisualElement target, Func<string> textProvider)
        {
            target.RegisterCallback<TooltipEvent>(evt =>
            {
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerEnterEvent>(evt =>
            {
                var text = textProvider();
                if (!string.IsNullOrWhiteSpace(text)) Show(target, text, evt.position);
            });
            target.RegisterCallback<PointerMoveEvent>(evt => Position(target, evt.position));
            target.RegisterCallback<PointerLeaveEvent>(_ => Hide(target));
            target.RegisterCallback<DetachFromPanelEvent>(_ => Hide(target));
        }

        private static void Show(VisualElement target, string text, Vector2 worldPosition)
        {
            var root = ResolveRoot(target);
            if (root == null) return;
            var popup = root.Q<VisualElement>(LayerName);
            if (popup == null)
            {
                popup = AgentUi.RoundedPanel(8);
                popup.name = LayerName;
                popup.pickingMode = PickingMode.Ignore;
                popup.style.position = UnityEngine.UIElements.Position.Absolute;
                popup.style.maxWidth = 360;
                popup.style.paddingTop = 7;
                popup.style.paddingRight = 9;
                popup.style.paddingBottom = 7;
                popup.style.paddingLeft = 9;
                popup.style.backgroundColor = AgentUi.Popup;
                AgentUi.SetBorder(popup, AgentUi.BorderStrong, 1);
                var label = new Label { name = LayerName + "-text", pickingMode = PickingMode.Ignore };
                label.style.color = AgentUi.Text;
                label.style.whiteSpace = WhiteSpace.Normal;
                AgentUi.ApplyTypography(label, AgentTypography.Caption, false);
                popup.Add(label);
                root.Add(popup);
            }
            var textLabel = popup.Q<Label>(LayerName + "-text");
            if (textLabel != null) textLabel.text = text;
            popup.style.display = DisplayStyle.Flex;
            popup.BringToFront();
            Position(target, worldPosition);
        }

        private static void Position(VisualElement target, Vector2 worldPosition)
        {
            var root = ResolveRoot(target);
            var popup = root?.Q<VisualElement>(LayerName);
            if (root == null || popup == null || popup.style.display == DisplayStyle.None) return;
            var local = root.WorldToLocal(worldPosition);
            var width = float.IsNaN(popup.resolvedStyle.width) ? 320f : popup.resolvedStyle.width;
            var height = float.IsNaN(popup.resolvedStyle.height) ? 58f : popup.resolvedStyle.height;
            popup.style.left = Mathf.Clamp(local.x + 12f, 8f,
                Mathf.Max(8f, root.resolvedStyle.width - width - 8f));
            popup.style.top = Mathf.Clamp(local.y + 17f, 8f,
                Mathf.Max(8f, root.resolvedStyle.height - height - 8f));
        }

        private static void Hide(VisualElement target)
        {
            var popup = ResolveRoot(target)?.Q<VisualElement>(LayerName);
            if (popup != null) popup.style.display = DisplayStyle.None;
        }

        internal static void HideAll(VisualElement root)
        {
            var popup = root.Q<VisualElement>(LayerName);
            if (popup != null) popup.style.display = DisplayStyle.None;
        }

        private static VisualElement? ResolveRoot(VisualElement target)
        {
            var panelRoot = target.panel?.visualTree;
            if (panelRoot == null) return null;
            for (var current = target; current != null && current != panelRoot; current = current.parent)
                if (current.ClassListContains(RootClassName) || current is UnityAgentWorkbenchView) return current;
            return panelRoot;
        }
    }
}
