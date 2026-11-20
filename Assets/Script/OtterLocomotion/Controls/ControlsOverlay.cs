using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// What the controls show, at the pointer's level and drawn over everything as a cursor is: the ring round the otter,
    /// coloured by the state of the controls and swelling a moment at each kick, jump, shift, pick-up and let-go;
    /// picked up, an arrow in the ring's colour from it to the guide point, along the line the head looks down.
    /// </summary>
    [DefaultExecutionOrder(210)]
    [DisallowMultipleComponent]
    public class ControlsOverlay : MonoBehaviour
    {
        public OtterControls controls;
        [Header("State colours")]
        [Tooltip("未选中: not picked up, lying parked on its back.")]
        public Color unselectedColor = new Color(1f, 1f, 1f, 0.6f);
        [Tooltip("选中: picked up, parked.")]
        public Color selectedColor = new Color(0.3f, 1f, 0.4f, 1f);
        [Tooltip("仰泳: swimming slowly on its back.")]
        public Color slowColor = new Color(0.25f, 0.8f, 1f, 1f);
        [Tooltip("巡航: cruising on its belly.")]
        public Color cruiseColor = new Color(1f, 0.92f, 0.016f, 1f);
        [Tooltip("海豚冲刺: sprinting, kicking up to the dolphin wave and in it.")]
        public Color sprintColor = new Color(1f, 0.45f, 0.1f, 1f);
        [Tooltip("海豚跳: flashed for jumpFlash seconds at a jump.")]
        public Color jumpColor = new Color(1f, 0.3f, 0.9f, 1f);
        public float jumpFlash = 0.6f;
        [Tooltip("How long (s) the ring takes to change colour.")]
        public float colorTime = 0.15f;
        [Tooltip("Each kick, jump, shift, pick-up and let-go swells the ring's line to this many times its width, easing back over pulseTime (s).")]
        public float pulse = 2.5f;
        public float pulseTime = 0.3f;
        [Tooltip("The ring's line width, the arrow's shaft width and its head's length (m).")]
        public float ringWidth = 0.015f;
        public float width = 0.025f;
        public float headLength = 0.08f;

        private const int Segments = 64;
        private LineRenderer ring, shaft, tip;
        private Material material;
        private Color shown;
        private float pulsedAt = float.NegativeInfinity, jumpedAt = float.NegativeInfinity;

        /// <summary>Everything drawn, for cameras that show only chosen renderers.</summary>
        public Renderer[] Renderers => new Renderer[] { ring, shaft, tip };

        private void Awake()
        {
            if (controls == null) controls = GetComponent<OtterControls>();
            material = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.DontSave, renderQueue = 4000 };
            material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            ring = Line("Selection ring", Segments);
            ring.loop = true;
            shaft = Line("Guide arrow", 2);
            tip = Line("Guide arrow head", 2);
            shown = unselectedColor;
        }

        private void OnEnable()
        {
            if (controls != null) controls.Control += OnControl;
        }

        private void OnControl(ControlEvent e)
        {
            pulsedAt = Time.time;
            if (e.kind == ControlEventKind.Jump) jumpedAt = Time.time;
        }

        private Color StateColor(GearMachine gears)
        {
            if (Time.time - jumpedAt < jumpFlash) return jumpColor;
            if (!gears.Selected) return unselectedColor;
            return gears.Gear switch
            {
                Gear.Park => selectedColor,
                Gear.Slow => slowColor,
                Gear.Cruise => cruiseColor,
                _ => sprintColor,
            };
        }

        private LineRenderer Line(string name, int points)
        {
            // kept off the otter's hierarchy, out of anything that measures it by its renderers
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = points;
            line.sharedMaterial = material;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.alignment = LineAlignment.TransformZ;
            go.transform.rotation = Quaternion.LookRotation(Vector3.down);
            line.enabled = false;
            return line;
        }

        private void LateUpdate()
        {
            bool on = controls != null && controls.isActiveAndEnabled;
            ring.enabled = on;
            shaft.enabled = tip.enabled = false;
            if (!on) return;
            var gears = controls.Gears;
            Vector3 centre = controls.GrabPoint;
            float radius = controls.RingRadius;
            Color state = StateColor(gears);
            shown = colorTime > 0f ? Color.Lerp(shown, state, 1f - Mathf.Exp(-Time.deltaTime / colorTime)) : state;
            float swell = pulseTime > 0f ? Mathf.Clamp01(1f - (Time.time - pulsedAt) / pulseTime) : 0f;
            ring.startColor = ring.endColor = shown;
            ring.startWidth = ring.endWidth = ringWidth * Mathf.Lerp(1f, pulse, swell * swell);
            for (int i = 0; i < Segments; i++)
            {
                float a = i * (2f * Mathf.PI / Segments);
                ring.SetPosition(i, centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius);
            }

            if (!gears.Selected) return;
            Vector3 from = controls.HeadPoint, to = gears.AimPoint;
            to.y = from.y;
            if ((to - from).sqrMagnitude < 1e-6f) return;
            Vector3 dir = (to - from).normalized;
            // the head sits inside the ring: start where its line of sight leaves it
            Vector3 m = from - centre;
            float b = Vector3.Dot(m, dir), c = m.sqrMagnitude - radius * radius;
            if (c < 0f) from += dir * (-b + Mathf.Sqrt(b * b - c));
            float length = Vector3.Dot(to - from, dir);
            if (length < 1e-3f) return;
            Vector3 neck = to - dir * Mathf.Min(headLength, 0.5f * length);
            shaft.enabled = tip.enabled = true;
            shaft.startColor = shaft.endColor = tip.startColor = tip.endColor = shown;
            shaft.startWidth = shaft.endWidth = width;
            shaft.SetPosition(0, from);
            shaft.SetPosition(1, neck);
            tip.startWidth = 3f * width;
            tip.endWidth = 0f;
            tip.SetPosition(0, neck);
            tip.SetPosition(1, to);
        }

        private void OnDisable()
        {
            if (controls != null) controls.Control -= OnControl;
            if (ring != null) ring.enabled = false;
            if (shaft != null) shaft.enabled = false;
            if (tip != null) tip.enabled = false;
        }

        private void OnDestroy()
        {
            if (ring != null) Destroy(ring.gameObject);
            if (shaft != null) Destroy(shaft.gameObject);
            if (tip != null) Destroy(tip.gameObject);
            if (material != null) Destroy(material);
        }
    }
}
