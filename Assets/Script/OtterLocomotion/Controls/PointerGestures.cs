using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class GestureSettings
    {
        [Tooltip("A left press on the water held this long (s) is a long press; let go sooner it is a click.")]
        public float holdTime = 0.3f;
        [Tooltip("A press within this long (s) of a click's release makes the two a double click.")]
        public float doubleClickTime = 0.3f;
        [Tooltip("The right button held this long (s) brakes to a stop; let go sooner it only shifts down.")]
        public float rightHoldTime = 0.3f;
    }

    public enum GestureKind { DragBegin, DragEnd, Press, Click, HoldBegin, HoldEnd, RightPress, RightHoldBegin, RightRelease }

    /// <summary>Something the player did with the mouse, and where on the water.</summary>
    public struct Gesture
    {
        public GestureKind kind;
        public float time;
        public Vector3 point;
        /// <summary>Press and Click: the second of a double click.</summary>
        public bool second;

        public override string ToString() => second ? kind + "(2nd)" : kind.ToString();
    }

    /// <summary>
    /// Turns the two mouse buttons into gestures. A left press that starts on the otter is a drag, held until the button
    /// comes up wherever the pointer has gone; one on the water is a click if let go within holdTime, a long press if
    /// not. A press within doubleClickTime of a click's release is the second of a double click; a drag or a long press
    /// breaks the chain, so neither one's release counts toward a double click. The right button gives a press at once
    /// and a hold once it has been down rightHoldTime.
    /// </summary>
    public sealed class PointerGestures
    {
        public bool Dragging { get; private set; }
        public bool Holding { get; private set; }
        public bool RightHolding { get; private set; }

        private bool left, right, pressed, pressSecond;
        private float pressTime, rightTime;
        private float lastClick = float.NegativeInfinity;

        public void Reset()
        {
            Dragging = Holding = RightHolding = false;
            left = right = pressed = pressSecond = false;
            lastClick = float.NegativeInfinity;
        }

        /// <param name="point">The pointer on the water.</param>
        /// <param name="overOtter">Whether the pointer is on the otter; only read as the left button goes down.</param>
        public void Step(GestureSettings s, float time, bool leftHeld, bool rightHeld, Vector3 point, bool overOtter, List<Gesture> output)
        {
            void Emit(GestureKind kind, bool second = false) =>
                output.Add(new Gesture { kind = kind, time = time, point = point, second = second });

            if (leftHeld && !left)
            {
                if (overOtter)
                {
                    Dragging = true;
                    lastClick = float.NegativeInfinity;
                    Emit(GestureKind.DragBegin);
                }
                else
                {
                    pressed = true;
                    pressTime = time;
                    pressSecond = time - lastClick <= s.doubleClickTime;
                    Emit(GestureKind.Press, pressSecond);
                }
            }
            if (leftHeld && pressed && !Holding && time - pressTime >= s.holdTime)
            {
                Holding = true;
                Emit(GestureKind.HoldBegin);
            }
            if (!leftHeld && left)
            {
                if (Dragging)
                {
                    Dragging = false;
                    Emit(GestureKind.DragEnd);
                }
                else if (pressed)
                {
                    if (Holding)
                    {
                        Holding = false;
                        lastClick = float.NegativeInfinity;
                        Emit(GestureKind.HoldEnd);
                    }
                    else
                    {
                        // a double click ends the chain: a third click starts a new one
                        lastClick = pressSecond ? float.NegativeInfinity : time;
                        Emit(GestureKind.Click, pressSecond);
                    }
                    pressed = false;
                }
            }
            left = leftHeld;

            if (rightHeld && !right)
            {
                rightTime = time;
                Emit(GestureKind.RightPress);
            }
            if (rightHeld && !RightHolding && time - rightTime >= s.rightHoldTime)
            {
                RightHolding = true;
                Emit(GestureKind.RightHoldBegin);
            }
            if (!rightHeld && right)
            {
                RightHolding = false;
                Emit(GestureKind.RightRelease);
            }
            right = rightHeld;
        }
    }
}
