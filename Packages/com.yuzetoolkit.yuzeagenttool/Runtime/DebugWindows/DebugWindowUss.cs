#nullable enable
using UnityEngine;
using UnityEngine.UIElements;
using YuzeToolkit.UnityAgent;

namespace YuzeToolkit
{
    internal static class DebugWindowUss
    {
        public const string LayerClass = "yuzu-debug-debug-layer";
        public const string PanelClass = "yuzu-debug-panel";
        public const string PanelHeaderClass = "yuzu-debug-panel-header";
        public const string PanelTitleClass = "yuzu-debug-panel-title";
        public const string PanelTabBarClass = "yuzu-debug-panel-tab-bar";
        public const string PanelTabClass = "yuzu-debug-panel-tab";
        public const string PanelTabActiveClass = "yuzu-debug-panel-tab-active";
        public const string PanelContentClass = "yuzu-debug-panel-content";
        public const string WindowClass = "yuzu-debug-window";
        public const string WindowContentClass = "yuzu-debug-window-content";
        public const string WindowBackgroundClass = "yuzu-debug-window-background";
        public const string FoldoutClass = "yuzu-debug-foldout";
        public const string HeaderClass = "yuzu-debug-header";
        public const string RowClass = "yuzu-debug-row";
        public const string SectionClass = "yuzu-debug-section";
        public const string FirstSectionClass = "yuzu-debug-first-section";
        public const string InlineGroupClass = "yuzu-debug-inline-group";
        public const string InlineRowGroupClass = "yuzu-debug-inline-group-row";
        public const string InlineColumnGroupClass = "yuzu-debug-inline-group-column";
        public const string InlineFieldLabelClass = "yuzu-debug-inline-field-label";
        public const string LabelClass = "yuzu-debug-label";
        public const string MutedLabelClass = "yuzu-debug-label-muted";
        public const string FieldClass = "yuzu-debug-field";
        public const string FieldWithoutLabelClass = "yuzu-debug-field-no-label";
        public const string ButtonClass = "yuzu-debug-button";
        public const string StateButtonClass = "yuzu-debug-state-button";
        public const string BoolButtonClass = "yuzu-debug-bool-button";
        public const string PrimaryButtonClass = "yuzu-debug-primary-button";
        public const string IconButtonClass = "yuzu-debug-icon-button";
        public const string StateLabelClass = "yuzu-debug-state-label";
        public const string TagClass = "yuzu-debug-tag";
        public const string ReadOnlyLabelClass = "yuzu-debug-readonly-label";
        public const string SegmentedRowClass = "yuzu-debug-segmented-row";
        public const string SegmentButtonClass = "yuzu-debug-segment-button";
        public const string ActiveClass = "yuzu-debug-active";
        public const string ToneSuccessClass = "yuzu-debug-tone-success";
        public const string ToneDangerClass = "yuzu-debug-tone-danger";
        public const string ToneRedClass = "yuzu-debug-tone-red";
        public const string ToneGreenClass = "yuzu-debug-tone-green";
        public const string ToneBlueClass = "yuzu-debug-tone-blue";
        public const string ToneYellowClass = "yuzu-debug-tone-yellow";
        public const string TonePinkClass = "yuzu-debug-tone-pink";
        public const string ToneWhiteClass = "yuzu-debug-tone-white";
        public const string MiniValueClass = "yuzu-debug-mini-value";
        public const string SliderRowClass = "yuzu-debug-slider-row";
        public const string SliderValueClass = "yuzu-debug-slider-value";
        public const string PreviewClass = "yuzu-debug-preview";
        public const string ImageClass = "yuzu-debug-image";
        public const string EnumFieldClass = "yuzu-debug-enum-field";
        public const string EnumLabelClass = "yuzu-debug-enum-label";
        public const string EnumButtonClass = "yuzu-debug-enum-button";
        public const string EnumButtonOpenClass = "yuzu-debug-enum-button-open";
        public const string EnumPopupClass = "yuzu-debug-enum-popup";
        public const string EnumPopupScrollClass = "yuzu-debug-enum-popup-scroll";
        public const string EnumPopupItemClass = "yuzu-debug-enum-popup-item";
        public const string EnumPopupItemSelectedClass = "yuzu-debug-enum-popup-item-selected";

