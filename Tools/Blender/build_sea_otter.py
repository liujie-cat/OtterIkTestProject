"""Build a low-poly sea otter on the cat rig's skeleton conventions.

The armature is the cat's own (cat_lowpoly.fbx): same object transform, bone names, hierarchy and bone
rolls, so every IK component that works on the cat can be pointed at the same-named otter bones.
Only the face bones are dropped. Bones are moved to sea otter anatomy and re-rolled toward the cat's
Z axes, so local frames (used by AdvancedTwoBoneIK axis settings and the hind trajectory) carry over.

Run (Blender 5.x) from the project root:
  blender -b --factory-startup --python Tools/Blender/build_sea_otter.py -- \
      "Assets/ArtByLiv/Lowpoly Cat/Mesh/cat_lowpoly.fbx" Assets/ArtResource/Otter/SeaOtter ArtSource/SeaOtter [preview_dir]
Outputs:
  <unity_out_dir>/sea_otter.fbx, sea_otter_palette.png   (the committed .meta files and SeaOtter.mat are kept)
  <source_out_dir>/sea_otter.blend, sea_otter_palette.png
"""
import bpy, bmesh, sys, os, math, random
from mathutils import Vector, Matrix, Quaternion

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

# ============================================================================ anatomy (meters)
# Blender world space: +Z up, -Y forward (Unity +Z), +X = otter's left. z = 0 is the cat's ground plane.
# Sea otter proportions: head-body ~72% / tail ~28% of total length, small round head on a thick neck,
# long plump cylindrical torso, short forelimbs, large backward-pointing webbed hind flippers.

SPINE = {
    # name: (head, tail)  - spine bones point forward like the cat's
    "root.x":        ((0, 0.100, 0.080), (0, 0.072, 0.080)),
    "spine_01.x":    ((0, 0.072, 0.080), (0, 0.012, 0.081)),
    "spine_02.x":    ((0, 0.012, 0.081), (0, -0.048, 0.082)),
    "spine_03.x":    ((0, -0.048, 0.082), (0, -0.082, 0.084)),
    "c_subneck_1.x": ((0, -0.082, 0.084), (0, -0.106, 0.090)),
    "neck.x":        ((0, -0.106, 0.090), (0, -0.134, 0.098)),
    "head.x":        ((0, -0.134, 0.098), (0, -0.185, 0.094)),
}
TAIL = [(0, 0.110, 0.078), (0, 0.126, 0.077), (0, 0.150, 0.075), (0, 0.176, 0.073), (0, 0.200, 0.072), (0, 0.224, 0.071)]
TAIL_BONES = ["c_tail_00.x", "c_tail_01.x", "c_tail_02.x", "c_tail_03.x", "c_tail_04.x"]

# Left limbs (mirrored for .r). Four segments like the cat: girdle, upper, lower, foot, toes.
# Hind legs are mostly inside the body; the flippers trail backward at belly level beside the tail.
HIND = [(0.026, 0.088, 0.066),   # hip joint (inside body)
        (0.038, 0.072, 0.050),   # knee
        (0.040, 0.100, 0.046),   # ankle
        (0.044, 0.122, 0.044),   # flipper mid
        (0.048, 0.143, 0.043),   # toe base
        (0.051, 0.163, 0.042)]   # flipper tip
HIND_BONES = ["c_thigh_b", "thigh_stretch", "leg_stretch", "foot", "toes_01"]
# Short forelimbs: paws sit just under the chest.
FRONT = [(0.024, -0.060, 0.085),  # scapula top (inside body)
         (0.034, -0.078, 0.058),  # shoulder
         (0.040, -0.068, 0.042),  # elbow
         (0.036, -0.084, 0.030),  # wrist
         (0.032, -0.094, 0.026),  # paw
         (0.030, -0.102, 0.025)]  # paw tip
FRONT_BONES = ["c_thigh_b_dupli_001", "thigh_stretch_dupli_001", "leg_stretch_dupli_001", "foot_dupli_001", "toes_01_dupli_001"]
TWIST = {"thigh_twist": "thigh_stretch", "leg_twist": "leg_stretch",
         "thigh_twist_dupli_001": "thigh_stretch_dupli_001", "leg_twist_dupli_001": "leg_stretch_dupli_001"}

