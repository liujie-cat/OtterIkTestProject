using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class GestureSettings
    {
        [Tooltip("The go or back key held this long (s) is a hold: pressed cruising, go held sprints; back held brakes to a stop and lets the otter go.")]
        public float holdTime = 0.3f;
        [Tooltip("A key up for less than this (s) and down again is still the one press: a held key can drop out for a frame or two (key bounce, input methods), which would otherwise shift twice.")]
        public float releaseTime = 0.08f;
    }

    public enum GestureKind { Click, GoPress, GoHoldBegin, GoRelease, BackPress, BackHoldBegin, BackRelease }

    /// <summary>Something the player did with the mouse or the two keys, and where the pointer was on the water.</summary>
    public struct Gesture
    {
        public GestureKind kind;
        public float time;
        public Vector3 point;
        /// <summary>The pointer was on the otter (inside the ring round it), where it points nowhere.</summary>
        public bool onOtter;

        public override string ToString() => kind.ToString();
    }

    /// <summary>
    /// Turns the mouse button and the go and back keys into gestures. A mouse press is a click at once. Each key gives
    /// a press as it goes down, a hold once it has been down holdTime, and a release once it has stayed up releaseTime.
    /// </summary>
    public sealed class ControlGestures
    {
        public bool GoHolding { get; private set; }
        public bool BackHolding { get; private set; }

        private bool click, go, back;
        private float goTime, backTime, goSeen, backSeen;

        public void Reset()
        {
            GoHolding = BackHolding = false;
            click = go = back = false;
        }

        /// <param name="point">The pointer on the water.</param>
        /// <param name="overOtter">Whether the pointer is on the otter.</param>
        public void Step(GestureSettings s, float time, bool clickHeld, bool goHeld, bool backHeld, Vector3 point, bool overOtter, List<Gesture> output)
        {
            void Emit(GestureKind kind) => output.Add(new Gesture { kind = kind, time = time, point = point, onOtter = overOtter });
            bool Down(bool held, bool was, ref float seen)
            {
                if (held) seen = time;
                return held || was && time - seen < s.releaseTime;
            }

            if (clickHeld && !click) Emit(GestureKind.Click);
            click = clickHeld;

            goHeld = Down(goHeld, go, ref goSeen);
            backHeld = Down(backHeld, back, ref backSeen);

            if (goHeld && !go)
            {
                goTime = time;
                Emit(GestureKind.GoPress);
            }
            if (goHeld && !GoHolding && time - goTime >= s.holdTime)
            {
                GoHolding = true;
                Emit(GestureKind.GoHoldBegin);
            }
            if (!goHeld && go)
            {
                GoHolding = false;
                Emit(GestureKind.GoRelease);
            }
            go = goHeld;

            if (backHeld && !back)
            {
                backTime = time;
                Emit(GestureKind.BackPress);
            }
            if (backHeld && !BackHolding && time - backTime >= s.holdTime)
            {
                BackHolding = true;
                Emit(GestureKind.BackHoldBegin);
            }
            if (!backHeld && back)
            {
                BackHolding = false;
                Emit(GestureKind.BackRelease);
            }
            back = backHeld;
        }
    }
}
