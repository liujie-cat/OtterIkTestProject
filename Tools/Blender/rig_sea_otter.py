"""Rig the generated low-poly sea otter on the cat rig's skeleton conventions.

The armature is the cat's own (cat_lowpoly.fbx): same object transform, bone names, hierarchy and bone
rolls, so every IK component that works on the cat can be pointed at the same-named otter bones.
Only the face bones are dropped. Bones are moved to the otter's anatomy and re-rolled toward the cat's
Z axes, so local frames (used by AdvancedTwoBoneIK axis settings and the hind trajectory) carry over.

The source mesh (ArtSource/SeaOtter/generated, an image-to-3D export) stands on all fours, crouched with
an arched back, is off-centre, 1 m long and has its tail curled on the ground. It is recentred on its
mirror plane, scaled so shoulders and hips land near the cat's, and the tail is unbent along its centre
line. Bones are placed on that standing anatomy and skinned with Blender's bone heat (the eyes ride
rigidly on the head); then the skeleton is posed into a relaxed float (straight level spine, neck
reaching forward, hind legs trailing with the feet pointed back, forelegs hanging loose, tail straight)
and the mesh is baked into it, so that float is the bind pose.

The rest pose matters at runtime: the IK leaves feet, paws and toes at their rest angle to the shin and
forearm, the hind knee limits are centred on the rest knee, and the spine and neck controllers treat the
rest pose as neutral.

Run (Blender 5.x) from the project root:
  blender -b --factory-startup --python Tools/Blender/rig_sea_otter.py -- \
      "Assets/ArtByLiv/Lowpoly Cat/Mesh/cat_lowpoly.fbx" ArtSource/SeaOtter/generated/low-poly_otter_3d_model.fbx \
      Assets/ArtResource/Otter/SeaOtter ArtSource/SeaOtter [preview_dir]
Outputs:
  <unity_out_dir>/sea_otter.fbx, sea_otter_basecolor.jpg   (the committed .meta files and SeaOtter.mat are kept)
  <source_out_dir>/sea_otter.blend
"""
import bpy, bmesh, sys, os, math, heapq, shutil
from mathutils import Vector, Matrix, Quaternion
from mathutils.kdtree import KDTree

argv = [os.path.abspath(a) for a in sys.argv[sys.argv.index("--") + 1:]]
CAT_FBX, OTTER_FBX, UNITY_DIR, SOURCE_DIR = argv[:4]
PREVIEW_DIR = argv[4] if len(argv) > 4 else None
for d in (UNITY_DIR, SOURCE_DIR, PREVIEW_DIR):
    if d:
        os.makedirs(d, exist_ok=True)

FBX_EXPORT = dict(
    use_selection=False, apply_unit_scale=True, global_scale=1.0, apply_scale_options='FBX_SCALE_NONE',
    axis_forward='-Z', axis_up='Y', bake_space_transform=False, object_types={'ARMATURE', 'MESH'},
    use_mesh_modifiers=False, mesh_smooth_type='FACE', add_leaf_bones=False, primary_bone_axis='Y',
    secondary_bone_axis='X', use_armature_deform_only=False, armature_nodetype='NULL', bake_anim=False,
    path_mode='STRIP')

SCALE = 0.5             # source model units -> meters
EYE_DECIMATE = 0.1      # the eyes come as two dense spheres (most of the source's vertices)
FLAT_SHADING = True

# ============================================================================ anatomy
# Source model units, after recentring on the mirror plane: +Z up, -Y forward (Unity +Z),
# +X = otter's left. Read off plane slices of the standing mesh; scaled by SCALE when placed.
SPINE = {
    # name: (head, tail)  - spine bones point forward like the cat's.
    # ExpShadowNeckController pins c_subneck_1 onto the spine_03 pivot every frame, so their heads are close.
    "root.x":        ((0, 0.190, 0.310), (0, 0.120, 0.335)),
    "spine_01.x":    ((0, 0.120, 0.335), (0, 0.015, 0.350)),
    "spine_02.x":    ((0, 0.015, 0.350), (0, -0.090, 0.355)),
    "spine_03.x":    ((0, -0.090, 0.355), (0, -0.135, 0.370)),
    "c_subneck_1.x": ((0, -0.105, 0.365), (0, -0.200, 0.410)),
    "neck.x":        ((0, -0.200, 0.410), (0, -0.270, 0.470)),
    "head.x":        ((0, -0.270, 0.470), (0, -0.460, 0.490)),
}
TAIL_BONES = ["c_tail_00.x", "c_tail_01.x", "c_tail_02.x", "c_tail_03.x", "c_tail_04.x"]
TAIL_ROOT = (0, 0.200, 0.300)           # c_tail_00 head, inside the rump
TAIL_SPLITS = [0.0, 0.17, 0.37, 0.57, 0.78, 0.97]  # joint positions as fractions of the unbent tail length

