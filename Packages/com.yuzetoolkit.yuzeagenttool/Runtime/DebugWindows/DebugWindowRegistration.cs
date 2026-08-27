#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit
{
    internal sealed class DebugWindowRegistration
    {
        private readonly DebugWindowNode _windowNode;

        private DebugWindowRegistration(DebugWindowNode windowNode)
        {
            _windowNode = windowNode;
        }

        public string Title => _windowNode.Title;

        public static DebugWindowRegistration Create(DebugWindowBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            return new DebugWindowRegistration(builder.WindowNode);
        }

        public DebugWindowVisualInstance CreateVisualElement(bool allowDragging)
        {
            var bindings = new List<IDebugValueBinding>();
            return new DebugWindowVisualInstance(
                DebugVisualFactory.CreateWindow(_windowNode, allowDragging, bindings), bindings);
        }
    }

    internal sealed class DebugWindowVisualInstance : IDisposable
    {
        private readonly List<IDebugValueBinding> _bindings;
        private bool _reportedRefreshError;

        public DebugWindowVisualInstance(VisualElement visualElement, List<IDebugValueBinding> bindings)
        {
            VisualElement = visualElement;
            _bindings = bindings;
        }

        public VisualElement VisualElement { get; }

        public void Refresh()
        {
            var hasError = false;
            foreach (var binding in _bindings)
            {
                try
                {
                    binding.Refresh();
                }
                catch (Exception exception)
                {
                    hasError = true;
                    if (!_reportedRefreshError) LogSys.LogException(exception);
                }
            }
            _reportedRefreshError = hasError;
        }

        public void Dispose()
        {
            VisualElement.RemoveFromHierarchy();
            _bindings.Clear();
        }
    }

    internal interface IDebugValueBinding
    {
        void Refresh();
    }
}
