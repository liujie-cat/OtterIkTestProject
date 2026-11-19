using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>停驻, 慢游, 巡航, 冲刺.</summary>
    public enum Gear { Park, Slow, Cruise, Sprint }

    /// <summary>
    /// How the hind feet swim: at rest between single kicks, taking turns on the back, kicking together on the belly,
    /// or beating as one fluke in the dolphin wave.
    /// </summary>
    public enum Gait { Rest, SupineAlternate, ProneTogether, Dolphin }

    [Serializable]
    public class GearSettings
    {
        [Header("Speeds (body lengths per second)")]
        [Tooltip("Swimming slowly on the back, cruising on the belly and sprinting.")]
        public float slowSpeedBL = 0.8f;
        public float cruiseSpeedBL = 1.2f;
        public float sprintSpeedBL = 2f;
        [Tooltip("Parked, guided round more than parkDriftAngle (deg) it drifts on at this speed, so it draws a small arc rather than spinning on the spot.")]
        public float parkDriftBL = 0.1f;
        public float parkDriftAngle = 20f;

        [Header("Sprint")]
        [Tooltip("The sprint goes over from kicking to the dolphin wave once the body is this share of the way from cruise to sprint speed, or has kicked toward it for dolphinAfter seconds.")]
        [Range(0f, 1f)] public float dolphinAt = 0.75f;
        public float dolphinAfter = 1.5f;
        [Tooltip("Let go, it keeps the wave, and can jump, while it is faster than this share of the way from cruise to sprint speed, and for at least jumpGrace seconds; then it cruises.")]
        [Range(0f, 1f)] public float dolphinUntil = 0.4f;
        public float jumpGrace = 0.6f;

        [Header("Kicks")]
        [Tooltip("Strength (0..1) of the kick a click gives when parked, and when swimming.")]
        [Range(0f, 1f)] public float parkKick = 0.6f;
        [Range(0f, 1f)] public float swimKick = 1f;

        [Header("Aim")]
        [Tooltip("How long (s) the head and spine keep aiming where the guide point or a click was, fading out once it is let go.")]
        public float aimFade = 0.6f;
        [Tooltip("The head turns first: the body swims toward where the head looked this long (s) before.")]
        public float headLead = 0.12f;
    }

    public enum ControlEventKind { Kick, Jump, Shift }

    public struct ControlEvent
    {
        public ControlEventKind kind;
        public float time;
        /// <summary>Kick: which way it kicks off toward (world, on the water).</summary>
        public Vector3 direction;
        public float strength;
        public Gear from, to;
        public string reason;

        public override string ToString() => kind switch
        {
            ControlEventKind.Kick => $"Kick {strength:F1}",
            ControlEventKind.Jump => "Jump",
            _ => $"{from}->{to} ({reason})",
        };
    }

    /// <summary>The body as the gears see it.</summary>
    public struct GearBody
    {
        /// <summary>The body's middle and its head, on the water, and its facing.</summary>
        public Vector3 position, head, forward;
        public float speedBL;
        public bool airborne;
    }

    /// <summary>
    /// The gears the mouse shifts through (行动操作). Parked and swimming slowly the otter lies on its back; cruising and
    /// sprinting it swims on its belly. Selected, the pointer is its guide point in whatever gear it is in: the head
    /// looks at it and the body turns after the head, headLead behind, until it is let go. A click on the water turns it
    /// toward the click and kicks once, without shifting; a double click shifts up from
    /// parked to slow and from slow to cruising, its first click kicking as a click does. Cruising, a long press
    /// sprints along the heading: kicking at first, then in the dolphin wave; let go before the wave it cruises on, let
    /// go in the wave it slows of itself, keeping the wave while still fast and for at least jumpGrace. In the wave a
    /// double click jumps instead, neither click kicking; whether a double click jumps or shifts goes by the gear when
    /// its first press came. The right button shifts down a gear as it goes down, and held it brakes to parked; either
    /// way a pending jump is off. Clicks while in the air are spent.
    /// </summary>
    public sealed class GearMachine
    {
        public Gear Gear { get; private set; }
        /// <summary>Sprinting in the dolphin wave, rather than still kicking up to it.</summary>
        public bool Dolphin { get; private set; }
        /// <summary>A long press is driving the sprint on.</summary>
        public bool Accelerating { get; private set; }
        public bool Braking { get; private set; }
        /// <summary>Picked up by a right click on it, guided by the pointer until a left click on it lets go.</summary>
        public bool Selected { get; private set; }
        /// <summary>Where the head looks to go (world, unit, on the water): from the head toward the guide point or the click.</summary>
        public Vector3 Heading { get; private set; } = Vector3.forward;
        /// <summary>Where the body is headed: the head's heading of headLead ago.</summary>
        public Vector3 BodyHeading { get; private set; } = Vector3.forward;
        /// <summary>Where the head and spine aim, and how much (1 while selected, fading after it is let go or after a click).</summary>
        public Vector3 AimPoint { get; private set; }
        public float AimBlend { get; private set; }
        /// <summary>Speed asked for (body lengths/s).</summary>
        public float SpeedBL { get; private set; }

        public bool JumpEligible => Gear == Gear.Sprint && Dolphin;
        public Posture Posture => PostureOf(Gear);
        public static Posture PostureOf(Gear gear) => gear <= Gear.Slow ? Posture.Supine : Posture.Prone;
        public Gait Gait => Gear switch
        {
            Gear.Park => Gait.Rest,
            Gear.Slow => Gait.SupineAlternate,
            Gear.Sprint when Dolphin => Gait.Dolphin,
            _ => Gait.ProneTogether,
        };

        private Gear pressGear;
        private bool pressEligible;
        private bool clickPending;
        private Vector3 pendingPoint;
        private float pendingUntil, sprintSince, releasedAt, aimUntil;
        private readonly List<(float time, Vector3 heading)> looked = new List<(float, Vector3)>();

        public void Reset(in GearBody body, Gear initial = Gear.Park)
        {
            Gear = initial;
            Dolphin = Accelerating = Braking = Selected = false;
            Heading = BodyHeading = Flat(body.forward, Vector3.forward);
            looked.Clear();
            AimPoint = body.head + Heading;
            AimBlend = 0f;
            SpeedBL = 0f;
            clickPending = pressEligible = false;
            aimUntil = float.NegativeInfinity;
        }

        /// <param name="pointer">The pointer on the water now: selected, the guide point, once it is off the otter.</param>
        public void Step(GearSettings s, GestureSettings gs, List<Gesture> gestures, float time, Vector3 pointer, bool pointerOnOtter, in GearBody b, List<ControlEvent> events)
        {
            foreach (var g in gestures)
            {
                switch (g.kind)
                {
                    case GestureKind.Select:
                        Selected = true;
                        break;
                    case GestureKind.Deselect:
                        if (Selected) aimUntil = time;
                        Selected = false;
                        break;
                    case GestureKind.Press:
                        if (!g.second)
                        {
                            pressGear = Gear;
                            pressEligible = JumpEligible;
                        }
                        // the first click of a double click in the wave waits to see whether a second comes
                        else clickPending = false;
                        break;
                    case GestureKind.Click:
                        if (b.airborne) break;
                        if (!g.second)
                        {
                            if (pressEligible)
                            {
                                clickPending = true;
                                pendingPoint = g.point;
                                pendingUntil = time + gs.doubleClickTime;
                            }
                            else Click(s, g.point, time, b, events);
                        }
                        else if (pressEligible) events.Add(new ControlEvent { kind = ControlEventKind.Jump, time = time, direction = Heading });
                        else if (pressGear == Gear.Park) Shift(Gear.Slow, "double click", time, events);
                        else if (pressGear == Gear.Slow) Shift(Gear.Cruise, "double click", time, events);
                        else Click(s, g.point, time, b, events);
                        break;
                    case GestureKind.HoldBegin:
                        if (b.airborne || Gear < Gear.Cruise) break;
                        Accelerating = true;
                        if (Gear != Gear.Sprint)
                        {
                            sprintSince = time;
                            Shift(Gear.Sprint, "long press", time, events);
                        }
                        break;
                    case GestureKind.HoldEnd:
                        if (!Accelerating) break;
                        Accelerating = false;
                        releasedAt = time;
                        if (!Dolphin) Shift(Gear.Cruise, "let go before the wave", time, events);
                        break;
                    case GestureKind.RightPress:
                        Accelerating = false;
                        clickPending = pressEligible = false;
                        if (Gear > Gear.Park) Shift(Gear - 1, "right click", time, events);
                        break;
                    case GestureKind.RightHoldBegin:
                        Braking = true;
                        Shift(Gear.Park, "brake", time, events);
                        break;
                    case GestureKind.RightRelease:
                        Braking = false;
                        break;
                }
            }

            if (clickPending && time >= pendingUntil)
            {
                clickPending = false;
                Click(s, pendingPoint, time, b, events);
            }

            if (Gear == Gear.Sprint && !b.airborne)
            {
                float toSprint = Mathf.InverseLerp(s.cruiseSpeedBL, s.sprintSpeedBL, b.speedBL);
                if (Accelerating && !Dolphin && (toSprint >= s.dolphinAt || time - sprintSince >= s.dolphinAfter))
                    Dolphin = true;
                else if (!Accelerating && Dolphin && toSprint < s.dolphinUntil && time - releasedAt >= s.jumpGrace)
                    Shift(Gear.Cruise, "slowed out of the wave", time, events);
            }

            if (Selected && !pointerOnOtter)
            {
                Heading = Flat(pointer - b.head, Heading);
                AimPoint = pointer;
            }
            if (Selected) aimUntil = time;
            AimBlend = Selected ? 1f : s.aimFade > 0f ? Mathf.Clamp01(1f - (time - aimUntil) / s.aimFade) : 0f;

            looked.Add((time, Heading));
            float then = time - s.headLead;
            int gone = 0;
            while (gone + 1 < looked.Count && looked[gone + 1].time <= then) gone++;
            looked.RemoveRange(0, gone);
            if (looked[0].time <= then) BodyHeading = looked[0].heading;

            bool turning = Vector3.Angle(Flat(b.forward, Heading), Heading) > s.parkDriftAngle;
            SpeedBL = Gear switch
            {
                Gear.Park => Selected && turning && !Braking ? s.parkDriftBL : 0f,
                Gear.Slow => s.slowSpeedBL,
                Gear.Cruise => s.cruiseSpeedBL,
                _ => Accelerating ? s.sprintSpeedBL : s.cruiseSpeedBL,
            };
        }

        private void Click(GearSettings s, Vector3 point, float time, in GearBody b, List<ControlEvent> events)
        {
            Vector3 to = Flat(point - b.head, Heading);
            Heading = to;
            AimPoint = point;
            aimUntil = time;
            if (Gear == Gear.Sprint) return;
            events.Add(new ControlEvent { kind = ControlEventKind.Kick, time = time, direction = to, strength = Gear == Gear.Park ? s.parkKick : s.swimKick });
        }

        private void Shift(Gear to, string reason, float time, List<ControlEvent> events)
        {
            if (to == Gear) return;
            var from = Gear;
            Gear = to;
            if (to != Gear.Sprint)
            {
                Dolphin = false;
                Accelerating = false;
            }
            events.Add(new ControlEvent { kind = ControlEventKind.Shift, time = time, from = from, to = to, reason = reason });
        }

        private static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-8f ? v.normalized : fallback;
        }
    }
}
