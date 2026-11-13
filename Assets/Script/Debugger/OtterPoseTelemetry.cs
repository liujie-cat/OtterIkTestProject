using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using OtterIK.Curve;
using OtterIK.Neo.Experiment;

/// <summary>
/// Records the final pose of the spine/neck bones (after every driver and IK has run) so that
/// legacy and new drivers can be compared with the same numbers.
/// - Per frame CSV in Logs/ (body-space yaw/pitch/roll of every bone relative to its rest pose).
/// - One summary line per bone in the Console when Play mode stops.
/// </summary>
[DefaultExecutionOrder(1000)]
[DisallowMultipleComponent]
public class OtterPoseTelemetry : MonoBehaviour
{
    [Header("References")]
    public MovementControllerRB movement;
    [Tooltip("Body frame. Defaults to the MovementControllerRB transform.")]
    public Transform body;
    [Tooltip("Bones from hip to head. Auto-filled on Reset from SpineDecouplerWithShadows + neck joints.")]
    public Transform[] bones;

    [Header("Recording")]
    public bool writeCsv = true;
    public bool logRigOnStart = true;
    [Tooltip("Frame-to-frame change in angular speed (deg/s) counted as a pop.")]
    public float popThresholdDegPerSec = 600f;
    [Tooltip("Head yaw (deg) above which a frame counts as turning, for the hip inside/outside stats.")]
    public float turnHeadYawDeg = 10f;
    [Tooltip("Sideways hip displacement (m) that counts as inside / outside the turn.")]
    public float hipSideThreshold = 0.01f;

    private class BoneStats
    {
        public Quaternion restRel;
        public Vector3 restDir;
        public Quaternion prevDelta;
        public float prevSpeed;
        public bool hasPrev;
        public float maxYaw, maxPitch, maxRoll, maxSpeed, maxJointBend;
        public int pops;
    }

    private readonly List<BoneStats> stats = new List<BoneStats>();
    private StreamWriter csv;
    private string csvPath;
    private string mode;
    private int frames;
    private float startTime;
    private float lastRootYaw;
    private bool hasRootYaw;
    private float maxRootYawRate;
    private float maxSpeed;
    private float restHipX;
    private float maxHipSide;
    private int turnFrames, hipOutsideFrames, hipInsideFrames;
    private readonly StringBuilder row = new StringBuilder(512);

    private void Reset()
    {
        movement = GetComponentInParent<MovementControllerRB>();
        if (movement != null) body = movement.transform;

        var list = new List<Transform>();
        var decoupler = GetComponent<SpineDecouplerWithShadows>();
        if (decoupler != null && decoupler.spineJoints != null) list.AddRange(decoupler.spineJoints);

        var neck = GetComponent<ExpShadowNeckController>();
        if (neck != null && neck.neckJoints != null) list.AddRange(neck.neckJoints);

        bones = list.ToArray();
    }

    private void Start()
    {
        if (movement == null) movement = GetComponentInParent<MovementControllerRB>();
        if (body == null && movement != null) body = movement.transform;
        if (body == null || bones == null || bones.Length == 0)
        {
            Debug.LogWarning("[OtterTelemetry] body or bones not assigned; telemetry disabled.", this);
            enabled = false;
            return;
        }

        var curve = FindObjectOfType<OtterBodyCurve>();
        mode = curve != null && curve.isActiveAndEnabled
            ? $"curve-{curve.backMode}-{(curve.segmentFollowsOwnBone ? "ownbone" : "legacyfk")}"
            : "legacy";

        Quaternion bodyInv = Quaternion.Inverse(body.rotation);
        stats.Clear();
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            var st = new BoneStats();
            if (b != null)
            {
                st.restRel = bodyInv * b.rotation;
                Vector3 dir = Vector3.forward;
                if (i + 1 < bones.Length && bones[i + 1] != null) dir = bones[i + 1].position - b.position;
                else if (i > 0 && bones[i - 1] != null) dir = b.position - bones[i - 1].position;
                st.restDir = (bodyInv * dir).normalized;
            }
            stats.Add(st);
        }
        if (bones[0] != null) restHipX = body.InverseTransformPoint(bones[0].position).x;

