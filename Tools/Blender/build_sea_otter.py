"""Build a cute low-poly sea otter on the cat rig's skeleton conventions.

The armature is the cat's own (cat_lowpoly.fbx): same object transform, bone names, hierarchy and bone
rolls, so every IK component that works on the cat can be pointed at the same-named otter bones.
Only the face bones are dropped. Bones are moved to otter anatomy and re-rolled toward the cat's
Z axes, so local frames (used by AdvancedTwoBoneIK axis settings and the hind trajectory) carry over.

Mesh: smooth primitives (head, cheeks, body, limbs, tail) are fused with a voxel remesh, symmetrized and
decimated into flat-shaded facets; eyes, ears, nose and mouth are separate crisp pieces placed on that
surface. Skin weights come from Blender's bone heat; colours come from a small palette texture.

Run (Blender 5.x) from the project root:
  blender -b --factory-startup --python Tools/Blender/build_sea_otter.py -- \
      "Assets/ArtByLiv/Lowpoly Cat/Mesh/cat_lowpoly.fbx" Assets/ArtResource/Otter/SeaOtter ArtSource/SeaOtter [preview_dir]
Outputs:
  <unity_out_dir>/sea_otter.fbx, sea_otter_palette.png   (the committed .meta files and SeaOtter.mat are kept)
  <source_out_dir>/sea_otter.blend, sea_otter_palette.png
"""
import bpy, bmesh, sys, os, math
from mathutils import Vector, Matrix, Quaternion
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index("--") + 1:]
CAT_FBX, UNITY_DIR, SOURCE_DIR = argv[0], argv[1], argv[2]
PREVIEW_DIR = argv[3] if len(argv) > 3 else None
for d in (UNITY_DIR, SOURCE_DIR, PREVIEW_DIR):
    if d:
        os.makedirs(d, exist_ok=True)

FBX_EXPORT = dict(
    use_selection=False, apply_unit_scale=True, global_scale=1.0, apply_scale_options='FBX_SCALE_NONE',
    axis_forward='-Z', axis_up='Y', bake_space_transform=False, object_types={'ARMATURE', 'MESH'},
    use_mesh_modifiers=False, mesh_smooth_type='FACE', add_leaf_bones=False, primary_bone_axis='Y',
    secondary_bone_axis='X', use_armature_deform_only=False, armature_nodetype='NULL', bake_anim=False,
    path_mode='STRIP')

VOXEL = 0.0014          # fusing resolution (m)
TARGET_TRIS = 2600      # hull triangles after decimation

# ============================================================================ anatomy (meters)
# Blender world space: +Z up, -Y forward (Unity +Z), +X = otter's left. z = 0 is the cat's ground plane.
# Cute but long-bodied: big round head, plump body, four clearly separated limbs that reach past the
# body outline (they read from the top-down camera), thick tapered tail.

SPINE = {
    # name: (head, tail)  - spine bones point forward like the cat's
    "root.x":        ((0, 0.095, 0.080), (0, 0.062, 0.081)),
    "spine_01.x":    ((0, 0.062, 0.081), (0, 0.010, 0.083)),
    "spine_02.x":    ((0, 0.010, 0.083), (0, -0.045, 0.085)),
    "spine_03.x":    ((0, -0.045, 0.085), (0, -0.082, 0.088)),
    "c_subneck_1.x": ((0, -0.082, 0.088), (0, -0.106, 0.093)),
    "neck.x":        ((0, -0.106, 0.093), (0, -0.128, 0.099)),
    "head.x":        ((0, -0.128, 0.099), (0, -0.195, 0.100)),
}
TAIL = [(0, 0.095, 0.080), (0, 0.122, 0.079), (0, 0.150, 0.077), (0, 0.177, 0.075), (0, 0.204, 0.073), (0, 0.230, 0.071)]
TAIL_BONES = ["c_tail_00.x", "c_tail_01.x", "c_tail_02.x", "c_tail_03.x", "c_tail_04.x"]

# Left limbs (mirrored for .r): girdle, upper, lower, foot, toes - the cat's five-bone chains.
HIND = [(0.022, 0.072, 0.072),   # hip joint (inside body)
        (0.038, 0.058, 0.052),   # knee
        (0.044, 0.088, 0.042),   # ankle
        (0.049, 0.110, 0.038),   # flipper mid
        (0.054, 0.130, 0.035),   # toe base
        (0.058, 0.150, 0.033)]   # flipper tip