# Body tube stations, nose -> tail tip.
# (y, z_center, half_width_top, half_width_bottom, half_height_top, half_height_bottom, squareness)
BODY = [
    (-0.1935, 0.0915, 0.011, 0.014, 0.009, 0.008, 2.0),   # nose pad
    (-0.1860, 0.0910, 0.019, 0.027, 0.015, 0.014, 2.1),   # muzzle / puffy whisker pads
    (-0.1750, 0.0925, 0.025, 0.032, 0.021, 0.019, 2.1),
    (-0.1620, 0.0960, 0.030, 0.033, 0.027, 0.022, 2.0),   # eyes
    (-0.1480, 0.0975, 0.033, 0.034, 0.029, 0.024, 2.0),   # widest skull
    (-0.1340, 0.0965, 0.032, 0.033, 0.027, 0.026, 2.0),   # ears
    (-0.1200, 0.0930, 0.032, 0.034, 0.026, 0.028, 2.1),   # thick neck
    (-0.1040, 0.0890, 0.035, 0.037, 0.029, 0.032, 2.2),
    (-0.0860, 0.0850, 0.039, 0.041, 0.032, 0.036, 2.3),   # shoulders
    (-0.0660, 0.0830, 0.043, 0.045, 0.035, 0.041, 2.3),   # chest
    (-0.0440, 0.0820, 0.046, 0.048, 0.037, 0.045, 2.3),
    (-0.0200, 0.0810, 0.048, 0.050, 0.038, 0.048, 2.3),   # belly
    (+0.0060, 0.0810, 0.048, 0.050, 0.038, 0.048, 2.3),
    (+0.0320, 0.0800, 0.046, 0.048, 0.037, 0.046, 2.3),
    (+0.0580, 0.0800, 0.043, 0.045, 0.035, 0.042, 2.3),
    (+0.0800, 0.0790, 0.039, 0.041, 0.033, 0.038, 2.3),   # hips
    (+0.1000, 0.0780, 0.032, 0.033, 0.028, 0.029, 2.3),
    (+0.1160, 0.0770, 0.025, 0.025, 0.020, 0.019, 2.3),   # tail base
    (+0.1350, 0.0750, 0.022, 0.022, 0.013, 0.012, 2.6),   # thick, flattened tail
    (+0.1580, 0.0735, 0.021, 0.021, 0.011, 0.010, 2.6),
    (+0.1820, 0.0720, 0.019, 0.019, 0.010, 0.009, 2.6),
    (+0.2040, 0.0710, 0.015, 0.015, 0.008, 0.007, 2.5),
    (+0.2220, 0.0700, 0.009, 0.009, 0.006, 0.005, 2.3),
]
NOSE_TIP = (0, -0.1990, 0.0905)
TAIL_TIP = (0, 0.2310, 0.0700)
BODY_RING = 12

# Limb ring radii at each joint (half_width, half_height); mid-segment rings are interpolated.
HIND_RADII = [(0.018, 0.018), (0.015, 0.014), (0.011, 0.009), (0.017, 0.006), (0.022, 0.005), (0.020, 0.004)]
FRONT_RADII = [(0.015, 0.015), (0.014, 0.014), (0.011, 0.011), (0.010, 0.008), (0.011, 0.007), (0.008, 0.005)]
LIMB_RING = 6

# Skinning along the body: (bone, y at its front edge). Regions are contiguous in y.
BODY_REGIONS = [  # ordered by y ascending; each bone owns [prev_edge, edge)
    ("head.x", -0.134), ("neck.x", -0.106), ("c_subneck_1.x", -0.082), ("spine_03.x", -0.048),
    ("spine_02.x", 0.012), ("spine_01.x", 0.072), ("root.x", 0.110), ("c_tail_00.x", 0.126),
    ("c_tail_01.x", 0.150), ("c_tail_02.x", 0.176), ("c_tail_03.x", 0.200), ("c_tail_04.x", 9.0),
]
BODY_BLEND = [0.012, 0.016, 0.016, 0.024, 0.030, 0.024, 0.012, 0.014, 0.016, 0.016, 0.014]

# ============================================================================ palette
PALETTE = {  # 4 shades per row, 8-bit sRGB
    "brown": [(112, 77, 54), (122, 85, 59), (103, 70, 49), (131, 92, 64)],
    "dark":  [(76, 53, 39), (84, 59, 43), (69, 48, 36), (92, 65, 47)],
    "pale":  [(214, 194, 164), (224, 206, 178), (204, 182, 151), (232, 216, 190)],
    "misc":  [(28, 23, 21), (168, 134, 101), (182, 148, 113), (245, 243, 238)],  # black, tan, tan2, white
}
ROWS = ["brown", "dark", "pale", "misc"]
CELLS = 4
TEX_SIZE = 256

