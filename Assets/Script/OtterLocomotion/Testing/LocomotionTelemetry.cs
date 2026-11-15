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
        public float stopSpeed = 0.1f;

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

        private struct Sample
        {
            public float t, dt, intentYaw, steerYaw, bodyYaw, yawRate, speedFwd, speedLat, targetSpeed, demand;
            public float headYaw, chestBend, chestRoll, reach;
            public int seg;
            public bool held, kick;
            public MovementControllerRB.MoveZone zone;
            public float[] limb;
            public Posture posture;
            public float supine, roll, speedTarget, speedCap, yawRateTarget, surgeForce, swayForce, yawMoment, bank;
            public bool allocated;
            public Vector3 achieved;
            public Vector2[] limbForce;
            public float[] limbYaw;
            public int saturated;
            public bool rhythm;
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
        private readonly List<Sample> samples = new List<Sample>();
        private Transform hip, chest, head;
        private bool kickThisFrame;
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
            }
            limbs.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            hip = OtterRig.FindActiveBone(movement.transform, OtterRig.Hip);
            chest = OtterRig.FindActiveBone(movement.transform, OtterRig.Chest);
            head = OtterRig.FindActiveBone(movement.transform, OtterRig.Head);
            movement.OnKickCycleStart += OnKick;
            if (brain == null) brain = movement.GetComponent<OtterLocomotionBrain>();
            if (brain != null) brain.Posture.Changed += OnPosture;
        }

        private void OnDestroy()
        {
            if (movement != null) movement.OnKickCycleStart -= OnKick;
            if (brain != null) brain.Posture.Changed -= OnPosture;
        }

        private void OnPosture(Posture posture, string reason)
        {
            if (playback != null && playback.SegmentIndex >= 0 && !playback.Finished)
                postureEvents.Add(new PostureEvent { t = Time.time, seg = playback.SegmentIndex, posture = posture, reason = reason });
        }

        private void OnKick(MovementControllerRB.KickCycleEvent e) => kickThisFrame = true;

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
                targetSpeed = movement.GetTargetSpeed(), demand = movement.GetDemand01(), kick = kickThisFrame, reach = movement.GetReach01(),
                // bones point at their child along local +Y, so .up is the head/chest/hip direction
                headYaw = head ? Vector3.SignedAngle(fwd, Flat(head.up), Vector3.up) : 0f,
                chestBend = hip && chest ? Vector3.SignedAngle(Flat(hip.up), Flat(chest.up), Vector3.up) : 0f,
                chestRoll = chest ? ChestRoll(body) : 0f,
                limb = limbs.Select(l => l.space.TransformVector(l.space.InverseTransformPoint(l.target.position) - l.restLocal).magnitude * 100f).ToArray(),
            };
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
                    s.phase = r.Select(x => x.active ? x.phase : -1f).ToArray();
                    s.drive = r.Select(x => x.drive).ToArray();
                    s.strokes = r.Select(x => x.strokes).ToArray();
                    for (int i = 0; i < r.Length; i++)
                        if (r[i].InPower) s.powerMask |= 1 << i;
                }
            }
            kickThisFrame = false;
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
            }
            sb.AppendLine($"\nfeel targets met: {passCount}/{checkCount}");
            return sb.ToString();
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
            sb.AppendLine($"  rhythm: strokes {counts} | hind L-R phase {PairOffset(LimbId.HL, LimbId.HR)}, fore L-R {PairOffset(LimbId.FL, LimbId.FR)} | all limbs resting {idle.ToString("P0", ci)} of frames");
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
                if (tPush == null && fromRest && samples[i].speedFwd > 0.3f) tPush = samples[i].t - t0;
            }
            float end = samples[idx[idx.Count - 1]].t;
            var tail = idx.Where(i => samples[i].t >= end - 1f).Select(i => samples[i]).ToList();
            float mean = tail.Average(s => s.speedFwd);
            float minS = tail.Min(s => s.speedFwd), maxS = tail.Max(s => s.speedFwd);
            var kickTimes = idx.Where(i => samples[i].kick).Select(i => samples[i].t).ToList();
            string interval = kickTimes.Count > 1 ? ((kickTimes[kickTimes.Count - 1] - kickTimes[0]) / (kickTimes.Count - 1)).ToString("F2", ci) + " s" : "n/a";
            sb.AppendLine($"  speed: target {target:F2} m/s, 90% at {(t90.HasValue ? t90.Value.ToString("F2", ci) : "never")} s" +
                          (fromRest ? $", first push (>0.3 m/s) at {(tPush.HasValue ? tPush.Value.ToString("F2", ci) : "never")} s" : "") +
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