HIND_BONES = ["c_thigh_b", "thigh_stretch", "leg_stretch", "foot", "toes_01"]
FRONT = [(0.020, -0.060, 0.090),  # scapula top (inside body)
         (0.032, -0.076, 0.068),  # shoulder
         (0.045, -0.072, 0.046),  # elbow
         (0.049, -0.092, 0.031),  # wrist
         (0.050, -0.104, 0.025),  # paw
         (0.050, -0.116, 0.022)]  # paw tip
FRONT_BONES = ["c_thigh_b_dupli_001", "thigh_stretch_dupli_001", "leg_stretch_dupli_001", "foot_dupli_001", "toes_01_dupli_001"]
TWIST = {"thigh_twist": "thigh_stretch", "leg_twist": "leg_stretch",
         "thigh_twist_dupli_001": "thigh_stretch_dupli_001", "leg_twist_dupli_001": "leg_stretch_dupli_001"}

# Smooth shapes fused into the hull: (part, center, radii)
ELLIPSOIDS = [
    ("head",   (0, -0.152, 0.103), (0.043, 0.040, 0.038)),
    ("muzzle", (0, -0.186, 0.090), (0.021, 0.016, 0.015)),
    ("cheek",  (0.013, -0.183, 0.088), (0.016, 0.014, 0.013)),
    ("cheek",  (-0.013, -0.183, 0.088), (0.016, 0.014, 0.013)),
]
# One continuous body from inside the head to the tail tip: (y, z, lateral radius, vertical radius),
# interpolated with Catmull-Rom so there are no bulges between stations.
BODY_PROFILE = [
    (-0.128, 0.097, 0.031, 0.030),
    (-0.108, 0.092, 0.035, 0.032),
    (-0.085, 0.088, 0.040, 0.036),
    (-0.055, 0.085, 0.045, 0.039),
    (-0.020, 0.083, 0.047, 0.040),
    (+0.015, 0.082, 0.046, 0.039),
    (+0.045, 0.081, 0.042, 0.036),
    (+0.072, 0.080, 0.036, 0.032),
    (+0.095, 0.080, 0.029, 0.025),
    (+0.115, 0.079, 0.025, 0.019),
    (+0.140, 0.077, 0.023, 0.016),
    (+0.168, 0.075, 0.020, 0.014),
    (+0.195, 0.073, 0.017, 0.012),
    (+0.218, 0.072, 0.013, 0.009),
    (+0.234, 0.071, 0.008, 0.006),
]
BODY_PARTS = [(-0.095, "neck"), (-0.035, "chest"), (0.045, "belly"), (0.100, "hips"), (9.0, "tail")]
# Limb tubes: (part per segment, points, radii (lateral, vertical) per point)
FRONT_TUBE = (["arm", "forearm", "paw", "paw"], FRONT[1:],
              [(0.0150, 0.0150), (0.0120, 0.0120), (0.0095, 0.0088), (0.0130, 0.0075), (0.0095, 0.0058)])
HIND_TUBE = (["thigh", "shin", "foot", "foot", "foot"], HIND,
             [(0.019, 0.019), (0.015, 0.015), (0.0095, 0.0085), (0.014, 0.0055), (0.019, 0.005), (0.017, 0.004)])

# ============================================================================ palette
PALETTE = {  # name: shades (8-bit sRGB)
    "fur":     [(198, 150, 106), (205, 157, 111), (191, 144, 101), (201, 153, 108)],
    "back":    [(181, 133, 92), (186, 138, 96), (176, 129, 89), (184, 136, 94)],
    "tail":    [(170, 124, 86), (176, 129, 89), (165, 120, 83), (173, 126, 87)],
    "cream":   [(238, 215, 180), (243, 222, 188), (233, 209, 173), (240, 218, 184)],
    "paw":     [(124, 86, 62), (131, 91, 65), (117, 81, 58), (127, 88, 63)],
    "ear":     [(132, 93, 67)],
    "earin":   [(98, 66, 49)],
    "nose":    [(84, 54, 43)],
    "mouth":   [(92, 60, 47)],
    "eye":     [(22, 18, 18)],
    "glint":   [(252, 252, 250)],
}
GRID = 8
TEX_SIZE = 256
CELL_OF = {}
for name, shades in PALETTE.items():
    for k in range(len(shades)):
        CELL_OF[(name, k)] = len(CELL_OF)