        public static void ApplyLayer(VisualElement layer)
        {
            layer.AddToClassList(LayerClass);
            layer.pickingMode = PickingMode.Ignore;
            AgentUi.ApplyFont(layer);
        }

        public static void ApplyWindow(VisualElement window)
        {
            window.AddToClassList(WindowClass);
            window.pickingMode = PickingMode.Position;
            window.style.flexGrow = 1;
            window.style.minWidth = 0;
            window.style.minHeight = 0;
            window.style.backgroundColor = AgentUi.Transparent;
            DisableKeyboardFocus(window);
        }

        public static void ApplyPanel(VisualElement panel)
        {
            panel.AddToClassList(PanelClass);
            panel.pickingMode = PickingMode.Position;
        }

        public static void ApplyPanelHeader(VisualElement header) => header.AddToClassList(PanelHeaderClass);

        public static void ApplyPanelTitle(Label title) => title.AddToClassList(PanelTitleClass);

        public static void ApplyPanelTabBar(VisualElement tabBar) => tabBar.AddToClassList(PanelTabBarClass);

        public static void ApplyPanelTab(Button tab)
        {
            tab.AddToClassList(PanelTabClass);
            DisableKeyboardFocus(tab);
        }

        public static void ApplyPanelTabState(Button tab, bool active) =>
            tab.EnableInClassList(PanelTabActiveClass, active);

        public static void ApplyPanelContent(VisualElement content) => content.AddToClassList(PanelContentClass);

        public static void ApplyWindowBackground(VisualElement background)
        {
            background.AddToClassList(WindowBackgroundClass);
        }

        public static void ApplyWindowContent(VisualElement content)
        {
            content.AddToClassList(WindowContentClass);
            content.style.flexGrow = 1;
            content.style.minWidth = 0;
            content.style.minHeight = 0;
            content.style.paddingLeft = 20;
            content.style.paddingRight = 20;
            content.style.paddingTop = 16;
            content.style.paddingBottom = 20;
            content.style.backgroundColor = AgentUi.Background;
            DisableKeyboardFocus(content);
            if (content is ScrollView scrollView)
            {
                DisableKeyboardFocus(scrollView.horizontalScroller);
                DisableKeyboardFocus(scrollView.verticalScroller);
            }
        }

        public static void ApplyFoldout(VisualElement foldout)
        {
            foldout.AddToClassList(FoldoutClass);
            foldout.style.marginBottom = 10;
            foldout.style.paddingLeft = 12;
            foldout.style.paddingRight = 12;
            foldout.style.paddingTop = 8;
            foldout.style.paddingBottom = 8;
            foldout.style.backgroundColor = AgentUi.Panel;
            foldout.style.borderTopLeftRadius = 10;
            foldout.style.borderTopRightRadius = 10;
            foldout.style.borderBottomLeftRadius = 10;
            foldout.style.borderBottomRightRadius = 10;
            AgentUi.SetBorder(foldout, AgentUi.Border, 1);
            DisableKeyboardFocus(foldout);
        }

        public static void ApplyFoldoutHeader(AgentButton header)
        {
            header.AddToClassList(HeaderClass);
            header.style.width = Length.Percent(100);
            header.style.justifyContent = Justify.FlexStart;
            header.style.marginBottom = 6;
            header.style.backgroundColor = AgentUi.Transparent;
        }

        public static void ApplyRow(VisualElement row)
        {
            row.AddToClassList(RowClass);
            row.style.minWidth = 0;
            row.style.minHeight = 38;
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6;
            row.style.paddingLeft = 10;
            row.style.paddingRight = 10;
            row.style.paddingTop = 5;
            row.style.paddingBottom = 5;
            row.style.backgroundColor = AgentUi.PanelInset;
            row.style.borderTopLeftRadius = 8;
            row.style.borderTopRightRadius = 8;
            row.style.borderBottomLeftRadius = 8;
            row.style.borderBottomRightRadius = 8;
            AgentUi.SetBorder(row, AgentUi.Border, 1);
        }

        public static void ApplySection(Label label)
        {
            label.enableRichText = false;
            label.AddToClassList(SectionClass);
            AgentUi.ApplyTypography(label, AgentTypography.BodyStrong, false);
            label.style.minWidth = 0;
            label.style.color = AgentUi.Text;
            label.style.marginTop = 14;
            label.style.marginBottom = 8;
        }

