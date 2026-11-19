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
    /// curl comes and goes with the roll. The neck is set on the chest every frame from the skin's rest shape, so it
    /// keeps its length whatever moved or regrouped the bones before.
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

        private void Awake()
        {
            if (controls == null) controls = GetComponent<OtterControls>();
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