def cell_uv(name, k=0):
    i = CELL_OF[(name, k % len(PALETTE[name]))]
    return ((i % GRID + 0.5) / GRID, (i // GRID + 0.5) / GRID)

def save_palette(path):
    img = bpy.data.images.new("sea_otter_palette", TEX_SIZE, TEX_SIZE, alpha=False)
    cell = TEX_SIZE // GRID
    px = [0.0] * (TEX_SIZE * TEX_SIZE * 4)
    for (name, k), i in CELL_OF.items():
        col = [v / 255.0 for v in PALETTE[name][k]] + [1.0]  # byte image pixels are stored as sRGB
        cx, cy = i % GRID, i // GRID
        for y in range(cy * cell, (cy + 1) * cell):
            for x in range(cx * cell, (cx + 1) * cell):
                j = (y * TEX_SIZE + x) * 4
                px[j:j + 4] = col
    img.pixels = px
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    return img

def shade_index(c):
    h = hash((round(abs(c.x), 3), round(c.y, 3), round(c.z, 3))) & 0xFFFF
    return h % 4

# ============================================================================ 1. template armature
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=CAT_FBX)
arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
for o in [o for o in bpy.data.objects if o.type != 'ARMATURE']:
    bpy.data.objects.remove(o, do_unlink=True)
arm.animation_data_clear()
for a in list(bpy.data.actions):
    bpy.data.actions.remove(a)
for pb in arm.pose.bones:
    pb.matrix_basis = Matrix.Identity(4)

KEEP = set(SPINE) | set(TAIL_BONES)
for side in ("l", "r"):
    KEEP |= {f"{b}.{side}" for b in HIND_BONES + FRONT_BONES + list(TWIST)}

cat_frames = {b.name: (arm.matrix_world @ b.matrix_local).copy() for b in arm.data.bones}
cat_z = {b.name: b.matrix_local.to_3x3().col[2].copy() for b in arm.data.bones}
missing = KEEP - set(cat_frames)
if missing:
    raise RuntimeError(f"cat rig is missing expected bones: {sorted(missing)}")

bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='EDIT')
ebs = arm.data.edit_bones
for eb in list(ebs):
    if eb.name not in KEEP:
        ebs.remove(eb)
for eb in ebs:
    eb.use_connect = False

Minv = arm.matrix_world.inverted()
def place(name, head, tail):
    eb = ebs[name]
    eb.head = Minv @ Vector(head)
    eb.tail = Minv @ Vector(tail)
    eb.align_roll(cat_z[name])

def mirror(p):
    return (-p[0], p[1], p[2])

for name, (h, t) in SPINE.items():
    place(name, h, t)
for i, name in enumerate(TAIL_BONES):
    place(name, TAIL[i], TAIL[i + 1])
for side, f in (("l", lambda p: p), ("r", mirror)):
    for chain, bones in ((HIND, HIND_BONES), (FRONT, FRONT_BONES)):
        for i, b in enumerate(bones):
            place(f"{b}.{side}", f(chain[i]), f(chain[i + 1]))
    for twist, parent in TWIST.items():
        p = ebs[f"{parent}.{side}"]
        ebs[f"{twist}.{side}"].head = p.head.copy()
        ebs[f"{twist}.{side}"].tail = p.head.lerp(p.tail, 0.5)
        ebs[f"{twist}.{side}"].align_roll(cat_z[f"{twist}.{side}"])
bpy.ops.object.mode_set(mode='OBJECT')
for b in arm.data.bones:
    b.use_deform = b.name.split(".")[0] not in TWIST

# ============================================================================ 2. hull
def frame(direction):
    d = Vector(direction).normalized()
    lat = Vector((1, 0, 0))
    lat = lat - d * d.dot(lat)
    if lat.length < 1e-4:
        lat = Vector((0, 0, 1)).cross(d)
    lat.normalize()
    up = d.cross(lat).normalized()
    if up.z < 0:
        up = -up
    return lat, d, up