        public static void ApplyFirstSection(Label label)
        {
            label.AddToClassList(FirstSectionClass);
            label.style.marginTop = 0;
        }

        public static void ApplyInlineGroup(VisualElement group)
        {
            group.AddToClassList(InlineGroupClass);
            group.style.minWidth = 0;
            group.style.minHeight = 36;
            group.style.alignSelf = Align.Stretch;
            group.style.marginBottom = 6;
            group.style.alignItems = Align.Center;
        }

        public static void ApplyControlRow(VisualElement row)
        {
            row.AddToClassList(RowClass);
            row.style.minWidth = 0;
            row.style.minHeight = 36;
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6;
        }

        public static void ApplyControlLabel(Label label)
        {
            label.enableRichText = false;
            label.style.width = StyleKeyword.Auto;
            label.style.minWidth = 130;
            label.style.flexShrink = 1;
            label.style.color = AgentUi.TextSecondary;
            AgentUi.ApplyTypography(label, AgentTypography.Control, false);
            ApplyContentWrapping(label);
        }

        public static void ApplyInlineGroupDirection(VisualElement group, FlexDirection direction)
        {
            group.AddToClassList(direction == FlexDirection.Row ? InlineRowGroupClass : InlineColumnGroupClass);
            group.style.flexDirection = direction;
            group.style.alignItems = direction == FlexDirection.Row ? Align.Center : Align.Stretch;
            group.style.flexWrap = direction == FlexDirection.Row ? Wrap.Wrap : Wrap.NoWrap;
        }

        public static void ApplyInlineFieldLabel(Label label)
        {
            label.enableRichText = false;
            label.AddToClassList(InlineFieldLabelClass);
            label.style.width = StyleKeyword.Auto;
            label.style.minWidth = 130;
            label.style.flexShrink = 1;
            if (!label.ClassListContains(StateLabelClass))
                label.style.color = AgentUi.TextSecondary;
            AgentUi.ApplyTypography(label, AgentTypography.Control, false);
            ApplyContentWrapping(label);
        }

        public static void ApplyInlineValueLabel(Label label)
        {
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            label.style.minWidth = 0;
            ApplyContentWrapping(label);
        }

        public static void ApplyLabel(Label label, bool muted = false)
        {
            label.enableRichText = false;
            label.AddToClassList(LabelClass);
            label.style.color = muted ? AgentUi.Muted : AgentUi.Text;
            label.style.minWidth = 0;
            AgentUi.ApplyTypography(label, AgentTypography.Body, false);
            if (muted)
                label.AddToClassList(MutedLabelClass);
        }

        public static void ApplyField<TValue>(BaseField<TValue> field)
        {
            field.AddToClassList(FieldClass);
            field.style.minWidth = 0;
            field.style.flexGrow = 1;
            field.style.height = 32;
            field.style.marginLeft = 0;
            field.style.marginRight = 0;
            field.style.marginTop = 0;
            field.style.marginBottom = 0;
            field.style.backgroundImage = StyleKeyword.None;
            field.style.backgroundColor = AgentUi.Transparent;
            field.style.color = AgentUi.Text;
            AgentUi.SetBorder(field, AgentUi.Transparent, 0);

            var label = field.labelElement;
            ApplyControlLabel(label);
            label.style.display = string.IsNullOrWhiteSpace(field.label) ? DisplayStyle.None : DisplayStyle.Flex;

            var input = field.Q<VisualElement>(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.minWidth = 0;
                input.style.flexGrow = 1;
                input.style.height = 32;
                input.style.marginLeft = 0;
                input.style.paddingLeft = 8;
                input.style.paddingRight = 8;
                input.style.backgroundImage = StyleKeyword.None;
                input.style.backgroundColor = AgentUi.Input;
                input.style.borderTopLeftRadius = 8;
                input.style.borderTopRightRadius = 8;
                input.style.borderBottomLeftRadius = 8;
                input.style.borderBottomRightRadius = 8;
                AgentUi.SetBorder(input, AgentUi.BorderStrong, 1);
            }
            if (field is TextField)
            {
                // Pointer focus is admitted by DebugVisualFactory only after a left-click in this field.
                field.focusable = true;
                field.tabIndex = -1;
            }
            else
            {
                DisableKeyboardFocus(field);
            }
        }