# Left limbs (mirrored for .r): girdle, upper, lower, foot, toes - the cat's five-bone chains.
HIND = [(0.075, 0.165, 0.290),   # hip joint (inside the haunch)
        (0.120, 0.105, 0.190),   # knee
        (0.122, 0.155, 0.065),   # ankle
        (0.125, 0.115, 0.033),   # mid sole
        (0.127, 0.060, 0.024),   # ball of the foot
        (0.130, 0.008, 0.012)]   # toe tips
HIND_BONES = ["c_thigh_b", "thigh_stretch", "leg_stretch", "foot", "toes_01"]
FRONT = [(0.055, -0.115, 0.370),  # scapula top (inside the chest)
         (0.090, -0.195, 0.270),  # shoulder
         (0.100, -0.165, 0.175),  # elbow
         (0.090, -0.198, 0.065),  # wrist
         (0.088, -0.250, 0.022),  # knuckles
         (0.088, -0.298, 0.010)]  # toe tips
FRONT_BONES = ["c_thigh_b_dupli_001", "thigh_stretch_dupli_001", "leg_stretch_dupli_001", "foot_dupli_001", "toes_01_dupli_001"]
TWIST = {"thigh_twist": "thigh_stretch", "leg_twist": "leg_stretch",
         "thigh_twist_dupli_001": "thigh_stretch_dupli_001", "leg_twist_dupli_001": "leg_stretch_dupli_001"}

# Relaxed float = the bind pose: world direction of each bone (head -> tail), left side; .r is mirrored.
# Applied parent-first as swings about each bone's head; bones not listed follow their parent.
def _deg(back_from_down, out=0.0):
    a = math.radians(back_from_down)
    return (out, math.sin(a), -math.cos(a))

RELAXED = {
    "root.x":        (0, -1, 0),
    "spine_01.x":    (0, -1, 0),
    "spine_02.x":    (0, -1, 0),
    "spine_03.x":    (0, -1, 0.03),
    "c_subneck_1.x": (0, -1, math.tan(math.radians(15))),
    "neck.x":        (0, -1, math.tan(math.radians(25))),
    "head.x":        (0, -1, 0),
    "c_tail_00.x":   (0, 1, -0.14),
    "c_tail_01.x":   (0, 1, -0.10),
    "c_tail_02.x":   (0, 1, -0.06),
    "c_tail_03.x":   (0, 1, -0.03),
    "c_tail_04.x":   (0, 1, 0),
    # hind: thigh down and a little back, shin trailing, foot pointed back in line with the shin
    "c_thigh_b":     _deg(13, 0.32),
    "thigh_stretch": _deg(61, 0.10),
    "leg_stretch":   _deg(76, 0.12),
    "foot":          _deg(83, 0.12),
    "toes_01":       _deg(87, 0.10),
    # fore: hanging loose, swept back under the chest, paw curled back toward the palm
    "thigh_stretch_dupli_001": _deg(40, 0.08),
    "leg_stretch_dupli_001":   _deg(20, -0.03),
    "foot_dupli_001":          _deg(55),
    "toes_01_dupli_001":       _deg(70),
}

# Tail unbending: the tail is everything behind TAIL_START_Y; its centre line is straightened into
# TAIL_DIRECTION, bending away from the source's droop over TAIL_BLEND so the rump stays put.
TAIL_START_Y = 0.23
TAIL_DIRECTION = Vector((0, math.cos(math.radians(15)), -math.sin(math.radians(15))))
TAIL_BLEND = 0.10

def P(p):
    return Vector(p) * SCALE

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
cat_z = {b.name: b.matrix_local.to_3x3().col[2].copy() for b in arm.data.bones}
missing = KEEP - set(cat_z)
if missing:
    raise RuntimeError(f"cat rig is missing expected bones: {sorted(missing)}")

