using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// While the otter is picked up, an arrow on the water from its head to the guide point, as the old drag drew one.
    /// </summary>
    [DefaultExecutionOrder(210)]
    [DisallowMultipleComponent]
    public class GuideArrow : MonoBehaviour
    {
        public OtterControls controls;
        public Color color = new Color(1f, 0.92f, 0.016f, 1f);
        [Tooltip("Shaft width and arrowhead length (m).")]
        public float width = 0.03f;
        public float headLength = 0.08f;
        [Tooltip("Height above the water (m), so it isn't lost in the surface.")]
        public float lift = 0.01f;

        private LineRenderer shaft, tip;
        private Material material;

        /// <summary>Everything drawn, for cameras that show only chosen renderers.</summary>
        public Renderer[] Renderers => new Renderer[] { shaft, tip };

        private void Awake()
        {
            if (controls == null) controls = GetComponent<OtterControls>();
            material = new Material(Shader.Find("Sprites/Default"));
            shaft = Line("Guide arrow");
            tip = Line("Guide arrow head");
        }

        private LineRenderer Line(string name)
        {
            // kept off the otter's hierarchy, out of anything that measures it by its renderers
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
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
            bool show = controls != null && controls.isActiveAndEnabled && controls.Gears.Selected;
            Vector3 from = show ? controls.HeadPoint : default;
            Vector3 to = show ? controls.Gears.AimPoint : default;
            to.y = from.y;
            Vector3 along = to - from;
            float length = along.magnitude;
            show &= length > 1e-3f;
            shaft.enabled = tip.enabled = show;
            if (!show) return;
            Vector3 dir = along / length;
            Vector3 up = Vector3.up * lift;
            Vector3 neck = to - dir * Mathf.Min(headLength, 0.5f * length);
            shaft.startColor = shaft.endColor = tip.startColor = tip.endColor = color;
            shaft.startWidth = shaft.endWidth = width;
            shaft.SetPosition(0, from + up);
            shaft.SetPosition(1, neck + up);
            tip.startWidth = 3f * width;
            tip.endWidth = 0f;
            tip.SetPosition(0, neck + up);
            tip.SetPosition(1, to + up);
        }

        private void OnDisable()
        {
            if (shaft != null) shaft.enabled = false;
            if (tip != null) tip.enabled = false;
        }

        private void OnDestroy()
        {
            if (shaft != null) Destroy(shaft.gameObject);
            if (tip != null) Destroy(tip.gameObject);
            if (material != null) Destroy(material);
        }
    }
}
