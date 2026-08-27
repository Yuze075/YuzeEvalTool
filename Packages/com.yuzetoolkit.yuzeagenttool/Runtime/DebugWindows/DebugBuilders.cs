#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit.Agent
{
    public enum DebugTone : byte
    {
        Default,
        Success,
        Danger,
        Red,
        Green,
        Blue,
        Yellow,
        Pink,
        White
    }

    public enum DebugButtonStyle : byte
    {
        Default,
        Primary,
        Previous,
        Next
    }

    public sealed class DebugWindowBuilder : DebugGroupBuilder
    {
        internal DebugWindowBuilder()
            : base(new DebugWindowNode())
        {
        }

        internal DebugWindowNode WindowNode => (DebugWindowNode)GroupNode;

        public DebugWindowBuilder SetTitle(string title)
        {
            WindowNode.Title = string.IsNullOrWhiteSpace(title) ? "Debug" : title;
            return this;
        }

        public DebugWindowBuilder SetDraggable(bool draggable)
        {
            WindowNode.Draggable = draggable;
            return this;
        }

        public new DebugWindowBuilder AddLabel(string text)
        {
            base.AddLabel(text);
            return this;
        }

        public new DebugWindowBuilder AddSection(string text)
        {
            base.AddSection(text);
            return this;
        }

        public new DebugWindowBuilder AddDynamicLabel(Func<string> getter)
        {
            base.AddDynamicLabel(getter);
            return this;
        }

        public new DebugWindowBuilder AddTag(string text)
        {
            base.AddTag(text);
            return this;
        }

        public new DebugWindowBuilder AddStateLabel(
            string label,
            Func<bool> getter,
            DebugTone tone = DebugTone.Default)
        {
            base.AddStateLabel(label, getter, tone);
            return this;
        }

        public new DebugWindowBuilder AddStateButton(
            Func<string> labelGetter,
            Func<bool> stateGetter,
            Action action,
            DebugTone tone = DebugTone.Default)
        {
            base.AddStateButton(labelGetter, stateGetter, action, tone);
            return this;
        }

        public new DebugWindowBuilder AddBoolButton(
            string label,
            Func<bool> getter,
            Action<bool> setter,
            DebugTone tone = DebugTone.Default)
        {
            base.AddBoolButton(label, getter, setter, tone);
            return this;
        }

        public new DebugWindowBuilder AddSegmentedInt(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            Action<int> setter,
            DebugTone tone = DebugTone.Danger)
        {
            base.AddSegmentedInt(label, lowValue, highValue, getter, setter, tone);
            return this;
        }

        public new DebugWindowBuilder AddSpace(float height = 8f)
        {
            base.AddSpace(height);
            return this;
        }

        public new DebugWindowBuilder AddButton(string label, Action action)
        {
            base.AddButton(label, action);
            return this;
        }

        public new DebugWindowBuilder AddPrimaryButton(string label, Action action)
        {
            base.AddPrimaryButton(label, action);
            return this;
        }

        public new DebugWindowBuilder AddPreviousButton(Action action)
        {
            base.AddPreviousButton(action);
            return this;
        }

        public new DebugWindowBuilder AddNextButton(Action action)
        {
            base.AddNextButton(action);
            return this;
        }

        public new DebugWindowBuilder AddReadOnly<TValue>(string label, Func<TValue> getter)
        {
            base.AddReadOnly(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddValue<TValue>(string label, Func<TValue> getter, Action<TValue> setter)
        {
            base.AddValue(label, getter, setter);
            return this;
        }

        public new DebugWindowBuilder AddField<TValue>(string label, Func<TValue> getter)
        {
            base.AddField(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddField<TValue>(string label, Func<TValue> getter, Action<TValue> setter)
        {
            base.AddField(label, getter, setter);
            return this;
        }

        public new DebugWindowBuilder AddReadOnlyBool(string label, Func<bool> getter)
        {
            base.AddReadOnly(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddReadOnlyInt(string label, Func<int> getter)
        {
            base.AddReadOnly(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddReadOnlyFloat(string label, Func<float> getter)
        {
            base.AddReadOnly(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddReadOnlyString(string label, Func<string> getter)
        {
            base.AddReadOnly(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddBool(string label, Func<bool> getter, Action<bool> setter)
        {
            base.AddBool(label, getter, setter);
            return this;
        }

        public new DebugWindowBuilder AddInt(string label, Func<int> getter, Action<int> setter)
        {
            base.AddValue(label, getter, setter);
            return this;
        }

        public new DebugWindowBuilder AddFloat(string label, Func<float> getter, Action<float> setter)
        {
            base.AddValue(label, getter, setter);
            return this;
        }

        public new DebugWindowBuilder AddString(string label, Func<string> getter, Action<string> setter)
        {
            base.AddValue(label, getter, setter);
            return this;
        }

        public new DebugWindowBuilder AddTextArea(string label, Func<string> getter, Action<string>? setter)
        {
            base.AddTextArea(label, getter, setter);
            return this;
        }

        public new DebugWindowBuilder AddReadOnlyTextArea(string label, Func<string> getter)
        {
            base.AddReadOnlyTextArea(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddChoice(
            string label,
            Func<IReadOnlyList<string>> optionsGetter,
            Func<int> indexGetter,
            Action<int> setter)
        {
            base.AddChoice(label, optionsGetter, indexGetter, setter);
            return this;
        }

        public new DebugWindowBuilder AddSlider(
            string label,
            float lowValue,
            float highValue,
            Func<float> getter,
            Action<float>? setter = null,
            string format = "0.##")
        {
            base.AddSlider(label, lowValue, highValue, getter, setter, format);
            return this;
        }

        public new DebugWindowBuilder AddSlider(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            Action<int>? setter = null,
            string format = "0")
        {
            base.AddSlider(label, lowValue, highValue, getter, setter, format);
            return this;
        }

        public new DebugWindowBuilder AddSlider(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            string format)
        {
            base.AddSlider(label, lowValue, highValue, getter, format);
            return this;
        }

        public new DebugWindowBuilder AddProgress(
            string label,
            float lowValue,
            float highValue,
            Func<float> getter,
            string format = "0.##")
        {
            base.AddProgress(label, lowValue, highValue, getter, format);
            return this;
        }

        public new DebugWindowBuilder AddProgress(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            string format = "0")
        {
            base.AddProgress(label, lowValue, highValue, getter, format);
            return this;
        }

        public new DebugWindowBuilder AddProgressBar(
            string label,
            float lowValue,
            float highValue,
            Func<float> getter,
            string format = "[{0:F2}]")
        {
            base.AddProgressBar(label, lowValue, highValue, getter, format);
            return this;
        }

        public new DebugWindowBuilder AddProgressBar(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            string format = "[{0}]")
        {
            base.AddProgressBar(label, lowValue, highValue, getter, format);
            return this;
        }

        public new DebugWindowBuilder AddImage(string label, Texture2D texture)
        {
            base.AddImage(label, texture);
            return this;
        }

        public new DebugWindowBuilder AddImage(string label, Sprite sprite)
        {
            base.AddImage(label, sprite);
            return this;
        }

        public new DebugWindowBuilder AddImage(string label, RenderTexture renderTexture)
        {
            base.AddImage(label, renderTexture);
            return this;
        }

        public new DebugWindowBuilder AddImage(string label, VectorImage vectorImage)
        {
            base.AddImage(label, vectorImage);
            return this;
        }

        public new DebugWindowBuilder AddImage(string label, Func<Texture2D> getter)
        {
            base.AddImage(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddImage(string label, Func<Sprite> getter)
        {
            base.AddImage(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddImage(string label, Func<RenderTexture> getter)
        {
            base.AddImage(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddImage(string label, Func<VectorImage> getter)
        {
            base.AddImage(label, getter);
            return this;
        }

        public new DebugWindowBuilder AddGroup(string label, Action<DebugGroupBuilder> configure)
        {
            base.AddGroup(label, configure);
            return this;
        }

        public new DebugWindowBuilder AddFoldout(string label, Action<DebugGroupBuilder> configure)
        {
            base.AddFoldout(label, configure);
            return this;
        }

        public new DebugWindowBuilder AddFoldout(
            string label,
            Func<bool> isOpenGetter,
            Action<bool> setOpen,
            Action<DebugGroupBuilder> configure)
        {
            base.AddFoldout(label, isOpenGetter, setOpen, configure);
            return this;
        }

        public new DebugWindowBuilder AddHorizontalGroup(Action<DebugGroupBuilder> configure)
        {
            base.AddHorizontalGroup(configure);
            return this;
        }

        public new DebugWindowBuilder AddVerticalGroup(Action<DebugGroupBuilder> configure)
        {
            base.AddVerticalGroup(configure);
            return this;
        }
    }

    public class DebugGroupBuilder
    {
        internal DebugGroupBuilder(DebugGroupNode groupNode)
        {
            GroupNode = groupNode;
        }

        internal DebugGroupNode GroupNode { get; }

        public DebugGroupBuilder AddLabel(string text)
        {
            GroupNode.Children.Add(new DebugLabelNode(text));
            return this;
        }

        public DebugGroupBuilder AddSection(string text)
        {
            GroupNode.Children.Add(new DebugSectionNode(text));
            return this;
        }

        public DebugGroupBuilder AddDynamicLabel(Func<string> getter)
        {
            GroupNode.Children.Add(new DebugDynamicLabelNode(getter));
            return this;
        }

        public DebugGroupBuilder AddTag(string text)
        {
            GroupNode.Children.Add(new DebugTagNode(text));
            return this;
        }

        public DebugGroupBuilder AddStateLabel(
            string label,
            Func<bool> getter,
            DebugTone tone = DebugTone.Default)
        {
            GroupNode.Children.Add(new DebugStateLabelNode(label, getter, tone));
            return this;
        }

        public DebugGroupBuilder AddStateButton(
            Func<string> labelGetter,
            Func<bool> stateGetter,
            Action action,
            DebugTone tone = DebugTone.Default)
        {
            GroupNode.Children.Add(new DebugStateButtonNode(
                labelGetter, stateGetter, action, tone));
            return this;
        }

        public DebugGroupBuilder AddBoolButton(
            string label,
            Func<bool> getter,
            Action<bool> setter,
            DebugTone tone = DebugTone.Default)
        {
            GroupNode.Children.Add(new DebugBoolButtonNode(label, getter, setter, tone));
            return this;
        }

        public DebugGroupBuilder AddSegmentedInt(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            Action<int> setter,
            DebugTone tone = DebugTone.Danger)
        {
            if (highValue <= lowValue) throw new ArgumentOutOfRangeException(nameof(highValue));
            GroupNode.Children.Add(new DebugSegmentedIntNode(
                label, lowValue, highValue, getter, setter, tone));
            return this;
        }

        public DebugGroupBuilder AddSpace(float height = 8f)
        {
            GroupNode.Children.Add(new DebugSpaceNode(Mathf.Max(0f, height)));
            return this;
        }

        public DebugGroupBuilder AddButton(string label, Action action)
        {
            GroupNode.Children.Add(new DebugButtonNode(label, action, DebugButtonStyle.Default));
            return this;
        }

        public DebugGroupBuilder AddPrimaryButton(string label, Action action)
        {
            GroupNode.Children.Add(new DebugButtonNode(label, action, DebugButtonStyle.Primary));
            return this;
        }

        public DebugGroupBuilder AddPreviousButton(Action action)
        {
            GroupNode.Children.Add(new DebugButtonNode(string.Empty, action, DebugButtonStyle.Previous));
            return this;
        }

        public DebugGroupBuilder AddNextButton(Action action)
        {
            GroupNode.Children.Add(new DebugButtonNode(string.Empty, action, DebugButtonStyle.Next));
            return this;
        }

        public DebugGroupBuilder AddReadOnly<TValue>(string label, Func<TValue> getter)
        {
            GroupNode.Children.Add(new DebugFieldNode<TValue>(label, getter, null));
            return this;
        }

        public DebugGroupBuilder AddValue<TValue>(string label, Func<TValue> getter, Action<TValue> setter)
        {
            GroupNode.Children.Add(new DebugFieldNode<TValue>(label, getter, setter));
            return this;
        }

        public DebugGroupBuilder AddField<TValue>(string label, Func<TValue> getter)
        {
            return AddReadOnly(label, getter);
        }

        public DebugGroupBuilder AddField<TValue>(string label, Func<TValue> getter, Action<TValue> setter)
        {
            return AddValue(label, getter, setter);
        }

        public DebugGroupBuilder AddReadOnlyBool(string label, Func<bool> getter) =>
            AddReadOnly(label, getter);

        public DebugGroupBuilder AddReadOnlyInt(string label, Func<int> getter) =>
            AddReadOnly(label, getter);

        public DebugGroupBuilder AddReadOnlyFloat(string label, Func<float> getter) =>
            AddReadOnly(label, getter);

        public DebugGroupBuilder AddReadOnlyString(string label, Func<string> getter) =>
            AddReadOnly(label, getter);

        public DebugGroupBuilder AddBool(string label, Func<bool> getter, Action<bool> setter) =>
            AddBoolButton(label, getter, setter, DebugTone.Success);

        public DebugGroupBuilder AddInt(string label, Func<int> getter, Action<int> setter) =>
            AddValue(label, getter, setter);

        public DebugGroupBuilder AddFloat(string label, Func<float> getter, Action<float> setter) =>
            AddValue(label, getter, setter);

        public DebugGroupBuilder AddString(string label, Func<string> getter, Action<string> setter) =>
            AddValue(label, getter, setter);

        public DebugGroupBuilder AddTextArea(string label, Func<string> getter, Action<string>? setter)
        {
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            GroupNode.Children.Add(new DebugTextAreaNode(label, getter, setter));
            return this;
        }

        public DebugGroupBuilder AddReadOnlyTextArea(string label, Func<string> getter) =>
            AddTextArea(label, getter, null);

        public DebugGroupBuilder AddChoice(
            string label,
            Func<IReadOnlyList<string>> optionsGetter,
            Func<int> indexGetter,
            Action<int> setter)
        {
            GroupNode.Children.Add(new DebugChoiceNode(label, optionsGetter, indexGetter, setter));
            return this;
        }

        public DebugGroupBuilder AddSlider(
            string label,
            float lowValue,
            float highValue,
            Func<float> getter,
            Action<float>? setter = null,
            string format = "0.##")
        {
            GroupNode.Children.Add(new DebugFloatSliderNode(
                label, lowValue, highValue, getter, setter, format));
            return this;
        }

        public DebugGroupBuilder AddSlider(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            Action<int>? setter = null,
            string format = "0")
        {
            GroupNode.Children.Add(new DebugIntSliderNode(
                label, lowValue, highValue, getter, setter, format));
            return this;
        }

        public DebugGroupBuilder AddSlider(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            string format)
        {
            GroupNode.Children.Add(new DebugIntSliderNode(
                label, lowValue, highValue, getter, null, format));
            return this;
        }

        public DebugGroupBuilder AddProgress(
            string label,
            float lowValue,
            float highValue,
            Func<float> getter,
            string format = "0.##")
        {
            GroupNode.Children.Add(new DebugProgressNode(label, lowValue, highValue, getter, format));
            return this;
        }

        public DebugGroupBuilder AddProgress(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            string format = "0")
        {
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            GroupNode.Children.Add(new DebugProgressNode(label, lowValue, highValue, () => getter(), format));
            return this;
        }

        public DebugGroupBuilder AddProgressBar(
            string label,
            float lowValue,
            float highValue,
            Func<float> getter,
            string format = "[{0:F2}]")
        {
            return AddProgress(label, lowValue, highValue, getter, format);
        }

        public DebugGroupBuilder AddProgressBar(
            string label,
            int lowValue,
            int highValue,
            Func<int> getter,
            string format = "[{0}]")
        {
            return AddProgress(label, lowValue, highValue, getter, format);
        }

        public DebugGroupBuilder AddImage(string label, Texture2D texture)
        {
            GroupNode.Children.Add(new DebugImageNode(label, () => Background.FromTexture2D(texture)));
            return this;
        }

        public DebugGroupBuilder AddImage(string label, Sprite sprite)
        {
            GroupNode.Children.Add(new DebugImageNode(label, () => Background.FromSprite(sprite)));
            return this;
        }

        public DebugGroupBuilder AddImage(string label, RenderTexture renderTexture)
        {
            GroupNode.Children.Add(new DebugImageNode(label, () => Background.FromRenderTexture(renderTexture)));
            return this;
        }

        public DebugGroupBuilder AddImage(string label, VectorImage vectorImage)
        {
            GroupNode.Children.Add(new DebugImageNode(label, () => Background.FromVectorImage(vectorImage)));
            return this;
        }

        public DebugGroupBuilder AddImage(string label, Func<Texture2D> getter)
        {
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            GroupNode.Children.Add(new DebugImageNode(label, () => Background.FromTexture2D(getter())));
            return this;
        }

        public DebugGroupBuilder AddImage(string label, Func<Sprite> getter)
        {
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            GroupNode.Children.Add(new DebugImageNode(label, () => Background.FromSprite(getter())));
            return this;
        }

        public DebugGroupBuilder AddImage(string label, Func<RenderTexture> getter)
        {
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            GroupNode.Children.Add(new DebugImageNode(label, () => Background.FromRenderTexture(getter())));
            return this;
        }

        public DebugGroupBuilder AddImage(string label, Func<VectorImage> getter)
        {
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            GroupNode.Children.Add(new DebugImageNode(label, () => Background.FromVectorImage(getter())));
            return this;
        }

        public DebugGroupBuilder AddGroup(string label, Action<DebugGroupBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var node = new DebugGroupNode(label);
            configure(new DebugGroupBuilder(node));
            GroupNode.Children.Add(node);
            return this;
        }

        public DebugGroupBuilder AddFoldout(string label, Action<DebugGroupBuilder> configure)
        {
            return AddGroup(label, configure);
        }

        public DebugGroupBuilder AddFoldout(
            string label,
            Func<bool> isOpenGetter,
            Action<bool> setOpen,
            Action<DebugGroupBuilder> configure)
        {
            if (isOpenGetter == null) throw new ArgumentNullException(nameof(isOpenGetter));
            if (setOpen == null) throw new ArgumentNullException(nameof(setOpen));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var node = new DebugGroupNode(label)
            {
                IsOpenGetter = isOpenGetter,
                IsOpenSetter = setOpen
            };
            configure(new DebugGroupBuilder(node));
            GroupNode.Children.Add(node);
            return this;
        }

        public DebugGroupBuilder AddHorizontalGroup(Action<DebugGroupBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var node = new DebugInlineGroupNode(FlexDirection.Row);
            configure(new DebugGroupBuilder(node));
            GroupNode.Children.Add(node);
            return this;
        }

        public DebugGroupBuilder AddVerticalGroup(Action<DebugGroupBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var node = new DebugInlineGroupNode(FlexDirection.Column);
            configure(new DebugGroupBuilder(node));
            GroupNode.Children.Add(node);
            return this;
        }

    }
}