        public static void ApplyFieldWithoutLabel<TValue>(BaseField<TValue> field)
        {
            field.AddToClassList(FieldWithoutLabelClass);
        }

        public static void ApplyTextArea(TextField field)
        {
            ApplyField(field);
            field.multiline = true;
            field.style.height = StyleKeyword.Auto;
            field.style.minHeight = 128;

            var input = field.Q<VisualElement>(className: "unity-base-text-field__input");
            if (input == null)
                input = field.Q<VisualElement>(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.height = StyleKeyword.Auto;
                input.style.minHeight = 112;
                input.style.alignItems = Align.FlexStart;
                input.style.paddingTop = 7;
                input.style.paddingBottom = 7;
            }
        }

        public static void ApplyButton(AgentButton button, DebugButtonStyle style)
        {
            var fixedDirectionButton = style is DebugButtonStyle.Previous or DebugButtonStyle.Next;
            button.AddToClassList(ButtonClass);
            button.style.minHeight = 32;
            button.style.height = fixedDirectionButton
                ? new StyleLength(32)
                : new StyleLength(StyleKeyword.Auto);
            button.style.minWidth = fixedDirectionButton
                ? new StyleLength(32)
                : new StyleLength(StyleKeyword.Auto);
            button.style.width = fixedDirectionButton
                ? new StyleLength(32)
                : new StyleLength(StyleKeyword.Auto);
            button.style.maxWidth = fixedDirectionButton
                ? new StyleLength(32)
                : new StyleLength(Length.Percent(100));
            button.style.flexGrow = 0;
            button.style.flexShrink = fixedDirectionButton ? 0 : 1;
            button.style.flexBasis = StyleKeyword.Auto;
            button.style.alignSelf = Align.FlexStart;
            button.style.overflow = Overflow.Hidden;
            button.style.marginLeft = 3;
            button.style.marginRight = 3;
            button.style.borderTopLeftRadius = 16;
            button.style.borderTopRightRadius = 16;
            button.style.borderBottomLeftRadius = 16;
            button.style.borderBottomRightRadius = 16;
            if (style == DebugButtonStyle.Primary)
                button.AddToClassList(PrimaryButtonClass);
            if (style is DebugButtonStyle.Previous or DebugButtonStyle.Next)
                button.AddToClassList(IconButtonClass);
            DisableKeyboardFocus(button);
        }

        public static void ApplyStateButton(AgentButton button)
        {
            button.AddToClassList(StateButtonClass);
            button.style.minWidth = StyleKeyword.Auto;
            button.style.width = StyleKeyword.Auto;
            button.style.maxWidth = Length.Percent(100);
            button.style.flexGrow = 0;
            button.style.flexShrink = 1;
        }

        public static void ApplyBoolButton(AgentButton button)
        {
            button.AddToClassList(BoolButtonClass);
            button.style.width = 78;
            button.style.minWidth = 78;
            button.style.maxWidth = 78;
            button.style.flexShrink = 0;
        }

        public static void ApplyStateLabel(Label label)
        {
            label.enableRichText = false;
            label.AddToClassList(StateLabelClass);
            ApplyLabel(label);
            label.style.paddingLeft = 9;
            label.style.paddingRight = 9;
            label.style.paddingTop = 5;
            label.style.paddingBottom = 5;
            label.style.backgroundColor = AgentUi.Surface3;
            label.style.borderTopLeftRadius = 12;
            label.style.borderTopRightRadius = 12;
            label.style.borderBottomLeftRadius = 12;
            label.style.borderBottomRightRadius = 12;
            label.style.maxWidth = Length.Percent(100);
            label.style.flexShrink = 1;
            ApplyContentWrapping(label);
        }

        public static void ApplyTag(Label label)
        {
            label.enableRichText = false;
            label.AddToClassList(TagClass);
            ApplyLabel(label);
            label.style.color = AgentUi.Accent;
            label.style.backgroundColor = AgentUi.Active;
            label.style.paddingLeft = 8;
            label.style.paddingRight = 8;
            label.style.borderTopLeftRadius = 10;
            label.style.borderTopRightRadius = 10;
            label.style.borderBottomLeftRadius = 10;
            label.style.borderBottomRightRadius = 10;
            label.style.flexShrink = 1;
            ApplyContentWrapping(label);
        }