# ============================================================================ 2. source mesh
before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=OTTER_FBX)
src = [o for o in set(bpy.data.objects) - before if o.type == 'MESH']
if len(src) != 1:
    raise RuntimeError(f"expected one mesh in {OTTER_FBX}, got {[o.name for o in src]}")
obj = src[0]
obj.name = "SeaOtter"
me = obj.data
me.name = "SeaOtter"
me.transform(obj.matrix_world)
obj.matrix_world = Matrix.Identity(4)
src_mat = me.materials[0]
src_img = next((n.image for n in src_mat.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image), None)
if src_img is None:
    raise RuntimeError("source material has no base colour image")

bm = bmesh.new()
bm.from_mesh(me)
bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-6)
bm.verts.index_update()
bm.verts.ensure_lookup_table()

def islands(bm):
    seen, out = set(), []
    for v in bm.verts:
        if v.index in seen:
            continue
        comp, stack = [], [v]
        seen.add(v.index)
        while stack:
            x = stack.pop()
            comp.append(x)
            for e in x.link_edges:
                o = e.other_vert(x)
                if o.index not in seen:
                    seen.add(o.index)
                    stack.append(o)
        out.append(comp)
    return out

def extent(vs):
    return max(v.co.y for v in vs) - min(v.co.y for v in vs)

# the eyes are denser than the body, so the body is the island with the largest extent
parts = sorted(islands(bm), key=extent, reverse=True)
body, eyes = parts[0], parts[1:]
if len(eyes) != 2:
    raise RuntimeError(f"expected body + 2 eyes, got islands {[len(p) for p in parts]}")
eye_centres = [sum((v.co for v in e), Vector()) / len(e) for e in eyes]
x0 = (eye_centres[0].x + eye_centres[1].x) / 2
bmesh.ops.translate(bm, verts=bm.verts[:], vec=(-x0, 0, 0))
print(f"recentred on mirror plane x={x0:+.4f}; body verts={len(body)} eye verts={[len(e) for e in eyes]}")

# ---------------------------------------------------------------- tail unbending
tail = [v for v in body if v.co.y > TAIL_START_Y]
tail_ids = {v.index for v in tail}
ring = [v for v in tail if v.co.y < TAIL_START_Y + 0.02]
dist = {v.index: 0.0 for v in ring}
heap = [(0.0, v.index) for v in ring]
while heap:
    d, i = heapq.heappop(heap)
    if d > dist[i]:
        continue
    v = bm.verts[i]
    for e in v.link_edges:
        o = e.other_vert(v)
        if o.index in tail_ids and d + e.calc_length() < dist.get(o.index, 1e9):
            dist[o.index] = d + e.calc_length()
            heapq.heappush(heap, (dist[o.index], o.index))
BIN = 0.03
bins = {}
for i, d in dist.items():
    bins.setdefault(int(d / BIN), []).append(bm.verts[i].co)
centre = [sum(bins[k], Vector()) / len(bins[k]) for k in sorted(bins)]
tip = bm.verts[max(dist, key=dist.get)].co.copy()
centre.append(tip)
for _ in range(3):  # light smoothing, ends fixed
    centre = [centre[0]] + [(centre[i - 1] + centre[i] * 2 + centre[i + 1]) / 4 for i in range(1, len(centre) - 1)] + [centre[-1]]

def resample(pts, step):
    out = [pts[0].copy()]
    carry = 0.0
    for a, b in zip(pts, pts[1:]):
        seg = (b - a).length
        t = step - carry
        while t <= seg:
            out.append(a.lerp(b, t / seg))
            t += step
        carry = seg - (t - step)
    if (out[-1] - pts[-1]).length > 1e-6:
        out.append(pts[-1].copy())
    return out

C = resample(centre, 0.004)
s_of = [0.0]
for a, b in zip(C, C[1:]):
    s_of.append(s_of[-1] + (b - a).length)
tail_len = s_of[-1]
T = [(C[min(i + 1, len(C) - 1)] - C[max(i - 1, 0)]).normalized() for i in range(len(C))]
Qc = [Quaternion()]
for i in range(1, len(C)):
    Qc.append(T[i - 1].rotation_difference(T[i]) @ Qc[-1])