def add_ellipsoid(bm, center, radii, basis=None, u=36, v=18):
    rot = (basis or Matrix.Identity(3)).to_4x4()
    bmesh.ops.create_uvsphere(bm, u_segments=u, v_segments=v, radius=1.0,
                              matrix=Matrix.Translation(center) @ rot @ Matrix.Diagonal((*radii, 1.0)))

def catmull_rom(stations, step):
    """Dense samples of a station list (tuples of floats, first component strictly increasing)."""
    out = []
    P = [stations[0]] + list(stations) + [stations[-1]]
    for i in range(1, len(P) - 2):
        p0, p1, p2, p3 = P[i - 1], P[i], P[i + 1], P[i + 2]
        n = max(1, int(abs(p2[0] - p1[0]) / step))
        for s in range(n):
            t = s / n
            out.append(tuple(0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t * t + (-a + 3 * b - 3 * c + d) * t ** 3)
                             for a, b, c, d in zip(p0, p1, p2, p3)))
    out.append(stations[-1])
    return out

BODY_DENSE = catmull_rom(BODY_PROFILE, 0.003)
BODY_PTS = [(0.0, s[0], s[1]) for s in BODY_DENSE]
BODY_RADII = [(s[2], s[3]) for s in BODY_DENSE]

def body_part(y):
    return next(name for edge, name in BODY_PARTS if y < edge)

def add_tube(bm, pts, radii, n=20, joint_caps=True):
    pts = [Vector(p) for p in pts]
    for i in range(len(pts) - 1):
        a, b = pts[i], pts[i + 1]
        lat, d, up = frame(b - a)
        steps = max(2, int((b - a).length / 0.002))
        rings = []
        for s in range(steps + 1):
            t = s / steps
            c = a.lerp(b, t)
            rl = radii[i][0] + (radii[i + 1][0] - radii[i][0]) * t
            rv = radii[i][1] + (radii[i + 1][1] - radii[i][1]) * t
            rings.append([bm.verts.new(c + lat * (rl * math.cos(2 * math.pi * k / n)) + up * (rv * math.sin(2 * math.pi * k / n)))
                          for k in range(n)])
        for r0, r1 in zip(rings, rings[1:]):
            for k in range(n):
                bm.faces.new((r0[k], r0[(k + 1) % n], r1[(k + 1) % n], r1[k]))
    for i, p in enumerate(pts):
        if not joint_caps and 0 < i < len(pts) - 1:
            continue
        seg = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)])
        lat, d, up = frame(seg)
        basis = Matrix((lat, d, up)).transposed()
        add_ellipsoid(bm, p, (radii[i][0], max(radii[i][0], radii[i][1]), radii[i][1]), basis, u=20, v=10)

def mirrored(pts):
    return [mirror(p) for p in pts]

bm = bmesh.new()
for part, c, r in ELLIPSOIDS:
    add_ellipsoid(bm, Vector(c), r)
add_tube(bm, BODY_PTS, BODY_RADII, n=32, joint_caps=False)
for flip in (False, True):
    for parts, pts, radii in (FRONT_TUBE, HIND_TUBE):
        add_tube(bm, mirrored(pts) if flip else pts, radii)
src_me = bpy.data.meshes.new("otter_src")
bm.to_mesh(src_me)
bm.free()
src = bpy.data.objects.new("otter_src", src_me)
bpy.context.scene.collection.objects.link(src)

rm = src.modifiers.new("remesh", 'REMESH')
rm.mode = 'VOXEL'
rm.voxel_size = VOXEL
rm.adaptivity = 0.0
sm = src.modifiers.new("smooth", 'SMOOTH')
sm.factor = 0.5
sm.iterations = 6
dg = bpy.context.evaluated_depsgraph_get()
fused = bpy.data.meshes.new_from_object(src.evaluated_get(dg))
bpy.data.objects.remove(src, do_unlink=True)

bm = bmesh.new()
bm.from_mesh(fused)
bmesh.ops.symmetrize(bm, input=bm.verts[:] + bm.edges[:] + bm.faces[:], direction='X')
bm.to_mesh(fused)
fused_faces = len(bm.faces)
bm.free()

