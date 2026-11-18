using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace OtterLocomotion.Testing
{
    /// <summary>
    /// Records an OtterInputPlayback run every frame (after IK): intent, body heading/speed, head and chest response,
    /// and each limb's IK target offset from its rest. WriteFiles() saves a CSV and a per-segment summary to Logs/:
    /// when the head, limbs, spine and body first react, how fast and how cleanly turns complete, speed rise, stopping.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    public class LocomotionTelemetry : MonoBehaviour
    {
        public OtterInputPlayback playback;
        public MovementControllerRB movement;
        [Tooltip("Optional: also records posture and the demand layer's output.")]
        public OtterLocomotionBrain brain;
        public string label = "baseline";

        [Header("Reaction thresholds")]
        public float bodyYawThresholdDeg = 2f;
        public float headYawThresholdDeg = 3f;
        public float spineBendThresholdDeg = 2f;
        public float limbThresholdCm = 1f;
        public float turnMinDeg = 15f;
        public float settleToleranceDeg = 5f;
        public float stopSpeed = 0.02f;
        [Tooltip("Hind foot speed relative to the hip (m/s) below which its web direction is not judged, and above which it counts as sweeping back or forward.")]
        public float footStillSpeed = 0.05f;
        public float footSweepSpeed = 0.1f;
        [Tooltip("Draw each hind ankle's recent path relative to the hip (left orange, right cyan), seen in the playback snapshots.")]
        public bool drawFootTrails = true;
        public float trailSeconds = 1f;

        [Header("Feel targets (checked per segment in the summary)")]
        public float headReactionMax = 0.10f;
        public float limbLeadMin = 0.10f;
        [Tooltip("Time to 90% of a 90 degree turn; scales linearly to turn170Budget for a 170 degree turn.")]
        public float turn90Budget = 1.0f;
        public float turn170Budget = 1.5f;
        public float overshootMaxDeg = 8f;
        [Tooltip("Mean forward speed over the last second within this fraction of the target.")]
        public float speedMeanTolerance = 0.10f;
        [Tooltip("(max - min) / (2 * mean) over the last second.")]
        public float speedRippleMax = 0.25f;
        [Tooltip("Coasting from >= 1 m/s after release should take this long to drop below stopSpeed.")]
        public float stopTimeMin = 0.8f;
        public float stopTimeMax = 1.5f;

        private struct Limb
        {
            public string name;
            public Transform target, space;
            public Vector3 restLocal;
        }

        /// <summary>A hind foot's web, to see whether it faces the way it moves (pushing) or slides edge-on; tracked at the web's middle.</summary>
        private class Foot
        {
            public string name;
            public Transform bone;
            public Vector3 normalLocal, lengthLocal, centerLocal;
            /// <summary>Web middle last frame, relative to the hip and in the world.</summary>
            public Vector3 previous, previousWorld;
            public bool hasPrevious;
            /// <summary>The leg's IK chain, for the ankle's path relative to the hip joint in the hip's axes; side = +1 when the leg is on the hip's +X.</summary>
            public Transform hipJoint, ankle, girdle;
            public float legLength, side;
            public LineRenderer trail;
            public readonly List<(float t, Vector3 p)> path = new List<(float t, Vector3 p)>();
        }

        private struct Sample
        {
            public float t, dt, intentYaw, steerYaw, bodyYaw, yawRate, speedFwd, speedLat, targetSpeed, demand;
            public float headYaw, chestBend, chestRoll, reach;
            public int seg;
            public bool held, kick;
            public MovementControllerRB.MoveZone zone;
            public float[] limb, limbOut;
            public int[] limbSide;
            /// <summary>Per hind foot: |web normal · direction of motion| relative to the hip (1 = pushing flat-on, 0 = edge-on, -1 = still), and fore-aft speed (m/s, + = forward).</summary>
            public float[] footFace, footFwd;
            /// <summary>Per hind foot: speed relative to the hip (m/s), and the web normal's fore-aft component (±1 = facing straight back or ahead).</summary>
            public float[] footSpeed, footNormalFwd;
            /// <summary>Distance between the two hind webs' centres (cm), -1 without two feet.</summary>
            public float feetGap;
            /// <summary>Per hind foot: ankle from the hip joint in the hip's axes (cm: forward, outward, ventral), and its distance over the leg's length.</summary>
            public Vector3[] footPath;
            public float[] footReach;
            /// <summary>Per hind foot, from LimbStrokeAnimator: ankle turn from rest (deg), push flag, web width, turn the goal asks for (deg).</summary>
            public Vector4[] flipperPose;
            /// <summary>Per hind foot, from LimbStrokeAnimator: how far its kick loop swings out for the turn (deg).</summary>
            public float[] kickYaw;
            /// <summary>LimbStrokeAnimator's fluke (0..1) and its beat (cycles); the hip and chest bones' pitch (deg, + = front up); per hind foot the ankle's height over the hip joint (cm, world up).</summary>
            public float fluke, flukePhase, hipPitch, chestPitch;
            public float[] footHeave;
            /// <summary>SpineKickWave sent a wave packet down the spine this frame.</summary>
            public bool pulse;
            /// <summary>Per hind foot: the web's fore-aft speed through the water (m/s, world, + = forward); a push that holds its water keeps it near 0 or below.</summary>
            public float[] footWorldFwd;
            /// <summary>Per hind foot: the web middle's speed up through the water (m/s) and its normal's up component.</summary>
            public float[] footWorldUp, footNormalUp;
            /// <summary>Per hind foot: which way the toes point, ankle to web middle: up and forward components.</summary>
            public float[] footToesUp, footToesFwd;
            /// <summary>TailRudder: the tip's bend from the hips' line (deg, + = right) and lift above it (deg, + = toward the back), and how deep the tail sits in the hind legs (cm); NaN without one.</summary>
            public float tailBend, tailLift, tailHit;
            /// <summary>Side bend at the waist, the hips' heading to the chest's seen from above (deg, + = curled to the right), and
            /// the hind pushes' bend that SpineKickSway puts on the spine (-1..1, NaN without one).</summary>
            public float waist, swayBend;
            /// <summary>Per hind foot: WebThrust's push on the body along its heading (N, + = forward), 0 without it.</summary>
            public float[] webPush;
            public Posture posture;
            public float supine, roll, speedTarget, speedCap, yawRateTarget, surgeForce, swayForce, yawMoment, bank;
            public bool allocated;
            public Vector3 achieved;
            public Vector2[] limbForce;
            public float[] limbYaw;
            public int saturated;
            public bool rhythm, hindTogether;
            public float[] phase, drive;
            public int[] strokes;
            public int powerMask;
        }

        private struct PostureEvent
        {
            public float t;
            public int seg;
            public Posture posture;
            public string reason;
        }

        private int checkCount, passCount;
        private readonly List<PostureEvent> postureEvents = new List<PostureEvent>();
        private readonly List<Limb> limbs = new List<Limb>();
        private readonly List<Foot> feet = new List<Foot>();
        private readonly List<Sample> samples = new List<Sample>();
        private Transform hip, chest, head;
        private LimbStrokeAnimator animator;
        private SpineKickWave spineWave;
        private TailRudder tail;
        private SpineKickSway sway;
        private WebThrust webThrust;
        private string footInfo = "";
        private bool kickThisFrame, pulseThisFrame;
        private float prevYaw;
        private bool hasPrevYaw;

        private void Start()
        {
            if (movement == null) movement = FindObjectOfType<MovementControllerRB>();
            if (playback == null) playback = FindObjectOfType<OtterInputPlayback>();
            if (movement == null || playback == null)
            {
                Debug.LogWarning("[LocomotionTelemetry] Needs a MovementControllerRB and an OtterInputPlayback.", this);
                enabled = false;
                return;
            }
            foreach (var ik in FindObjectsOfType<AdvancedTwoBoneIK>().Where(k => k.isActiveAndEnabled && k.target))
            {
                if (!OtterRig.TryClassify(ik, out var limb)) continue;
                var space = OtterRig.Girdle(ik.root.bone);
                limbs.Add(new Limb { name = limb.ToString(), target = ik.target, space = space, restLocal = space.InverseTransformPoint(ik.target.position) });
                var flipper = OtterRig.IsFore(limb) ? null : OtterRig.Flipper(ik.end.bone);
                if (flipper != null && OtterRig.TryFootPlane(flipper, out var normal, out var length, out var center, out _))
                {
                    Transform a = ik.root.bone, b = ik.mid.bone, c = ik.end.bone;
                    var foot = new Foot
                    {
                        name = limb.ToString(), bone = flipper, normalLocal = normal, lengthLocal = length, centerLocal = center,
                        hipJoint = a, ankle = c, girdle = space,
                        legLength = Vector3.Distance(a.position, b.position) + Vector3.Distance(b.position, c.position),
                        side = space.InverseTransformPoint(a.position).x >= 0f ? 1f : -1f,
                    };
                    if (drawFootTrails) foot.trail = MakeTrail(space, limb == LimbId.HL ? new Color(1f, 0.55f, 0.1f) : new Color(0.1f, 0.85f, 1f));
                    feet.Add(foot);
                }
            }
            limbs.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            feet.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            var b0 = movement.transform;
            footInfo = string.Join(" | ", feet.Select(f =>
            {
                Vector3 n = f.bone.TransformDirection(f.normalLocal), l = f.bone.TransformDirection(f.lengthLocal);
                return $"{f.name} web normal (fwd {Vector3.Dot(n, b0.forward):F2}, right {Vector3.Dot(n, b0.right):F2}, up {Vector3.Dot(n, b0.up):F2})" +
                       $" toes (fwd {Vector3.Dot(l, b0.forward):F2}, right {Vector3.Dot(l, b0.right):F2}, up {Vector3.Dot(l, b0.up):F2})";
            }));
            hip = OtterRig.FindActiveBone(movement.transform, OtterRig.Hip);
            chest = OtterRig.FindActiveBone(movement.transform, OtterRig.Chest);
            head = OtterRig.FindActiveBone(movement.transform, OtterRig.Head);
            movement.OnKickCycleStart += OnKick;
            if (brain == null) brain = movement.GetComponent<OtterLocomotionBrain>();
            animator = movement.GetComponent<LimbStrokeAnimator>();
            spineWave = movement.GetComponent<SpineKickWave>();
            tail = movement.GetComponent<TailRudder>();
            sway = movement.GetComponent<SpineKickSway>();
            webThrust = movement.GetComponent<WebThrust>();
            if (spineWave != null) spineWave.Pulsed += OnPulse;
            if (brain != null) brain.Posture.Changed += OnPosture;
        }

        private static LineRenderer MakeTrail(Transform girdle, Color color)
        {
            var go = new GameObject("FootTrail") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(girdle, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.widthMultiplier = 0.0025f;
            line.numCapVertices = 2;
            line.positionCount = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            // drawn over the meshes: the ankle sits inside the leg
            var material = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.DontSave, renderQueue = 4000 };
            material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            line.sharedMaterial = material;
            line.startColor = new Color(color.r, color.g, color.b, 0.15f);
            line.endColor = color;
            return line;
        }

        private void OnDestroy()
        {
            foreach (var f in feet)
                if (f.trail != null)
                {
                    Destroy(f.trail.sharedMaterial);
                    Destroy(f.trail.gameObject);
                }
            if (movement != null) movement.OnKickCycleStart -= OnKick;
            if (spineWave != null) spineWave.Pulsed -= OnPulse;
            if (brain != null) brain.Posture.Changed -= OnPosture;
        }

        private void OnPosture(Posture posture, string reason)
        {
            if (playback != null && playback.SegmentIndex >= 0 && !playback.Finished)
                postureEvents.Add(new PostureEvent { t = Time.time, seg = playback.SegmentIndex, posture = posture, reason = reason });
        }

        private void OnKick(MovementControllerRB.KickCycleEvent e) => kickThisFrame = true;
        private void OnPulse() => pulseThisFrame = true;

        private void LateUpdate()
        {
            if (playback == null || playback.SegmentIndex < 0 || playback.Finished) return;
            var body = movement.transform;
            float dt = Mathf.Max(Time.deltaTime, 1e-5f);
            float yaw = body.eulerAngles.y;
            if (hasPrevYaw) yaw = prevYaw + Mathf.DeltaAngle(prevYaw, yaw);
            float yawRate = hasPrevYaw ? (yaw - prevYaw) / dt : 0f;
            prevYaw = yaw;
            hasPrevYaw = true;

            Vector3 fwd = Flat(body.forward), right = Vector3.Cross(Vector3.up, fwd);
            Vector3 v = movement.GetVelocity();
            var s = new Sample
            {
                t = Time.time, dt = dt, seg = playback.SegmentIndex, held = playback.Held, zone = movement.GetZone(),
                intentYaw = Yaw(movement.GetWorldDir()), steerYaw = Yaw(movement.GetSteerDir()),
                bodyYaw = yaw, yawRate = yawRate,
                speedFwd = Vector3.Dot(v, fwd), speedLat = Vector3.Dot(v, right),
                // the brain asks for its own speeds, capped by posture; the movement controller's belong to its impulse kicks
                targetSpeed = brain != null && brain.isActiveAndEnabled ? brain.Demand.speedCap : movement.GetTargetSpeed(), demand = movement.GetDemand01(), kick = kickThisFrame, reach = movement.GetReach01(),
                // bones point at their child along local +Y, so .up is the head/chest/hip direction
                headYaw = head ? Vector3.SignedAngle(fwd, Flat(head.up), Vector3.up) : 0f,
                chestBend = hip && chest ? Vector3.SignedAngle(Flat(hip.up), Flat(chest.up), Vector3.up) : 0f,
                chestRoll = chest ? ChestRoll(body) : 0f,
                limb = limbs.Select(l => l.space.TransformVector(l.space.InverseTransformPoint(l.target.position) - l.restLocal).magnitude * 100f).ToArray(),
                limbSide = limbs.Select(l => Vector3.Dot(l.space.TransformPoint(l.restLocal) - l.space.position, right) >= 0f ? 1 : -1).ToArray(),
            };
            s.limbOut = limbs.Select((l, k) => Vector3.Dot(l.target.position - l.space.TransformPoint(l.restLocal), right) * s.limbSide[k] * 100f).ToArray();
            s.footFace = new float[feet.Count];
            s.footFwd = new float[feet.Count];
            s.footSpeed = new float[feet.Count];
            s.footNormalFwd = new float[feet.Count];
            s.footPath = new Vector3[feet.Count];
            s.footReach = new float[feet.Count];
            for (int k = 0; k < feet.Count; k++)
            {
                var f = feet[k];
                Vector3 rel = Quaternion.Inverse(f.girdle.rotation) * (f.ankle.position - f.hipJoint.position);
                // hip bone axes: +Y toward the head, +Z ventral
                s.footPath[k] = new Vector3(rel.y, rel.x * f.side, rel.z) * 100f;
                s.footReach[k] = rel.magnitude / Mathf.Max(f.legLength, 1e-4f);
                if (f.trail == null) continue;
                f.path.Add((Time.time, f.girdle.InverseTransformPoint(f.ankle.position)));
                f.path.RemoveAll(e => Time.time - e.t > trailSeconds);
                f.trail.positionCount = f.path.Count;
                for (int i = 0; i < f.path.Count; i++) f.trail.SetPosition(i, f.path[i].p);
            }
            s.feetGap = feet.Count == 2
                ? Vector3.Distance(feet[0].bone.TransformPoint(feet[0].centerLocal), feet[1].bone.TransformPoint(feet[1].centerLocal)) * 100f
                : -1f;
            s.footWorldFwd = new float[feet.Count];
            s.footWorldUp = new float[feet.Count];
            s.footNormalUp = new float[feet.Count];
            s.footToesUp = new float[feet.Count];
            s.footToesFwd = new float[feet.Count];
            for (int k = 0; k < feet.Count && hip != null; k++)
            {
                var f = feet[k];
                Vector3 world = f.bone.TransformPoint(f.centerLocal);
                Vector3 p = hip.InverseTransformPoint(world);
                Vector3 velocity = f.hasPrevious ? hip.TransformVector(p - f.previous) / dt : Vector3.zero;
                s.footWorldFwd[k] = f.hasPrevious ? Vector3.Dot(world - f.previousWorld, fwd) / dt : 0f;
                s.footWorldUp[k] = f.hasPrevious ? (world.y - f.previousWorld.y) / dt : 0f;
                f.previous = p;
                f.previousWorld = world;
                f.hasPrevious = true;
                float speed = velocity.magnitude;
                Vector3 normal = f.bone.TransformDirection(f.normalLocal);
                s.footFace[k] = speed > footStillSpeed ? Mathf.Abs(Vector3.Dot(normal, velocity / speed)) : -1f;
                s.footFwd[k] = Vector3.Dot(velocity, fwd);
                s.footSpeed[k] = speed;
                s.footNormalFwd[k] = Vector3.Dot(normal, fwd);
                s.footNormalUp[k] = normal.y;
                Vector3 toes = (world - f.bone.position).normalized;
                s.footToesUp[k] = toes.y;
                s.footToesFwd[k] = Vector3.Dot(toes, fwd);
            }
            s.flipperPose = feet.Select(f => animator != null && System.Enum.TryParse<LimbId>(f.name, out var id) ? animator.FlipperState(id) : Vector4.zero).ToArray();
            s.kickYaw = feet.Select(f => animator != null && System.Enum.TryParse<LimbId>(f.name, out var id) ? animator.KickYaw(id) : 0f).ToArray();
            s.fluke = animator != null ? animator.Fluke : 0f;
            s.flukePhase = animator != null ? animator.FlukePhase : 0f;
            s.hipPitch = hip != null ? Mathf.Asin(Mathf.Clamp(Vector3.Dot(hip.up, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg : 0f;
            s.chestPitch = chest != null ? Mathf.Asin(Mathf.Clamp(Vector3.Dot(chest.up, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg : 0f;
            s.footHeave = feet.Select(f => (f.ankle.position.y - f.hipJoint.position.y) * 100f).ToArray();
            s.pulse = pulseThisFrame;
            s.tailBend = tail != null && tail.isActiveAndEnabled ? tail.TipBend : float.NaN;
            s.tailHit = tail != null && tail.isActiveAndEnabled ? tail.LegOverlap * 100f : float.NaN;
            s.tailLift = tail != null && tail.isActiveAndEnabled ? tail.TipLift : float.NaN;
            s.swayBend = sway != null && sway.isActiveAndEnabled ? sway.Bend : float.NaN;
            s.webPush = feet.Select(f => webThrust != null && webThrust.isActiveAndEnabled && System.Enum.TryParse<LimbId>(f.name, out var id) ? webThrust.Push(id) : 0f).ToArray();
            // spine bones point +Y toward the head
            s.waist = hip != null && chest != null
                ? Vector3.SignedAngle(Vector3.ProjectOnPlane(hip.up, Vector3.up), Vector3.ProjectOnPlane(chest.up, Vector3.up), Vector3.up)
                : 0f;
            if (brain != null)
            {
                var d = brain.Demand;
                s.posture = brain.Posture.Current;
                s.supine = brain.Posture.SupineFraction;
                s.roll = brain.Posture.RollDeg;
                s.speedTarget = d.speedTarget;
                s.speedCap = d.speedCap;
                s.yawRateTarget = d.yawRateTarget;
                s.surgeForce = d.surgeForce;
                s.swayForce = d.swayForce;
                s.yawMoment = d.yawMoment;
                s.bank = d.bankTargetDeg;
                if (brain.HasLimbGeometry)
                {
                    var a = brain.Allocation;
                    s.allocated = true;
                    s.achieved = a.Achieved;
                    s.limbForce = a.limbs.Select(l => l.force).ToArray();
                    s.limbYaw = a.limbs.Select(l => l.yawMoment).ToArray();
                    s.saturated = a.SaturatedStrokes;
                    var r = brain.Rhythm.limbs;
                    s.rhythm = true;
                    s.hindTogether = brain.Rhythm.HindTogether;
                    s.phase = r.Select(x => x.active ? x.phase : -1f).ToArray();
                    s.drive = r.Select(x => x.drive).ToArray();
                    s.strokes = r.Select(x => x.strokes).ToArray();
                    for (int i = 0; i < r.Length; i++)
                        if (r[i].InPower) s.powerMask |= 1 << i;
                }
            }
            kickThisFrame = false;
            pulseThisFrame = false;
            samples.Add(s);
        }

        // Roll of the chest about its own axis, 0 = back up, ±180 = belly up. The forelimbs hang on the chest's +Z.
        private float ChestRoll(Transform body)
        {
            Vector3 axis = chest.up;
            Vector3 dorsal = Vector3.ProjectOnPlane(-chest.forward, axis);
            Vector3 up = Vector3.ProjectOnPlane(body.up, axis);
            return Vector3.SignedAngle(up, dorsal, axis);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward;
        }

        private static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        // ------------------------------------------------------------------ output

        public string WriteFiles()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
            Directory.CreateDirectory(dir);
            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string csvPath = Path.Combine(dir, $"Locomotion_{label}_{stamp}.csv");
            File.WriteAllText(csvPath, Csv(), new UTF8Encoding(false));
            string summary = Summary() + $"\ncsv: {csvPath}\n";
            File.WriteAllText(Path.Combine(dir, $"Locomotion_{label}_summary.txt"), summary, new UTF8Encoding(false));
            Debug.Log($"[LocomotionTelemetry] {label}\n{summary}");
            return summary;
        }

        private string Csv()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("t,seg,held,zone,intentYaw,steerYaw,bodyYaw,yawRate,speedFwd,speedLat,targetSpeed,kick,demand,headYaw,chestBend,chestRoll,reach");
            foreach (var l in limbs) sb.Append(',').Append(l.name).Append("_cm");
            // horizontal offset away from the body midline, + = outward; side = which side of the midline the limb is on
            // now (+1 right), which swaps left and right while on the back
            foreach (var l in limbs) sb.Append(',').Append(l.name).Append("_out");
            foreach (var l in limbs) sb.Append(',').Append(l.name).Append("_side");
            // hind foot web vs its motion relative to the hip: face 1 = flat-on, 0 = edge-on, -1 = still; vfwd m/s, + = forward
            // spd m/s; nfwd = web normal's fore-aft component
            foreach (var f in feet) sb.Append(',').Append(f.name).Append("_face,").Append(f.name).Append("_vfwd,").Append(f.name).Append("_spd,").Append(f.name).Append("_nfwd");
            // distance between the two webs' centres, cm
            sb.Append(",feetGap");
            // LimbStrokeAnimator's flipper pass: ankle turn from rest (deg), push flag, web width, turn the goal asks for (deg)
            foreach (var f in feet) sb.Append(',').Append(f.name).Append("_turn,").Append(f.name).Append("_push,").Append(f.name).Append("_width,").Append(f.name).Append("_need");
            // ankle from the hip joint in the hip's axes, cm (forward, outward, ventral), and over the leg's length
            foreach (var f in feet) sb.Append(',').Append(f.name).Append("_pf,").Append(f.name).Append("_po,").Append(f.name).Append("_pv,").Append(f.name).Append("_reach");
            // how far each kick loop swings out for the turn (deg)
            foreach (var f in feet) sb.Append(',').Append(f.name).Append("_kyaw");
            // the fluke: how far in (0..1), its beat (cycles), the hip bone's pitch (deg) and each ankle's height over its hip joint (cm)
            sb.Append(",fluke,flukePh,hipPitch,chestPitch");
            foreach (var f in feet) sb.Append(',').Append(f.name).Append("_heave");
            // 1 = SpineKickWave sent a wave packet down the spine
            sb.Append(",pulse");
            // each web's fore-aft speed through the water, m/s
            foreach (var f in feet) sb.Append(',').Append(f.name).Append("_wfwd");
            // the tail tip's bend from the hips' line, deg, + = right; the waist's side bend, deg, + = curled right; the
            // hind pushes' bend on the spine, -1..1; how deep the tail sits in the hind legs, cm; the tail tip's lift above
            // the hips' line, deg, + = toward the back
            sb.Append(",tail,waist,bend,tailHit,tailLift");
            // each web's push on the body along its heading, N
            foreach (var f in feet) sb.Append(',').Append(f.name).Append("_wpush");
            // the web middle's speed up through the water (m/s) and its normal's up component
            foreach (var f in feet) sb.Append(',').Append(f.name).Append("_wup,").Append(f.name).Append("_nup");
            // which way the toes point: up and forward components
            foreach (var f in feet) sb.Append(',').Append(f.name).Append("_tup,").Append(f.name).Append("_tfwd");
            if (brain != null) sb.Append(",posture,supine,roll,dSpeedTarget,dSpeedCap,dYawRate,dSurgeN,dSwayN,dYawNm,dBank");
            bool allocated = brain != null && brain.HasLimbGeometry;
            if (allocated)
            {
                sb.Append(",aSurgeN,aSwayN,aYawNm,saturated");
                for (int i = 0; i < OtterRig.LimbCount; i++) sb.Append($",{(LimbId)i}_fx,{(LimbId)i}_fy,{(LimbId)i}_mz");
                // phase -1 = resting; pw 1 = in the power phase
                for (int i = 0; i < OtterRig.LimbCount; i++) sb.Append($",{(LimbId)i}_ph,{(LimbId)i}_dr,{(LimbId)i}_pw");
            }
            sb.AppendLine();
            float t0 = samples.Count > 0 ? samples[0].t : 0f;
            foreach (var s in samples)
            {
                sb.Append(string.Format(ci, "{0:F4},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7:F1},{8:F3},{9:F3},{10:F2},{11},{12:F2},{13:F2},{14:F2},{15:F2}",
                    s.t - t0, s.seg, s.held ? 1 : 0, s.zone, s.intentYaw, s.steerYaw, s.bodyYaw, s.yawRate, s.speedFwd, s.speedLat,
                    s.targetSpeed, s.kick ? 1 : 0, s.demand, s.headYaw, s.chestBend, s.chestRoll));
                sb.Append(',').Append(s.reach.ToString("F2", ci));
                foreach (var x in s.limb) sb.Append(',').Append(x.ToString("F2", ci));
                foreach (var x in s.limbOut) sb.Append(',').Append(x.ToString("F2", ci));
                foreach (var x in s.limbSide) sb.Append(',').Append(x);
                for (int k = 0; k < feet.Count; k++)
                    sb.Append(string.Format(ci, ",{0:F2},{1:F2},{2:F2},{3:F2}", s.footFace[k], s.footFwd[k], s.footSpeed[k], s.footNormalFwd[k]));
                sb.Append(',').Append(s.feetGap.ToString("F2", ci));
                foreach (var p in s.flipperPose)
                    sb.Append(string.Format(ci, ",{0:F1},{1:F0},{2:F2},{3:F0}", p.x, p.y, p.z, p.w));
                for (int k = 0; k < feet.Count; k++)
                    sb.Append(string.Format(ci, ",{0:F2},{1:F2},{2:F2},{3:F3}", s.footPath[k].x, s.footPath[k].y, s.footPath[k].z, s.footReach[k]));
                foreach (var y in s.kickYaw) sb.Append(',').Append(y.ToString("F1", ci));
                sb.Append(string.Format(ci, ",{0:F2},{1:F3},{2:F1},{3:F1}", s.fluke, s.flukePhase, s.hipPitch, s.chestPitch));
                foreach (var h in s.footHeave) sb.Append(',').Append(h.ToString("F2", ci));
                sb.Append(',').Append(s.pulse ? 1 : 0);
                foreach (var w in s.footWorldFwd) sb.Append(',').Append(w.ToString("F2", ci));
                sb.Append(',').Append(float.IsNaN(s.tailBend) ? "" : s.tailBend.ToString("F1", ci));
                sb.Append(',').Append(s.waist.ToString("F1", ci));
                sb.Append(',').Append(float.IsNaN(s.swayBend) ? "" : s.swayBend.ToString("F2", ci));
                sb.Append(',').Append(float.IsNaN(s.tailHit) ? "" : s.tailHit.ToString("F2", ci));
                sb.Append(',').Append(float.IsNaN(s.tailLift) ? "" : s.tailLift.ToString("F1", ci));                foreach (var w in s.webPush) sb.Append(',').Append(w.ToString("F3", ci));
                for (int k = 0; k < s.footWorldUp.Length; k++) sb.Append(string.Format(ci, ",{0:F2},{1:F2}", s.footWorldUp[k], s.footNormalUp[k]));
                for (int k = 0; k < s.footToesUp.Length; k++) sb.Append(string.Format(ci, ",{0:F2},{1:F2}", s.footToesUp[k], s.footToesFwd[k]));
                if (brain != null)
                    sb.Append(string.Format(ci, ",{0},{1:F2},{2:F1},{3:F2},{4:F2},{5:F1},{6:F2},{7:F2},{8:F3},{9:F1}",
                        s.posture, s.supine, s.roll, s.speedTarget, s.speedCap, s.yawRateTarget, s.surgeForce, s.swayForce, s.yawMoment, s.bank));
                if (allocated)
                {
                    sb.Append(string.Format(ci, ",{0:F2},{1:F2},{2:F3},{3}", s.achieved.x, s.achieved.y, s.achieved.z, s.saturated));
                    for (int i = 0; i < OtterRig.LimbCount; i++)
                    {
                        Vector2 f = s.allocated ? s.limbForce[i] : Vector2.zero;
                        float mz = s.allocated ? s.limbYaw[i] : 0f;
                        sb.Append(string.Format(ci, ",{0:F2},{1:F2},{2:F3}", f.x, f.y, mz));
                    }
                    for (int i = 0; i < OtterRig.LimbCount; i++)
                    {
                        if (s.rhythm) sb.Append(string.Format(ci, ",{0:F3},{1:F2},{2}", s.phase[i], s.drive[i], (s.powerMask >> i) & 1));
                        else sb.Append(",-1,0,0");
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private string Check(string name, bool ok)
        {
            checkCount++;
            if (ok) passCount++;
            return (ok ? "ok " : "MISS ") + name;
        }

        private string Summary()
        {
            checkCount = passCount = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"# Locomotion playback '{label}', {samples.Count} frames, limbs: {string.Join(" ", limbs.Select(l => l.name))}");
            sb.AppendLine("# times in seconds from segment start; 'lead' = body reaction time minus limb reaction time (+ = limbs first);" +
                          " with the stroke rhythm the limb time is the first turning power stroke");
            sb.AppendLine(feet.Count > 0 ? $"# hind feet at start (body axes): {footInfo}" : "# hind feet: no web found in the skinned mesh");
            if (brain != null && brain.HasLimbGeometry)
            {
                var p = brain.Parameters;
                var al = brain.allocatorSettings;
                sb.AppendLine($"# allocator priorities surge {al.surgePriority} sway {al.swayPriority} yaw {al.yawPriority}, effort {al.effortWeight}, change {al.changeWeight}");
                sb.AppendLine($"# body: length {brain.BodyLength * 100f:F1} cm, mass {p.mass:F2} kg, yaw inertia {p.yawInertia:F4} kg m^2, " +
                              $"drag {p.linearDrag:F2} + {p.quadraticDrag:F2}|v|, angular drag {p.angularLinearDrag:F2}");
                for (int i = 0; i < OtterRig.LimbCount; i++)
                {
                    var g = brain.Geometry[i];
                    if (!g.valid) continue;
                    Vector3 a = (g.anchor - brain.CenterOfMass) * 100f;
                    sb.AppendLine($"#   {(LimbId)i}: joint from centre (fwd {a.x:F1}, right {a.y:F1}, up {a.z:F1}) cm, limb {g.length * 100f:F1} cm");
                }
            }
            var legShapes = movement.GetComponent<HindLegColliders>();
            if (tail != null && legShapes != null && legShapes.Ready)
                sb.AppendLine($"# collision: tail {(tail.collide ? "keeps out of" : "passes through")} the hind legs; tail bends at {tail.joints} joints, radius {tail.Describe()} cm; legs {legShapes.Describe()} cm");
            if (webThrust != null && webThrust.isActiveAndEnabled)
                sb.AppendLine($"# web thrust: water push {webThrust.waterPush:F0} face-on, {webThrust.slicePush:F0} slicing N/(m^2 (m/s)^2), area x{webThrust.areaScale:F2}, " +
                              $"HL {webThrust.Area(LimbId.HL) * 1e4f:F1} cm^2, HR {webThrust.Area(LimbId.HR) * 1e4f:F1} cm^2");
            for (int seg = 0; seg < playback.segments.Count; seg++)
            {
                var idx = Enumerable.Range(0, samples.Count).Where(i => samples[i].seg == seg).ToList();
                if (idx.Count < 2) continue;
                var spec = playback.segments[seg];
                var first = samples[idx[0]];
                float t0 = first.t;
                sb.AppendLine($"\n[{seg}] {spec.name}  ({spec.zone}, {spec.duration:F1}s)");
                int kicks = idx.Count(i => samples[i].kick);
                if (kicks > 0) sb.AppendLine($"  kicks {kicks}");

                if (spec.zone != OtterInputPlayback.Zone.Release)
                {
                    float delta = Mathf.DeltaAngle(first.bodyYaw, playback.TargetYaw(seg));
                    if (Mathf.Abs(delta) >= turnMinDeg) TurnMetrics(sb, idx, t0, first, delta);
                    else if (first.targetSpeed > 0f || samples[idx[idx.Count - 1]].targetSpeed > 0f) SpeedMetrics(sb, idx, t0);
                }
                else if (Mathf.Abs(first.speedFwd) > stopSpeed)
                {
                    StopMetrics(sb, idx, t0);
                }
                if (brain != null) DemandMetrics(sb, idx, seg, t0);
                RhythmMetrics(sb, idx);
                FootMetrics(sb, idx);
                PathMetrics(sb, idx);
                FlukeMetrics(sb, idx);
                BodyMetrics(sb, idx);
            }
            sb.AppendLine($"\nfeel targets met: {passCount}/{checkCount}");
            return sb.ToString();
        }

        // How the trunk and tail bend side to side: the waist's range and its swing about its own mean (half the 5th..95th
        // percentile spread), the same for the tail tip, and how closely the waist follows the hind pushes' bend.
        private void BodyMetrics(StringBuilder sb, List<int> idx)
        {
            var ci = CultureInfo.InvariantCulture;
            var waist = idx.Select(i => samples[i].waist).OrderBy(x => x).ToList();
            var tails = idx.Select(i => samples[i].tailBend).Where(x => !float.IsNaN(x)).OrderBy(x => x).ToList();
            float P(List<float> v, float q) => v[Mathf.Clamp(Mathf.RoundToInt(q * (v.Count - 1)), 0, v.Count - 1)];
            string Range(List<float> v) => v.Count > 0
                ? $"{P(v, 0.05f).ToString("F1", ci)}..{P(v, 0.95f).ToString("F1", ci)} deg (swing ±{(0.5f * (P(v, 0.95f) - P(v, 0.05f))).ToString("F1", ci)})"
                : "-";
            var pairs = idx.Select(i => samples[i]).Where(s => !float.IsNaN(s.swayBend)).ToList();
            string follow = "";
            if (pairs.Count > 2)
            {
                float mw = pairs.Average(s => s.waist), mb = pairs.Average(s => s.swayBend);
                float cov = pairs.Sum(s => (s.waist - mw) * (s.swayBend - mb));
                float vw = Mathf.Sqrt(pairs.Sum(s => (s.waist - mw) * (s.waist - mw)));
                float vb = Mathf.Sqrt(pairs.Sum(s => (s.swayBend - mb) * (s.swayBend - mb)));
                if (vw > 1e-4f && vb > 1e-4f) follow = $" | waist follows the pushes' bend: corr {(cov / (vw * vb)).ToString("+0.00;-0.00", ci)}";
            }
            sb.AppendLine($"  body (seen from above, + = curled right): waist {Range(waist)} | tail tip {Range(tails)}{follow}");
            var hits = idx.Select(i => samples[i].tailHit).Where(x => !float.IsNaN(x)).ToList();
            var lifts = idx.Select(i => samples[i].tailLift).Where(x => !float.IsNaN(x)).OrderBy(x => x).ToList();
            if (hits.Count > 0)
                sb.AppendLine($"  tail in the hind legs: deepest {hits.Max().ToString("F2", ci)} cm, in them {hits.Count(x => x > 0.2f) / (float)hits.Count:P0} of frames (over 2 mm) | tip lift (+ = up) {Range(lifts)}");
        }

        // Whether the hind feet push water: how squarely the web faces its motion relative to the hip, weighted by the
        // fore-aft speed, while sweeping back (power, should be near 1) and coming forward (recovery, near 0).
        private void FootMetrics(StringBuilder sb, List<int> idx)
        {
            if (feet.Count == 0) return;
            var ci = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            for (int k = 0; k < feet.Count; k++)
            {
                float backWeight = 0f, backFace = 0f, forwardWeight = 0f, forwardFace = 0f, peak = 0f;
                foreach (int i in idx)
                {
                    var s = samples[i];
                    if (s.footFace == null || k >= s.footFace.Length || s.footFace[k] < 0f) continue;
                    float u = s.footFwd[k];
                    if (u < -footSweepSpeed)
                    {
                        backWeight -= u;
                        backFace -= u * s.footFace[k];
                        peak = Mathf.Max(peak, -u);
                    }
                    else if (u > footSweepSpeed)
                    {
                        forwardWeight += u;
                        forwardFace += u * s.footFace[k];
                    }
                }
                string Mean(float sum, float weight) => weight > 1e-3f ? (sum / weight).ToString("F2", ci) : "-";
                // through its power strokes, how fast the web moves forward through the water beside the body
                var pushing = System.Enum.TryParse<LimbId>(feet[k].name, out var id)
                    ? idx.Where(i => (samples[i].powerMask & (1 << (int)id)) != 0 && samples[i].footWorldFwd != null).ToList()
                    : new List<int>();
                string water = pushing.Count > 0
                    ? $", pushing web {pushing.Average(i => samples[i].footWorldFwd[k]).ToString("F2", ci)} vs body {pushing.Average(i => samples[i].speedFwd).ToString("F2", ci)} m/s"
                    : "";
                if (webThrust != null && idx.Count > 0)
                    water += $", web push {idx.Average(i => samples[i].webPush[k]).ToString("F3", ci)} N avg" +
                             (pushing.Count > 0 ? $" ({pushing.Average(i => samples[i].webPush[k]).ToString("F3", ci)} pushing)" : "");
                parts.Add($"{feet[k].name} back {Mean(backFace, backWeight)} fwd {Mean(forwardFace, forwardWeight)} peak {peak.ToString("F2", ci)} m/s{water}");
            }
            var gaps = idx.Select(i => samples[i].feetGap).Where(g => g >= 0f).ToList();
            string closest = gaps.Count > 0 ? $" | webs closest {gaps.Min().ToString("F1", ci)} cm" : "";
            sb.AppendLine($"  feet (web facing its motion, 1 = flat-on push, 0 = edge-on): {string.Join(" | ", parts)}{closest}");
        }

        // The shape of each ankle's path relative to the hip: how far the leg draws in and stretches out (reach over the
        // leg's length, 5th..95th percentile), how its travel splits between fore-aft, outward and dorso-ventral, and the
        // loop's size along its principal axes (peak to peak) with flat = thinnest / middle (0 = stuck in one plane).
        // The axes weigh each sample by the distance moved, so the path's shape counts, not where the foot lingers.
        private void PathMetrics(StringBuilder sb, List<int> idx)
        {
            if (feet.Count == 0) return;
            var ci = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            for (int k = 0; k < feet.Count; k++)
            {
                var seq = idx.Select(i => samples[i]).Where(s => s.footPath != null && k < s.footPath.Length).ToList();
                if (seq.Count < 8) continue;
                var reach = seq.Select(s => s.footReach[k]).OrderBy(r => r).ToList();
                float lo = reach[(int)(0.05f * (reach.Count - 1))], hi = reach[(int)(0.95f * (reach.Count - 1))];
                Vector3 travel = Vector3.zero, mean = Vector3.zero, pushed = Vector3.zero;
                var moved = new float[seq.Count];
                float length = 0f;
                for (int i = 1; i < seq.Count; i++)
                {
                    Vector3 d = seq[i].footPath[k] - seq[i - 1].footPath[k];
                    travel += new Vector3(Mathf.Abs(d.x), Mathf.Abs(d.y), Mathf.Abs(d.z));
                    moved[i] = d.magnitude;
                    length += moved[i];
                    if (seq[i].flipperPose != null && seq[i].flipperPose[k].y > 0.5f && seq[i - 1].flipperPose[k].y > 0.5f) pushed += d;
                }
                // which way the pushes sweep the foot seen from the back: 0 = straight back, + = out, +-180 = forward
                string heading = pushed.sqrMagnitude > 1e-4f ? $"{Mathf.Atan2(pushed.y, -pushed.x) * Mathf.Rad2Deg:F0}" : "-";
                float yaw = seq.Where(s => s.kickYaw != null).Select(s => s.kickYaw[k]).DefaultIfEmpty(0f).Average();
                if (length < 1e-3f) length = moved[0] = 1f;
                for (int i = 0; i < seq.Count; i++) mean += seq[i].footPath[k] * (moved[i] / length);
                var c = new float[3, 3];
                for (int i = 0; i < seq.Count; i++)
                {
                    Vector3 d = seq[i].footPath[k] - mean;
                    for (int a = 0; a < 3; a++)
                        for (int b = 0; b < 3; b++)
                            c[a, b] += d[a] * d[b] * moved[i] / length;
                }
                OtterRig.SymmetricEigen(c, out var values, out var vectors);
                var order = Enumerable.Range(0, 3).OrderByDescending(i => values[i]).ToArray();
                float Span(int i) => 2f * Mathf.Sqrt(2f * Mathf.Max(0f, values[order[i]]));
                Vector3 main = vectors[order[0]].normalized;
                float total = Mathf.Max(travel.x + travel.y + travel.z, 1e-4f);
                parts.Add($"{feet[k].name} reach {lo.ToString("F2", ci)}-{hi.ToString("F2", ci)}" +
                          $" travel fwd/out/vent {100f * travel.x / total:F0}/{100f * travel.y / total:F0}/{100f * travel.z / total:F0}%" +
                          $" loop {Span(0).ToString("F1", ci)}x{Span(1).ToString("F1", ci)}x{Span(2).ToString("F1", ci)} cm" +
                          $" flat {(Span(1) > 1e-3f ? Span(2) / Span(1) : 0f).ToString("F2", ci)}" +
                          $" main ({Mathf.Abs(main.x).ToString("F2", ci)},{Mathf.Abs(main.y).ToString("F2", ci)},{Mathf.Abs(main.z).ToString("F2", ci)})" +
                          $" push {heading} deg, loop swung {yaw.ToString("F0", ci)} deg");
            }
            sb.AppendLine($"  hind paths (ankle from hip joint; travel and main axis in fwd/out/vent; push heading from straight back, + = out): {string.Join(" | ", parts)}");
        }

        // The fluke over the frames it is mostly in: its beat, how far the hips pitch and the feet heave (5th..95th
        // percentile), and how long the feet run behind the hips (the delay that best lines the feet's heave up with
        // the tail end of the hips dropping; 0 = the legs move rigidly with the hips).
        private void FlukeMetrics(StringBuilder sb, List<int> idx)
        {
            var seq = idx.Select(i => samples[i]).Where(s => s.fluke > 0.5f && s.footHeave != null).ToList();
            if (seq.Count < 20 || feet.Count == 0) return;
            var ci = CultureInfo.InvariantCulture;
            float duration = Mathf.Max(1e-3f, seq[seq.Count - 1].t - seq[0].t);
            int beats = 0;
            for (int i = 1; i < seq.Count; i++)
                if (seq[i].flukePhase < seq[i - 1].flukePhase - 0.5f) beats++;
            string Range(IEnumerable<float> xs)
            {
                var o = xs.OrderBy(x => x).ToList();
                return $"{o[(int)(0.05f * (o.Count - 1))].ToString("F1", ci)}..{o[(int)(0.95f * (o.Count - 1))].ToString("F1", ci)}";
            }
            var parts = new List<string>
            {
                $"beat {(beats / duration).ToString("F2", ci)} Hz",
                $"pitch hips {Range(seq.Select(s => s.hipPitch))} chest {Range(seq.Select(s => s.chestPitch))} deg",
            };
            float pitchMean = seq.Average(s => s.hipPitch);
            for (int k = 0; k < feet.Count; k++)
            {
                float heaveMean = seq.Average(s => s.footHeave[k]);
                float bestLag = 0f, best = float.MinValue;
                for (float lag = 0f; lag <= 0.6f; lag += 0.02f)
                {
                    float sum = 0f;
                    int n = 0, j = 0;
                    for (int i = 0; i < seq.Count; i++)
                    {
                        while (j < seq.Count - 1 && seq[j].t < seq[i].t + lag) j++;
                        if (seq[j].t < seq[i].t + lag - 0.03f) break;
                        sum += -(seq[i].hipPitch - pitchMean) * (seq[j].footHeave[k] - heaveMean);
                        n++;
                    }
                    if (n > 10 && sum / n > best) { best = sum / n; bestLag = lag; }
                }
                parts.Add($"{feet[k].name} heave {Range(seq.Select(s => s.footHeave[k]))} cm reach {seq.Average(s => s.footReach[k]).ToString("F2", ci)} behind hips {bestLag.ToString("F2", ci)} s");
            }
            sb.AppendLine($"  fluke ({seq.Count} frames): {string.Join(" | ", parts)}");
        }

        // What the demand layer asked for over the segment, next to what the body actually did.
        private void DemandMetrics(StringBuilder sb, List<int> idx, int seg, float t0)
        {
            var ci = CultureInfo.InvariantCulture;
            var seq = idx.Select(i => samples[i]).ToList();
            var last = seq[seq.Count - 1];
            foreach (var e in postureEvents.Where(e => e.seg == seg))
                sb.AppendLine($"  posture -> {e.posture} at {(e.t - t0).ToString("F2", ci)} s ({e.reason})");
            Sample PeakBy(System.Func<Sample, float> f) => seq.OrderByDescending(x => Mathf.Abs(f(x))).First();
            var rPeak = PeakBy(x => x.yawRateTarget);
            var mPeak = PeakBy(x => x.yawMoment);
            var fPeak = PeakBy(x => x.surgeForce);
            var yPeak = PeakBy(x => x.swayForce);
            var bPeak = PeakBy(x => x.bank);
            sb.AppendLine($"  demand: end {last.posture} (supine {last.supine:F2}), speed target {last.speedTarget:F2} cap {last.speedCap:F2}; " +
                          $"peak r* {rPeak.yawRateTarget:F0} deg/s, Mz {mPeak.yawMoment:F3} N·m, Fx {fPeak.surgeForce:F1} N (mean {seq.Average(x => x.surgeForce):F1}), " +
                          $"Fy {yPeak.swayForce:F1} N, bank {bPeak.bank:F0} deg at {(bPeak.t - t0).ToString("F2", ci)} s");

            var alloc = seq.Where(x => x.allocated).ToList();
            if (alloc.Count == 0) return;
            string Tracking(System.Func<Sample, float> demanded, System.Func<Sample, float> achieved)
            {
                float total = alloc.Sum(x => Mathf.Abs(demanded(x)));
                if (total < 1e-3f * alloc.Count) return "-";
                return (1f - alloc.Sum(x => Mathf.Abs(demanded(x) - achieved(x))) / total).ToString("P0", ci);
            }
            float yawPeak = alloc.Max(x => Mathf.Abs(x.yawMoment));
            var turning = alloc.Where(x => Mathf.Abs(x.yawMoment) > 0.1f * yawPeak && yawPeak > 1e-3f).ToList();
            // each limb's share of the moment actually produced, signed along that frame's total
            float foreYaw = turning.Sum(x => (x.limbYaw[(int)LimbId.FL] + x.limbYaw[(int)LimbId.FR]) * Mathf.Sign(x.achieved.z));
            float allYaw = turning.Sum(x => Mathf.Abs(x.achieved.z));
            float hindThrust = alloc.Sum(x => Mathf.Max(0f, x.limbForce[(int)LimbId.HL].x) + Mathf.Max(0f, x.limbForce[(int)LimbId.HR].x));
            float allThrust = alloc.Sum(x => x.limbForce.Sum(f => Mathf.Max(0f, f.x)));
            float peakLimb = alloc.Max(x => x.limbForce.Max(f => f.magnitude));
            float saturatedShare = alloc.Count(x => x.saturated > 0) / (float)alloc.Count;
            string Share(float part, float whole) => whole > 1e-4f ? (part / whole).ToString("P0", ci) : "-";
            sb.AppendLine($"  allocation: met yaw {Tracking(x => x.yawMoment, x => x.achieved.z)}, surge {Tracking(x => x.surgeForce, x => x.achieved.x)}, " +
                          $"sway {Tracking(x => x.swayForce, x => x.achieved.y)} | yaw from forepaws {Share(foreYaw, allYaw)}, thrust from hind {Share(hindThrust, allThrust)}, " +
                          $"braking {alloc.Average(x => x.limbForce.Sum(f => Mathf.Max(0f, -f.x))):F2} N avg | " +
                          $"peak limb {peakLimb:F1} N, limits hit {saturatedShare.ToString("P0", ci)} of frames");
        }

        // Left-minus-right IK target offset of a limb pair; symmetric cruise strokes cancel, steering strokes don't.
        private float Asymmetry(Sample s, string pair)
        {
            int l = limbs.FindIndex(x => x.name == pair + "L"), r = limbs.FindIndex(x => x.name == pair + "R");
            return l >= 0 && r >= 0 ? s.limb[l] - s.limb[r] : 0f;
        }

        private void TurnMetrics(StringBuilder sb, List<int> idx, float t0, Sample first, float delta)
        {
            // a near-reversal can legitimately go either way; measure against the way the body actually went
            float net = samples[idx[idx.Count - 1]].bodyYaw - first.bodyYaw;
            if (Mathf.Abs(delta) > 170f && Mathf.Abs(net) > 90f && Mathf.Sign(net) != Mathf.Sign(delta))
                delta -= 360f * Mathf.Sign(delta);
            float target = first.bodyYaw + delta;
            float? tBody = null, tHead = null, tSpine = null, tFront = null, tHind = null, t50 = null, t90 = null;
            float maxProgress = 0f, peakRate = 0f, lastOutside = 0f;
            float front0 = Asymmetry(first, "F"), hind0 = Asymmetry(first, "H");
            foreach (int i in idx)
            {
                var s = samples[i];
                float t = s.t - t0;
                float turned = s.bodyYaw - first.bodyYaw;
                float progress = turned / delta;
                if (tBody == null && Mathf.Abs(turned) > bodyYawThresholdDeg) tBody = t;
                if (tHead == null && Mathf.Abs(Mathf.DeltaAngle(first.headYaw, s.headYaw)) > headYawThresholdDeg) tHead = t;
                if (tSpine == null && Mathf.Abs(Mathf.DeltaAngle(first.chestBend, s.chestBend)) > spineBendThresholdDeg) tSpine = t;
                if (tFront == null && Mathf.Abs(Asymmetry(s, "F") - front0) > limbThresholdCm) tFront = t;
                if (tHind == null && Mathf.Abs(Asymmetry(s, "H") - hind0) > limbThresholdCm) tHind = t;
                if (t50 == null && progress >= 0.5f) t50 = t;
                if (t90 == null && progress >= 0.9f) t90 = t;
                maxProgress = Mathf.Max(maxProgress, progress);
                peakRate = Mathf.Max(peakRate, Mathf.Abs(s.yawRate));
                if (Mathf.Abs(target - s.bodyYaw) > settleToleranceDeg) lastOutside = t;
            }
            float end = samples[idx[idx.Count - 1]].t - t0;
            string F(float? x) => x.HasValue ? x.Value.ToString("F2", CultureInfo.InvariantCulture) : "never";
            var (tStroke, strokeLimb) = TurningStroke(idx, t0, delta);
            float? tLimb = tFront.HasValue && tHind.HasValue ? Mathf.Min(tFront.Value, tHind.Value) : tFront ?? tHind;
            if (first.rhythm) tLimb = tStroke;
            float? leadValue = tBody.HasValue && tLimb.HasValue ? tBody.Value - tLimb.Value : (float?)null;
            string lead = leadValue.HasValue ? leadValue.Value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) : "n/a";
            float overshoot = Mathf.Max(0f, maxProgress - 1f) * Mathf.Abs(delta);
            string stroke = first.rhythm ? $"  turning stroke {F(tStroke)}{(strokeLimb.HasValue ? $" ({strokeLimb})" : "")}" : "";
            sb.AppendLine($"  turn {delta:+0;-0} deg | first reaction: head {F(tHead)}  spine {F(tSpine)}  limb L/R asymmetry front {F(tFront)} hind {F(tHind)}{stroke}  body {F(tBody)} | limb lead {lead}");
            sb.AppendLine($"  progress: 50% at {F(t50)}  90% at {F(t90)}  overshoot {overshoot:F1} deg  " +
                          $"settled(±{settleToleranceDeg:F0}) {(lastOutside < end - 0.2f ? lastOutside.ToString("F2", CultureInfo.InvariantCulture) : "no")}  peak yaw rate {peakRate:F0} deg/s");
            var bends = idx.Select(i => samples[i]).Where(s => !float.IsNaN(s.tailBend)).ToList();
            if (bends.Count > 0)
            {
                // toward the turn = + ; the swing back the other way after it
                var toward = bends.Select(s => s.tailBend * Mathf.Sign(delta)).ToList();
                var peakAt = bends[toward.IndexOf(toward.Max())];
                sb.AppendLine($"  tail: bend toward the turn peak {toward.Max():F0} deg at {F(peakAt.t - t0)}, mean {toward.Average():F0} deg, swing back {Mathf.Max(0f, -toward.Min()):F0} deg");
            }
            float budget = Mathf.Lerp(turn90Budget, turn170Budget, Mathf.InverseLerp(90f, 170f, Mathf.Abs(delta)));
            sb.AppendLine("  targets: " + string.Join(" | ",
                Check($"head <= {headReactionMax:F2}", tHead.HasValue && tHead.Value <= headReactionMax),
                Check($"limb lead >= +{limbLeadMin:F2}", leadValue.HasValue && leadValue.Value >= limbLeadMin),
                Check($"90% <= {budget:F2}", t90.HasValue && t90.Value <= budget),
                Check($"overshoot <= {overshootMaxDeg:F0}", overshoot <= overshootMaxDeg)));
        }

        // First power stroke begun in the segment whose allocated force yaws the body toward the target, by at least
        // a tenth of the segment's peak yaw demand: the moment the limbs start doing the turn.
        private (float?, LimbId?) TurningStroke(List<int> idx, float t0, float delta)
        {
            if (!samples[idx[0]].rhythm) return (null, null);
            float peak = idx.Max(i => Mathf.Abs(samples[i].yawMoment));
            if (peak < 1e-4f) return (null, null);
            var before = idx[0] > 0 && samples[idx[0] - 1].rhythm ? samples[idx[0] - 1].strokes : new int[OtterRig.LimbCount];
            foreach (int i in idx)
            {
                var s = samples[i];
                if (!s.rhythm) continue;
                for (int k = 0; k < OtterRig.LimbCount; k++)
                    if (s.strokes[k] > before[k] && s.limbYaw[k] * Mathf.Sign(delta) > 0.1f * peak)
                        return (s.t - t0, (LimbId)k);
                before = s.strokes;
            }
            return (null, null);
        }

        // Stroke counts and how the pairs are phased: 0.5 = alternating, 0 = together.
        private void RhythmMetrics(StringBuilder sb, List<int> idx)
        {
            var ci = CultureInfo.InvariantCulture;
            var seq = idx.Select(i => samples[i]).Where(s => s.rhythm).ToList();
            if (seq.Count == 0) return;
            var before = idx[0] > 0 && samples[idx[0] - 1].rhythm ? samples[idx[0] - 1].strokes : new int[OtterRig.LimbCount];
            var last = seq[seq.Count - 1];
            string counts = string.Join(" ", Enumerable.Range(0, OtterRig.LimbCount).Select(k => $"{(LimbId)k} {last.strokes[k] - before[k]}"));
            string PairOffset(LimbId left, LimbId right)
            {
                var both = seq.Where(s => s.phase[(int)left] >= 0f && s.phase[(int)right] >= 0f).ToList();
                if (both.Count < 10) return "-";
                float c = 0f, sn = 0f;
                foreach (var s in both)
                {
                    float a = 2f * Mathf.PI * (s.phase[(int)left] - s.phase[(int)right]);
                    c += Mathf.Cos(a);
                    sn += Mathf.Sin(a);
                }
                float mean = Mathf.Repeat(Mathf.Atan2(sn, c) / (2f * Mathf.PI), 1f);
                float spread = 1f - Mathf.Sqrt(c * c + sn * sn) / both.Count;
                return $"{mean.ToString("F2", ci)} (spread {spread.ToString("F2", ci)}, {both.Count * 100 / seq.Count}% of frames)";
            }
            float idle = seq.Count(s => s.phase.All(p => p < 0f)) / (float)seq.Count;
            float together = seq.Count(s => s.hindTogether) / (float)seq.Count;
            sb.AppendLine($"  rhythm: strokes {counts} | hind kicking together {together.ToString("P0", ci)} of frames, L-R phase {PairOffset(LimbId.HL, LimbId.HR)}, " +
                          $"fore L-R {PairOffset(LimbId.FL, LimbId.FR)} | all limbs resting {idle.ToString("P0", ci)} of frames");
        }

        private void SpeedMetrics(StringBuilder sb, List<int> idx, float t0)
        {
            var ci = CultureInfo.InvariantCulture;
            float target = samples[idx[idx.Count - 1]].targetSpeed;
            bool fromRest = Mathf.Abs(samples[idx[0]].speedFwd) < stopSpeed;
            float? t90 = null, tPush = null;
            foreach (int i in idx)
            {
                if (t90 == null && samples[i].speedFwd >= 0.9f * target) t90 = samples[i].t - t0;
                if (tPush == null && fromRest && samples[i].speedFwd > 0.3f * target) tPush = samples[i].t - t0;
            }
            float end = samples[idx[idx.Count - 1]].t;
            var tail = idx.Where(i => samples[i].t >= end - 1f).Select(i => samples[i]).ToList();
            float mean = tail.Average(s => s.speedFwd);
            float minS = tail.Min(s => s.speedFwd), maxS = tail.Max(s => s.speedFwd);
            var kickTimes = idx.Where(i => samples[i].kick).Select(i => samples[i].t).ToList();
            string interval = kickTimes.Count > 1 ? ((kickTimes[kickTimes.Count - 1] - kickTimes[0]) / (kickTimes.Count - 1)).ToString("F2", ci) + " s" : "n/a";
            sb.AppendLine($"  speed: target {target:F2} m/s, 90% at {(t90.HasValue ? t90.Value.ToString("F2", ci) : "never")} s" +
                          (fromRest ? $", first push (>30% of target) at {(tPush.HasValue ? tPush.Value.ToString("F2", ci) : "never")} s" : "") +
                          $", last 1 s mean {mean:F2} (min {minS:F2}, max {maxS:F2}) m/s, kick interval {interval}");
            float ripple = mean > 1e-3f ? (maxS - minS) / (2f * mean) : float.PositiveInfinity;
            sb.AppendLine("  targets: " + string.Join(" | ",
                Check($"mean within ±{speedMeanTolerance:P0} of target ({mean / Mathf.Max(target, 1e-3f):P0})", Mathf.Abs(mean - target) <= speedMeanTolerance * target),
                Check($"ripple <= ±{speedRippleMax:P0} (±{ripple:P0})", ripple <= speedRippleMax)));
        }

        private void StopMetrics(StringBuilder sb, List<int> idx, float t0)
        {
            float dist = 0f;
            float? tStop = null;
            foreach (int i in idx)
            {
                var s = samples[i];
                if (tStop != null) break;
                dist += Mathf.Abs(s.speedFwd) * s.dt;
                if (Mathf.Abs(s.speedFwd) < stopSpeed) tStop = s.t - t0;
            }
            sb.AppendLine($"  stop: from {samples[idx[0]].speedFwd:F2} m/s, below {stopSpeed:F2} at {(tStop.HasValue ? tStop.Value.ToString("F2", CultureInfo.InvariantCulture) : "never")} s, coasted {dist:F2} m");
            if (samples[idx[0]].speedFwd >= 1f)
                sb.AppendLine("  targets: " + Check($"glide {stopTimeMin:F1}-{stopTimeMax:F1} s", tStop.HasValue && tStop.Value >= stopTimeMin && tStop.Value <= stopTimeMax));
        }
    }
}