full = T[0].rotation_difference(TAIL_DIRECTION)
Qs, S = [], [C[0].copy()]
for i in range(len(C)):
    u = min(max(s_of[i] / TAIL_BLEND, 0.0), 1.0)
    u = u * u * (3 - 2 * u)
    Qs.append(Quaternion().slerp(full, u))
    if i:
        S.append(S[-1] + (Qs[i - 1] @ T[0]) * (s_of[i] - s_of[i - 1]))

kd = KDTree(len(C))
for i, c in enumerate(C):
    kd.insert(c, i)
kd.balance()
for v in tail:
    _, i, _ = kd.find(v.co)
    if i == 0:
        continue
    q = Qs[i] @ Qc[i].inverted()
    v.co = S[i] + q @ (v.co - C[i])
print(f"tail: centre line {len(centre)} points, length={tail_len:.3f} (source units), "
      f"base droop {math.degrees(T[0].angle(TAIL_DIRECTION)):.1f} deg -> {TAIL_DIRECTION[:]}, tip -> {tuple(round(a, 3) for a in S[-1])}")
TAIL_PATH = (S, s_of)

# ---------------------------------------------------------------- scale, eyes
bmesh.ops.scale(bm, vec=(SCALE,) * 3, verts=bm.verts[:])
bm.to_mesh(me)
bm.free()

# split into loose parts, decimate the two eye spheres, join back; a vertex group remembers the eyes
bpy.context.view_layer.objects.active = obj
for o in bpy.context.scene.objects:
    o.select_set(o == obj)
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.separate(type='LOOSE')
bpy.ops.object.mode_set(mode='OBJECT')
pieces = sorted(bpy.context.selected_objects, key=lambda o: o.dimensions.length, reverse=True)
obj, eye_objs = pieces[0], pieces[1:]
obj.name = obj.data.name = "SeaOtter"
for eo in eye_objs:
    dm = eo.modifiers.new("decimate", 'DECIMATE')
    dm.ratio = EYE_DECIMATE
    dm.use_collapse_triangulate = True
    bpy.context.view_layer.objects.active = eo
    bpy.ops.object.modifier_apply(modifier=dm.name)
    eo.vertex_groups.new(name="eyes").add(range(len(eo.data.vertices)), 1.0, 'REPLACE')
bpy.context.view_layer.objects.active = obj
bpy.ops.object.join()
me = obj.data
eyes_group = obj.vertex_groups["eyes"]
eye_vert_ids = {v.index for v in me.vertices if any(g.group == eyes_group.index for g in v.groups)}
obj.vertex_groups.remove(eyes_group)
print(f"eyes decimated to {len(eye_vert_ids)} verts")

# ============================================================================ 3. bones
bpy.context.view_layer.objects.active = arm
for o in bpy.context.scene.objects:
    o.select_set(o == arm)
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
    eb.head = Minv @ head
    eb.tail = Minv @ tail
    eb.align_roll(cat_z[name])

def mirror(p):
    return (-p[0], p[1], p[2])

def tail_point(s):
    S_, s_ = TAIL_PATH
    for i in range(1, len(s_)):
        if s_[i] >= s:
            return S_[i - 1].lerp(S_[i], (s - s_[i - 1]) / max(s_[i] - s_[i - 1], 1e-9))
    return S_[-1].copy()

for name, (h, t) in SPINE.items():
    place(name, P(h), P(t))
tail_joints = [P(TAIL_ROOT)] + [tail_point(f * tail_len) * SCALE for f in TAIL_SPLITS[1:]]
for i, name in enumerate(TAIL_BONES):
    place(name, tail_joints[i], tail_joints[i + 1])
for side, f in (("l", lambda p: p), ("r", mirror)):
    for chain, bones in ((HIND, HIND_BONES), (FRONT, FRONT_BONES)):
        for i, b in enumerate(bones):
            place(f"{b}.{side}", P(f(chain[i])), P(f(chain[i + 1])))
    for twist, parent in TWIST.items():
        p = ebs[f"{parent}.{side}"]
        ebs[f"{twist}.{side}"].head = p.head.copy()
        ebs[f"{twist}.{side}"].tail = p.head.lerp(p.tail, 0.5)
        ebs[f"{twist}.{side}"].align_roll(cat_z[f"{twist}.{side}"])
