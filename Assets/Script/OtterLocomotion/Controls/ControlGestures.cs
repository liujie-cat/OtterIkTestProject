using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class GestureSettings
    {
        [Tooltip("A key held this long (s) is a hold: go held cruising sprints; back held brakes to a stop and lets the otter go; dive held under water swims deeper.")]
        public float holdTime = 0.3f;
        [Tooltip("A key up for less than this (s) and down again is still the one press, unless the keyboard reports it going down again: a held key can drop out of the polled state for a frame or two, which would otherwise shift twice, while a quick double tap comes down twice.")]
        public float releaseTime = 0.08f;
    }

    public enum GestureKind { GoPress, GoHoldBegin, GoRelease, BackPress, BackHoldBegin, BackRelease, DivePress, DiveHoldBegin, DiveRelease }

    /// <summary>Something the player did with the keys.</summary>
    public struct Gesture
    {
        public GestureKind kind;
        public float time;

        public override string ToString() => kind.ToString();
    }

    /// <summary>
    /// Turns the go, back and dive keys into gestures: each gives a press as it goes down, a hold once it has been down
    /// holdTime, and a release once it has stayed up releaseTime, or at once as the keyboard reports it going down
    /// again, however quickly, which starts the next press.
    /// </summary>
    public sealed class ControlGestures
    {
        private struct Key
        {
            public bool down, holding;
            public float pressed, seen;
        }

        private Key go, back, dive;

        public void Reset() => go = back = dive = default;

        /// <param name="goDown">The keyboard reported the key going down since the last step (and likewise backDown, diveDown).</param>
        public void Step(GestureSettings s, float time, bool goHeld, bool backHeld, bool diveHeld, List<Gesture> output,
            bool goDown = false, bool backDown = false, bool diveDown = false)
        {
            void Emit(GestureKind kind) => output.Add(new Gesture { kind = kind, time = time });
            void Press(ref Key k, bool held, bool wentDown, GestureKind press, GestureKind hold, GestureKind release)
            {
                if (held || wentDown) k.seen = time;
                held = held || wentDown || k.down && time - k.seen < s.releaseTime;
                if (wentDown && k.down)
                {
                    k.down = k.holding = false;
                    Emit(release);
                }
                if (held && !k.down)
                {
                    k.pressed = time;
                    Emit(press);
                }
                if (held && !k.holding && time - k.pressed >= s.holdTime)
                {
                    k.holding = true;
                    Emit(hold);
                }
                if (!held && k.down)
                {
                    k.holding = false;
                    Emit(release);
                }
                k.down = held;
            }

            Press(ref go, goHeld, goDown, GestureKind.GoPress, GestureKind.GoHoldBegin, GestureKind.GoRelease);
            Press(ref back, backHeld, backDown, GestureKind.BackPress, GestureKind.BackHoldBegin, GestureKind.BackRelease);
            Press(ref dive, diveHeld, diveDown, GestureKind.DivePress, GestureKind.DiveHoldBegin, GestureKind.DiveRelease);
        }
    }
}
