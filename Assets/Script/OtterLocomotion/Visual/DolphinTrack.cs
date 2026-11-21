using System;
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
    /// the body, an additive provider on the spine harmonizer, whose chain smoothing gives way while the wave is on;
    /// the head rides it too (NeckLook). The hips go over a crest at a set point of the fluke's beat. Adds itself to
    /// the harmonizer's providers for this Play.
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
        private Buoyancy buoyancy;
        private Rigidbody body;
        private Water water;
        // the spine's shadows, hips (0) to chest; each one's segment from the joint ahead of it at rest, in the shadows'
        // root space, as the harmonizer lays them; and how far behind the chest the hips are (m)
        private Transform[] shadows;
        private Vector3[] restSegment;
        private float hipsAlong;
        // each shadow's pitch onto the track (deg) about the body's side, in the shadows' root space
        private float[] pitch = new float[0];
        private Vector3 side = Vector3.right;
        private float restFlexibility, restDamping;
        private bool added;

        public override bool Additive => true;

        /// <summary>How far the body has gone over to swimming along the track (0..1).</summary>
        public float Weight => shadows != null && animator != null && animator.isActiveAndEnabled ? animator.Fluke : 0f;

        /// <summary>The track's length from crest to crest (m).</summary>
        public float Stride => brain.rhythmSettings.dolphinStrideBL * Mathf.Max(0.1f, brain.Parameters.bodyLength);

        /// <summary>How high above the water's surface the track runs (m) where a point is along the body, now.</summary>
        public float Height(Vector3 point) => middle + Wave(Vector3.Dot(point - shadows[shadows.Length - 1].position, Heading), Phase(Time.time));

        private Vector3 Heading => Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;

        // the track's phase at the chest (cycles): the hips, behind it, on a crest at hipsCrest
        private float Phase(float time) => animator.FlukePhaseAt(time) - hipsCrest - hipsAlong / Stride;

        // how far above its middle the track runs at a place along the body (m ahead of the chest), at a phase
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
            restFlexibility = harmonizer.flexibility;
            restDamping = harmonizer.dampingHalfLife;
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
            Array.Clear(pitch, 0, pitch.Length);
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
            int n = shadows.Length;
            if (w <= 1e-3f)
            {
                Array.Clear(pitch, 0, n);
                return;
            }
            Transform root = shadows[0].parent;
            Vector3 up = root.InverseTransformDirection(Vector3.up), ahead = root.InverseTransformDirection(Heading);
            side = Vector3.Cross(up, ahead).normalized;
            float phase = Phase(Time.time), chest = Wave(0f, phase);
            // from the chest back, each joint where the track runs at its place along the body, as far off it as at
            // rest, its segment from the joint ahead keeping its length: the joint's shadow ahead turns the segment there
            Vector3 restAt = Vector3.zero;
            float along = 0f, height = 0f;
            for (int i = n - 2; i >= 0; i--)
            {
                Vector3 segment = restSegment[i];
                restAt += segment;
                float across = Vector3.Dot(segment, side);
                float length = Mathf.Sqrt(Mathf.Max(0f, segment.sqrMagnitude - across * across));
                float restHeight = Vector3.Dot(restAt, up);
                float to = along + Vector3.Dot(segment, ahead), y = height;
                for (int k = 0; k < 2; k++)
                {
                    y = restHeight + Wave(to, phase) - chest;
                    to = along - Mathf.Sqrt(Mathf.Max(0f, length * length - (y - height) * (y - height)));
                }
                y = restHeight + Wave(to, phase) - chest;
                Vector3 laid = ahead * (to - along) + up * (y - height) + side * across;
                pitch[i + 1] = w * Vector3.SignedAngle(segment, laid, side);
                along = to;
                height = y;
            }
            // the hips' own bone, which carries the tail and legs, along the track there
            pitch[0] = w * Vector3.SignedAngle(-ahead, -ahead - up * Slope(along, phase), side);
        }

        // the body held to the track's height at the chest, in the water's place, after Buoyancy has pushed for the step
        private void FixedUpdate()
        {
            float w = Weight;
            if (w <= 1e-3f || buoyancy == null || !buoyancy.isActiveAndEnabled) return;
            float t = Time.time, phase = Phase(t);
            float beat = Mathf.Repeat(animator.FlukePhaseAt(t + 0.01f) - animator.FlukePhaseAt(t), 1f) / 0.01f;
            float p = 2f * Mathf.PI * phase, omega = 2f * Mathf.PI * beat;
            float goal = water.SurfaceY + middle + rise * Mathf.Cos(p) - (shadows[shadows.Length - 1].position.y - transform.position.y);
            float goalSpeed = -rise * omega * Mathf.Sin(p), goalAccel = -rise * omega * omega * Mathf.Cos(p);
            float k = 2f * Mathf.PI * holdFrequency;
            float accel = goalAccel + k * k * (goal - body.position.y) + 2f * holdDamping * k * (goalSpeed - body.velocity.y);
            float hold = body.mass * (accel - Physics.gravity.y) - buoyancy.Push;
            body.AddForce(Vector3.up * (w * hold), ForceMode.Force);
        }

        public override bool Evaluate(int index, SpineChainDefinition spine, Transform jointOrShadow, Quaternion bindLocalRot, out Quaternion targetLocalRot, out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;
            if (index < 0 || index >= pitch.Length || Mathf.Abs(pitch[index]) < 0.01f) return false;
            targetLocalRot = Quaternion.AngleAxis(pitch[index], side) * bindLocalRot;
            weight = 1f;
            return true;
        }
    }
}
