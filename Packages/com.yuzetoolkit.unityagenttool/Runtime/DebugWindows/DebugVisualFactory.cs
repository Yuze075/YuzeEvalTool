#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using YuzeToolkit.UnityAgent;

namespace YuzeToolkit
{
    internal static class DebugVisualFactory
    {
        public static VisualElement CreateWindow(
            DebugWindowNode node,
            bool allowDragging,
            ICollection<IDebugValueBinding> bindings)
        {
            var window = new VisualElement { name = "unity-debug-tool-window-page" };
            DebugWindowUss.ApplyWindow(window);

            var content = new ScrollView(ScrollViewMode.Vertical);
            DebugWindowUss.ApplyWindowContent(content);
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            for (var index = 0; index < node.Children.Count; index++)
            {
                var child = node.Children[index];
                var childVisual = CreateNode(child, bindings);
                if (index == 0 && child is DebugSectionNode)
                    DebugWindowUss.ApplyFirstSection((Label)childVisual);
                content.Add(childVisual);
            }
            window.Add(content);

            SuppressKeyboardInteraction(window);

            return window;
        }

        private static void SuppressKeyboardInteraction(VisualElement root)
        {
            TextField? activeTextField = null;

            root.RegisterCallback<PointerDownEvent>(evt =>
            {
                DebugPanel.ReleaseEventSystemSelection();
                var textField = evt.button == 0 ? FindTextField(evt.target as VisualElement, root) : null;
                if (textField != null && textField.enabledInHierarchy)
                {
                    activeTextField = textField;
                    return;
                }

                activeTextField?.Blur();
                activeTextField = null;
                BlurFocusedElement(root);
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(evt =>
            {
                DebugPanel.ReleaseEventSystemSelection();
                if (FindTextField(evt.target as VisualElement, root) == null)
                    BlurFocusedElement(root);
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<FocusInEvent>(evt =>
            {
                if (activeTextField != null && IsDescendantOf(evt.target as VisualElement, activeTextField)) return;
                if (evt.target is VisualElement focused)
                    focused.schedule.Execute(focused.Blur);
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<FocusOutEvent>(evt =>
            {
                if (activeTextField != null && IsDescendantOf(evt.target as VisualElement, activeTextField))
                    activeTextField = null;
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (activeTextField != null && IsDescendantOf(evt.target as VisualElement, activeTextField))
                {
                    if (activeTextField.multiline) return;
                    if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
                    {
                        evt.PreventDefault();
                        evt.StopImmediatePropagation();
                        var submittedField = activeTextField;
                        activeTextField = null;
                        submittedField.schedule.Execute(submittedField.Blur);
                        DebugPanel.ReleaseEventSystemSelection();
                    }

                    return;
                }

                SuppressEvent(evt);
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyUpEvent>(evt =>
            {
                if (activeTextField != null && IsDescendantOf(evt.target as VisualElement, activeTextField)) return;
                SuppressEvent(evt);
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationMoveEvent>(SuppressEvent, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationSubmitEvent>(SuppressEvent, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationCancelEvent>(SuppressEvent, TrickleDown.TrickleDown);
        }

        private static TextField? FindTextField(VisualElement? target, VisualElement root)
        {
            for (var current = target; current != null && current != root; current = current.parent)
                if (current is TextField textField)
                    return textField;
            return null;
        }

        private static bool IsDescendantOf(VisualElement? target, VisualElement ancestor)
        {
            for (var current = target; current != null; current = current.parent)
                if (current == ancestor)
                    return true;
            return false;
        }

        private static void BlurFocusedElement(VisualElement root)
        {
            if (root.panel?.focusController.focusedElement is VisualElement focused && IsDescendantOf(focused, root))
                focused.Blur();
        }

        private static void SuppressEvent(KeyDownEvent evt)
        {
            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private static void SuppressEvent(KeyUpEvent evt)
        {
            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private static void SuppressEvent(NavigationMoveEvent evt)
        {
            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private static void SuppressEvent(NavigationSubmitEvent evt)
        {
            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private static void SuppressEvent(NavigationCancelEvent evt)
        {
            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private static VisualElement CreateNode(DebugNode node, ICollection<IDebugValueBinding> bindings)
        {
            switch (node)
            {
                case DebugInlineGroupNode inlineGroup:
                    return CreateInlineGroup(inlineGroup, bindings);
                case DebugGroupNode group:
                    return CreateGroup(group, bindings);
                case DebugSectionNode section:
                    return CreateSection(section.Label);
                case DebugDynamicLabelNode dynamicLabel:
                    return CreateDynamicLabel(dynamicLabel, bindings);
                case DebugTagNode tag:
                    return CreateTag(tag.Label);
                case DebugLabelNode label:
                    return CreateLabel(label.Label);
                case DebugSpaceNode space:
                    return new VisualElement { style = { height = space.Height } };
                case DebugImageNode image:
                    return CreateImage(image, bindings);
                case DebugButtonNode button:
                    return CreateButton(button);
                case DebugChoiceNode choice:
                    return CreateChoiceField(choice, bindings);
                case DebugStateButtonNode stateButton:
                    return CreateStateButton(stateButton, bindings);
                case DebugStateLabelNode stateLabel:
                    return CreateStateLabel(stateLabel, bindings);
                case DebugBoolButtonNode boolButton:
                    return CreateBoolButton(boolButton, bindings);
                case DebugSegmentedIntNode segmentedInt:
                    return CreateSegmentedInt(segmentedInt, bindings);
                case DebugFloatSliderNode slider:
                    return CreateFloatSlider(slider, bindings);
                case DebugIntSliderNode slider:
                    return CreateIntSlider(slider, bindings);
                case DebugProgressNode progress:
                    return CreateProgress(progress, bindings);
                case DebugTextAreaNode textArea:
                    return CreateTextArea(textArea, bindings);
                case IDebugFieldNode field:
                    return CreateField(field, node.Label, bindings);
                default:
                    return CreateLabel(node.Label);
            }
        }

        private static VisualElement CreateGroup(DebugGroupNode group, ICollection<IDebugValueBinding> bindings)
        {
            var foldout = new VisualElement();
            DebugWindowUss.ApplyFoldout(foldout);
            var isOpen = group.IsOpenGetter?.Invoke() ?? false;
            var content = new VisualElement
            {
                style = { display = isOpen ? DisplayStyle.Flex : DisplayStyle.None, minWidth = 0 }
            };
            AgentButton? header = null;
            header = CreateAgentButton(group.Label, DebugButtonStyle.Default, () =>
            {
                var open = content.resolvedStyle.display == DisplayStyle.None;
                content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
                group.IsOpenSetter?.Invoke(open);
                header!.SetIcon(open ? AgentIconKind.ChevronDown : AgentIconKind.ChevronRight);
            });
            header.SetIcon(isOpen ? AgentIconKind.ChevronDown : AgentIconKind.ChevronRight);
            DebugWindowUss.ApplyFoldoutHeader(header);
            foldout.Add(header);
            foreach (var child in group.Children)
                content.Add(CreateNode(child, bindings));
            foldout.Add(content);
            return foldout;
        }

        private static VisualElement CreateInlineGroup(DebugInlineGroupNode group, ICollection<IDebugValueBinding> bindings)
        {
            var root = new VisualElement();
            DebugWindowUss.ApplyInlineGroup(root);
            DebugWindowUss.ApplyInlineGroupDirection(root, group.Direction);
            for (var index = 0; index < group.Children.Count; index++)
            {
                var child = group.Children[index];
                var visual = CreateNode(child, bindings);
                if (group.Direction == FlexDirection.Row && visual is Label label)
                {
                    if (index == 0)
                        DebugWindowUss.ApplyInlineFieldLabel(label);
                    else
                        DebugWindowUss.ApplyInlineValueLabel(label);
                }
                root.Add(visual);
            }
            return root;
        }

        private static Label CreateLabel(string text)
        {
            var label = new Label(text);
            DebugWindowUss.ApplyLabel(label);
            return label;
        }

        private static Label CreateSection(string text)
        {
            var label = new Label(text);
            DebugWindowUss.ApplySection(label);
            return label;
        }

        private static Label CreateDynamicLabel(
            DebugDynamicLabelNode node,
            ICollection<IDebugValueBinding> bindings)
        {
            var label = CreateLabel(string.Empty);
            var binding = new FieldBinding<string>(node.Getter, value => label.text = value);
            bindings.Add(binding);
            binding.Refresh();
            return label;
        }

        private static Label CreateTag(string text)
        {
            var label = new Label(text);
            DebugWindowUss.ApplyTag(label);
            return label;
        }

        private static VisualElement CreateImage(DebugImageNode image, ICollection<IDebugValueBinding> bindings)
        {
            var root = new VisualElement();
            DebugWindowUss.ApplyPreview(root);

            if (!string.IsNullOrWhiteSpace(image.Label))
                root.Add(CreateLabel(image.Label));

            var preview = new VisualElement();
            DebugWindowUss.ApplyImage(preview);
            preview.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            root.Add(preview);

            var binding = new FieldBinding<Background>(image.BackgroundGetter, value => preview.style.backgroundImage = value);
            bindings.Add(binding);
            binding.Refresh();
            return root;
        }

        private static VisualElement CreateButton(DebugButtonNode node)
        {
            return CreateAgentButton(node.Label, node.Style, node.Action);
        }

        private static AgentButton CreateAgentButton(string label, DebugButtonStyle style, Action action)
        {
            var icon = style switch
            {
                DebugButtonStyle.Previous => AgentIconKind.Back,
                DebugButtonStyle.Next => AgentIconKind.ChevronRight,
                _ => AgentIconKind.None
            };
            var surface = style == DebugButtonStyle.Primary ? AgentUi.Accent : AgentUi.Surface3;
            var foreground = style == DebugButtonStyle.Primary ? AgentUi.AccentForeground : AgentUi.Text;
            var button = new AgentButton(label, string.Empty, action, surface, foreground, icon);
            button.EnableContentWrapping();
            button.focusable = false;
            button.tabIndex = -1;
            DebugWindowUss.ApplyButton(button, style);
            return button;
        }

        private static VisualElement CreateStateButton(
            DebugStateButtonNode node,
            ICollection<IDebugValueBinding> bindings)
        {
            AgentButton? button = null;
            button = CreateAgentButton(node.LabelGetter(), DebugButtonStyle.Default, () =>
            {
                node.Action();
                ApplyButtonState(button!, node.StateGetter(), node.Tone);
                button!.text = node.LabelGetter();
            });
            DebugWindowUss.ApplyStateButton(button);

            var binding = new FieldBinding<bool>(node.StateGetter, value =>
            {
                button.text = node.LabelGetter();
                ApplyButtonState(button!, value, node.Tone);
            });
            bindings.Add(binding);
            binding.Refresh();
            return button;
        }

        private static VisualElement CreateStateLabel(
            DebugStateLabelNode node,
            ICollection<IDebugValueBinding> bindings)
        {
            var label = CreateLabel(string.Empty);
            DebugWindowUss.ApplyStateLabel(label);
            DebugWindowUss.ApplyTone(label, node.Tone);
            var binding = new FieldBinding<bool>(node.Getter, value =>
                label.text = string.IsNullOrWhiteSpace(node.Label)
                    ? value.ToString()
                    : $"{node.Label} [{value}]");
            bindings.Add(binding);
            binding.Refresh();
            return label;
        }

        private static VisualElement CreateBoolButton(
            DebugBoolButtonNode node,
            ICollection<IDebugValueBinding> bindings)
        {
            AgentButton? button = null;
            void Apply(bool value)
            {
                button!.text = value ? "On" : "Off";
                ApplyButtonState(button, value, node.Tone);
            }

            var root = new VisualElement();
            DebugWindowUss.ApplyControlRow(root);
            if (!string.IsNullOrWhiteSpace(node.Label))
            {
                var label = new Label(node.Label);
                DebugWindowUss.ApplyControlLabel(label);
                root.Add(label);
            }
            button = CreateAgentButton(string.Empty, DebugButtonStyle.Default, () =>
            {
                var value = !node.Getter();
                node.Setter?.Invoke(value);
                Apply(node.Getter());
            });
            DebugWindowUss.ApplyBoolButton(button);
            root.Add(button);

            var binding = new FieldBinding<bool>(node.Getter, Apply);
            bindings.Add(binding);
            binding.Refresh();
            return root;
        }

        private static VisualElement CreateSegmentedInt(
            DebugSegmentedIntNode node,
            ICollection<IDebugValueBinding> bindings)
        {
            var root = new VisualElement();
            DebugWindowUss.ApplySegmentedRow(root);

            if (!string.IsNullOrWhiteSpace(node.Label))
            {
                var label = new Label(node.Label);
                DebugWindowUss.ApplyInlineFieldLabel(label);
                root.Add(label);
            }

            var buttons = new List<AgentButton>();
            for (var value = node.LowValue + 1; value <= node.HighValue; value++)
            {
                var targetValue = value;
                var button = CreateAgentButton(targetValue.ToString(), DebugButtonStyle.Default, () =>
                {
                    var current = Mathf.Clamp(node.Getter(), node.LowValue, node.HighValue);
                    node.Setter?.Invoke(current == targetValue ? targetValue - 1 : targetValue);
                });
                DebugWindowUss.ApplySegmentButton(button);
                buttons.Add(button);
                root.Add(button);
            }

            var binding = new FieldBinding<int>(node.Getter, current =>
            {
                current = Mathf.Clamp(current, node.LowValue, node.HighValue);
                for (var index = 0; index < buttons.Count; index++)
                    ApplyButtonState(buttons[index], current >= node.LowValue + index + 1, node.Tone);
            });
            bindings.Add(binding);
            binding.Refresh();
            return root;
        }

        private static void ApplyState(VisualElement element, bool active, DebugTone tone)
        {
            DebugWindowUss.ApplyActiveState(element, active);
            DebugWindowUss.ApplyTone(element, active ? tone : DebugTone.Default);
        }

        private static void ApplyButtonState(AgentButton button, bool active, DebugTone tone)
        {
            var foreground = active ? DebugWindowUss.GetToneColor(tone) : AgentUi.Text;
            button.SetPalette(active ? AgentUi.Active : AgentUi.Surface3, foreground);
            DebugWindowUss.ApplyActiveStateClass(button, active);
            DebugWindowUss.ApplyToneClasses(button, active ? tone : DebugTone.Default);
        }

        private static VisualElement CreateField(
            IDebugFieldNode node,
            string label,
            ICollection<IDebugValueBinding> bindings)
        {
            if (node.IsReadOnly)
                return CreateReadOnlyLabel(node, label, bindings);

            var type = Nullable.GetUnderlyingType(node.ValueType) ?? node.ValueType;
            if (type == typeof(bool)) return CreateBoolField(node, label, bindings);
            if (type == typeof(int)) return CreateTypedField<int, IntegerField>(node, label, new IntegerField(), bindings);
            if (type == typeof(float)) return CreateTypedField<float, FloatField>(node, label, new FloatField(), bindings);
            if (type == typeof(double)) return CreateTypedField<double, DoubleField>(node, label, new DoubleField(), bindings);
            if (type == typeof(string)) return CreateTypedField<string, TextField>(node, label, new TextField(), bindings);
            if (type == typeof(Vector2)) return CreateTypedField<Vector2, Vector2Field>(node, label, new Vector2Field(), bindings);
            if (type == typeof(Vector3)) return CreateTypedField<Vector3, Vector3Field>(node, label, new Vector3Field(), bindings);
            if (type == typeof(Vector4)) return CreateTypedField<Vector4, Vector4Field>(node, label, new Vector4Field(), bindings);
            if (type == typeof(Vector2Int)) return CreateTypedField<Vector2Int, Vector2IntField>(node, label, new Vector2IntField(), bindings);
            if (type == typeof(Vector3Int)) return CreateTypedField<Vector3Int, Vector3IntField>(node, label, new Vector3IntField(), bindings);
            if (type == typeof(Rect)) return CreateTypedField<Rect, RectField>(node, label, new RectField(), bindings);
            if (type == typeof(RectInt)) return CreateTypedField<RectInt, RectIntField>(node, label, new RectIntField(), bindings);
            if (type == typeof(Bounds)) return CreateTypedField<Bounds, BoundsField>(node, label, new BoundsField(), bindings);
            if (type == typeof(BoundsInt)) return CreateTypedField<BoundsInt, BoundsIntField>(node, label, new BoundsIntField(), bindings);
            if (type.IsEnum) return CreateEnumField(node, label, bindings);
            return CreateObjectField(node, label, bindings);
        }

        private static VisualElement CreateTextArea(DebugTextAreaNode node,
            ICollection<IDebugValueBinding> bindings)
        {
            var field = new TextField { multiline = true };
            field.label = node.Label;
            field.SetEnabled(!node.IsReadOnly);
            DebugWindowUss.ApplyTextArea(field);
            if (string.IsNullOrEmpty(node.Label))
                DebugWindowUss.ApplyFieldWithoutLabel(field);

            var binding = new ObjectFieldBinding<string>(node, field);
            bindings.Add(binding);
            binding.Refresh();
            if (!node.IsReadOnly)
            {
                field.RegisterValueChangedCallback(evt =>
                {
                    if (binding.IsRefreshing) return;
                    node.SetObjectValue(evt.newValue);
                    binding.Refresh();
                });
            }
            return field;
        }

        private static VisualElement CreateChoiceField(DebugChoiceNode node,
            ICollection<IDebugValueBinding> bindings)
        {
            var options = node.OptionsGetter();
            if (options == null || options.Count == 0)
            {
                var empty = CreateLabel(string.IsNullOrWhiteSpace(node.Label)
                    ? "No options"
                    : $"{node.Label} [No options]");
                return empty;
            }

            var field = new DebugChoiceDropdown(node.Label, options, node.IndexGetter());
            var binding = new ChoiceBinding(node, field);
            bindings.Add(binding);
            binding.Refresh();
            field.ValueChanged += index =>
            {
                if (binding.IsRefreshing) return;
                node.Setter(index);
                binding.Refresh();
            };
            return field;
        }

        private static VisualElement CreateReadOnlyLabel(
            IDebugFieldNode node,
            string label,
            ICollection<IDebugValueBinding> bindings)
        {
            var value = CreateLabel(string.Empty);
            DebugWindowUss.ApplyReadOnlyLabel(value);
            var binding = new FieldBinding<object?>(node.GetObjectValue, current =>
            {
                var formatted = DebugToolUtility.FormatValue(current);
                value.text = string.IsNullOrWhiteSpace(label) ? formatted : $"{label} [{formatted}]";
            });
            bindings.Add(binding);
            binding.Refresh();
            return value;
        }

        private static VisualElement CreateTypedField<TValue, TField>(
            IDebugFieldNode node,
            string label,
            TField field,
            ICollection<IDebugValueBinding> bindings)
            where TField : BaseField<TValue>
        {
            field.label = label;
            field.SetEnabled(!node.IsReadOnly);
            DebugWindowUss.ApplyField(field);
            if (string.IsNullOrEmpty(label))
                DebugWindowUss.ApplyFieldWithoutLabel(field);

            var binding = new ObjectFieldBinding<TValue>(node, field);
            bindings.Add(binding);
            binding.Refresh();

            if (!node.IsReadOnly)
            {
                field.RegisterValueChangedCallback(evt =>
                {
                    if (binding.IsRefreshing) return;
                    node.SetObjectValue(evt.newValue);
                    binding.Refresh();
                });
            }

            return field;
        }

        private static VisualElement CreateBoolField(
            IDebugFieldNode node,
            string label,
            ICollection<IDebugValueBinding> bindings)
        {
            var root = new VisualElement();
            DebugWindowUss.ApplyControlRow(root);
            if (!string.IsNullOrWhiteSpace(label))
            {
                var fieldLabel = new Label(label);
                DebugWindowUss.ApplyControlLabel(fieldLabel);
                root.Add(fieldLabel);
            }

            AgentButton? button = null;
            void Sync(bool value)
            {
                button!.text = value ? "On" : "Off";
                ApplyButtonState(button, value, DebugTone.Default);
            }

            bool GetCurrentValue() => node.GetObjectValue() is bool value && value;
            var binding = new FieldBinding<bool>(GetCurrentValue, Sync);
            button = CreateAgentButton(string.Empty, DebugButtonStyle.Default, () =>
            {
                node.SetObjectValue(!GetCurrentValue());
                binding.Refresh();
            });
            DebugWindowUss.ApplyBoolButton(button);
            root.Add(button);
            bindings.Add(binding);
            binding.Refresh();
            return root;
        }

        private static VisualElement CreateEnumField(
            IDebugFieldNode node,
            string label,
            ICollection<IDebugValueBinding> bindings)
        {
            var current = node.GetObjectValue() as Enum;
            if (current == null)
            {
                var enumType = Nullable.GetUnderlyingType(node.ValueType) ?? node.ValueType;
                var values = Enum.GetValues(enumType);
                current = values.Length > 0 ? values.GetValue(0) as Enum : null;
                if (current == null)
                    return CreateReadOnlyLabel(node, label, bindings);
            }

            var field = new DebugEnumDropdown(label, current.GetType(), current);
            field.SetEnabled(!node.IsReadOnly);
            if (string.IsNullOrEmpty(label))
                field.AddToClassList(DebugWindowUss.FieldWithoutLabelClass);

            var binding = new EnumFieldBinding(node, field);
            bindings.Add(binding);
            binding.Refresh();

            if (!node.IsReadOnly)
            {
                field.ValueChanged += value =>
                {
                    if (binding.IsRefreshing) return;
                    node.SetObjectValue(value);
                    binding.Refresh();
                };
            }

            return field;
        }

        private static VisualElement CreateObjectField(
            IDebugFieldNode node,
            string label,
            ICollection<IDebugValueBinding> bindings)
        {
            var row = new VisualElement();
            DebugWindowUss.ApplyRow(row);

            var name = new Label(label);
            DebugWindowUss.ApplyInlineFieldLabel(name);
            row.Add(name);

            var value = new Label();
            DebugWindowUss.ApplyMiniValue(value);
            row.Add(value);

            var binding = new LabelBinding(node.GetObjectValue, value);
            bindings.Add(binding);
            binding.Refresh();
            return row;
        }

        private static VisualElement CreateFloatSlider(DebugFloatSliderNode node, ICollection<IDebugValueBinding> bindings)
        {
            if (node.IsReadOnly)
                return CreateReadOnlyLabel(node, node.Label, bindings);

            var root = new VisualElement();
            DebugWindowUss.ApplySliderRow(root);
            var label = new Label(node.Label);
            DebugWindowUss.ApplyControlLabel(label);
            root.Add(label);
            var slider = new DebugRangeControl(true);

            var valueLabel = new Label();
            DebugWindowUss.ApplySliderValue(valueLabel);

            void Apply(float value)
            {
                var clamped = Mathf.Clamp(value, node.LowValue, node.HighValue);
                slider.SetValueWithoutNotify(Mathf.InverseLerp(node.LowValue, node.HighValue, clamped));
                valueLabel.text = DebugToolUtility.FormatNumber(node.Format, clamped);
            }

            var binding = new FieldBinding<float>(() => node.Getter(), Apply);
            bindings.Add(binding);
            binding.Refresh();

            slider.ValueChanged += normalized =>
            {
                var value = Mathf.Lerp(node.LowValue, node.HighValue, normalized);
                node.Setter?.Invoke(value);
                Apply(node.Getter());
            };

            root.Add(slider);
            root.Add(valueLabel);
            return root;
        }

        private static VisualElement CreateIntSlider(DebugIntSliderNode node, ICollection<IDebugValueBinding> bindings)
        {
            if (node.IsReadOnly)
                return CreateReadOnlyLabel(node, node.Label, bindings);

            var root = new VisualElement();
            DebugWindowUss.ApplySliderRow(root);
            var label = new Label(node.Label);
            DebugWindowUss.ApplyControlLabel(label);
            root.Add(label);
            var slider = new DebugRangeControl(true);

            var valueLabel = new Label();
            DebugWindowUss.ApplySliderValue(valueLabel);

            void Apply(int value)
            {
                var clamped = Mathf.Clamp(value, node.LowValue, node.HighValue);
                slider.SetValueWithoutNotify(Mathf.InverseLerp(node.LowValue, node.HighValue, clamped));
                valueLabel.text = DebugToolUtility.FormatNumber(node.Format, clamped);
            }

            var binding = new FieldBinding<int>(() => node.Getter(), Apply);
            bindings.Add(binding);
            binding.Refresh();

            slider.ValueChanged += normalized =>
            {
                var value = Mathf.RoundToInt(Mathf.Lerp(node.LowValue, node.HighValue, normalized));
                node.Setter?.Invoke(value);
                Apply(node.Getter());
            };

            root.Add(slider);
            root.Add(valueLabel);
            return root;
        }

        private static VisualElement CreateProgress(DebugProgressNode node, ICollection<IDebugValueBinding> bindings)
        {
            var progress = new VisualElement();
            DebugWindowUss.ApplySliderRow(progress);
            var label = new Label(node.Label);
            DebugWindowUss.ApplyControlLabel(label);
            progress.Add(label);
            var bar = new DebugRangeControl(false);
            progress.Add(bar);
            var valueLabel = new Label();
            DebugWindowUss.ApplySliderValue(valueLabel);
            progress.Add(valueLabel);
            var binding = new FieldBinding<float>(node.Getter, value =>
            {
                bar.SetValueWithoutNotify(Mathf.InverseLerp(node.LowValue, node.HighValue, value));
                valueLabel.text = DebugToolUtility.FormatNumber(node.Format, value);
            });
            bindings.Add(binding);
            binding.Refresh();
            return progress;
        }

        private sealed class DebugRangeControl : VisualElement
        {
            private readonly VisualElement _fill;
            private readonly VisualElement _thumb;
            private readonly bool _interactive;
            private float _value;

            public DebugRangeControl(bool interactive)
            {
                _interactive = interactive;
                pickingMode = interactive ? PickingMode.Position : PickingMode.Ignore;
                focusable = false;
                style.flexGrow = 1;
                style.minWidth = 80;
                style.height = 28;
                style.justifyContent = Justify.Center;

                var track = new VisualElement { pickingMode = PickingMode.Ignore };
                track.style.position = Position.Absolute;
                track.style.left = 0;
                track.style.right = 0;
                track.style.top = 12;
                track.style.height = 4;
                track.style.backgroundColor = AgentUi.Surface3;
                track.style.borderTopLeftRadius = 2;
                track.style.borderTopRightRadius = 2;
                track.style.borderBottomLeftRadius = 2;
                track.style.borderBottomRightRadius = 2;
                Add(track);

                _fill = new VisualElement { pickingMode = PickingMode.Ignore };
                _fill.style.height = Length.Percent(100);
                _fill.style.backgroundColor = AgentUi.Accent;
                _fill.style.borderTopLeftRadius = 2;
                _fill.style.borderTopRightRadius = 2;
                _fill.style.borderBottomLeftRadius = 2;
                _fill.style.borderBottomRightRadius = 2;
                track.Add(_fill);

                _thumb = new VisualElement { pickingMode = PickingMode.Ignore };
                _thumb.style.position = Position.Absolute;
                _thumb.style.width = 12;
                _thumb.style.height = 12;
                _thumb.style.top = -4;
                _thumb.style.marginLeft = -6;
                _thumb.style.backgroundColor = AgentUi.Text;
                _thumb.style.borderTopLeftRadius = 6;
                _thumb.style.borderTopRightRadius = 6;
                _thumb.style.borderBottomLeftRadius = 6;
                _thumb.style.borderBottomRightRadius = 6;
                _thumb.style.display = interactive ? DisplayStyle.Flex : DisplayStyle.None;
                track.Add(_thumb);

                if (!interactive) return;
                RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0 || !enabledInHierarchy) return;
                    this.CapturePointer(evt.pointerId);
                    UpdateFromPointer(evt.localPosition.x);
                    evt.StopPropagation();
                });
                RegisterCallback<PointerMoveEvent>(evt =>
                {
                    if (!this.HasPointerCapture(evt.pointerId)) return;
                    UpdateFromPointer(evt.localPosition.x);
                    evt.StopPropagation();
                });
                RegisterCallback<PointerUpEvent>(evt =>
                {
                    if (!this.HasPointerCapture(evt.pointerId)) return;
                    this.ReleasePointer(evt.pointerId);
                    UpdateFromPointer(evt.localPosition.x);
                    evt.StopPropagation();
                });
            }

            public event Action<float>? ValueChanged;

            public void SetValueWithoutNotify(float value)
            {
                _value = Mathf.Clamp01(value);
                var percentage = Length.Percent(_value * 100f);
                _fill.style.width = percentage;
                _thumb.style.left = percentage;
            }

            private void UpdateFromPointer(float localX)
            {
                if (!_interactive) return;
                var width = Mathf.Max(1f, resolvedStyle.width);
                var value = Mathf.Clamp01(localX / width);
                SetValueWithoutNotify(value);
                ValueChanged?.Invoke(_value);
            }
        }

        private sealed class ObjectFieldBinding<TValue> : IDebugValueBinding
        {
            private readonly IDebugFieldNode _node;
            private readonly BaseField<TValue> _field;
            private readonly Action<TValue>? _afterRefresh;

            public ObjectFieldBinding(
                IDebugFieldNode node,
                BaseField<TValue> field,
                Action<TValue>? afterRefresh = null)
            {
                _node = node;
                _field = field;
                _afterRefresh = afterRefresh;
            }

            public bool IsRefreshing { get; private set; }

            public void Refresh()
            {
                try
                {
                    IsRefreshing = true;
                    var value = _node.GetObjectValue();
                    if (value is TValue typed)
                    {
                        _field.SetValueWithoutNotify(typed);
                        _afterRefresh?.Invoke(typed);
                    }
                }
                finally
                {
                    IsRefreshing = false;
                }
            }
        }

        private sealed class EnumFieldBinding : IDebugValueBinding
        {
            private readonly IDebugFieldNode _node;
            private readonly DebugEnumDropdown _field;

            public EnumFieldBinding(IDebugFieldNode node, DebugEnumDropdown field)
            {
                _node = node;
                _field = field;
            }

            public bool IsRefreshing { get; private set; }

            public void Refresh()
            {
                try
                {
                    IsRefreshing = true;
                    if (_node.GetObjectValue() is Enum value)
                        _field.SetValueWithoutNotify(value);
                }
                finally
                {
                    IsRefreshing = false;
                }
            }
        }

        private sealed class ChoiceBinding : IDebugValueBinding
        {
            private readonly DebugChoiceNode _node;
            private readonly DebugChoiceDropdown _field;

            public ChoiceBinding(DebugChoiceNode node, DebugChoiceDropdown field)
            {
                _node = node;
                _field = field;
            }

            public bool IsRefreshing { get; private set; }

            public void Refresh()
            {
                IsRefreshing = true;
                try
                {
                    _field.SetOptionsWithoutNotify(_node.OptionsGetter(), _node.IndexGetter());
                }
                finally
                {
                    IsRefreshing = false;
                }
            }
        }

        private sealed class FieldBinding<TValue> : IDebugValueBinding
        {
            private readonly Func<TValue> _getter;
            private readonly Action<TValue> _apply;

            public FieldBinding(Func<TValue> getter, Action<TValue> apply)
            {
                _getter = getter;
                _apply = apply;
            }

            public void Refresh()
            {
                _apply(_getter());
            }
        }

        private sealed class LabelBinding : IDebugValueBinding
        {
            private readonly Func<object?> _getter;
            private readonly Label _label;

            public LabelBinding(Func<object?> getter, Label label)
            {
                _getter = getter;
                _label = label;
            }

            public void Refresh()
            {
                try
                {
                    _label.text = DebugToolUtility.FormatValue(_getter());
                }
                catch (Exception ex)
                {
                    _label.text = ex.Message;
                }
            }
        }
    }
}
