using System;
using System.Linq;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Layout;
using UnityEngine;

namespace Mahjong.Renderer.Input
{
    public sealed class PointerInput : MonoBehaviour
    {
        private RendererState state;
        private SemanticActions actions;
        private CameraPicker picker;
        private bool down;
        private bool moved;
        private bool longPressed;
        private bool touchPointer;
        private int fingerId = -1;
        private Vector2 origin;
        private float started;
        private Pick pressed;
        private string lastTapId;
        private float lastTapTime;
        private float ignoreMouseUntil;

        public void Initialize(RendererState state, SemanticActions actions, CameraPicker picker)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGLInput.captureAllKeyboardInput = false;
#endif
            this.state = state;
            this.actions = actions;
            this.picker = picker;
            UnityEngine.Input.simulateMouseWithTouches = false;
            state.Invalidated += Invalidate;
        }

        private void Update()
        {
            if (state == null || !state.Actionable || state.Viewport == null) { Reset(); return; }
            if (UnityEngine.Input.touchCount > 1)
            {
                ignoreMouseUntil = Time.unscaledTime + 0.4f;
                actions.EndDrag(false);
                Invalidate();
                return;
            }
            if (UnityEngine.Input.touchCount == 1)
            {
                ignoreMouseUntil = Time.unscaledTime + 0.4f;
                Touch touch = UnityEngine.Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began) Begin(touch.position, true, touch.fingerId);
                else if (fingerId == touch.fingerId)
                {
                    if (touch.phase == TouchPhase.Canceled) { actions.EndDrag(false); Reset(); }
                    else if (touch.phase == TouchPhase.Ended) End(touch.position, false);
                    else Move(touch.position);
                }
                return;
            }
            if (touchPointer && down) { actions.EndDrag(false); Reset(); }
            if (Time.unscaledTime < ignoreMouseUntil) return;
            Vector2 mouse = UnityEngine.Input.mousePosition;
            if (UnityEngine.Input.GetMouseButtonDown(0)) Begin(mouse, false, -1);
            if (down) Move(mouse);
            else
            {
                Pick hit = picker.Entity(mouse, false);
                actions.Hover(hit.Found && hit.Kind == PickKind.Entity ? hit.Id : null);
            }
            if (UnityEngine.Input.GetMouseButtonUp(0) && down) End(mouse, Modifier());
            if (UnityEngine.Input.GetMouseButtonDown(1))
            {
                Pick hit = picker.Entity(mouse, false);
                if (hit.Found && hit.Kind == PickKind.Entity) actions.Flip(hit.Id, Modifier() ? -1 : 1);
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.F))
                actions.Flip(actions.Hovered ?? actions.Selection.FirstOrDefault(), Modifier() ? -1 : 1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                actions.EndDrag(false);
                actions.Select(null, false);
                Reset();
            }
        }

        private void Begin(Vector2 position, bool touch, int finger)
        {
            if (!touch && Time.unscaledTime < ignoreMouseUntil) return;
            if (picker.At(position) == null) return;
            down = true;
            moved = false;
            longPressed = false;
            touchPointer = touch;
            fingerId = finger;
            origin = position;
            started = Time.unscaledTime;
            pressed = picker.Entity(position, touch);
        }

        private void Move(Vector2 position)
        {
            if (!down) return;
            Vector2 cssOrigin = ViewportGeometry.ScreenToCss(origin, new Vector2(Screen.width, Screen.height), state.Viewport);
            Vector2 cssNow = ViewportGeometry.ScreenToCss(position, new Vector2(Screen.width, Screen.height), state.Viewport);
            if ((cssNow - cssOrigin).sqrMagnitude > 64)
            {
                moved = true;
                if (!actions.Dragging && pressed.Found && pressed.Kind == PickKind.Entity && !longPressed)
                    actions.BeginDrag(pressed.Id);
            }
            if (actions.Dragging)
            {
                Pick target = picker.Slot(position);
                if (!target.Found || !actions.DragToSlot(target.Id)) actions.ClearDragTargetCue();
            }
            else if (touchPointer && !moved && !longPressed && Time.unscaledTime - started >= 0.45f &&
                     pressed.Found && pressed.Kind == PickKind.Entity)
            {
                longPressed = actions.Select(pressed.Id, true);
            }
        }

        private void End(Vector2 position, bool additive)
        {
            if (!down) return;
            if (touchPointer) ignoreMouseUntil = Time.unscaledTime + 0.4f;
            if (actions.Dragging)
            {
                Pick target = picker.Slot(position);
                actions.EndDrag(target.Found && actions.DragToSlot(target.Id));
            }
            else if (!moved && !longPressed)
            {
                Pick hit = picker.Entity(position, touchPointer);
                if (hit.Found && pressed.Found && hit.Kind == pressed.Kind && hit.Id == pressed.Id)
                {
                    if (hit.Kind == PickKind.Dice) actions.RollDice();
                    else if (hit.Kind == PickKind.Entity)
                    {
                        bool doubleTap = hit.Id == lastTapId && Time.unscaledTime - lastTapTime < 0.32f;
                        if (!actions.TakePickup(hit.Id))
                        {
                            if (touchPointer && actions.Discard(hit.Id))
                                doubleTap = true;
                            else if (doubleTap)
                            {
                                if (!actions.Discard(hit.Id)) actions.Flip(hit.Id, additive ? -1 : 1);
                            }
                            else actions.Select(hit.Id, additive);
                        }
                        lastTapId = doubleTap ? null : hit.Id;
                        lastTapTime = Time.unscaledTime;
                    }
                }
                else if (!hit.Found) actions.Select(null, false);
            }
            Reset();
        }

        private static bool Modifier() => UnityEngine.Input.GetKey(KeyCode.LeftShift) ||
            UnityEngine.Input.GetKey(KeyCode.RightShift) || UnityEngine.Input.GetKey(KeyCode.LeftControl) ||
            UnityEngine.Input.GetKey(KeyCode.RightControl);

        public void Reset()
        {
            down = false;
            touchPointer = false;
            fingerId = -1;
            pressed = default;
        }

        private void Invalidate()
        {
            Reset();
            lastTapId = null;
            lastTapTime = 0;
        }

        private void OnDestroy() { if (state != null) state.Invalidated -= Invalidate; }
    }
}
