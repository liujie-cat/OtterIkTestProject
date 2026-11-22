using System.Linq;
using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Picked up and parked at the surface (GearMachine.Upright), the otter stands up in the water to look about
    /// (periscoping): the spine pitches up nose first until it stands nearly upright, its belly toward where it faces,
    /// and the hind feet's treading (HindTread) holds the chest up out of the water against the water's lift
    /// (Buoyancy), the head and chest out over the water. It turns there after the pointer and drifts on only a little, the water dragging
    /// hard on the trunk stood broadside to its way. An additive provider on the spine harmonizer, every joint
    /// pitched alike about the body's side so the spine stands straight, the chain's smoothing giving way meanwhile
    /// (DolphinTrack); the harmonizer hangs the spine from the chest, so the chest stays where it is and the hips swing
    /// down under it. The head looks out level (NeckLook) and the forepaws tuck in on the chest (LimbStrokeAnimator).
    /// Adds itself to the harmonizer's providers for this Play.
    /// </summary>
    [DefaultExecutionOrder(905)]
    [DisallowMultipleComponent]
    public class Periscope : ExpSpinePoseProviderBase
    {
        [Tooltip("Found on this object when empty.")]
        public OtterControls controls;
        [Tooltip("Found in the scene when empty.")]
        public ExpShadowSpineHarmonizer harmonizer;
        [Tooltip("How far up from lying level the spine stands (deg): short of straight up, where the harmonizer's angles fold over.")]
        [Range(0f, 85f)] public float pitch = 80f;
        [Tooltip("How long standing up and lying back down take (s).")]
        public float standTime = 0.6f;
        public float lieTime = 0.5f;
        [Tooltip("How high the chest joint stands out of the water upright (m); afloat on its own it rides at about the surface.")]
        public float standHeight = 0.045f;
        [Tooltip("How stiffly the treading holds it there: the frequency (Hz) and damping (share of critical) it settles onto it with.")]
        public float holdFrequency = 1.5f;
        public float holdDamping = 1f;
        [Tooltip("The water's drag on the trunk stood broadside to its way, on top of the body's own (WaterDrag), per (m/s)² (N): half the water's density times a cylinder's drag coefficient of about 1 times the trunk's side under water, about 0.035 m².")]
        public float uprightDrag = 18f;

        public override bool Additive => true;

        /// <summary>How far it has stood up (0 lying .. 1 upright).</summary>
        public float Weight { get; private set; }
        /// <summary>How far up from lying level it has stood the spine (deg).</summary>
        public float Raised => pitch * Weight;
        /// <summary>Standing up or stood, rather than lying back down or lying.</summary>
        public bool Up { get; private set; }
        /// <summary>The treading's push up on the body last physics step, holding the chest at standHeight (N).</summary>
        public float Lift { get; private set; }

        private float progress;
        private bool ready, added;
        private Rigidbody body;
        private Buoyancy buoyancy;
        private DolphinTrack track;
        private Water water;
        private Transform chest;

        private void Awake()
        {
            if (controls == null) controls = GetComponent<OtterControls>();
            if (harmonizer == null)
                harmonizer = FindObjectsByType<ExpShadowSpineHarmonizer>(FindObjectsSortMode.None).FirstOrDefault(h => h.isActiveAndEnabled);
        }

        // after SpineDecouplerWithShadows has made the shadows in Awake, and the components added after this one have woken
        private void Start()
        {
            var decoupler = harmonizer != null ? harmonizer.decoupler : null;
            if (controls == null || decoupler == null || decoupler.shadowNodes.Count < 2)
            {
                Debug.LogWarning($"[Periscope] Needs OtterControls on '{name}' and a spine harmonizer with its shadows; the otter does not stand up in the water.", this);
                enabled = false;
                return;
            }
            ready = true;
            body = GetComponent<Rigidbody>();
            buoyancy = GetComponent<Buoyancy>();
            track = GetComponent<DolphinTrack>();
            water = FindFirstObjectByType<Water>();
            chest = OtterRig.FindActiveBone(transform, OtterRig.Chest);
            if (!harmonizer.providers.Contains(this))
            {
                harmonizer.providers.Add(this);
                added = true;
            }
        }

        private void OnDisable()
        {
            progress = 0f;
            Weight = 0f;
        }

        private void OnDestroy()
        {
            if (added && harmonizer != null) harmonizer.providers.Remove(this);
        }

        // before the harmonizer reads the providers in its LateUpdate
        private void Update()
        {
            if (!ready) return;
            Up = controls.isActiveAndEnabled && controls.Gears.Upright;
            progress = Mathf.MoveTowards(progress, Up ? 1f : 0f, Time.deltaTime / Mathf.Max(1e-3f, Up ? standTime : lieTime));
            Weight = Mathf.SmoothStep(0f, 1f, progress);
        }

        // the trunk stood broadside to its way through the water; and the chest held up at standHeight in the water's
        // place, after Buoyancy has pushed for the step, the feet pushing it up, never down. Going under, the dive's
        // track takes the body over from it (DolphinTrack)
        private void FixedUpdate()
        {
            Lift = 0f;
            if (Weight <= 1e-3f || body == null) return;
            Vector3 drift = Vector3.ProjectOnPlane(body.velocity, Vector3.up);
            body.AddForce(-Weight * uprightDrag * drift.magnitude * drift, ForceMode.Force);
            float w = Weight * (1f - (track != null && track.isActiveAndEnabled ? track.Weight : 0f));
            if (w <= 1e-3f || buoyancy == null || !buoyancy.isActiveAndEnabled || water == null || chest == null) return;
            float goal = water.SurfaceY + standHeight - (chest.position.y - body.position.y);
            float k = 2f * Mathf.PI * holdFrequency;
            float accel = k * k * (goal - body.position.y) - 2f * holdDamping * k * body.velocity.y;
            float hold = Mathf.Max(0f, body.mass * (accel - Physics.gravity.y) - buoyancy.Push);
            Lift = w * hold;
            body.AddForce(Vector3.up * Lift, ForceMode.Force);
        }

        // the shadows' root space has the spine along +Z and the body's side along +X, as the harmonizer rolls about +Z;
        // a turn about +X alone keeps the roll it reads off the angles at nothing
        public override bool Evaluate(int index, SpineChainDefinition spine, Transform jointOrShadow, Quaternion bindLocalRot, out Quaternion targetLocalRot, out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;
            if (Weight <= 1e-4f) return false;
            targetLocalRot = Quaternion.AngleAxis(-Raised, Vector3.right) * bindLocalRot;
            weight = 1f;
            return true;
        }
    }
}
