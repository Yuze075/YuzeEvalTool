#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit.Agent
{
    internal sealed class SystemInfoView
    {
        private const int MaxVisibleRows = 12;

        private readonly VisualTreeAsset _templateAsset;
        private readonly List<SystemInfoRow> _rows = new();
        private TemplateContainer? _template;
        private VisualElement? _hud;
        private VisualElement? _lines;

        public SystemInfoView(VisualTreeAsset templateAsset)
        {
            _templateAsset = templateAsset;
        }

        public void AttachTo(VisualElement root)
        {
            _template = DebugPanelTemplate.Clone(_templateAsset, nameof(SystemInfoView));
            _hud = DebugPanelTemplate.QueryRequired<VisualElement>(_template, "unity-debug-tool-system-info-hud");
            _hud.RemoveFromHierarchy();
            root.Add(_hud);
            _lines = DebugPanelTemplate.QueryRequired<VisualElement>(_hud, "system-info-lines");
        }

        public void Detach()
        {
            _hud?.RemoveFromHierarchy();
            _template?.RemoveFromHierarchy();
            _template = null;
            _hud = null;
            _lines = null;
            _rows.Clear();
        }

        public void SetEmbeddedLayout()
        {
            if (_hud == null) return;
            _hud.style.position = Position.Relative;
            _hud.style.left = StyleKeyword.Auto;
            _hud.style.right = StyleKeyword.Auto;
            _hud.style.top = StyleKeyword.Auto;
            _hud.style.bottom = StyleKeyword.Auto;
            _hud.style.alignSelf = Align.FlexStart;
            _hud.style.alignItems = Align.Stretch;
            _hud.style.width = 320;
            _hud.style.maxWidth = new Length(100, LengthUnit.Percent);
            _hud.style.maxHeight = StyleKeyword.None;
            _hud.style.flexShrink = 0;
        }

        public void ApplySnapshot(SystemInfoSnapshot snapshot)
        {
            if (_lines == null) return;

            if (_hud != null)
                _hud.style.display = snapshot.Lines.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;

            var visibleCount = Mathf.Min(snapshot.Lines.Count, MaxVisibleRows);
            while (_rows.Count < visibleCount)
                AddLine(_lines);

            for (var i = 0; i < _rows.Count; i++)
            {
                var visible = i < visibleCount;
                _rows[i].Row.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!visible) continue;

                if (i == MaxVisibleRows - 1 && snapshot.Lines.Count > MaxVisibleRows)
                {
                    _rows[i].Key.text = "More";
                    _rows[i].Value.text = $"+{snapshot.Lines.Count - MaxVisibleRows + 1} hidden";
                    continue;
                }

                _rows[i].Key.text = snapshot.Lines[i].Key;
                _rows[i].Value.text = snapshot.Lines[i].Value;
            }
        }

        private void AddLine(VisualElement parent)
        {
            var row = new VisualElement();
            row.AddToClassList(SystemInfoUss.RowClass);

            var key = new Label();
            key.AddToClassList(SystemInfoUss.LabelClass);
            key.AddToClassList(SystemInfoUss.MutedLabelClass);
            key.AddToClassList(SystemInfoUss.KeyClass);

            var value = new Label();
            value.AddToClassList(SystemInfoUss.LabelClass);
            value.AddToClassList(SystemInfoUss.ValueClass);

            row.Add(key);
            row.Add(value);
            parent.Add(row);
            _rows.Add(new SystemInfoRow(row, key, value));
        }

        private readonly struct SystemInfoRow
        {
            public SystemInfoRow(VisualElement row, Label key, Label value)
            {
                Row = row;
                Key = key;
                Value = value;
            }

            public VisualElement Row { get; }

            public Label Key { get; }

            public Label Value { get; }
        }
    }
}