hull_src = bpy.data.objects.new("hull_src", fused)
bpy.context.scene.collection.objects.link(hull_src)
dm = hull_src.modifiers.new("decimate", 'DECIMATE')
dm.decimate_type = 'COLLAPSE'
dm.ratio = min(1.0, TARGET_TRIS / (2.0 * fused_faces))
dm.use_symmetry = True
dm.symmetry_axis = 'X'
dm.use_collapse_triangulate = True
dg = bpy.context.evaluated_depsgraph_get()
hull_me = bpy.data.meshes.new_from_object(hull_src.evaluated_get(dg))
bpy.data.objects.remove(hull_src, do_unlink=True)
hull_me.name = "SeaOtter"

bm = bmesh.new()
bm.from_mesh(hull_me)
# edge flips only (no vertex moves): trades the collapse's long slivers for rounder facets
bmesh.ops.beautify_fill(bm, faces=bm.faces[:], edges=bm.edges[:], method='AREA')
bm.to_mesh(hull_me)
bm.free()
print(f"fused faces={fused_faces} -> hull tris={len(hull_me.polygons)}")

# ---------------------------------------------------------------- region colours on the hull
def ellipsoid_sd(p, c, r):
    q = Vector(((p.x - c[0]) / r[0], (p.y - c[1]) / r[1], (p.z - c[2]) / r[2]))
    k0 = q.length
    k1 = Vector((q.x / r[0], q.y / r[1], q.z / r[2])).length
    return k0 * (k0 - 1.0) / max(k1, 1e-9)

def segment_sd(p, a, b, ra, rb):
    a, b = Vector(a), Vector(b)
    ab = b - a
    t = max(0.0, min(1.0, (p - a).dot(ab) / ab.length_squared))
    lat, d, up = frame(ab)
    rel = p - (a + ab * t)
    rl = ra[0] + (rb[0] - ra[0]) * t
    rv = ra[1] + (rb[1] - ra[1]) * t
    q = Vector((rel.dot(lat) / rl, rel.dot(up) / rv))
    radial = (q.length - 1.0) * min(rl, rv)
    along = abs(rel.dot(d))  # non-zero only past the segment ends
    return math.hypot(max(radial, 0.0), along) if along > 1e-9 else radial

SHAPES = []
for part, c, r in ELLIPSOIDS:
    SHAPES.append((part, lambda p, c=c, r=r: ellipsoid_sd(p, c, r)))
for i in range(len(BODY_PTS) - 1):
    SHAPES.append((body_part(BODY_PTS[i][1]),
                   lambda p, a=BODY_PTS[i], b=BODY_PTS[i + 1], ra=BODY_RADII[i], rb=BODY_RADII[i + 1]: segment_sd(p, a, b, ra, rb)))
for parts, pts, radii in (FRONT_TUBE, HIND_TUBE):
    for flip in (False, True):
        P = mirrored(pts) if flip else pts
        for i in range(len(P) - 1):
            SHAPES.append((parts[i], lambda p, a=P[i], b=P[i + 1], ra=radii[i], rb=radii[i + 1]: segment_sd(p, a, b, ra, rb)))

HEAD_C, HEAD_R = Vector(ELLIPSOIDS[0][1]), Vector(ELLIPSOIDS[0][2])

def region_colour(p, normal):
    part = min(SHAPES, key=lambda s: s[1](p))[0]
    if part in ("muzzle", "cheek"):
        return "cream"
    if part == "head":
        q = Vector(((p.x - HEAD_C.x) / HEAD_R.x, (p.y - HEAD_C.y) / HEAD_R.y, (p.z - HEAD_C.z) / HEAD_R.z))
        if q.z < -0.35 or (q.y < -0.45 and q.z < 0.12):
            return "cream"
        return "fur"
    if part in ("neck", "chest", "belly", "hips"):
        st = min(BODY_DENSE, key=lambda s: abs(s[0] - p.y))
        qz = (p.z - st[1]) / st[3]
        if qz < (-0.45 if part in ("neck", "chest") else -0.7):
            return "cream"
        return "back" if qz > 0.8 else "fur"
    if part == "tail":
        return "tail"
    if part in ("paw", "foot"):
        return "paw"
    return "fur"

bm = bmesh.new()
bm.from_mesh(hull_me)
uv = bm.loops.layers.uv.verify()
region_counts = {}
for f in bm.faces:
    c = f.calc_center_median()
    name = region_colour(c, f.normal)
    region_counts[name] = region_counts.get(name, 0) + 1
    u_, v_ = cell_uv(name, shade_index(c))
    for loop in f.loops:
        loop[uv].uv = (u_, v_)