        public static void ApplyReadOnlyLabel(Label label)
        {
            label.enableRichText = false;
            label.AddToClassList(ReadOnlyLabelClass);
            ApplyLabel(label, true);
            label.style.flexGrow = 1;
            label.style.minWidth = 0;
            ApplyContentWrapping(label);
        }

        public static void ApplySegmentedRow(VisualElement row)
        {
            row.AddToClassList(SegmentedRowClass);
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.minWidth = 0;
        }

        public static void ApplySegmentButton(AgentButton button)
        {
            button.AddToClassList(SegmentButtonClass);
            button.style.minWidth = 38;
            button.style.width = 38;
            button.style.marginLeft = 2;
            button.style.marginRight = 2;
        }

        public static void ApplyActiveState(VisualElement element, bool active)
        {
            element.EnableInClassList(ActiveClass, active);
            element.style.backgroundColor = active ? AgentUi.Active : AgentUi.Surface3;
            element.style.color = active ? AgentUi.Accent : AgentUi.Text;
        }

        public static void ApplyActiveStateClass(VisualElement element, bool active) =>
            element.EnableInClassList(ActiveClass, active);

        public static void ApplyToneClasses(VisualElement element, DebugTone tone)
        {
            element.EnableInClassList(ToneSuccessClass, tone == DebugTone.Success);
            element.EnableInClassList(ToneDangerClass, tone == DebugTone.Danger);
            element.EnableInClassList(ToneRedClass, tone == DebugTone.Red);
            element.EnableInClassList(ToneGreenClass, tone == DebugTone.Green);
            element.EnableInClassList(ToneBlueClass, tone == DebugTone.Blue);
            element.EnableInClassList(ToneYellowClass, tone == DebugTone.Yellow);
            element.EnableInClassList(TonePinkClass, tone == DebugTone.Pink);
            element.EnableInClassList(ToneWhiteClass, tone == DebugTone.White);
        }

        public static Color GetToneColor(DebugTone tone) => tone switch
        {
            DebugTone.Success or DebugTone.Green => AgentUi.Success,
            DebugTone.Danger or DebugTone.Red => AgentUi.Error,
            DebugTone.Yellow => AgentUi.Warning,
            DebugTone.Blue => AgentUi.Accent,
            DebugTone.Pink => new Color32(236, 128, 191, 255),
            _ => AgentUi.Text
        };

        public static void ApplyTone(VisualElement element, DebugTone tone)
        {
            ApplyToneClasses(element, tone);
            element.style.color = GetToneColor(tone);
        }

        public static void ApplyMiniValue(Label label)
        {
            label.AddToClassList(MiniValueClass);
            ApplyLabel(label);
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            ApplyContentWrapping(label);
        }

        public static void ApplySliderRow(VisualElement row)
        {
            row.AddToClassList(SliderRowClass);
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.alignItems = Align.Center;
            row.style.minWidth = 0;
            row.style.minHeight = 36;
            row.style.marginBottom = 6;
        }

        public static void ApplySliderValue(Label label)
        {
            label.AddToClassList(SliderValueClass);
            ApplyLabel(label);
            label.style.width = StyleKeyword.Auto;
            label.style.minWidth = 58;
            label.style.flexShrink = 0;
            label.style.marginLeft = 10;
            label.style.unityTextAlign = TextAnchor.MiddleRight;
            ApplyContentWrapping(label);
        }

        public static void ApplyPreview(VisualElement previewRoot)
        {
            previewRoot.AddToClassList(PreviewClass);
            previewRoot.style.minHeight = 80;
            previewRoot.style.backgroundColor = AgentUi.PanelInset;
            previewRoot.style.borderTopLeftRadius = 8;
            previewRoot.style.borderTopRightRadius = 8;
            previewRoot.style.borderBottomLeftRadius = 8;
            previewRoot.style.borderBottomRightRadius = 8;
        }

        public static void ApplyImage(VisualElement preview)
        {
            preview.AddToClassList(ImageClass);
            preview.style.flexGrow = 1;
        }

        private static void DisableKeyboardFocus(VisualElement element)
        {
            element.focusable = false;
            element.tabIndex = -1;
        }

        private static void ApplyContentWrapping(Label label)
        {
            label.style.maxWidth = Length.Percent(100);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.overflow = Overflow.Visible;
            label.style.textOverflow = TextOverflow.Clip;
        }

    }
}