def cell_uv(row, col):
    return ((col + 0.5) / CELLS, (ROWS.index(row) + 0.5) / CELLS)

def save_palette(path):
    img = bpy.data.images.new("sea_otter_palette", TEX_SIZE, TEX_SIZE, alpha=False)
    cell = TEX_SIZE // CELLS
    px = [0.0] * (TEX_SIZE * TEX_SIZE * 4)
    for r, row in enumerate(ROWS):
        for c, rgb in enumerate(PALETTE[row]):
            col = [v / 255.0 for v in rgb] + [1.0]  # byte image pixels are stored as sRGB
            for y in range(r * cell, (r + 1) * cell):
                for x in range(c * cell, (c + 1) * cell):
                    i = (y * TEX_SIZE + x) * 4
                    px[i:i + 4] = col
    img.pixels = px
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    return img

# ============================================================================ helpers
def sgnpow(v, e):
    return math.copysign(abs(v) ** e, v)

def smoothstep(x):
    x = max(0.0, min(1.0, x))
    return x * x * (3 - 2 * x)

def sym_rng(*key):
    h = 1469598103934665603
    for k in key:
        h = ((h ^ (int(k) & 0xFFFFFFFF)) * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return random.Random(h)

class Builder:
    def __init__(self):
        self.bm = bmesh.new()
        self.vweights = []      # per vertex: {bone: weight}
        self.face_part = {}     # face -> (part, extra)

    def vert(self, co, weights):
        v = self.bm.verts.new(co)
        self.vweights.append(dict(weights))
        return v

    def tri(self, a, b, c, part, extra=None):
        try:
            f = self.bm.faces.new((a, b, c))
        except ValueError:
            return None
        self.face_part[f] = (part, extra)
        return f

    def ring(self, ring_id, center, t, hw_top, hw_bot, ht, hb, p, n, odd, weights, noise=0.05):
        t = t.normalized()
        lat = Vector((1, 0, 0))
        lat = (lat - t * t.dot(lat)).normalized()
        up = t.cross(lat)
        offset = math.pi / n if odd else 0.0
        verts, angles = [], []
        for k in range(n):
            phi = offset + 2 * math.pi * k / n
            s, c = math.sin(phi), math.cos(phi)
            mirror = (n - k) % n if not odd else n - 1 - k
            rng = sym_rng(ring_id, min(k, mirror))
            f = 1.0 + noise * (rng.random() * 2 - 1)
            along = 0.0012 * (rng.random() * 2 - 1)
            hw = hw_bot + (hw_top - hw_bot) * (c + 1) * 0.5
            h = ht if c >= 0 else hb
            co = Vector(center) + lat * (hw * sgnpow(s, 2.0 / p) * f) + up * (h * sgnpow(c, 2.0 / p) * f) + t * along
            verts.append(self.vert(co, weights))
            angles.append(phi)
        return verts, angles

    def stitch(self, A, angA, B, angB, part, extra=None):
        n, m = len(A), len(B)
        ea = angA + [angA[0] + 2 * math.pi]
        eb = angB + [angB[0] + 2 * math.pi]
        i = j = 0
        while i < n or j < m:
            if i < n and (j >= m or ea[i + 1] <= eb[j + 1]):
                self.tri(A[i % n], B[j % m], A[(i + 1) % n], part, extra)
                i += 1
            else:
                self.tri(A[i % n], B[j % m], B[(j + 1) % m], part, extra)
                j += 1

    def cap(self, ring, apex, part, extra=None):
        for k in range(len(ring)):
            self.tri(ring[k], ring[(k + 1) % len(ring)], apex, part, extra)

    def blob(self, kind, matrix, weights, part, **kw):
        before = set(self.bm.faces)
        if kind == 'sphere':
            res = bmesh.ops.create_uvsphere(self.bm, u_segments=kw.get('u', 6), v_segments=kw.get('v', 4), radius=1.0, matrix=matrix)
        else:
            res = bmesh.ops.create_cone(self.bm, cap_ends=True, cap_tris=True, segments=kw.get('segments', 5),
                                        radius1=1.0, radius2=kw.get('r2', 0.25), depth=1.0, matrix=matrix)
        for v in res['verts']:
            self.vweights.append(dict(weights))
        for f in set(self.bm.faces) - before:
            self.face_part[f] = (part, None)

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

# ============================================================================ 2. mesh
B = Builder()

def body_weights(y):
    w = {}
    lo_edge = -9.0
    for i, (bone, edge) in enumerate(BODY_REGIONS):
        lo = 1.0 if i == 0 else smoothstep((y - (lo_edge - BODY_BLEND[i - 1] / 2)) / BODY_BLEND[i - 1])
        hi = 0.0 if i == len(BODY_REGIONS) - 1 else smoothstep((y - (edge - BODY_BLEND[i] / 2)) / BODY_BLEND[i])
        v = lo - hi
        if v > 1e-4:
            w[bone] = v
        lo_edge = edge
    return w

# body tube
centers = [Vector((0, s[0], s[1])) for s in BODY]
rings = []
for i, s in enumerate(BODY):
    if i == 0:
        t = centers[1] - Vector(NOSE_TIP)
    elif i == len(BODY) - 1:
        t = Vector(TAIL_TIP) - centers[i - 1]
    else:
        t = centers[i + 1] - centers[i - 1]
    t = -t  # forward direction (toward the nose), so +up stays +Z
    rings.append(B.ring(100 + i, centers[i], t, s[2], s[3], s[4], s[5], s[6], BODY_RING, i % 2 == 1, body_weights(s[0])))
for i in range(len(rings) - 1):
    B.stitch(rings[i][0], rings[i][1], rings[i + 1][0], rings[i + 1][1], "body")
nose_apex = B.vert(Vector(NOSE_TIP), body_weights(NOSE_TIP[1]))
tail_apex = B.vert(Vector(TAIL_TIP), body_weights(TAIL_TIP[1]))
B.cap(rings[0][0], nose_apex, "body")
B.cap(rings[-1][0], tail_apex, "body")

# limbs
def build_limb(limb_id, pts, radii, bones, parent_bone, side):
    f = (lambda p: Vector(p)) if side == "l" else (lambda p: Vector(mirror(p)))
    names = [f"{b}.{side}" for b in bones]
    stations = []  # (pos, (hw, hh), weights)
    for i in range(len(pts)):
        if i == 0:
            w = {parent_bone: 1.0}
        elif i == len(pts) - 1:
            w = {names[-1]: 1.0}
        else:
            w = {names[i - 1]: 0.5, names[i]: 0.5}
        stations.append((f(pts[i]), radii[i], w))
        if i < len(pts) - 1:
            mid = f(pts[i]).lerp(f(pts[i + 1]), 0.5)
            r = tuple((radii[i][k] + radii[i + 1][k]) / 2 for k in range(2))
            stations.append((mid, r, {names[i]: 1.0}))
    limb_rings = []
    for i, (p, r, w) in enumerate(stations):
        if i == 0:
            t = stations[1][0] - p
        elif i == len(stations) - 1:
            t = p - stations[i - 1][0]
        else:
            t = stations[i + 1][0] - stations[i - 1][0]
        limb_rings.append(B.ring(limb_id * 100 + i, p, t, r[0], r[0], r[1], r[1], 2.2, LIMB_RING, i % 2 == 1, w, noise=0.06))
    for i in range(len(limb_rings) - 1):
        B.stitch(limb_rings[i][0], limb_rings[i][1], limb_rings[i + 1][0], limb_rings[i + 1][1], "limb", side)
    tip_dir = (stations[-1][0] - stations[-2][0]).normalized()
    tip = B.vert(stations[-1][0] + tip_dir * stations[-1][1][1] * 1.5, {names[-1]: 1.0})
    base = B.vert(stations[0][0] - (stations[1][0] - stations[0][0]).normalized() * 0.003, {parent_bone: 1.0})
    B.cap(limb_rings[-1][0], tip, "limb", side)
    B.cap(limb_rings[0][0], base, "limb", side)

for side in ("l", "r"):
    build_limb(20, HIND, HIND_RADII, HIND_BONES, "root.x", side)
    build_limb(30, FRONT, FRONT_RADII, FRONT_BONES, "spine_03.x", side)

# eyes, ears, nose (rigid on the head)
HEAD_W = {"head.x": 1.0}
def surface_point(station, phi):
    s = BODY[station]
    hw = s[3] + (s[2] - s[3]) * (math.cos(phi) + 1) * 0.5
    h = s[4] if math.cos(phi) >= 0 else s[5]
    return Vector((hw * sgnpow(math.sin(phi), 2.0 / s[6]), s[0], s[1] + h * sgnpow(math.cos(phi), 2.0 / s[6])))

for sx in (1, -1):
    eye = surface_point(3, math.radians(55))
    eye = Vector((eye.x * sx, eye.y - 0.0015, eye.z))
    r = 0.0042
    eye_c = eye - Vector((0.0012 * sx, 0, 0.0008))
    B.blob('sphere', Matrix.Translation(eye_c) @ Matrix.Diagonal((r, r * 0.9, r, 1)), HEAD_W, "eye", u=6, v=4)
    glint = eye_c + Vector((0.0028 * sx, -0.0022, 0.0020))
    B.blob('sphere', Matrix.Translation(glint) @ Matrix.Diagonal((0.0012, 0.0012, 0.0012, 1)), HEAD_W, "glint", u=4, v=3)

    ear = surface_point(5, math.radians(64))
    ear = Vector((ear.x * sx, ear.y + 0.002, ear.z))
    normal = Vector((0.75 * sx, 0.35, 0.55)).normalized()
    rot = Vector((0, 0, 1)).rotation_difference(normal).to_matrix().to_4x4()
    B.blob('sphere', Matrix.Translation(ear + normal * 0.0012) @ rot @ Matrix.Diagonal((0.0055, 0.0035, 0.0045, 1)), HEAD_W, "ear", u=5, v=3)

nose = Vector((0, -0.1975, 0.0965))
B.blob('sphere', Matrix.Translation(nose) @ Matrix.Diagonal((0.0068, 0.0045, 0.0050, 1)), HEAD_W, "nose", u=5, v=3)

bm = B.bm
bm.verts.index_update()
bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))