bm.to_mesh(hull_me)
bm.free()
print("hull faces per colour:", region_counts)

obj = bpy.data.objects.new("SeaOtter", hull_me)
bpy.context.scene.collection.objects.link(obj)

# ---------------------------------------------------------------- bone heat weights
for o in bpy.context.scene.objects:
    o.select_set(False)
obj.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.parent_set(type='ARMATURE_AUTO')
obj.select_set(False)
arm.select_set(False)

# ---------------------------------------------------------------- face details (rigid on the head)
bm = bmesh.new()
bm.from_mesh(hull_me)
bm.verts.ensure_lookup_table()
bvh = BVHTree.FromBMesh(bm)
uv = bm.loops.layers.uv.verify()
dl = bm.verts.layers.deform.verify()
head_group = obj.vertex_groups["head.x"].index
detail_verts = []

def surface(origin, direction):
    hit = bvh.ray_cast(Vector(origin) + Vector(direction).normalized() * 0.2, -Vector(direction).normalized())
    if hit[0] is None:
        raise RuntimeError(f"no surface hit from {origin} along {direction}")
    n = hit[1]
    return hit[0], (n if n.dot(Vector(direction)) > 0 else -n).normalized()

def onto(p, offset):
    loc, n, _, _ = bvh.find_nearest(Vector(p))
    return loc + n * offset

def finish(res_verts, colour):
    new_faces = {f for v in res_verts for f in v.link_faces}
    for f in new_faces:
        for loop in f.loops:
            loop[uv].uv = cell_uv(colour)
    for v in res_verts:
        detail_verts.append(v)

def blob(center, basis, radii, colour, u=8, v=6, shape=None):
    res = bmesh.ops.create_uvsphere(bm, u_segments=u, v_segments=v, radius=1.0)
    for vv in res['verts']:
        co = vv.co.copy()
        if shape:
            co = shape(co)
        vv.co = Vector(center) + basis @ Vector((co.x * radii[0], co.y * radii[1], co.z * radii[2]))
    finish(res['verts'], colour)

def basis_facing(n, up_hint=Vector((0, 0, 1))):
    """columns: x = sideways, y = along the normal, z = up on the surface"""
    y = n.normalized()
    x = up_hint.cross(y)
    if x.length < 1e-4:
        x = Vector((1, 0, 0))
    x.normalize()
    z = y.cross(x).normalized()
    if z.z < 0:
        x, z = -x, -z
    return Matrix((x, y, z)).transposed()

def tube(points, radius, colour, sides=4):
    pts = [Vector(p) for p in points]
    rings = []
    for i, p in enumerate(pts):
        d = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
        a = d.orthogonal().normalized()
        b = d.cross(a).normalized()
        rings.append([bm.verts.new(p + (a * math.cos(2 * math.pi * k / sides) + b * math.sin(2 * math.pi * k / sides)) * radius)
                      for k in range(sides)])
    made = [v for r in rings for v in r]
    for r0, r1 in zip(rings, rings[1:]):
        for k in range(sides):
            bm.faces.new((r0[k], r0[(k + 1) % sides], r1[(k + 1) % sides], r1[k]))
    for r in (rings[0], rings[-1]):
        bm.faces.new(r if r is rings[-1] else list(reversed(r)))
    finish(made, colour)

for sx in (1, -1):
    # eyes: big glossy domes on the front of the face, with a highlight
    hit, n = surface(HEAD_C, (0.50 * sx, -0.74, 0.36))
    r_eye = 0.0088
    eb = basis_facing(n)
    blob(hit - n * (0.30 * r_eye), eb, (r_eye, r_eye * 0.75, r_eye * 1.08), "eye", u=10, v=7)
    light = Vector((-0.5, 0, 1))  # same key-light side on both eyes
    light = (light - n * light.dot(n)).normalized()
    glint = hit + n * (0.40 * r_eye) + light * (0.45 * r_eye)
    blob(glint, eb, (0.0024, 0.0016, 0.0024), "glint", u=6, v=4)

    # round ears on the upper sides of the head, facing forward
    hit, n = surface(HEAD_C, (0.86 * sx, 0.15, 0.50))
    facing = (n + Vector((0, -0.6, 0))).normalized()
    ebasis = basis_facing(facing)
    ear_c = hit + n * 0.0045
    blob(ear_c, ebasis, (0.0128, 0.0055, 0.0118), "ear", u=10, v=6)
    blob(ear_c + facing * 0.0036 + Vector((0, 0, -0.0012)), ebasis, (0.0080, 0.0025, 0.0072), "earin", u=8, v=5)

