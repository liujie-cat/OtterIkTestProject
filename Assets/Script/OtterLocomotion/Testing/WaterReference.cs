using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Fixed marks in the water, so the body's motion can be read against the world and set beside the strokes: a
    /// grid of lines on the surface and a coarser one on a floor below, laid around the otter and stepped along with
    /// it by whole cells, so the lines stay put in the world while it swims over them; and a trail behind each hind
    /// web in the world, which shows whether a push holds its water (the web nearly still while the body passes it)
    /// or slips back through it. Built for Play only, never saved.
    /// </summary>
    [DisallowMultipleComponent]
    public class WaterReference : MonoBehaviour
    {
        [Tooltip("Water surface height (m).")]
        public float surfaceY = 0f;
        [Header("Surface grid")]
        public float surfaceCell = 0.25f;
        [Tooltip("Cells out from the otter on each side.")]
        public int surfaceCells = 16;
        public Color surfaceColor = new Color(1f, 1f, 1f, 0.35f);
        [Tooltip("Every this many cells the line is drawn stronger.")]
        public int majorEvery = 4;
        public Color majorColor = new Color(1f, 1f, 1f, 0.7f);
        [Header("Floor grid")]
        public float floorDepth = 0.6f;
        public float floorCell = 0.5f;
        public int floorCells = 10;
        public Color floorColor = new Color(0.1f, 0.3f, 0.35f, 0.8f);
        [Header("Hind web trails in the world")]
        public bool webTrails = true;
        [Tooltip("How long a trail lasts (s).")]
        public float trailTime = 0.8f;
        public float trailWidth = 0.004f;
        public Color leftTrail = new Color(1f, 0.85f, 0.2f);
        public Color rightTrail = new Color(0.4f, 1f, 0.5f);

        private readonly List<Renderer> renderers = new List<Renderer>();
        private readonly List<Object> owned = new List<Object>();
        private Transform surface, floor;
        private bool started;

        /// <summary>Everything drawn, for cameras that show only chosen renderers.</summary>
        public IReadOnlyList<Renderer> Renderers => renderers;

        // on Start: when added while the scene loads, the IK chains are not enabled yet
        private void Start()
        {
            started = true;
            Build();
        }

        private void OnEnable()
        {
            if (started) Build();
        }

        private void OnDisable()
        {
            foreach (var o in owned)
                if (o != null) Destroy(o);
            owned.Clear();
            renderers.Clear();
            surface = floor = null;
        }

        private void Build()
        {
            var lines = NewMaterial();
            surface = Grid("WaterReferenceSurface", surfaceCell, surfaceCells, surfaceColor, majorColor, majorEvery, lines);
            floor = Grid("WaterReferenceFloor", floorCell, floorCells, floorColor, floorColor, 0, lines);
            if (webTrails) AddWebTrails(lines);
            LateUpdate();
        }

        private void LateUpdate()
        {
            if (surface == null) return;
            Vector3 p = transform.position;
            surface.position = Snap(p, surfaceCell, surfaceY);
            floor.position = Snap(p, floorCell, surfaceY - floorDepth);
        }

        // stepped by whole cells, so the lines keep their places in the world
        private static Vector3 Snap(Vector3 p, float cell, float y) =>
            new Vector3(Mathf.Round(p.x / cell) * cell, y, Mathf.Round(p.z / cell) * cell);

        private Transform Grid(string name, float cell, int cells, Color minor, Color major, int every, Material material)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            owned.Add(go);
            var vertices = new List<Vector3>();
            var colors = new List<Color>();
            var indices = new List<int>();
            float half = cells * cell;
            for (int k = -cells; k <= cells; k++)
            {
                Color c = every > 0 && k % every == 0 ? major : minor;
                float o = k * cell;
                Line(new Vector3(o, 0f, -half), new Vector3(o, 0f, half), c);
                Line(new Vector3(-half, 0f, o), new Vector3(half, 0f, o), c);
            }
            var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            owned.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            renderers.Add(r);
            return go.transform;

            void Line(Vector3 a, Vector3 b, Color c)
            {
                indices.Add(vertices.Count);
                vertices.Add(a);
                colors.Add(c);
                indices.Add(vertices.Count);
                vertices.Add(b);
                colors.Add(c);
            }
        }

        private void AddWebTrails(Material material)
        {
            var chains = OtterRig.FindLimbChains(transform);
            foreach (var limb in new[] { LimbId.HL, LimbId.HR })
            {
                var ik = chains[(int)limb];
                if (ik == null || ik.end.bone == null) continue;
                var web = OtterRig.Flipper(ik.end.bone);
                var go = new GameObject("WorldWebTrail") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(web, false);
                if (OtterRig.TryFootPlane(web, out _, out _, out var center, out _)) go.transform.localPosition = center;
                owned.Add(go);
                var t = go.AddComponent<TrailRenderer>();
                t.time = trailTime;
                t.widthMultiplier = trailWidth;
                t.minVertexDistance = 0.002f;
                t.numCapVertices = 2;
                t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                t.receiveShadows = false;
                t.sharedMaterial = material;
                Color c = limb == LimbId.HL ? leftTrail : rightTrail;
                t.startColor = c;
                t.endColor = new Color(c.r, c.g, c.b, 0f);
                renderers.Add(t);
            }
        }

        private Material NewMaterial()
        {
            var m = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.DontSave };
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            m.renderQueue = 3000;
            owned.Add(m);
            return m;
        }
    }
}
