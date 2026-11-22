using System;
using System.Linq;
using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Puts the hind legs' strokes on the spine, so the body moves with the legs. Kicking, each push the rhythm starts
    /// sends a wave packet along the spine (the harmonizer's ExpSpineKickWavePacketProvider), sized by the push and
    /// lasting until the next one, so the body undulates in time with the feet. On the back the packets fade out: lying
    /// on its back the otter paddles with its feet and keeps its trunk straight, and as a packet bends the spine one way
    /// only, pushes in quick succession would hold it bent into a bow. The fluke's beats in the dolphin wave send none,
    /// however far the hind legs have gone over to it (LimbStrokeAnimator): the body swims along the wave instead
    /// (DolphinTrack), and a packet as long as a beat would fold the hips up out of the water; nor does a kick while
    /// the body is still well on the track, leaving the wave or under water, where it lies straight along its way.
    /// Going under from the surface, one packet arches the back as the way lunges up over its crest (DivePath), the
    /// hips and hind feet coming up out of the water, whether or not a push comes in time to send one.
    /// </summary>
    [DefaultExecutionOrder(310)]
    [DisallowMultipleComponent]
    public class SpineKickWave : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public LimbStrokeAnimator animator;
        [Tooltip("Found in the scene when empty.")]
        public ExpShadowSpineHarmonizer harmonizer;
        [Header("Kicking: a wave packet with each push")]
        public bool pulseKicks = true;
        [Tooltip("Packet strength (share of the packet provider's full amplitude) at the lightest push and at full drive.")]
        [Range(0f, 1f)] public float lightPulse = 0.6f;
        [Range(0f, 1f)] public float fullPulse = 1f;
        [Tooltip("Packet length as a share of the time until the next push (1 = each packet runs into the next).")]
        public float pulseLength = 0.8f;
        [Tooltip("Packet strength on the back, as a share of that on the belly.")]
        [Range(0f, 1f)] public float supinePulse = 0f;
        [Header("Fluke")]
        [Tooltip("Leaving the dolphin wave or coming up, kicks send packets again once the body is this far off the track (DolphinTrack.Weight).")]
        [Range(0f, 1f)] public float pulsesOff = 0.5f;
        [Header("Duck dive")]
        [Tooltip("How long the packet that arches the back over the dive's crest lasts (s); it goes at full strength.")]
        public float archLength = 0.4f;

        private ExpSpineKickWavePacketProvider packet;
        private OtterLocomotionBrain brain;
        private DolphinTrack track;
        private bool started, wasUnder;

        /// <summary>A wave packet has been sent down the spine.</summary>
        public event Action Pulsed;

        private void Awake()
        {
            if (animator == null) animator = GetComponent<LimbStrokeAnimator>();
            if (harmonizer == null)
                harmonizer = FindObjectsByType<ExpShadowSpineHarmonizer>(FindObjectsSortMode.None).FirstOrDefault(h => h.isActiveAndEnabled);
        }

        private void Start()
        {
            if (harmonizer == null) return;
            packet = harmonizer.providers.OfType<ExpSpineKickWavePacketProvider>().FirstOrDefault(p => p != null);
            track = GetComponent<DolphinTrack>();
            started = true;
            Listen(true);
        }

        private void OnEnable()
        {
            if (started) Listen(true);
        }

        private void Listen(bool on)
        {
            var b = animator != null ? animator.brain : null;
            if (brain != null) brain.Rhythm.PowerStarted -= OnPush;
            brain = on ? b : null;
            if (brain != null) brain.Rhythm.PowerStarted += OnPush;
        }

        // A hind push starts: a packet down the spine that lasts until the next push, of either foot when they take turns
        private void OnPush(LimbId limb, LimbStroke st)
        {
            if (!pulseKicks || packet == null || OtterRig.IsFore(limb) || st.parking || st.drive <= 0f) return;
            if (animator == null || !animator.isActiveAndEnabled || brain.Intent.gait == Gait.Dolphin) return;
            if ((track != null && track.isActiveAndEnabled ? track.Weight : animator.Fluke) >= pulsesOff) return;
            float strength = Mathf.Lerp(lightPulse, fullPulse, st.drive) * Mathf.Lerp(1f, supinePulse, brain.Posture.SupineFraction);
            if (strength <= 1e-3f) return;
            float interval = (brain.Rhythm.HindTogether ? 1f : 0.5f) / Mathf.Max(0.1f, st.rate);
            packet.TriggerKick(strength, Mathf.Clamp(interval * pulseLength, 0.15f, 1.2f));
            Pulsed?.Invoke();
        }

        private void Update()
        {
            var dive = brain != null && brain.controls != null ? brain.controls.DivePath : null;
            bool under = dive != null && dive.Under;
            if (under && !wasUnder && pulseKicks && packet != null)
            {
                float strength = fullPulse * Mathf.Lerp(1f, supinePulse, brain.Posture.SupineFraction);
                if (strength > 1e-3f)
                {
                    packet.TriggerKick(strength, archLength);
                    Pulsed?.Invoke();
                }
            }
            wasUnder = under;
        }

        private void OnDisable() => Listen(false);
    }
}
