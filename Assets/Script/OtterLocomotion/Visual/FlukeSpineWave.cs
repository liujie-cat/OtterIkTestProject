using System.Collections.Generic;
using System.Linq;
using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Puts the fluke's wave on the spine: while LimbStrokeAnimator holds the hind legs out as a fluke, an
    /// ExpSpineHarmonicProvider on the spine harmonizer pitches the spine up and down on the hind rhythm's beat, chest
    /// to hips, so the hips heave in time with the feet. The kick-triggered spine pulses (KickEventBridge) are held off
    /// meanwhile. Uses the harmonizer's harmonic provider, or adds one for this Play when it has none.
    /// </summary>
    [DefaultExecutionOrder(310)]
    [DisallowMultipleComponent]
    public class FlukeSpineWave : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public LimbStrokeAnimator animator;
        [Tooltip("Found in the scene when empty.")]
        public ExpShadowSpineHarmonizer harmonizer;
        [Tooltip("The kick pulses are held off once the fluke is this far in.")]
        [Range(0f, 1f)] public float pulsesOff = 0.5f;

        private ExpSpineHarmonicProvider wave;
        private bool added;
        private readonly List<Behaviour> suspended = new List<Behaviour>();

        private void Awake()
        {
            if (animator == null) animator = GetComponent<LimbStrokeAnimator>();
            if (harmonizer == null)
                harmonizer = FindObjectsByType<ExpShadowSpineHarmonizer>(FindObjectsSortMode.None).FirstOrDefault(h => h.isActiveAndEnabled);
        }

        private void Start()
        {
            if (harmonizer == null) return;
            wave = harmonizer.providers.OfType<ExpSpineHarmonicProvider>().FirstOrDefault(p => p != null);
            if (wave == null)
            {
                wave = harmonizer.gameObject.AddComponent<ExpSpineHarmonicProvider>();
                wave.hideFlags = HideFlags.DontSave;
                harmonizer.providers.Add(wave);
                added = true;
            }
            if (wave.decoupler == null) wave.decoupler = harmonizer.decoupler;
        }

        private void Update()
        {
            if (wave == null) return;
            float fluke = animator != null && animator.isActiveAndEnabled ? animator.Fluke : 0f;
            if (fluke > 1e-3f)
            {
                wave.maxAmplitude = animator.fluke.spineAmplitude;
                wave.waveOffset = animator.fluke.spineWaveOffset;
                wave.Drive(animator.FlukePhase, fluke * animator.FlukeDrive);
            }
            else wave.StopDrive();
            HoldPulses(fluke >= pulsesOff);
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