bpy.ops.object.mode_set(mode='OBJECT')
for b in arm.data.bones:
    b.use_deform = b.name.split(".")[0] not in TWIST

# ============================================================================ 4. skin weights
for o in bpy.context.scene.objects:
    o.select_set(False)
obj.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.parent_set(type='ARMATURE_AUTO')

head_group = obj.vertex_groups["head.x"].index
bm = bmesh.new()
bm.from_mesh(me)
dl = bm.verts.layers.deform.verify()
bm.verts.ensure_lookup_table()
for i in eye_vert_ids:
    v = bm.verts[i]
    v[dl].clear()
    v[dl][head_group] = 1.0
unweighted = []
for v in bm.verts:
    w = sorted(((g, x) for g, x in v[dl].items() if x > 0.01), key=lambda kv: -kv[1])[:4]
    total = sum(x for _, x in w)
    v[dl].clear()
    if total <= 0:
        unweighted.append(v)
        continue
    for g, x in w:
        v[dl][g] = x / total
# bone heat can miss a few vertices on open or tangled spots: borrow the nearest weighted neighbour's
for _ in range(8):
    if not unweighted:
        break
    left = []
    for v in unweighted:
        n = next((e.other_vert(v) for e in v.link_edges if len(e.other_vert(v)[dl])), None)
        if n is None:
            left.append(v)
            continue
        for g, x in n[dl].items():
            v[dl][g] = x
    unweighted = left
bm.to_mesh(me)
bm.free()
if unweighted:
    raise RuntimeError(f"{len(unweighted)} vertices have no skin weights")

# ============================================================================ 5. relaxed float as bind pose
bpy.context.view_layer.objects.active = arm
for o in bpy.context.scene.objects:
    o.select_set(o == arm)
bpy.ops.object.mode_set(mode='POSE')
M = arm.matrix_world
R = M.to_3x3().normalized()

def relaxed_dir(name):
    base, _, side = name.partition(".")
    d = RELAXED.get(base if side in ("l", "r") else name)
    if d is None:
        return None
    return Vector((-d[0], d[1], d[2]) if side == "r" else d).normalized()

swings = {}
for pb in sorted(arm.pose.bones, key=lambda pb: len(pb.parent_recursive)):
    want = relaxed_dir(pb.name)
    if want is None:
        continue
    bpy.context.view_layer.update()
    cur = R @ (pb.tail - pb.head).normalized()
    q = R.inverted() @ cur.rotation_difference(want).to_matrix() @ R
    h = pb.head.copy()
    pb.matrix = Matrix.Translation(h) @ q.to_4x4() @ Matrix.Translation(-h) @ pb.matrix
    swings[pb.name] = math.degrees(cur.angle(want))
bpy.context.view_layer.update()
print("relaxed swing (deg):", ", ".join(f"{k}:{v:.0f}" for k, v in swings.items() if not k.endswith(".r")))

ev = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
baked = ev.to_mesh()
posed = [v.co.copy() for v in baked.vertices]
ev.to_mesh_clear()
for v, p in zip(me.vertices, posed):
    v.co = p
# swung bones keep their rolls, so each bone's axes stay where the cat's are relative to the bone
# (re-aligning to the cat's world Z degenerates on bones that turned ~90deg, e.g. the paws)
bpy.ops.pose.armature_apply(selected=False)

bpy.ops.object.mode_set(mode='EDIT')
ebs = arm.data.edit_bones
for side in ("l", "r"):
    for twist, parent in TWIST.items():
        p = ebs[f"{parent}.{side}"]
        ebs[f"{twist}.{side}"].head = p.head.copy()
        ebs[f"{twist}.{side}"].tail = p.head.lerp(p.tail, 0.5)
        ebs[f"{twist}.{side}"].align_roll(cat_z[f"{twist}.{side}"])
bpy.ops.object.mode_set(mode='OBJECT')

def bend(a, b, c):
    ba, bc = (ebs_w[a][0] - ebs_w[b][0]), (ebs_w[c][0] - ebs_w[b][0])
    return 180 - math.degrees(ba.angle(bc))
