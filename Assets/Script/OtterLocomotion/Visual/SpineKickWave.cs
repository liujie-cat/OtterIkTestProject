using System;
using System.Collections.Generic;
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
    /// only, pushes in quick succession would hold it bent into a bow. While LimbStrokeAnimator holds the
    /// hind legs out as a fluke, an ExpSpineHarmonicProvider pitches the spine up and down on the hind rhythm's beat
    /// instead, chest to hips, a little behind it (fluke.spineLag), so the hips heave with the feet and the crest runs
    /// on from the chest to the hips. The pulses KickEventBridge sends on the older movement
    /// controller's kick timing are held off meanwhile. Uses the harmonizer's harmonic provider, or adds one for this
    /// Play when it has none.
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
        [Tooltip("Once the fluke is this far in the harmonic wave takes over from the packets.")]
        [Range(0f, 1f)] public float pulsesOff = 0.5f;

        private ExpSpineHarmonicProvider wave;
        private ExpSpineKickWavePacketProvider packet;
        private OtterLocomotionBrain brain;
        private bool added;
        private readonly List<Behaviour> suspended = new List<Behaviour>();

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
            wave = harmonizer.providers.OfType<ExpSpineHarmonicProvider>().FirstOrDefault(p => p != null);
            if (wave == null)
            {
                wave = harmonizer.gameObject.AddComponent<ExpSpineHarmonicProvider>();
                wave.hideFlags = HideFlags.DontSave;
                harmonizer.providers.Add(wave);
                added = true;
            }
            if (wave.decoupler == null) wave.decoupler = harmonizer.decoupler;
            Listen(true);
        }

        private void OnEnable()
        {
            if (wave != null) Listen(true);
        }

        private void Listen(bool on)
        {
            var b = animator != null ? animator.brain : null;
            if (brain != null) brain.Rhythm.PowerStarted -= OnPush;
            brain = on ? b : null;
            if (brain != null) brain.Rhythm.PowerStarted += OnPush;
        }

        private void Update()
        {
            if (wave == null) return;
            bool live = animator != null && animator.isActiveAndEnabled;
            float fluke = live ? animator.Fluke : 0f;
            if (fluke > 1e-3f)
            {
                wave.maxAmplitude = animator.fluke.spineAmplitude;
                wave.waveOffset = animator.fluke.spineWaveOffset;
                wave.Drive(animator.FlukePhase - animator.fluke.spineLag, fluke * animator.FlukeDrive);
            }
            else wave.StopDrive();
            HoldPulses(live && (fluke >= pulsesOff || (pulseKicks && packet != null)));
        }

        // A hind push starts: a packet down the spine that lasts until the next push, of either foot when they take turns
        private void OnPush(LimbId limb, LimbStroke st)
        {
            if (!pulseKicks || packet == null || OtterRig.IsFore(limb) || st.parking || st.drive <= 0f) return;
            if (animator == null || !animator.isActiveAndEnabled || animator.Fluke >= pulsesOff) return;
            float strength = Mathf.Lerp(lightPulse, fullPulse, st.drive) * Mathf.Lerp(1f, supinePulse, brain.Posture.SupineFraction);
            if (strength <= 1e-3f) return;
            float interval = (brain.Rhythm.HindTogether ? 1f : 0.5f) / Mathf.Max(0.1f, st.rate);
            packet.TriggerKick(strength, Mathf.Clamp(interval * pulseLength, 0.15f, 1.2f));
            Pulsed?.Invoke();
        }

        private void HoldPulses(bool hold)
        {
            if (!hold)
            {
                foreach (var b in suspended.Where(b => b != null)) b.enabled = true;
                suspended.Clear();
                return;
            }
            if (suspended.Count > 0) return;
            foreach (var b in FindObjectsByType<KickEventBridge>(FindObjectsSortMode.None).Where(b => b.enabled))
            {
                b.enabled = false;
                suspended.Add(b);
            }
        }

        private void OnDisable()
        {
            Listen(false);
            if (wave != null) wave.StopDrive();
            HoldPulses(false);
        }

        private void OnDestroy()
        {
            if (!added || wave == null) return;
            if (harmonizer != null) harmonizer.providers.Remove(wave);
            Destroy(wave);
        }
    }
}
