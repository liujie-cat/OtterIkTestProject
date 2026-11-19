using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Floats the body as a hull floats: the water bears it up by the weight of what its skin holds below the surface,
    /// so it rides at the draft where that matches its own weight (the share of its volume under water equal to its
    /// density relative to the water's), and bobs back to it as its shape changes, rolling over or kicking. Up and
    /// down only: the body does not pitch or roll. Starts it at its draft, and frees its height to gravity and the
    /// water while on; off, the height is held where it is.
    /// </summary>
    [DefaultExecutionOrder(900)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class Buoyancy : MonoBehaviour
    {
        [Tooltip("The otter's density relative to the sea water: the share of it under the surface when it floats still. The air in a sea otter's fur and lungs keeps it light.")]
        [Range(0.2f, 0.95f)] public float density = 0.6f;
        [Tooltip("Damping of the bob up and down, as a share of critical: at 1 it settles without overshooting.")]
        [Range(0f, 2f)] public float heaveDamping = 0.6f;

        /// <summary>The skin's whole volume (m^3), measured as posed at the start.</summary>
        public float Volume { get; private set; }
        /// <summary>The share of the skin's volume under the surface at the last physics step.</summary>
        public float Submerged { get; private set; }
        /// <summary>How deep the skin's lowest point lies under the surface (m), as last posed.</summary>
        public float Draft => water != null && skin != null ? water.SurfaceY - body.position.y - skin.Bottom : 0f;
        /// <summary>How hard the water pushes back as the body sinks below its draft (N/m), at the start.</summary>
        public float Stiffness { get; private set; }

        private Rigidbody body;
        private Water water;
        private SkinVolume skin;
        private bool started;

        /// <summary>The height <paramref name="body"/>'s position floats still at on a surface at <paramref name="surfaceY"/>, its skin as posed now; null without a skin.</summary>
        public static float? FloatingHeight(Transform body, float surfaceY, float density)
        {
            var renderer = SkinVolume.FindSkin(body);
            if (renderer == null) return null;
            var volume = new SkinVolume(body, renderer);
            volume.Bake();
            float level = volume.Level(density);
            volume.Release();
            return volume.Total > 0f ? surfaceY - level : (float?)null;
        }

        // on Start: when added while the scene loads, the skin is not posed yet, and the controls hold the height as they wake
        private void Start()
        {
            body = GetComponent<Rigidbody>();
            water = FindObjectOfType<Water>();
            var renderer = SkinVolume.FindSkin(transform);
            if (water == null || renderer == null)
            {
                Debug.LogWarning($"[Buoyancy] Needs a Water in the scene and a skin under '{name}'; the body keeps its height.", this);
                enabled = false;
                return;
            }
            skin = new SkinVolume(transform, renderer);
            skin.Bake();
            Volume = skin.Total;
            if (Volume <= 0f)
            {
                Debug.LogWarning($"[Buoyancy] The skin under '{name}' holds no volume; the body keeps its height.", this);
                enabled = false;
                return;
            }
            // afloat from the first step, not dropped in from wherever the scene left it
            float level = skin.Level(density);
            Vector3 p = body.position;
            p.y = water.SurfaceY - level;
            body.position = p;
            transform.position = p;
            const float step = 0.002f;
            float area = (skin.Below(level + step) - skin.Below(level - step)) / (2f * step);
            Stiffness = body.mass * -Physics.gravity.y * area / (density * Volume);
            started = true;
            Float(true);
        }

        private void OnEnable()
        {
            if (started) Float(true);
        }

        private void OnDisable() => Float(false);

        private void OnDestroy() => skin?.Release();

        // afloat, the height is gravity's and the water's to settle; otherwise it is held where it is
        private void Float(bool on)
        {
            if (body == null) return;
            body.useGravity = on;
            if (on)
            {
                body.constraints &= ~RigidbodyConstraints.FreezePositionY;
                return;
            }
            body.constraints |= RigidbodyConstraints.FreezePositionY;
            Vector3 v = body.velocity;
            v.y = 0f;
            body.velocity = v;
        }

        // after the pose is made for the frame
        private void LateUpdate() => skin.Bake();

        private void FixedUpdate()
        {
            Submerged = skin.Below(water.SurfaceY - body.position.y) / Volume;
            float lift = Submerged / density;
            // the water stills the bob the more of the body is in it
            float damping = 2f * heaveDamping * Mathf.Sqrt(Stiffness * body.mass) * Mathf.Min(1f, lift);
            body.AddForce(Vector3.up * (body.mass * -Physics.gravity.y * lift - damping * body.velocity.y), ForceMode.Force);
        }
    }
}