# ---------------------------------------------------------------- colours
uv = bm.loops.layers.uv.new("UVMap")
def pick(face):
    part, side = B.face_part.get(face, ("body", None))
    c = face.calc_center_median()
    rng = random.Random(hash((round(abs(c.x), 4), round(c.y, 4), round(c.z, 4))) & 0xFFFFFFFF)
    shade = rng.randrange(CELLS)
    if part in ("eye", "nose"):
        return ("misc", 0)
    if part == "glint":
        return ("misc", 3)
    if part == "limb":
        return ("dark", shade)
    if part == "ear":
        return ("brown", 2)
    y, z = c.y, c.z
    if y > 0.112:
        return ("dark", shade)
    zc = 0.08
    # pale head/throat; the boundary sweeps back along the underside
    t = max(-1.0, min(1.0, (z - zc) / 0.035))
    edge = -0.112 - 0.016 * t
    if y < edge - 0.010:
        return ("pale", shade)
    if y < edge + 0.016:
        keep = (edge + 0.016 - y) / 0.026
        if rng.random() < keep:
            return ("pale", shade)
        return ("misc", 1 + rng.randrange(2)) if t < 0 else ("brown", 3)
    if y < -0.050 and z < zc - 0.012:
        return ("misc", 1 + rng.randrange(2)) if rng.random() < 0.7 else ("brown", shade)
    if z > zc + 0.022:
        return ("brown", (0, 2)[rng.randrange(2)])
    return ("brown", shade)