ebs_w = {b.name: (M @ b.head_local, M @ b.tail_local) for b in arm.data.bones}
print(f"rest bends: knee {bend('c_thigh_b.l', 'thigh_stretch.l', 'leg_stretch.l'):.0f} deg, "
      f"elbow {bend('thigh_stretch_dupli_001.l', 'leg_stretch_dupli_001.l', 'foot_dupli_001.l'):.0f} deg, "
      f"neck-pin gap {(ebs_w['spine_03.x'][0] - ebs_w['c_subneck_1.x'][0]).length:.4f} m")

# mesh data into armature space, identity parenting (like the cat)
me.transform(arm.matrix_world.inverted() @ obj.matrix_world)
obj.matrix_parent_inverse = Matrix.Identity(4)
obj.matrix_basis = Matrix.Identity(4)
if FLAT_SHADING:
    if me.has_custom_normals:
        bpy.context.view_layer.objects.active = obj
        bpy.ops.mesh.customdata_custom_splitnormals_clear()
    for p in me.polygons:
        p.use_smooth = False
deform_bones = [b.name for b in arm.data.bones if b.use_deform]

# ---------------------------------------------------------------- material
tex_unity = os.path.join(UNITY_DIR, "sea_otter_basecolor.jpg")
shutil.copyfile(bpy.path.abspath(src_img.filepath), tex_unity)
img = bpy.data.images.load(tex_unity)
mat = bpy.data.materials.new("SeaOtter")
mat.use_nodes = True
nt = mat.node_tree
bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
bsdf.inputs['Roughness'].default_value = 0.8
tn = nt.nodes.new('ShaderNodeTexImage')
tn.image = img
nt.links.new(tn.outputs['Color'], bsdf.inputs['Base Color'])
nt.nodes.active = tn
me.materials.clear()
me.materials.append(mat)
for m in [m for m in bpy.data.materials if m != mat]:
    bpy.data.materials.remove(m)
for i in [i for i in bpy.data.images if i != img]:
    bpy.data.images.remove(i)

# ============================================================================ 6. report
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

# ============================================================================ 7. save + export
for o in bpy.data.objects:
    o.select_set(True)
bpy.ops.export_scene.fbx(filepath=os.path.join(UNITY_DIR, "sea_otter.fbx"), **FBX_EXPORT)
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(SOURCE_DIR, "sea_otter.blend"), relative_remap=True)
print("exported", os.path.join(UNITY_DIR, "sea_otter.fbx"))

# ============================================================================ 8. previews
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
    shoot("rest_face", (0.35, -1, 0.3), scale=0.4, look=P((0, -0.36, 0.53)))

    # bones through a see-through skin
    sticks = bpy.data.meshes.new("sticks")
    sbm = bmesh.new()
    for b in arm.data.bones:
        h, t = arm.matrix_world @ b.head_local, arm.matrix_world @ b.tail_local
        r = 0.004 if b.use_deform else 0.002
        geo = bmesh.ops.create_cone(sbm, cap_ends=True, segments=6, radius1=r, radius2=r * 0.4, depth=(t - h).length)
        rotq = Vector((0, 0, 1)).rotation_difference((t - h).normalized())
        bmesh.ops.transform(sbm, verts=geo['verts'], matrix=Matrix.Translation((h + t) / 2) @ rotq.to_matrix().to_4x4())
        geo = bmesh.ops.create_uvsphere(sbm, u_segments=8, v_segments=6, radius=r * 1.4)
        bmesh.ops.translate(sbm, verts=geo['verts'], vec=h)
    sbm.to_mesh(sticks)
    sbm.free()
    stick_obj = bpy.data.objects.new("sticks", sticks)
    scene.collection.objects.link(stick_obj)
    stick_obj.color = (0.9, 0.1, 0.1, 1)
    obj.color = (0.85, 0.8, 0.7, 1)
    scene.display.shading.show_xray = True
    scene.display.shading.xray_alpha = 0.35
    scene.display.shading.color_type = 'OBJECT'
    shoot("bones_side", (1, 0, 0))
    shoot("bones_top", (0, 0, 1))
    shoot("bones_front", (0, -1, 0), scale=0.6)
    scene.display.shading.show_xray = False
    scene.display.shading.color_type = 'TEXTURE'
    bpy.data.objects.remove(stick_obj, do_unlink=True)

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
