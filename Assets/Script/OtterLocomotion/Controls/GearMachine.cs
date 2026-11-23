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
        [Tooltip("In the wave, a turn of more than wideTurn (deg) between the heading and where the body faces is swum on the belly with all four limbs, the feet kicking and the outside forepaw paddling it round, until the body is within backInWave (deg) of the heading and goes back into the wave.")]
        public float wideTurn = 60f;
        public float backInWave = 20f;

        [Header("Dive")]
        [Tooltip("Speed (body lengths/s) it swims at least while it goes under and while it comes back up, so it swims the way down or up however slow its gear.")]
        public float diveSpeedBL = 1f;

        [Header("Standing")]
        [Tooltip("Picked up, shifted down to parked or back at the surface parked, it waits this long (s) before standing up in the water, lying as it lay with only its head turning after the pointer: a press of the go key meanwhile sends it off on its back without standing up first, and of the back key lets it go.")]
        public float standDelay = 0.3f;

        [Header("Aim")]
        [Tooltip("How long (s) the head and spine keep aiming where the guide point was, fading out once it is let go.")]
        public float aimFade = 0.6f;
        [Tooltip("The head turns first: the body swims toward where the head looked this long (s) before.")]
        public float headLead = 0.12f;
    }

    public enum ControlEventKind { Kick, Jump, Shift, Select, Deselect, Dive, Surface }

    public struct ControlEvent
    {
        public ControlEventKind kind;
        public float time;
        public float strength;
        public Gear from, to;
        public string reason;

        public override string ToString() => kind switch
        {
            ControlEventKind.Kick => $"Kick {strength:F1}",
            ControlEventKind.Jump => "Jump",
            ControlEventKind.Select => "Select",
            ControlEventKind.Deselect => "Deselect",
            ControlEventKind.Dive => "Dive",
            ControlEventKind.Surface => "Surface",
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
        /// <summary>On its way through the depth (DivePath): under the water, or going under or coming up.</summary>
        public bool underwater;
        /// <summary>Going under or coming back up, which it swims however slow its gear.</summary>
        public bool divingOrSurfacing;
    }

    /// <summary>
    /// The gears the go and back keys climb and come down (行动操作). Not picked up, the otter lies parked on its back:
    /// the pointer does not guide it and it does not shift. The go key picks it up, then each press shifts up, from
    /// parked to slow and from slow to cruising. Picked up and parked at the surface it stands up in the water to look
    /// about (Upright, Periscope), on its belly side, once it has stayed so standDelay, its head alone turning after
    /// the pointer meanwhile: picked up and sent off at once, it swims off on its back without standing up first.
    /// Swimming slowly it lies on its back, cruising and sprinting it swims on its belly. Pressed cruising and held, it sprints along the heading: kicking at first, then in the
    /// dolphin wave, out of which it swims a wide turn on its belly with all four limbs, going back into the wave once
    /// round; let go before the wave it cruises on, let go in the wave it slows of itself, keeping the wave while
    /// still fast and for at least jumpGrace, and a press in the wave jumps (held on, it sprints on). Each press of the
    /// back key shifts down, and parked lets it go; held, it brakes to parked and lets it go, so let go it is always
    /// parked on its back. Picked up, the pointer is its guide point: the head looks at it and the body turns after the
    /// head, headLead behind; on the otter, inside the ring, it gives no guide, and the otter turns on to the heading
    /// it last gave and keeps it, looking on along it. A click of the mouse does nothing to it, in any gear or let go. The dive key sends it
    /// under (a duck dive); held, under water or on the way, it swims deeper, and let go it keeps its depth; a tap of
    /// it under water sends it back up. Under water, and until it is back at the surface, it swims on its belly,
    /// cruising and sprinting as on the surface, and does not jump. There it has no slow gear: sent under swimming
    /// slowly, it rolls onto its belly and cruises on once on its way down, and the go and back keys pass over slow,
    /// between parked and cruising. Presses while in the air are spent.
    /// </summary>
    public sealed class GearMachine
    {
        public Gear Gear { get; private set; }
        /// <summary>Sprinting in the dolphin wave, rather than still kicking up to it.</summary>
        public bool Dolphin { get; private set; }
        /// <summary>Sprinting in the wave, out of it for a wide turn, which it swims on the belly with all four limbs.</summary>
        public bool WideTurn { get; private set; }
        /// <summary>The go key, held, is driving the sprint on.</summary>
        public bool Accelerating { get; private set; }
        public bool Braking { get; private set; }
        /// <summary>Picked up by the go key, guided by the pointer until the back key lets it go.</summary>
        public bool Selected { get; private set; }
        /// <summary>The pointer guides it this step: picked up, the pointer off the otter, outside the ring.</summary>
        public bool Guided { get; private set; }
        /// <summary>Where the head looks to go (world, unit, on the water): from the head toward the guide point.</summary>
        public Vector3 Heading { get; private set; } = Vector3.forward;
        /// <summary>Where the body is headed: the head's heading of headLead ago; waiting to stand up, where it faces.</summary>
        public Vector3 BodyHeading { get; private set; } = Vector3.forward;
        /// <summary>Where the head and spine aim: the guide point, or with the pointer on the otter along the heading, as far from the head as the guide point last was; and how much (1 while selected, fading after it is let go).</summary>
        public Vector3 AimPoint { get; private set; }
        public float AimBlend { get; private set; }
        /// <summary>Speed asked for (body lengths/s).</summary>
        public float SpeedBL { get; private set; }
        /// <summary>Sent under by the dive key, until a tap of it under water sends it back up.</summary>
        public bool Diving { get; private set; }
        /// <summary>The dive key, held, takes it deeper.</summary>
        public bool Descending { get; private set; }
        /// <summary>Diving, or still under water on its way back up.</summary>
        public bool Underwater => Diving || under;

        /// <summary>Picked up and parked at the surface for standDelay: it stands up in the water on its belly side, turning after the pointer and drifting on only a little.</summary>
        public bool Upright => standing && StandsParked;

        public bool JumpEligible => Gait == Gait.Dolphin && !Underwater;
        /// <summary>Waiting to stand up, it lies as it lay before.</summary>
        public Posture Posture => Underwater || Upright ? Posture.Prone : StandsParked ? lay : PostureOf(Gear);
        public static Posture PostureOf(Gear gear) => gear <= Gear.Slow ? Posture.Supine : Posture.Prone;
        public Gait Gait => Gear switch
        {
            Gear.Park => Gait.Rest,
            Gear.Slow => Underwater ? Gait.ProneTogether : Gait.SupineAlternate,
            Gear.Sprint when Dolphin && !WideTurn => Gait.Dolphin,
            _ => Gait.ProneTogether,
        };

        private Gear goGear;
        private float sprintSince, releasedAt, aimUntil, aimReach;
        // under water by the body's way through the depth; and the dive key's press under water, a tap until it is held
        private bool under, diveTap;
        // picked up and parked at the surface since then, standing up once standDelay has gone by; lying meanwhile as it
        // lay the step before
        private float parkedSince;
        private bool standing;
        private Posture lastPosture, lay;
        private readonly List<(float time, Vector3 heading)> looked = new List<(float, Vector3)>();

        private bool StandsParked => Selected && Gear == Gear.Park && !Underwater;

        /// <param name="initial">Above parked, it starts picked up.</param>
        public void Reset(in GearBody body, Gear initial = Gear.Park)
        {
            Gear = initial;
            Dolphin = WideTurn = Accelerating = Braking = false;
            Diving = Descending = under = diveTap = standing = false;
            Selected = initial > Gear.Park;
            Guided = false;
            parkedSince = float.PositiveInfinity;
            lastPosture = lay = PostureOf(initial);
            Heading = BodyHeading = Flat(body.forward, Vector3.forward);
            looked.Clear();
            aimReach = 1f;
            AimPoint = body.head + Heading * aimReach;
            AimBlend = 0f;
            SpeedBL = 0f;
            aimUntil = float.NegativeInfinity;
        }

        /// <param name="pointer">The pointer on the water now: selected, the guide point, once it is off the otter.</param>
        public void Step(GearSettings s, List<Gesture> gestures, float time, Vector3 pointer, bool pointerOnOtter, in GearBody b, List<ControlEvent> events)
        {
            under = b.underwater;
            foreach (var g in gestures)
            {
                switch (g.kind)
                {
                    case GestureKind.GoPress:
                        // only a press that came cruising or sprinting goes on to sprint when held
                        goGear = Selected ? Gear : Gear.Park;
                        if (b.airborne) break;
                        if (!Selected)
                        {
                            Selected = true;
                            events.Add(new ControlEvent { kind = ControlEventKind.Select, time = time });
                        }
                        else if (JumpEligible) events.Add(new ControlEvent { kind = ControlEventKind.Jump, time = time });
                        else if (Gear < Gear.Cruise) Shift(Underwater ? Gear.Cruise : Gear + 1, "go", time, events);
                        break;
                    case GestureKind.GoHoldBegin:
                        if (b.airborne || goGear < Gear.Cruise) break;
                        Accelerating = true;
                        if (Gear != Gear.Sprint)
                        {
                            sprintSince = time;
                            Shift(Gear.Sprint, "go held", time, events);
                        }
                        break;
                    case GestureKind.GoRelease:
                        if (!Accelerating) break;
                        Accelerating = false;
                        releasedAt = time;
                        if (!Dolphin) Shift(Gear.Cruise, "let go before the wave", time, events);
                        break;
                    case GestureKind.BackPress:
                        Accelerating = false;
                        if (Gear > Gear.Park) Shift(Underwater && Gear == Gear.Cruise ? Gear.Park : Gear - 1, "back", time, events);
                        else Deselect(time, events);
                        break;
                    case GestureKind.BackHoldBegin:
                        Braking = true;
                        Shift(Gear.Park, "brake", time, events);
                        Deselect(time, events);
                        break;
                    case GestureKind.BackRelease:
                        Braking = false;
                        break;
                    case GestureKind.DivePress:
                        if (b.airborne) break;
                        diveTap = Diving;
                        if (!Diving) Dive(true, time, events);
                        break;
                    case GestureKind.DiveHoldBegin:
                        diveTap = false;
                        Descending = Diving;
                        break;
                    case GestureKind.DiveRelease:
                        Descending = false;
                        if (diveTap) Dive(false, time, events);
                        diveTap = false;
                        break;
                }
            }
            if (under && Gear == Gear.Slow) Shift(Gear.Cruise, "under water", time, events);

            if (!StandsParked) parkedSince = float.PositiveInfinity;
            else if (float.IsPositiveInfinity(parkedSince))
            {
                parkedSince = time;
                lay = lastPosture;
            }
            standing = time - parkedSince >= s.standDelay;
            lastPosture = Posture;

            if (Gear == Gear.Sprint && !b.airborne)
            {
                float toSprint = Mathf.InverseLerp(s.cruiseSpeedBL, s.sprintSpeedBL, b.speedBL);
                if (Accelerating && !Dolphin && (toSprint >= s.dolphinAt || time - sprintSince >= s.dolphinAfter))
                    Dolphin = true;
                else if (!Accelerating && Dolphin && toSprint < s.dolphinUntil && time - releasedAt >= s.jumpGrace)
                    Shift(Gear.Cruise, "slowed out of the wave", time, events);
            }

            Guided = Selected && !pointerOnOtter;
            if (Guided)
            {
                Vector3 guide = pointer - b.head;
                guide.y = 0f;
                aimReach = guide.magnitude;
                Heading = Flat(guide, Heading);
                AimPoint = pointer;
            }
            // the pointer on the otter is no input: the head and spine look on along the heading it last gave, which
            // goes with the body, rather than at the point by the ring it left, which the body would swing round
            else if (Selected) AimPoint = b.head + Heading * aimReach;
            if (Selected) aimUntil = time;
            AimBlend = Selected ? 1f : s.aimFade > 0f ? Mathf.Clamp01(1f - (time - aimUntil) / s.aimFade) : 0f;

            looked.Add((time, Heading));
            float then = time - s.headLead;
            int gone = 0;
            while (gone + 1 < looked.Count && looked[gone + 1].time <= then) gone++;
            looked.RemoveRange(0, gone);
            if (looked[0].time <= then) BodyHeading = looked[0].heading;
            bool waiting = StandsParked && !standing;
            if (waiting) BodyHeading = Flat(b.forward, BodyHeading);

            float off = Vector3.Angle(Flat(b.forward, Heading), Heading);
            WideTurn = Gear == Gear.Sprint && Dolphin && !b.airborne && off > (WideTurn ? s.backInWave : s.wideTurn);
            bool turning = off > s.parkDriftAngle;
            SpeedBL = Gear switch
            {
                Gear.Park => Selected && turning && !Braking && !waiting ? s.parkDriftBL : 0f,
                Gear.Slow => s.slowSpeedBL,
                Gear.Cruise => s.cruiseSpeedBL,
                _ => Accelerating ? s.sprintSpeedBL : s.cruiseSpeedBL,
            };
            if (b.divingOrSurfacing || Descending) SpeedBL = Mathf.Max(SpeedBL, s.diveSpeedBL);
        }

        private void Dive(bool on, float time, List<ControlEvent> events)
        {
            Diving = on;
            Descending = false;
            events.Add(new ControlEvent { kind = on ? ControlEventKind.Dive : ControlEventKind.Surface, time = time });
        }

        private void Deselect(float time, List<ControlEvent> events)
        {
            if (!Selected) return;
            Selected = false;
            aimUntil = time;
            events.Add(new ControlEvent { kind = ControlEventKind.Deselect, time = time });
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
