using System;
using System.Collections.Generic;
using System.Linq;
using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// In the dolphin wave (LimbStrokeAnimator.Fluke) the otter swims along a wave through the water rather than
    /// undulating where it is: a track rising and falling about the surface, still in the water, a stride
    /// (LimbRhythm's dolphinStrideBL) from crest to crest, and the fluke beats once each time the body swims over one.
    /// Each part of the body passes along the same track in turn. The chest is held on it, the body heaving to its
    /// height against the water's lift (Buoyancy); the spine's joints are pitched so each lies on it at its place along
    /// the body, and turned, seen from above, onto the way the body's centre came, so in a turn the track bends with
    /// it and what is behind the centre follows round its curve. An additive provider on the spine harmonizer, whose
    /// chain smoothing gives way while the wave is on, as do, well into it, the other providers' turns of the spine
    /// (the aim's and SpineKickSway's); the head
    /// (NeckLook) and the tail (TailRudder) ride the track too. The hips go over a crest at a set point of the fluke's
    /// beat. Diving, the track runs along the controls' way through the depth (DivePath), the dolphin wave, if it is
    /// on, rising and falling about it: in the duck dive the hips and hind feet go up over its crest as the chest
    /// plunges. Adds itself to the harmonizer's providers for this Play.
    /// </summary>
    [DefaultExecutionOrder(910)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class DolphinTrack : ExpSpinePoseProviderBase
    {
        [Tooltip("Found on this object when empty.")]
        public LimbStrokeAnimator animator;
        [Tooltip("Found in the scene when empty.")]
        public ExpShadowSpineHarmonizer harmonizer;
        [Tooltip("The track's middle height above the water's surface along the spine (m): below it, so the back breaks the surface over the crests and goes under in the troughs; and how far it rises and falls about that (m).")]
        public float middle = -0.035f;
        public float rise = 0.05f;
        [Tooltip("Where in the fluke's beat the hips go over a crest (cycles), which sets when the legs push on the wave.")]
        [Range(0f, 1f)] public float hipsCrest = 0.4f;
        [Tooltip("How stiffly the body is held to the track's height: the frequency (Hz) and damping (share of critical) it settles onto it with.")]
        public float holdFrequency = 4f;
        public float holdDamping = 1f;
        [Tooltip("The harmonizer's chain flexibility and damping half-life (s) while the wave is on: next to none, so each joint lies on the track rather than trailing the one ahead of it.")]
        [Range(0f, 1f)] public float waveFlexibility = 0f;
        [Range(0.01f, 1f)] public float waveDamping = 0.01f;

        private OtterLocomotionBrain brain;
        private DivePath dive;
        private Buoyancy buoyancy;
        private Rigidbody body;
        private Water water;
        // the spine's shadows, hips (0) to chest; each one's segment from the joint ahead of it at rest, in the shadows'
        // root space, as the harmonizer lays them; and how far behind the chest the hips are (m)
        private Transform[] shadows;
        private Vector3[] restSegment;
        private float hipsAlong;
        // each shadow's pitch onto the track about the body's side and turn about up onto the centre's way (deg), in
        // the shadows' root space
        private float[] pitch = new float[0], turn = new float[0];
        private Vector3 side = Vector3.right, up = Vector3.up;
        // the way the body's centre came, seen from above (oldest first), a point at least TrailSpacing apart: each
        // stretch it travelled laid along the heading it had then, so turning on the spot or sliding sideways the body
        // turns whole; where the centre is on it now, and where it really was last frame
        private readonly List<Vector3> trail = new List<Vector3>();
        private Vector3 head, lastCentre;
        private const float TrailSpacing = 0.01f;
        private const int TrailPoints = 120;
        private float restFlexibility, restDamping;
        // the other providers that turn the spine, and their weights
        private ExpSpinePoseProviderBase[] steering = new ExpSpinePoseProviderBase[0];
        private float[] steeringWeight = new float[0];
        private bool added;

        public override bool Additive => true;

        /// <summary>How far the body has gone over to swimming along the track (0..1): the dolphin wave's or the dive's.</summary>
        public float Weight => shadows != null && animator != null && animator.isActiveAndEnabled ? Mathf.Max(animator.Fluke, Diving) : 0f;

        /// <summary>The track's length from crest to crest (m).</summary>
        public float Stride => brain.rhythmSettings.dolphinStrideBL * Mathf.Max(0.1f, brain.Parameters.bodyLength);

        /// <summary>How high above the water's surface the track runs (m) where a point is along the body, now.</summary>
        public float Height(Vector3 point) => middle + Track(Vector3.Dot(point - shadows[shadows.Length - 1].position, Heading), Phase(Time.time));

        /// <summary>How far the track rises above the hips' line where a point is behind them (m), now, as far as the
        /// body has gone over to it: the hips lie along the track, so what trails straight back from them lies on it
        /// once lifted by this much.</summary>
        public float AboveHipsLine(Vector3 point)
        {
            float w = Weight;
            if (w <= 0f) return 0f;
            Vector3 chest = shadows[shadows.Length - 1].position, heading = Heading;
            float phase = Phase(Time.time), hips = Vector3.Dot(shadows[0].position - chest, heading), at = Vector3.Dot(point - chest, heading);
            return w * (Track(at, phase) - Track(hips, phase) - TrackSlope(hips, phase) * (at - hips));
        }

        private Vector3 Heading => Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;

        private float Diving => dive != null ? dive.Weight : 0f;

        // the wave's share of the track: all of it on the surface, as far as the fluke is in under water
        private float WaveShare
        {
            get
            {
                float w = Mathf.Max(animator.Fluke, Diving);
                return w > 1e-4f ? animator.Fluke / w : 1f;
            }
        }

        // the track's phase at the chest (cycles): the hips, behind it, on a crest at hipsCrest
        private float Phase(float time) => animator.FlukePhaseAt(time) - hipsCrest - hipsAlong / Stride;

        // how far above its middle the track runs at a place along the body (m ahead of the chest), at a phase: the
        // dive's way and the wave about it
        private float Track(float along, float phase) => (dive != null ? dive.Height(along) : 0f) + WaveShare * Wave(along, phase);
        private float TrackSlope(float along, float phase) => (dive != null ? dive.Slope(along) : 0f) + WaveShare * Slope(along, phase);

        private float Wave(float along, float phase) => rise * Mathf.Cos(2f * Mathf.PI * (phase + along / Stride));
        private float Slope(float along, float phase) => -rise * 2f * Mathf.PI / Stride * Mathf.Sin(2f * Mathf.PI * (phase + along / Stride));

        private void Awake()
        {
            if (animator == null) animator = GetComponent<LimbStrokeAnimator>();
            if (harmonizer == null)
                harmonizer = FindObjectsByType<ExpShadowSpineHarmonizer>(FindObjectsSortMode.None).FirstOrDefault(h => h.isActiveAndEnabled);
            body = GetComponent<Rigidbody>();
            buoyancy = GetComponent<Buoyancy>();
        }

        // after SpineDecouplerWithShadows has made the shadows in Awake
        private void Start()
        {
            brain = animator != null ? animator.brain : null;
            dive = brain != null && brain.controls != null ? brain.controls.DivePath : null;
            water = FindFirstObjectByType<Water>();
            var decoupler = harmonizer != null ? harmonizer.decoupler : null;
            if (brain == null || water == null || decoupler == null || decoupler.shadowNodes.Count < 2)
            {
                Debug.LogWarning($"[DolphinTrack] Needs a LimbStrokeAnimator and its brain on '{name}', a Water, and a spine harmonizer with its shadows; the dolphin wave stays off its track.", this);
                enabled = false;
                return;
            }
            shadows = decoupler.shadowNodes.ToArray();
            int n = shadows.Length;
            restSegment = new Vector3[n];
            // the harmonizer sets each shadow off the one ahead by its segment turned with that one, which it may have done already
            Vector3 hips = Vector3.zero;
            for (int i = n - 2; i >= 0; i--)
            {
                restSegment[i] = Quaternion.Inverse(shadows[i + 1].rotation) * (shadows[i].position - shadows[i + 1].position);
                hips += restSegment[i];
            }
            hipsAlong = Vector3.Dot(hips, shadows[0].parent.InverseTransformDirection(Heading));
            pitch = new float[n];
            turn = new float[n];
            restFlexibility = harmonizer.flexibility;
            restDamping = harmonizer.dampingHalfLife;
            steering = harmonizer.providers.OfType<ExpShadowAimProvider>().Cast<ExpSpinePoseProviderBase>()
                .Append(GetComponent<SpineKickSway>()).Where(p => p != null).Distinct().ToArray();
            steeringWeight = steering.Select(p => p.globalWeight).ToArray();
            if (!harmonizer.providers.Contains(this))
            {
                harmonizer.providers.Add(this);
                added = true;
            }
        }

        private void OnDisable()
        {
            if (shadows == null) return;
            harmonizer.flexibility = restFlexibility;
            harmonizer.dampingHalfLife = restDamping;
            for (int s = 0; s < steering.Length; s++)
                if (steering[s] != null) steering[s].globalWeight = steeringWeight[s];
            Array.Clear(pitch, 0, pitch.Length);
            Array.Clear(turn, 0, turn.Length);
            trail.Clear();
        }

        private void OnDestroy()
        {
            if (added && harmonizer != null) harmonizer.providers.Remove(this);
        }

        // the spine onto the track, before the harmonizer reads the providers in its LateUpdate
        private void Update()
        {
            if (shadows == null) return;
            float w = Weight;
            harmonizer.flexibility = Mathf.Lerp(restFlexibility, waveFlexibility, w);
            harmonizer.dampingHalfLife = Mathf.Lerp(restDamping, waveDamping, w);
            float giveWay = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1f, w));
            for (int s = 0; s < steering.Length; s++)
                if (steering[s] != null) steering[s].globalWeight = steeringWeight[s] * (1f - giveWay);
            int n = shadows.Length;
            Vector3 centre = Flat(transform.TransformPoint(body.centerOfMass));
            Record(centre);
            centre = head;
            if (w <= 1e-3f)
            {
                Array.Clear(pitch, 0, n);
                Array.Clear(turn, 0, n);
                return;
            }
            Transform root = shadows[0].parent;
            up = root.InverseTransformDirection(Vector3.up);
            Vector3 ahead = root.InverseTransformDirection(Heading);
            side = Vector3.Cross(up, ahead).normalized;
            float phase = Phase(Time.time), chest = Track(0f, phase);
            // how far ahead of the chest the centre is along the body (m; behind it, so negative)
            float centreAlong = Vector3.Dot(lastCentre - Flat(shadows[n - 1].position), Heading);
            // from the chest back, each joint where the track runs at its place along the body, as far off it as at
            // rest, its segment from the joint ahead keeping its length: the joint's shadow ahead turns the segment there.
            // Found by halving: the track can drop away as steeply as a plunge, past where it could be stepped onto
            Vector3 restAt = Vector3.zero;
            float along = 0f, height = 0f;
            for (int i = n - 2; i >= 0; i--)
            {
                Vector3 segment = restSegment[i];
                restAt += segment;
                float across = Vector3.Dot(segment, side);
                float length = Mathf.Sqrt(Mathf.Max(0f, segment.sqrMagnitude - across * across));
                float restHeight = Vector3.Dot(restAt, up);
                float near = along, far = along - length, to = along, y = height;
                for (int k = 0; k < 14; k++)
                {
                    to = 0.5f * (near + far);
                    y = restHeight + Track(to, phase) - chest;
                    if ((along - to) * (along - to) + (y - height) * (y - height) < length * length) near = to;
                    else far = to;
                }
                Vector3 laid = ahead * (to - along) + up * (y - height) + side * across;
                pitch[i + 1] = w * Vector3.SignedAngle(segment, laid, side);
                // and seen from above, from where the centre was at the joint ahead's place behind it to where it was at this one's
                Vector3 back = Behind(centre, centreAlong - to) - Behind(centre, centreAlong - along), restBack = Flat(root.TransformDirection(segment));
                turn[i + 1] = back.sqrMagnitude > 1e-10f && restBack.sqrMagnitude > 1e-10f ? w * Vector3.SignedAngle(restBack, back, Vector3.up) : 0f;
                along = to;
                height = y;
            }
            // the hips' own bone, which carries the tail and legs, along the track there
            pitch[0] = w * Vector3.SignedAngle(-ahead, -ahead - up * TrackSlope(along, phase), side);
            Vector3 hipsWay = Behind(centre, centreAlong - along + 2f * TrailSpacing) - Behind(centre, centreAlong - along - 2f * TrailSpacing);
            turn[0] = hipsWay.sqrMagnitude > 1e-10f ? w * Vector3.SignedAngle(-Heading, hipsWay, Vector3.up) : 0f;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // the centre's way on from where it really is, a point each TrailSpacing at least, the oldest let go; afresh
        // after a jump
        private void Record(Vector3 at)
        {
            float step = Vector3.Distance(at, lastCentre);
            lastCentre = at;
            if (trail.Count == 0 || step > 0.5f)
            {
                trail.Clear();
                head = at;
                trail.Add(head);
                return;
            }
            head += Heading * step;
            if ((head - trail[trail.Count - 1]).sqrMagnitude >= TrailSpacing * TrailSpacing) trail.Add(head);
            if (trail.Count > TrailPoints) trail.RemoveAt(0);
        }

        // where the centre was, seen from above, a distance back along its way from where it is now; ahead of it along
        // the heading, and straight back past the oldest point
        private Vector3 Behind(Vector3 at, float distance)
        {
            if (distance < 0f) return at - Heading * distance;
            Vector3 p = at;
            for (int j = trail.Count - 1; j >= 0 && distance > 0f; j--)
            {
                float d = Vector3.Distance(p, trail[j]);
                if (d >= distance) return Vector3.Lerp(p, trail[j], distance / d);
                distance -= d;
                p = trail[j];
            }
            return p - Heading * Mathf.Max(0f, distance);
        }

        // the body held to the track's height at the chest, in the water's place, after Buoyancy has pushed for the step
        private void FixedUpdate()
        {
            float w = Weight;
            if (w <= 1e-3f || buoyancy == null || !buoyancy.isActiveAndEnabled) return;
            float t = Time.time, phase = Phase(t), share = WaveShare;
            float beat = Mathf.Repeat(animator.FlukePhaseAt(t + 0.01f) - animator.FlukePhaseAt(t), 1f) / 0.01f;
            float p = 2f * Mathf.PI * phase, omega = 2f * Mathf.PI * beat;
            float way = dive != null ? dive.Height(0f) : 0f, climb = dive != null ? dive.ClimbRate : 0f;
            float goal = water.SurfaceY + middle + way + share * rise * Mathf.Cos(p) - (shadows[shadows.Length - 1].position.y - transform.position.y);
            float goalSpeed = climb - share * rise * omega * Mathf.Sin(p), goalAccel = -share * rise * omega * omega * Mathf.Cos(p);
            float k = 2f * Mathf.PI * holdFrequency;
            float accel = goalAccel + k * k * (goal - body.position.y) + 2f * holdDamping * k * (goalSpeed - body.velocity.y);
            float hold = body.mass * (accel - Physics.gravity.y) - buoyancy.Push;
            body.AddForce(Vector3.up * (w * hold), ForceMode.Force);
        }

        public override bool Evaluate(int index, SpineChainDefinition spine, Transform jointOrShadow, Quaternion bindLocalRot, out Quaternion targetLocalRot, out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;
            if (index < 0 || index >= pitch.Length || (Mathf.Abs(pitch[index]) < 0.01f && Mathf.Abs(turn[index]) < 0.01f)) return false;
            targetLocalRot = Quaternion.AngleAxis(turn[index], up) * Quaternion.AngleAxis(pitch[index], side) * bindLocalRot;
            weight = 1f;
            return true;
        }
    }
}