# nose: rounded triangle on top of the muzzle
mc = Vector(ELLIPSOIDS[1][1])
nose_hit, nose_n = surface(mc + Vector((0, 0, 0.003)), (0, -0.82, 0.57))
nb = basis_facing(nose_n)
blob(nose_hit + nose_n * 0.0015, nb, (0.0098, 0.0062, 0.0068), "nose", u=8, v=5,
     shape=lambda co: Vector((co.x * (1.0 + 0.38 * co.z), co.y, co.z)))

# mouth: a small "w" under the nose
down = nb @ Vector((0, 0, -1))
side = nb @ Vector((1, 0, 0))
top = nose_hit + down * 0.0058
mid = nose_hit + down * 0.0102
off = 0.0007
for s in (1, -1):
    arc = [mid, mid + side * (0.0038 * s) + down * 0.0016, mid + side * (0.0076 * s) + down * 0.0008, mid + side * (0.0100 * s) - down * 0.0016]
    tube([onto(p, off) for p in arc], 0.00085, "mouth")
tube([onto(top, off), onto(mid, off)], 0.00085, "mouth")

# ---------------------------------------------------------------- weights: details rigid, max 4 influences
for v in detail_verts:
    v[dl].clear()
    v[dl][head_group] = 1.0
unweighted = 0
for v in bm.verts:
    w = sorted(((g, x) for g, x in v[dl].items() if x > 0.01), key=lambda kv: -kv[1])[:4]
    total = sum(x for _, x in w)
    v[dl].clear()
    if total <= 0:
        unweighted += 1
        continue
    for g, x in w:
        v[dl][g] = x / total
bm.to_mesh(hull_me)
bm.free()
if unweighted:
    raise RuntimeError(f"bone heat left {unweighted} vertices unweighted")

# mesh data into armature space, identity parenting (like the cat)
hull_me.transform(arm.matrix_world.inverted() @ obj.matrix_world)
obj.matrix_parent_inverse = Matrix.Identity(4)
obj.matrix_basis = Matrix.Identity(4)
for p in hull_me.polygons:
    p.use_smooth = False
me = hull_me
deform_bones = [b.name for b in arm.data.bones if b.use_deform]

# ---------------------------------------------------------------- material
tex_unity = os.path.join(UNITY_DIR, "sea_otter_palette.png")
tex_source = os.path.join(SOURCE_DIR, "sea_otter_palette.png")
img = save_palette(tex_unity)
img.save(filepath=tex_source)
img.filepath = tex_unity
mat = bpy.data.materials.new("SeaOtter")
mat.use_nodes = True
nt = mat.node_tree
bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
bsdf.inputs['Roughness'].default_value = 0.8
tn = nt.nodes.new('ShaderNodeTexImage')
tn.image = img
tn.interpolation = 'Closest'
nt.links.new(tn.outputs['Color'], bsdf.inputs['Base Color'])
nt.nodes.active = tn
me.materials.clear()
me.materials.append(mat)

# ============================================================================ 3. report
tris = sum(len(p.vertices) - 2 for p in me.polygons)
infl = [sum(1 for g in v.groups if g.weight > 1e-4) for v in me.vertices]
print(f"\n==== SEA OTTER verts={len(me.vertices)} tris={tris} bones={len(arm.data.bones)} deform={len(deform_bones)} "
      f"maxInfluences={max(infl)}")
mins = Vector((1e9,) * 3); maxs = Vector((-1e9,) * 3)
for v in me.vertices:
    w = arm.matrix_world @ v.co
    mins = Vector(map(min, mins, w)); maxs = Vector(map(max, maxs, w))
print(f"bounds min=({mins.x:.3f},{mins.y:.3f},{mins.z:.3f}) max=({maxs.x:.3f},{maxs.y:.3f},{maxs.z:.3f})")
groups = {g.index: g.name for g in obj.vertex_groups}
counts = {}
for v in me.vertices:
    for g in v.groups:
        if g.weight > 0.3:
            counts[groups[g.group]] = counts.get(groups[g.group], 0) + 1