for face in bm.faces:
    row, col = pick(face)
    u, v = cell_uv(row, col)
    for loop in face.loops:
        loop[uv].uv = (u, v)

# ---------------------------------------------------------------- object + weights
bmesh.ops.transform(bm, matrix=Minv, verts=list(bm.verts))
me = bpy.data.meshes.new("SeaOtter")
obj = bpy.data.objects.new("SeaOtter", me)
bpy.context.scene.collection.objects.link(obj)
obj.parent = arm
obj.matrix_parent_inverse = Matrix.Identity(4)
obj.matrix_basis = Matrix.Identity(4)

deform_bones = [b.name for b in arm.data.bones if b.use_deform]
groups = {name: obj.vertex_groups.new(name=name) for name in deform_bones}
dl = bm.verts.layers.deform.verify()
for v in bm.verts:
    w = B.vweights[v.index]
    total = sum(w.values())
    for bone, val in sorted(w.items(), key=lambda kv: -kv[1])[:4]:
        v[dl][groups[bone].index] = val / total
bm.to_mesh(me)
bm.free()
for p in me.polygons:
    p.use_smooth = False
mod = obj.modifiers.new("Armature", 'ARMATURE')
mod.object = arm

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
bsdf.inputs['Roughness'].default_value = 0.85
tn = nt.nodes.new('ShaderNodeTexImage')
tn.image = img
tn.interpolation = 'Closest'
nt.links.new(tn.outputs['Color'], bsdf.inputs['Base Color'])
nt.nodes.active = tn
me.materials.append(mat)

