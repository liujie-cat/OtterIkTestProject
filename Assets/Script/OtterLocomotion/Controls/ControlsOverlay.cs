using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// What the controls show, at the pointer's level and drawn over everything as a cursor is: the ring round the otter,
    /// inside which a press lands on it, green while it is selected; selected, an arrow from the ring to the guide point,
    /// along the line the head looks down.
    /// </summary>
    [DefaultExecutionOrder(210)]
    [DisallowMultipleComponent]
    public class ControlsOverlay : MonoBehaviour
    {
        public OtterControls controls;
        public Color ringColor = new Color(1f, 1f, 1f, 0.6f);
        public Color selectedColor = new Color(0.3f, 1f, 0.4f, 1f);
        public Color arrowColor = new Color(1f, 0.92f, 0.016f, 1f);
        [Tooltip("The ring's line width, the arrow's shaft width and its head's length (m).")]
        public float ringWidth = 0.015f;
        public float width = 0.025f;
        public float headLength = 0.08f;

        private const int Segments = 64;
        private LineRenderer ring, shaft, tip;
        private Material material;

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
            ring.startColor = ring.endColor = gears.Selected ? selectedColor : ringColor;
            ring.startWidth = ring.endWidth = ringWidth;
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
            shaft.startColor = shaft.endColor = tip.startColor = tip.endColor = arrowColor;
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