print("verts per bone (w>0.3):", ", ".join(f"{k}:{counts.get(k, 0)}" for k in deform_bones))

# ============================================================================ 4. save + export
for o in bpy.data.objects:
    o.select_set(True)
bpy.ops.export_scene.fbx(filepath=os.path.join(UNITY_DIR, "sea_otter.fbx"), **FBX_EXPORT)
img.filepath = "//sea_otter_palette.png"
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(SOURCE_DIR, "sea_otter.blend"))
img.filepath = tex_source
print("exported", os.path.join(UNITY_DIR, "sea_otter.fbx"))

# ============================================================================ 5. previews
if PREVIEW_DIR:
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'TEXTURE'
    scene.display.shading.show_specular_highlight = False
    scene.render.resolution_x, scene.render.resolution_y = 1000, 700
    if scene.world is None:
        scene.world = bpy.data.worlds.new("w")
    scene.world.color = (0.62, 0.70, 0.64)
    center = (mins + maxs) / 2
    size = (maxs - mins).length
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = 'ORTHO'
    scene.collection.objects.link(cam)
    scene.camera = cam

    def shoot(name, d, scale=1.0, look=None):
        d = Vector(d).normalized()
        target = look if look is not None else center
        cam.data.ortho_scale = size * 0.95 * scale
        cam.location = target + d * size * 3
        # top-down shots are rolled 90 deg so the long body runs along the wide image axis
        q = (-d).to_track_quat('-Z', 'Y') if abs(d.z) < 0.99 else Quaternion((0, 0, 1), math.radians(90)) if d.z > 0 else Quaternion((0, 1, 0, 0))
        cam.rotation_euler = q.to_euler()
        scene.render.filepath = os.path.join(PREVIEW_DIR, name + ".png")
        bpy.ops.render.render(write_still=True)

    def axis_local(bone, axis_world):
        a = arm.matrix_world.inverted().to_3x3() @ Vector(axis_world)
        return (bone.matrix_local.to_3x3().inverted() @ a).normalized()

    def rot(name, axis_world, deg):
        pb = arm.pose.bones[name]
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = Quaternion(axis_local(arm.data.bones[name], axis_world), math.radians(deg)) @ pb.rotation_quaternion

    def reset_pose():
        for pb in arm.pose.bones:
            pb.matrix_basis = Matrix.Identity(4)

    shoot("rest_three_quarter", (1, -1.1, 0.7))
    shoot("rest_side", (1, 0, 0))
    shoot("rest_top", (0, 0, 1))
    shoot("rest_front", (0, -1, 0.15), scale=0.6)
    shoot("rest_face", (0.35, -1, 0.3), scale=0.38, look=Vector((0, -0.17, 0.1)))

    # C-shaped turn: front leads, back trails (yaw about world up)
    for n_, d in (("spine_01.x", 12), ("spine_02.x", 14), ("spine_03.x", 12), ("c_subneck_1.x", 10), ("neck.x", 12), ("head.x", 8)):
        rot(n_, (0, 0, 1), d)
    for n_ in TAIL_BONES:
        rot(n_, (0, 0, 1), -10)
    shoot("pose_turn_top", (0, 0, 1))
    reset_pose()

    # paddle stroke + kick + chest roll
    rot("spine_03.x", (0, -1, 0), 30)
    rot("spine_02.x", (0, -1, 0), 12)
    for side, sgn in (("l", 1), ("r", -1)):
        rot(f"thigh_stretch_dupli_001.{side}", (1, 0, 0), -55)
        rot(f"leg_stretch_dupli_001.{side}", (1, 0, 0), 35)
        rot(f"c_thigh_b.{side}", (1, 0, 0), 35 * sgn)
        rot(f"thigh_stretch.{side}", (1, 0, 0), -30)
    shoot("pose_paddle", (1, -1.1, 0.7))
    reset_pose()

    # flip: whole spine rolled onto the back (back-float)
    rot("root.x", (0, -1, 0), 180)
    shoot("pose_backfloat", (1, -0.6, 0.9))
    reset_pose()
    print("previews in", PREVIEW_DIR)