# ============================================================================ 3. report
tris = sum(len(p.vertices) - 2 for p in me.polygons)
infl = [sum(1 for g in v.groups if g.weight > 1e-4) for v in me.vertices]
unweighted = sum(1 for n in infl if n == 0)
print(f"\n==== SEA OTTER verts={len(me.vertices)} tris={tris} bones={len(arm.data.bones)} deform={len(deform_bones)} "
      f"maxInfluences={max(infl)} unweighted={unweighted}")
mins = Vector((1e9,) * 3); maxs = Vector((-1e9,) * 3)
for v in me.vertices:
    w = arm.matrix_world @ v.co
    mins = Vector(map(min, mins, w)); maxs = Vector(map(max, maxs, w))
print(f"bounds min=({mins.x:.3f},{mins.y:.3f},{mins.z:.3f}) max=({maxs.x:.3f},{maxs.y:.3f},{maxs.z:.3f})")
print("bone frames vs cat (deg between local X / Z axes; Y follows the new bone direction):")
for b in arm.data.bones:
    m = arm.matrix_world @ b.matrix_local
    c = cat_frames[b.name]
    ax = lambda mm, i: mm.to_3x3().col[i].normalized()
    dx = math.degrees(ax(m, 0).angle(ax(c, 0)))
    dz = math.degrees(ax(m, 2).angle(ax(c, 2)))
    dy = math.degrees(ax(m, 1).angle(ax(c, 1)))
    print(f"  {b.name:26s} dirChange={dy:6.1f} dX={dx:6.1f} dZ={dz:6.1f}")

# ============================================================================ 4. save + export
for o in bpy.data.objects:
    o.select_set(True)
bpy.ops.export_scene.fbx(filepath=os.path.join(UNITY_DIR, "sea_otter.fbx"), **FBX_EXPORT)
img.filepath = "//sea_otter_palette.png"
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
    shoot("rest_face", (0.6, -1, 0.35), scale=0.42, look=Vector((0, -0.16, 0.095)))

    # C-shaped turn: front leads, back trails (yaw about world up)
    for n_, d in (("spine_01.x", 12), ("spine_02.x", 14), ("spine_03.x", 12), ("c_subneck_1.x", 10), ("neck.x", 12), ("head.x", 8)):
        rot(n_, (0, 0, 1), d)
    for n_ in TAIL_BONES:
        rot(n_, (0, 0, 1), -10)
    shoot("pose_turn_top", (0, 0, 1))
    reset_pose()

    # kick + chest roll + head up
    rot("spine_03.x", (0, -1, 0), 35)
    rot("spine_02.x", (0, -1, 0), 15)
    rot("neck.x", (1, 0, 0), -15)
    for side, sgn in (("l", 1), ("r", -1)):
        rot(f"thigh_stretch.{side}", (1, 0, 0), 35 * sgn)
        rot(f"leg_stretch.{side}", (1, 0, 0), -25)
        rot(f"thigh_stretch_dupli_001.{side}", (1, 0, 0), -40 * sgn)
    shoot("pose_kick_roll", (1, -1.1, 0.7))
    reset_pose()

    # flip: whole spine rolled onto the back (back-float), to check the belly / chest colours
    rot("root.x", (0, -1, 0), 180)
    shoot("pose_backfloat", (1, -0.6, 0.9))
    reset_pose()

    # scale reference: the cat, lifted above the otter, same orthographic scale
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=CAT_FBX)
    cat_tex = os.path.join(os.path.dirname(os.path.dirname(CAT_FBX)), "Textures", "cat_tex_11.png")
    cat_img = bpy.data.images.load(cat_tex) if os.path.exists(cat_tex) else None
    for o in set(bpy.data.objects) - before:
        if o.parent is None:
            o.location.z += 0.16
        if o.type == 'MESH' and cat_img:
            for slot in o.material_slots:
                if slot.material is None:
                    continue
                slot.material.use_nodes = True
                cnt = slot.material.node_tree
                ctn = cnt.nodes.new('ShaderNodeTexImage')
                ctn.image = cat_img
                ctn.interpolation = 'Closest'
                cnt.nodes.active = ctn
    center = center + Vector((0, 0, 0.08))
    shoot("compare_cat_side", (1, 0, 0), scale=1.25)
    print("previews in", PREVIEW_DIR)
