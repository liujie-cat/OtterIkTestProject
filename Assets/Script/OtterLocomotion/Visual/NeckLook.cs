using System;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Turns the neck and head (OtterRig.Neck) toward where the otter is headed, each joint a share of the way, so the
    /// neck bends in an arc. On its front it looks where it goes, as far as the neck reaches. On its back it lifts its
    /// head and curls the neck over toward its chest, looking down at its belly, and turns only a little toward the
    /// heading, keeping it in the corner of its eye. How far the chest has rolled over decides between the two, so the
    /// curl comes and goes with the roll. In the dolphin wave (LimbStrokeAnimator.Fluke) the head rides the wave
    /// instead of holding level: the neck pitches so the nose rises and dips about the water's surface on the wave's
    /// beat, ahead of the hips, and the crest runs from the head back along the body. The neck is set on the chest
    /// every frame from the skin's rest shape, so it keeps its length whatever moved or regrouped the bones before.
    /// </summary>
    [DefaultExecutionOrder(320)]
    [DisallowMultipleComponent]
    public class NeckLook : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterControls controls;
        [Tooltip("How far ahead of the neck it looks along the heading (m), when the pointer does not guide it.")]
        public float reach = 1.2f;
        [Tooltip("How quickly the neck follows where it is told to look (1/s).")]
        public float followSpeed = 10f;

        [Header("On the front (deg from the neck's rest line on the chest)")]
        [Tooltip("Most it turns to either side.")]
        public float maxSide = 60f;
        [Tooltip("Most it lifts toward the back.")]
        public float maxUp = 35f;
        [Tooltip("Most it dips toward the belly.")]
        public float maxDown = 35f;

        [Header("On the back")]
        [Tooltip("How far the neck curls over toward the belly (deg): the head ends this much more tucked than at rest.")]
        public float bellyCurl = 125f;
        [Tooltip("Share of the heading's angle to the side that the curled head turns by.")]
        [Range(0f, 1f)] public float glance = 0.35f;

        [Header("In the dolphin wave")]
        [Tooltip("Found on this object when empty. Without it, or without a Water, the head only looks where it goes.")]
        public LimbStrokeAnimator animator;
        [Tooltip("The nose rides up and down about the water's surface: its middle height above the surface, and how far it rises above and dips below that (m).")]
        public float noseMiddle = 0f;
        public float noseSwing = 0.03f;
        [Tooltip("Where in the wave's beat the nose is highest (cycles): ahead of the hips, so a crest runs from the head back along the body to the tail.")]
        [Range(0f, 1f)] public float noseTop = 0.95f;
        [Tooltip("Most the neck pitches up or down for it, on top of where it looks (deg).")]
        public float maxBob = 35f;

        /// <summary>Where the neck is told to look (world): ahead along the heading, or at the pointer while guided.</summary>
        public Vector3 LookPoint { get; private set; }
        /// <summary>How far over on its back the chest is (0 on the front .. 1 on the back), which sets the curl.</summary>
        public float OnBack { get; private set; }

        private Transform chest;
        private Transform[] joints;
        // each joint at rest, in the chest's space
        private Quaternion[] restRotation;
        private Vector3[] restPosition;
        // where the neck points, in the chest's space, from its rest line (+Y, along the spine)
        private Vector3 look = Vector3.up;
        private Water water;
        // the nose from the head joint at rest, in the chest's space; and how far the neck pitches it up for the wave (deg)
        private Vector3 noseRest;
        private float bob;

        private void Awake()
        {
            if (controls == null) controls = GetComponent<OtterControls>();
            if (animator == null) animator = GetComponent<LimbStrokeAnimator>();
        }

        // the bones are found by name: SpineDecouplerWithShadows and the old neck controller regroup them as they wake
        private void Start()
        {
            chest = OtterRig.FindActiveBone(transform, OtterRig.Chest);
            joints = OtterRig.Neck.Select(n => OtterRig.FindActiveBone(transform, n)).ToArray();
            SkinnedMeshRenderer skin = null;
            Transform[] bones = null;
            if (chest != null && !joints.Contains(null))
                foreach (var r in chest.root.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    bones = r.bones;
                    if (r.sharedMesh != null && bones.Contains(chest) && joints.All(bones.Contains)) { skin = r; break; }
                }
            if (skin == null)
            {
                string found = string.Join(", ", OtterRig.Neck.Prepend(OtterRig.Chest).Select(n => $"{n} {(OtterRig.FindActiveBone(transform, n) != null ? "ok" : "missing")}"));
                Debug.LogWarning($"[NeckLook] No skin under '{name}' that the neck's bones all move ({found}).");
                enabled = false;
                return;
            }
            // the bind pose, as the bones may have been posed already when this starts
            var bind = skin.sharedMesh.bindposes;
            Matrix4x4 chestBind = bind[Array.IndexOf(bones, chest)];
            restRotation = new Quaternion[joints.Length];
            restPosition = new Vector3[joints.Length];
            for (int i = 0; i < joints.Length; i++)
            {
                Matrix4x4 rest = chestBind * bind[Array.IndexOf(bones, joints[i])].inverse;
                restRotation[i] = rest.rotation;
                restPosition[i] = rest.GetColumn(3);
            }
            water = FindFirstObjectByType<Water>();
            // the nose: the point of the head's skin farthest from the chest
            Transform head = joints[joints.Length - 1];
            var headSkin = OtterRig.FollowingVertices(head);
            if (headSkin != null && headSkin.Count > 0)
            {
                Vector3 nose = headSkin.OrderByDescending(p => (p - chest.position).sqrMagnitude).First();
                Matrix4x4 headRest = chestBind * bind[Array.IndexOf(bones, head)].inverse;
                noseRest = headRest.MultiplyPoint3x4(head.InverseTransformPoint(nose)) - restPosition[joints.Length - 1];
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (restRotation == null || dt <= 0f) return;

            Vector3 neck = chest.TransformPoint(restPosition[0]);
            Vector3 heading = controls != null ? controls.Gears.Heading : transform.forward;
            LookPoint = neck + heading * reach;
            if (controls != null) LookPoint = Vector3.Lerp(LookPoint, controls.Gears.AimPoint, controls.Gears.AimBlend);
            Vector3 toward = Vector3.ProjectOnPlane(LookPoint - neck, Vector3.up);
            if (toward.sqrMagnitude < 1e-4f) toward = heading;
            // the chest bone: +Y along the spine toward the head, +Z ventral, +X to the right on the front
            Vector3 t = Quaternion.Inverse(chest.rotation) * toward.normalized;
            float side = Mathf.Atan2(t.x, t.y) * Mathf.Rad2Deg;
            float dip = Mathf.Atan2(t.z, new Vector2(t.x, t.y).magnitude) * Mathf.Rad2Deg;

            OnBack = Mathf.SmoothStep(0f, 1f, 0.5f + 0.5f * Vector3.Dot(chest.forward, Vector3.up));
            Vector3 front = Direction(Mathf.Clamp(side, -maxSide, maxSide), Mathf.Clamp(dip, -maxUp, maxDown), sideFirst: true);
            Vector3 back = Direction(Mathf.Clamp(glance * side, -maxSide, maxSide), bellyCurl, sideFirst: false);
            look = Vector3.Slerp(look, Vector3.Slerp(front, back, OnBack), 1f - Mathf.Exp(-followSpeed * dt));

            Quaternion full = Quaternion.FromToRotation(Vector3.up, look);
            float fluke = animator != null && animator.isActiveAndEnabled ? animator.Fluke : 0f;
            if (fluke > 1e-3f && water != null && noseRest != Vector3.zero)
            {
                // Newton steps on the pitch that puts the nose at its height on the beat, from where it was last frame
                float goal = noseMiddle + noseSwing * Mathf.Cos(2f * Mathf.PI * (animator.FlukePhase - noseTop));
                for (int k = 0; k < 2; k++)
                {
                    float h = NoseHeight(Pitched(full, bob)), slope = NoseHeight(Pitched(full, bob + 1f)) - h;
                    if (slope > 1e-5f) bob = Mathf.Clamp(bob + (goal - h) / slope, -maxBob, maxBob);
                }
                full = Pitched(full, fluke * bob);
            }
            else bob = 0f;
            Quaternion before = Quaternion.identity;
            Vector3 at = restPosition[0];
            for (int i = 0; i < joints.Length; i++)
            {
                if (i > 0) at += before * (restPosition[i] - restPosition[i - 1]);
                Quaternion turn = Quaternion.Slerp(Quaternion.identity, full, (i + 1f) / joints.Length);
                joints[i].SetPositionAndRotation(chest.TransformPoint(at), chest.rotation * turn * restRotation[i]);
                before = turn;
            }
        }

        // The neck's turn pitched up toward the back by deg, about the chest's side axis (+X; +Z is ventral)
        private static Quaternion Pitched(Quaternion full, float deg) => Quaternion.AngleAxis(-deg, Vector3.right) * full;

        // How high the nose would be above the water's surface (m) with the neck turned by full, each joint its share as
        // LateUpdate sets them
        private float NoseHeight(Quaternion full)
        {
            Vector3 at = restPosition[0];
            for (int i = 1; i < joints.Length; i++)
                at += Quaternion.Slerp(Quaternion.identity, full, (float)i / joints.Length) * (restPosition[i] - restPosition[i - 1]);
            return -water.Depth(chest.TransformPoint(at + full * noseRest));
        }

        // A direction in the chest's space from angles off the spine (+Y): to the right (+X) and toward the belly (+Z).
        // Side first turns about the belly axis and then dips, so the side angle holds however far it dips; curl first
        // curls about the side axis and then turns, so the side angle still shows when curled square to the spine.
        private static Vector3 Direction(float side, float dip, bool sideFirst)
        {
            float s = side * Mathf.Deg2Rad, d = dip * Mathf.Deg2Rad;
            return sideFirst
                ? new Vector3(Mathf.Cos(d) * Mathf.Sin(s), Mathf.Cos(d) * Mathf.Cos(s), Mathf.Sin(d))
                : new Vector3(Mathf.Sin(s), Mathf.Cos(s) * Mathf.Cos(d), Mathf.Cos(s) * Mathf.Sin(d));
        }
    }
}