        if (logRigOnStart) LogRig();
        if (writeCsv) OpenCsv();
        startTime = Time.time;
    }

    private void LogRig()
    {
        var sb = new StringBuilder();
        sb.Append($"[OtterTelemetry] mode={mode} rig (body space, hip->head):");
        float total = 0f;
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            Vector3 p = body.InverseTransformPoint(bones[i].position);
            float len = (i + 1 < bones.Length && bones[i + 1] != null) ? Vector3.Distance(bones[i].position, bones[i + 1].position) : 0f;
            total += len;
            sb.Append($"\n  [{i}] {bones[i].name} pos=({p.x:F3},{p.y:F3},{p.z:F3}) seg={len:F3} restDir=({stats[i].restDir.x:F2},{stats[i].restDir.y:F2},{stats[i].restDir.z:F2})");
        }
        sb.Append($"\n  chain length={total:F3}");
        Debug.Log(sb.ToString(), this);
    }

    private void OpenCsv()
    {
        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
        Directory.CreateDirectory(dir);
        csvPath = Path.Combine(dir, $"otter_telemetry_{mode}_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv");
        csv = new StreamWriter(csvPath, false, new UTF8Encoding(false));

        var h = new StringBuilder("t,dt,speed,rootYaw,rootYawRate,dragging,zone,lead,trail,hipX");
        for (int i = 0; i < bones.Length; i++) h.Append($",yaw{i},pitch{i},roll{i}");
        csv.WriteLine(h.ToString());
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 1e-6f) return;

        float rootYaw = body.eulerAngles.y;
        float rootYawRate = hasRootYaw ? Mathf.DeltaAngle(lastRootYaw, rootYaw) / dt : 0f;
        lastRootYaw = rootYaw;
        hasRootYaw = true;
        maxRootYawRate = Mathf.Max(maxRootYawRate, Mathf.Abs(rootYawRate));

        float speed = movement != null ? movement.GetSpeed() : 0f;
        maxSpeed = Mathf.Max(maxSpeed, speed);

        var curve = mode.StartsWith("curve") ? FindCurveCached() : null;
        float lead = curve != null ? curve.DebugLeadDeg : 0f;
        float trail = curve != null ? curve.DebugTrailTime : 0f;
        float hipX = bones[0] != null ? body.InverseTransformPoint(bones[0].position).x - restHipX : 0f;
        maxHipSide = Mathf.Max(maxHipSide, Mathf.Abs(hipX));

        if (csv != null)
        {
            row.Clear();
            Append(Time.time - startTime); Append(dt); Append(speed); Append(rootYaw); Append(rootYawRate);
            row.Append(movement != null && movement.IsDragging() ? ",1" : ",0");
            row.Append(',').Append(movement != null ? (int)movement.GetZone() : 0);
            Append(lead); Append(trail); Append(hipX);
        }

        float headYaw = 0f;

        Quaternion bodyInv = Quaternion.Inverse(body.rotation);
        Quaternion prevBoneDelta = Quaternion.identity;
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            var st = stats[i];
            if (b == null) { if (csv != null) row.Append(",,,"); continue; }

            Quaternion delta = bodyInv * b.rotation * Quaternion.Inverse(st.restRel);
            Measure(delta, st.restDir, out float yaw, out float pitch, out float roll);

            st.maxYaw = Mathf.Max(st.maxYaw, Mathf.Abs(yaw));
            st.maxPitch = Mathf.Max(st.maxPitch, Mathf.Abs(pitch));
            st.maxRoll = Mathf.Max(st.maxRoll, Mathf.Abs(roll));

            if (st.hasPrev)
            {
                float angSpeed = Quaternion.Angle(st.prevDelta, delta) / dt;
                st.maxSpeed = Mathf.Max(st.maxSpeed, angSpeed);
                if (Mathf.Abs(angSpeed - st.prevSpeed) > popThresholdDegPerSec) st.pops++;
                st.prevSpeed = angSpeed;
            }
            st.prevDelta = delta;
            st.hasPrev = true;

            if (i > 0) st.maxJointBend = Mathf.Max(st.maxJointBend, Quaternion.Angle(prevBoneDelta, delta));
            prevBoneDelta = delta;

            if (csv != null) { Append(yaw); Append(pitch); Append(roll); }
            headYaw = yaw;
        }

        // Turning right = head yaw > 0; the hip is outside that turn when it has moved left (hipX < 0).
        if (Mathf.Abs(headYaw) > turnHeadYawDeg)
        {
            turnFrames++;
            float side = hipX * Mathf.Sign(headYaw);
            if (side < -hipSideThreshold) hipOutsideFrames++;
            else if (side > hipSideThreshold) hipInsideFrames++;
        }

        if (csv != null) csv.WriteLine(row.ToString());
        frames++;
    }

    private OtterBodyCurve cachedCurve;
    private OtterBodyCurve FindCurveCached()
    {
        if (cachedCurve == null) cachedCurve = FindObjectOfType<OtterBodyCurve>();
        return cachedCurve;
    }

    private void Append(float v) => row.Append(',').Append(v.ToString("0.###", CultureInfo.InvariantCulture));

    /// <summary>
    /// yaw/pitch: how the bone's rest segment direction has swung (around body up / elevation).
    /// roll: twist of the delta rotation around the rest segment direction.
    /// </summary>
    private static void Measure(Quaternion delta, Vector3 restDir, out float yaw, out float pitch, out float roll)
    {
        Vector3 dir = delta * restDir;

        Vector3 a = new Vector3(restDir.x, 0f, restDir.z);
        Vector3 b = new Vector3(dir.x, 0f, dir.z);
        yaw = (a.sqrMagnitude > 1e-8f && b.sqrMagnitude > 1e-8f) ? Vector3.SignedAngle(a, b, Vector3.up) : 0f;
        pitch = (Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(restDir.y, -1f, 1f))) * Mathf.Rad2Deg;

        Vector3 v = new Vector3(delta.x, delta.y, delta.z);
        Vector3 p = Vector3.Project(v, restDir);
        var twist = new Quaternion(p.x, p.y, p.z, delta.w);
        float mag = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
        if (mag < 1e-8f) { roll = 0f; return; }
        twist = new Quaternion(twist.x / mag, twist.y / mag, twist.z / mag, twist.w / mag);
        twist.ToAngleAxis(out float ang, out Vector3 axis);
        if (ang > 180f) ang -= 360f;
        roll = ang * Mathf.Sign(Vector3.Dot(axis, restDir));
    }

    private void OnDisable()
    {
        if (stats.Count == 0 || frames == 0) return;

        var sb = new StringBuilder();
        float duration = Time.time - startTime;
        sb.Append($"[OtterTelemetry] mode={mode} frames={frames} duration={duration:F1}s maxSpeed={maxSpeed:F2} maxRootYawRate={maxRootYawRate:F0}deg/s");
        float pct(int k) => turnFrames > 0 ? 100f * k / turnFrames : 0f;
        sb.Append($"\n  hip: maxSide={maxHipSide:F3}m turningFrames={turnFrames} outsideTurn={pct(hipOutsideFrames):F0}% insideTurn={pct(hipInsideFrames):F0}%");
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            var st = stats[i];
            sb.Append($"\n  [{i}] {bones[i].name,-14} maxYaw={st.maxYaw,6:F1} maxPitch={st.maxPitch,6:F1} maxRoll={st.maxRoll,6:F1} maxAngSpeed={st.maxSpeed,7:F0} jointBend={st.maxJointBend,6:F1} pops={st.pops}");
        }
        if (csvPath != null) sb.Append($"\n  csv: {csvPath}");
        Debug.Log(sb.ToString(), this);

        if (csv != null)
        {
            csv.Flush();
            csv.Dispose();
            csv = null;
        }
        frames = 0;
    }
}
