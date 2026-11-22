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
    /// curl comes and goes with the roll. Stood up in the water (Periscope) it looks as from the chest lying level, so
    /// the head looks out level over the water, most of the bend forward off the upright chest at the head's own joint
    /// with the neck standing up under it, and it is not taken for on its back. In the dolphin wave the head rides the
    /// track the body swims along (DolphinTrack) instead of holding level: the neck pitches to carry the head joint
    /// onto it where it is along the body, and the head pitches to put the nose onto it further on, so the snout
    /// points along the track and bores in nose first as it dips. The neck is set on the chest every frame from the skin's rest shape, so it keeps its
    /// length whatever moved or regrouped the bones before.
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
        public DolphinTrack track;
        [Tooltip("How far above the track the head joint and the nose ride (m): the track runs along the spine, and the head sits higher on the neck.")]
        public float rideHeight = 0.02f;
        [Tooltip("Most the neck pitches up or down to carry the head on the track, and the head from where it looks to put the nose on it, on top of where it looks (deg).")]
        public float maxBob = 40f;
        public float maxTilt = 30f;

        [Header("Standing up in the water")]
        [Tooltip("Found on this object when empty. Without it the head looks from the chest as it stands.")]
        public Periscope periscope;
        [Tooltip("Share of the bend forward off the upright chest that the neck takes; the head takes the rest at its joint, so the neck stands up under it.")]
        [Range(0f, 1f)] public float neckRaise = 0.3f;

        /// <summary>Where the neck is told to look (world): ahead along the heading, or at the pointer while guided.</summary>
        public Vector3 LookPoint { get; private set; }
        /// <summary>How far over on its back the chest is (0 on the front .. 1 on the back), which sets the curl.</summary>
        public float OnBack { get; private set; }

        private Transform chest;
        private Transform[] joints;
        // each joint at rest, in the chest's space
        private Quaternion[] restRotation;
        private Vector3[] restPosition;
        // where the neck points, in the space of the chest as it would lie level, from its rest line (+Y, along the spine)
        private Vector3 look = Vector3.up;
        // stood up, the turn from the chest's space to that of the chest lying level
        private Quaternion raise = Quaternion.identity;
        private Water water;
        // the nose from the head joint at rest, in the chest's space; how far the neck pitches up to carry the head onto
        // the track, and the head from where it looks to put the nose on it (deg)
        private Vector3 noseRest;
        private float bob, tilt;

        private void Awake()
        {
            if (controls == null) controls = GetComponent<OtterControls>();
            if (track == null) track = GetComponent<DolphinTrack>();
            if (periscope == null) periscope = GetComponent<Periscope>();
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
            // the chest bone: +Y along the spine toward the head, +Z ventral, +X to the right on the front; stood up, as
            // it would lie level
            Quaternion level = periscope != null && periscope.isActiveAndEnabled ? Quaternion.AngleAxis(periscope.Raised, transform.right) * chest.rotation : chest.rotation;
            Vector3 t = Quaternion.Inverse(level) * toward.normalized;
            float side = Mathf.Atan2(t.x, t.y) * Mathf.Rad2Deg;
            float dip = Mathf.Atan2(t.z, new Vector2(t.x, t.y).magnitude) * Mathf.Rad2Deg;

            OnBack = Mathf.SmoothStep(0f, 1f, 0.5f + 0.5f * Vector3.Dot(level * Vector3.forward, Vector3.up));
            Vector3 front = Direction(Mathf.Clamp(side, -maxSide, maxSide), Mathf.Clamp(dip, -maxUp, maxDown), sideFirst: true);
            Vector3 back = Direction(Mathf.Clamp(glance * side, -maxSide, maxSide), bellyCurl, sideFirst: false);
            look = Vector3.Slerp(look, Vector3.Slerp(front, back, OnBack), 1f - Mathf.Exp(-followSpeed * dt));

            raise = Quaternion.Inverse(chest.rotation) * level;
            Quaternion full = raise * Quaternion.FromToRotation(Vector3.up, look), head = full;
            float wave = track != null && track.isActiveAndEnabled ? track.Weight : 0f;
            if (wave > 1e-3f && water != null && noseRest != Vector3.zero)
            {
                // Newton steps on each, from where they were last frame
                for (int k = 0; k < 2; k++)
                {
                    float h = HeadOff(Pitched(full, bob)), slope = HeadOff(Pitched(full, bob + 1f)) - h;
                    if (slope > 1e-5f) bob = Mathf.Clamp(bob - h / slope, -maxBob, maxBob);
                }
                for (int k = 0; k < 2; k++)
                {
                    float h = NoseOff(Pitched(full, bob), Pitched(full, tilt)), slope = NoseOff(Pitched(full, bob), Pitched(full, tilt + 1f)) - h;
                    if (slope > 1e-5f) tilt = Mathf.Clamp(tilt - h / slope, -maxTilt, maxTilt);
                }
                head = Pitched(full, wave * tilt);
                full = Pitched(full, wave * bob);
            }
            else bob = tilt = 0f;
            Quaternion before = Quaternion.identity;
            Vector3 at = restPosition[0];
            for (int i = 0; i < joints.Length; i++)
            {
                if (i > 0) at += before * (restPosition[i] - restPosition[i - 1]);
                Quaternion turn = i == joints.Length - 1 ? head : Neck(full, (i + 1f) / joints.Length);
                joints[i].SetPositionAndRotation(chest.TransformPoint(at), chest.rotation * turn * restRotation[i]);
                before = turn;
            }
        }

        // The neck's turn pitched up toward the back by deg, about the chest's side axis (+X; +Z is ventral)
        private static Quaternion Pitched(Quaternion full, float deg) => Quaternion.AngleAxis(-deg, Vector3.right) * full;

        // A neck joint's turn, its share of the neck's turn full; of the bend off the upright chest, only neckRaise of
        // its share
        private Quaternion Neck(Quaternion full, float share) =>
            Quaternion.Slerp(Quaternion.identity, raise, neckRaise * share) * Quaternion.Slerp(Quaternion.identity, Quaternion.Inverse(raise) * full, share);

        // Where the head joint would be, in the chest's space, with the neck turned by full, each joint its share as
        // LateUpdate sets them
        private Vector3 HeadAt(Quaternion full)
        {
            Vector3 at = restPosition[0];
            for (int i = 1; i < joints.Length; i++)
                at += Neck(full, (float)i / joints.Length) * (restPosition[i] - restPosition[i - 1]);
            return at;
        }

        // How high the head joint, and the nose with the head turned by head, would be above where they ride on the track (m)
        private float HeadOff(Quaternion full) => OffTrack(chest.TransformPoint(HeadAt(full)));
        private float NoseOff(Quaternion full, Quaternion head) => OffTrack(chest.TransformPoint(HeadAt(full) + head * noseRest));
        private float OffTrack(Vector3 point) => -water.Depth(point) - track.Height(point) - rideHeight;

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
