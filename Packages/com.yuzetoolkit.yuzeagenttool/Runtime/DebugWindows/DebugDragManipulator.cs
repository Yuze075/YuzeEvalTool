#nullable enable
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit
{
    internal sealed class DebugDragManipulator : PointerManipulator
    {
        private const float DragStartThresholdSqr = 9f;

        private readonly VisualElement _dragTarget;
        private bool _active;
        private bool _dragging;
        private int _pointerId;
        private Vector2 _startPointer;
        private Vector3 _startPosition;

        public DebugDragManipulator(VisualElement dragHandle, VisualElement dragTarget)
        {
            target = dragHandle;
            _dragTarget = dragTarget;
        }

        protected override void RegisterCallbacksOnTarget()
        {
            // Foldout headers have their own Clickable; take pointer down before it consumes capture.
            target.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            target.RegisterCallback<PointerUpEvent>(OnPointerUp);
            target.RegisterCallback<PointerCancelEvent>(OnPointerCancel);
            target.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            target.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
            target.UnregisterCallback<PointerUpEvent>(OnPointerUp);
            target.UnregisterCallback<PointerCancelEvent>(OnPointerCancel);
            target.UnregisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _active = true;
            _dragging = false;
            _pointerId = evt.pointerId;
            _startPointer = evt.position;
            _startPosition = _dragTarget.transform.position;
            target.CapturePointer(evt.pointerId);
            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!IsActivePointer(evt.pointerId)) return;

            var delta = (Vector2)evt.position - _startPointer;
            if (!_dragging && delta.sqrMagnitude < DragStartThresholdSqr) return;
            _dragging = true;

            var next = _startPosition + (Vector3)delta;
            if (_dragTarget.parent != null)
            {
                var parentRect = _dragTarget.parent.contentRect;
                var width = Mathf.Max(24f, _dragTarget.resolvedStyle.width);
                var height = Mathf.Max(24f, _dragTarget.resolvedStyle.height);
                next.x = Mathf.Clamp(next.x, -_dragTarget.layout.xMin, parentRect.width - width - _dragTarget.layout.xMin);
                next.y = Mathf.Clamp(next.y, -_dragTarget.layout.yMin, parentRect.height - height - _dragTarget.layout.yMin);
            }

            _dragTarget.transform.position = next;
            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!IsActivePointer(evt.pointerId)) return;

            var shouldToggleFoldout = !_dragging;
            Finish(evt.pointerId);
            if (shouldToggleFoldout && target is Toggle toggle)
                toggle.value = !toggle.value;

            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            if (!IsActivePointer(evt.pointerId)) return;
            Finish(evt.pointerId);
            evt.PreventDefault();
            evt.StopImmediatePropagation();
        }

        private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            _active = false;
            _dragging = false;
        }

        private void Finish(int pointerId)
        {
            if (!_active) return;
            _active = false;
            _dragging = false;
            if (target.HasPointerCapture(pointerId))
                target.ReleasePointer(pointerId);
        }

        private bool IsActivePointer(int pointerId)
        {
            return _active && _pointerId == pointerId && target.HasPointerCapture(pointerId);
        }
    }
}
