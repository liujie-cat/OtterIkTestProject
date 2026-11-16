using System.Linq;
using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Bends the spine side to side with the hind pushes (HindBend): each push bows the waist out toward the pushing
    /// foot, the hips swinging toward it and the chest a little the other way, hips first and chest after, so the body
    /// curls away from the push. Swimming straight the feet take turns; in a turn the outside foot's pushes curl the
    /// body into it. An additive provider on the spine harmonizer, on top of its turn bend; adds itself to the
    /// harmonizer's providers for this Play.
    /// </summary>
    [DefaultExecutionOrder(315)]
    [DisallowMultipleComponent]
    public class SpineKickSway : ExpSpinePoseProviderBase
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        [Tooltip("Found in the scene when empty.")]
        public ExpShadowSpineHarmonizer harmonizer;
        [Tooltip("How far the hips swing toward the pushing foot at a full push (deg), before the harmonizer's chain softens it.")]
        public float sway = 24f;
        [Tooltip("The chest's swing the other way, as a share of the hips' (0 = the chest and head hold steady and the bend stays in the waist).")]
        [Range(0f, 1f)] public float chestCounter = 0f;
        [Tooltip("How long the bend takes from one spine joint to the next, hips to chest (s).")]
        public float waveLag = 0.015f;
        [Tooltip("Yaw moment of a push (N·m) that bends the body most of the way.")]
        public float fullMoment = 0.05f;
        [Tooltip("How far ahead of the strokes the bend is asked for (s), against the harmonizer's chain taking its time, so the waist bends through the push rather than after it.")]
        public float lead = 0.06f;

        private float[] bend = new float[0];
        private bool added;

        /// <summary>HindBend now, -1..1, + = curled to the right.</summary>
        public float Bend { get; private set; }

        public override bool Additive => true;

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
            if (harmonizer == null)
                harmonizer = FindObjectsByType<ExpShadowSpineHarmonizer>(FindObjectsSortMode.None).FirstOrDefault(h => h.isActiveAndEnabled);
        }

        private void Start()
        {
            if (harmonizer == null) return;
            if (!harmonizer.providers.Contains(this))
            {
                harmonizer.providers.Add(this);
                added = true;
            }
        }

        private void OnDestroy()
        {
            if (added && harmonizer != null) harmonizer.providers.Remove(this);
        }

        // before the harmonizer reads the providers in its LateUpdate
        private void Update()
        {
            int n = harmonizer != null && harmonizer.decoupler != null ? harmonizer.decoupler.shadowNodes.Count : 0;
            if (bend.Length != n) bend = new float[n];
            Bend = HindBend.Now(brain, fullMoment, lead);
            float follow = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, waveLag));
            for (int i = 0; i < n; i++) bend[i] = Mathf.Lerp(bend[i], i == 0 ? Bend : bend[i - 1], follow);
        }

        public override bool Evaluate(int index, SpineChainDefinition spine, Transform jointOrShadow, Quaternion bindLocalRot, out Quaternion targetLocalRot, out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;
            int n = bend.Length;
            if (index < 0 || index >= n || n < 2) return false;
            // hips (0) a full swing and then some, as the harmonizer's chain blends each joint toward the one ahead of it;
            // the chest (n - 1) back the other way
            float gain = Mathf.Lerp(1.5f, -chestCounter, (float)index / (n - 1));
            // curled to the right, the hips point left of the body's line
            float angle = -sway * gain * bend[index];
            if (Mathf.Abs(angle) < 0.01f) return false;
            targetLocalRot = Quaternion.Euler(0f, angle, 0f) * bindLocalRot;
            weight = globalWeight;
            return true;
        }
    }
}
