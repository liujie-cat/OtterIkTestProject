"""Build the sea otter model from the reference sculpt, on the otter skeleton.

The skeleton is the bind pose the runtime is tuned on (sea_otter_skeleton.json: the cat rig's bone names, hierarchy
and rolls placed on a relaxed float - straight level spine, neck reaching forward, hind legs trailing with the feet
pointed back, forepaws tucked on the chest, tail straight). Only the tail is cut to the reference's length.

The reference (Sea_Otter.stl from the "sea otter 3D print model" STL/VRML download, kept outside the project) floats
on its back with its head tucked, forepaws raised by the face, hind flippers raised and splayed, a flat print base
under its back, and is not quite symmetric. It is cut into head, arms and body, skinned on the same bones placed on
its own pose (REF below, millimetres), then every bone carries its flesh onto the bind pose:
D = T(bind head) F1 S F0^T T(-ref head), F0 / F1 the bone's frame (its axis and a side direction, REF / bind_side) in
either pose, S the scale (S0 across, the length ratio along, SHAPE on top). The sculpt's head is swapped for an
otter's built on the head bone (long rounded skull, blunt snout, cheeks, throat) and a lofted neck joins it on; the
carried mesh is fused on a voxel grid, made exactly symmetric, decimated to a low-poly flat-shaded model, given ears,
eyes, nose and mouth, coloured from a palette and skinned on the bind pose.

Run (Blender 5.x) from the project root:
  blender -b --factory-startup --python Tools/Blender/build_sea_otter.py -- \
      "Assets/ArtByLiv/Lowpoly Cat/Mesh/cat_lowpoly.fbx" <Sea_Otter.stl> Tools/Blender/sea_otter_skeleton.json \
      Assets/ArtResource/Otter/SeaOtter ArtSource/SeaOtter [preview_dir]
Outputs:
  <unity_out_dir>/sea_otter.fbx, sea_otter_basecolor.jpg   (the committed .meta files and SeaOtter.mat are kept)
  <source_out_dir>/sea_otter.blend
"""
import bpy, bmesh, sys, os, math, json
from mathutils import Vector, Matrix, Quaternion
from mathutils.kdtree import KDTree
from mathutils.bvhtree import BVHTree
from mathutils.geometry import intersect_point_line

argv = [os.path.abspath(a) for a in sys.argv[sys.argv.index("--") + 1:]]
CAT_FBX, REF_STL, SKELETON, UNITY_DIR, SOURCE_DIR = argv[:5]
PREVIEW_DIR = argv[5] if len(argv) > 5 else None
for d in (UNITY_DIR, SOURCE_DIR, PREVIEW_DIR):
    if d:
        os.makedirs(d, exist_ok=True)

FBX_EXPORT = dict(
    use_selection=False, apply_unit_scale=True, global_scale=1.0, apply_scale_options='FBX_SCALE_NONE',
    axis_forward='-Z', axis_up='Y', bake_space_transform=False, object_types={'ARMATURE', 'MESH'},
    use_mesh_modifiers=False, mesh_smooth_type='FACE', add_leaf_bones=False, primary_bone_axis='Y',
    secondary_bone_axis='X', use_armature_deform_only=False, armature_nodetype='NULL', bake_anim=False,
    path_mode='STRIP')

S0 = 0.004              # reference millimetres -> metres across every bone
TAIL_KEEP = 0.8         # the skeleton's tail is cut to this share of its length (the reference's tail on this body)
REF_VOXEL = 0.7         # mm: the reference is remeshed evenly first (its print base is a few big triangles)
REF_FACES = 40000       # faces the reference is skinned with
FUSE_VOXEL = 0.003      # m: the carried mesh is fused on this grid
SMOOTH_ITER = 8         # smoothing passes over the fused mesh (irons out the seams between the bones' carries)
SIDE = "r"              # the otter's half that is kept and mirrored (its arm and flipper carry more cleanly)
FACES = 3360            # final triangles (skin; the face pieces come on top)
FLAT_SHADING = True

# ============================================================================ reference anatomy (mm)
# The sculpt lies on its back: belly +Z, head +Y, otter's left +X; trunk mirror plane x = 42.2, spine at z = 56.5.
# Its back was cut flat at z = BASE_Z to stand on the print bed; the cut is bulged back out by BASE_BULGE of its
# half-width, parabolic across it, and smoothed into the sides within BASE_SEAM mm of its rim.
BASE_Z, BASE_BULGE, BASE_SEAM = 42.2, 0.4, 3.0
# head frame from the head's own mirror plane (seeded by the eyes): lateral (left), face (where the eyes look), and
# chin (toward the jaw); the head bone runs 15 deg from the face toward the chin, so the eyes look a little up.
HEAD_LEFT = Vector((0.865, 0.491, -0.105))
HEAD_FACE = Vector((0.295, -0.327, 0.898))
HEAD_CHIN = Vector((0.407, -0.807, -0.427))
HEAD_AXIS = (HEAD_FACE * math.cos(math.radians(15)) + HEAD_CHIN * math.sin(math.radians(15))).normalized()
HEAD_JOINT = Vector((35.6, 81.9, 68.8))

def _v(*a):
    return Vector(a)

_neck0, _neck2 = _v(42.2, 56.0, 55.85), HEAD_JOINT
_neck1 = _neck0.lerp(_neck2, 0.533)
_dorsal = _v(0, 0, -1)
_skull = -(HEAD_CHIN - HEAD_AXIS * HEAD_CHIN.dot(HEAD_AXIS)).normalized()
# name: (head, tail, side direction)
REF = {
    "root.x":        (_v(42.2, 4.0, 56.5), _v(42.2, 14.2, 56.5), _dorsal),
    "spine_01.x":    (_v(42.2, 14.2, 56.5), _v(42.2, 34.1, 56.5), _dorsal),
    "spine_02.x":    (_v(42.2, 34.1, 56.5), _v(42.2, 53.8, 56.5), _dorsal),
    "spine_03.x":    (_v(42.2, 53.8, 56.5), _v(42.2, 59.75, 56.3), _dorsal),
    "c_subneck_1.x": (_neck0, _neck1, _dorsal.slerp(_skull, 0.33)),
    "neck.x":        (_neck1, _neck2, _dorsal.slerp(_skull, 0.67)),
    "head.x":        (HEAD_JOINT, HEAD_JOINT + HEAD_AXIS * 23.85, _skull),
    "c_tail_00.x":   (_v(42.2, 2.25, 57.25), _v(37.6, -12.84, 54.9), _dorsal),
    "c_tail_01.x":   (_v(37.6, -12.84, 54.9), _v(37.0, -19.65, 56.4), _dorsal),
    "c_tail_02.x":   (_v(37.0, -19.65, 56.4), _v(37.0, -26.47, 56.8), _dorsal),
    "c_tail_03.x":   (_v(37.0, -26.47, 56.8), _v(37.5, -33.64, 56.2), _dorsal),
    "c_tail_04.x":   (_v(37.5, -33.64, 56.2), _v(38.2, -40.14, 55.2), _dorsal),
}
_out_l, _out_r, _web = _v(1, 0, 0), _v(-1, 0, 0), _v(0, -1, 0)
# hind: hip, knee (in the haunch), ankle, mid web, ball, toe tips; the web's tail-facing side is its dorsal face
for side, pts, out in (("l", [(53, 5, 60), (61, 4, 66), (61, -9, 66), (67, -8.5, 69), (72.5, -7.5, 71.5), (78, -7, 73)], _out_l),
                       ("r", [(31.4, 5, 60), (23, 4, 66), (20, -7.5, 64.5), (14, -5, 67), (8, -2.5, 70), (1, -0.5, 72)], _out_r)):
    for i, b in enumerate(["c_thigh_b", "thigh_stretch", "leg_stretch", "foot", "toes_01"]):
        REF[f"{b}.{side}"] = (_v(*pts[i]), _v(*pts[i + 1]), out if i < 2 else _web)
# fore: scapula top, shoulder, elbow, wrist, knuckles, finger tips - arms raised straight up by the cheeks, palms in
for side, pts, out in (("l", [(49.1, 57.35, 55.6), (58, 66, 60), (62, 71, 68), (65.5, 73, 76), (67.4, 73.8, 82), (68.5, 74, 87)], _out_l),
                       ("r", [(35.3, 57.35, 55.6), (26, 63, 60), (22, 64, 68), (19, 64.5, 76), (17.3, 65, 82), (16.9, 65, 88)], _out_r)):
    for i, b in enumerate(["c_thigh_b_dupli_001", "thigh_stretch_dupli_001", "leg_stretch_dupli_001", "foot_dupli_001", "toes_01_dupli_001"]):
        REF[f"{b}.{side}"] = (_v(*pts[i]), _v(*pts[i + 1]), out if i == 0 else -out)

def bind_side(name):
    """Side direction of a bone in the bind pose (build frame: +Z dorsal, -Y forward, +X the otter's left)."""
    base, _, side = name.partition(".")
    out = Vector((-1 if side == "r" else 1, 0, 0))
    if base in ("c_thigh_b", "thigh_stretch", "c_thigh_b_dupli_001"):
        return out
    if base in ("thigh_stretch_dupli_001", "leg_stretch_dupli_001", "foot_dupli_001", "toes_01_dupli_001"):
        return Vector((0, 0, -1))
    return Vector((0, 0, 1))

# ============================================================================ 1. skeleton
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
skel = json.load(open(SKELETON))["bones"]
missing = set(skel) - {b.name for b in arm.data.bones}
if missing:
    raise RuntimeError(f"cat rig is missing skeleton bones: {sorted(missing)}")

TAIL_BONES = [f"c_tail_0{i}.x" for i in range(5)]
tail0 = Vector(skel[TAIL_BONES[0]]["head"])
def cut_tail(p):
    return tail0 + (Vector(p) - tail0) * TAIL_KEEP

M = arm.matrix_world.copy()
Minv = M.inverted()
bpy.context.view_layer.objects.active = arm
arm.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
ebs = arm.data.edit_bones
for eb in list(ebs):
    if eb.name not in skel:
        ebs.remove(eb)
for name, b in skel.items():
    eb = ebs[name]
    eb.use_connect = False
    h, t = Vector(b["head"]), Vector(b["tail"])
    if name in TAIL_BONES:
        h, t = cut_tail(h), cut_tail(t)
    eb.head, eb.tail = Minv @ h, Minv @ t
    eb.align_roll(Minv.to_3x3() @ Vector(b["z"]))
    if (eb.parent.name if eb.parent else None) != b["parent"]:
        raise RuntimeError(f"{name}: parent {eb.parent.name if eb.parent else None} != {b['parent']}")
bpy.ops.object.mode_set(mode='OBJECT')
for b in arm.data.bones:
    b.use_deform = skel[b.name]["deform"]
deform_bones = [b.name for b in arm.data.bones if b.use_deform]
bind = {b.name: (M @ b.head_local, M @ b.tail_local) for b in arm.data.bones}
err = max((bind[n][0] - Vector(skel[n]["head"])).length for n in skel if n not in TAIL_BONES)
print(f"skeleton: {len(skel)} bones ({len(deform_bones)} deform), max head error {err:.2e} m; tail tip "
      f"{tuple(round(a, 4) for a in bind['c_tail_04.x'][1])}")
if set(REF) != set(deform_bones):
    raise RuntimeError(f"REF and the deform bones differ: {sorted(set(REF) ^ set(deform_bones))}")

# ============================================================================ 2. reference
before = set(bpy.data.objects)
bpy.ops.wm.stl_import(filepath=REF_STL)
ref = next(o for o in set(bpy.data.objects) - before if o.type == 'MESH')
ref.name = "Reference"
ref.data.transform(ref.matrix_world)
ref.matrix_world = Matrix.Identity(4)

def apply_modifier(obj, kind, **props):
    m = obj.modifiers.new(kind.lower(), kind)
    for k, v in props.items():
        setattr(m, k, v)
    bpy.context.view_layer.objects.active = obj
    for o in bpy.context.scene.objects:
        o.select_set(o == obj)
    bpy.ops.object.modifier_apply(modifier=m.name)

def snap(objs, tag, views, xray=False, scale=1.0, look=None, color='OBJECT'):
    scene = bpy.context.scene
    bpy.context.view_layer.update()
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = color
    scene.display.shading.show_xray = xray
    scene.display.shading.xray_alpha = 0.4
    scene.render.resolution_x, scene.render.resolution_y = 900, 700
    vs = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]
    mn = Vector(map(min, *vs)); mx = Vector(map(max, *vs))
    c = (mn + mx) / 2 if look is None else look; size = (mx - mn).length
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = 'ORTHO'; cam.data.ortho_scale = size * 0.9 * scale; cam.data.clip_end = size * 10
    scene.collection.objects.link(cam); scene.camera = cam
    hidden = [o for o in scene.objects if o not in objs and not o.hide_render and o.type in ('MESH', 'ARMATURE')]
    for o in hidden:
        o.hide_render = True
    for vname, d in views:
        d = Vector(d).normalized()
        cam.location = c + d * size * 3
        q = (-d).to_track_quat('-Z', 'Y') if abs(d.z) < 0.99 else Quaternion((0, 0, 1), math.radians(90)) if d.z > 0 else Quaternion((0, 1, 0, 0))
        cam.rotation_euler = q.to_euler()
        scene.render.filepath = os.path.join(PREVIEW_DIR, f"{tag}_{vname}.png")
        bpy.ops.render.render(write_still=True)
    for o in hidden:
        o.hide_render = False
    bpy.data.objects.remove(cam)

apply_modifier(ref, 'REMESH', mode='VOXEL', voxel_size=REF_VOXEL)

# print base: bulge the flat back out again
bm = bmesh.new()
bm.from_mesh(ref.data)
base = {v.index for v in bm.verts if v.co.z < BASE_Z}
edge = [v.co for v in bm.verts if v.index in base and any(e.other_vert(v).index not in base for e in v.link_edges)]
kd = KDTree(len(edge))
for i, c in enumerate(edge):
    kd.insert(Vector((c.x, c.y, 0)), i)
kd.balance()
dist = {v.index: kd.find(Vector((v.co.x, v.co.y, 0)))[2] for v in bm.verts if v.index in base}
W = max(dist.values())
bm.verts.ensure_lookup_table()
for i, d in dist.items():
    u = min(d / W, 1.0)
    bm.verts[i].co.z -= BASE_BULGE * W * (1 - (1 - u) ** 2)
kd3 = KDTree(len(edge))
for i, c in enumerate(edge):
    kd3.insert(c, i)
kd3.balance()
seam = {v.index: max(0.0, 1 - kd3.find(v.co)[2] / BASE_SEAM) for v in bm.verts}
bm.to_mesh(ref.data)
bm.free()
print(f"reference: {len(ref.data.vertices)} verts after remesh; print base {len(base)} verts, half-width {W:.1f} mm, "
      f"bulged {BASE_BULGE * W:.1f} mm")
# the bulge meets the sculpt's sides in a crease: smoothed away along the base's rim
vg = ref.vertex_groups.new(name="seam")
for i, w in seam.items():
    if w > 0:
        vg.add([i], w, 'REPLACE')
apply_modifier(ref, 'SMOOTH', factor=1.0, iterations=12, vertex_group="seam")
ref.vertex_groups.clear()
apply_modifier(ref, 'SMOOTH', factor=0.5, iterations=4)
apply_modifier(ref, 'DECIMATE', ratio=min(1.0, REF_FACES / len(ref.data.polygons)), use_collapse_triangulate=True)

# ---------------------------------------------------------------- parts
# The sculpt's head rests its chin on the chest and its left paw on the cheek, fused into one surface. Each vertex
# belongs to the bone it is nearest to (distance over the bone's REF_RADIUS, mm); the head and each arm are cut away
# from everything else along the part borders and capped, so each carries only its own flesh.
REF_RADIUS = {"root.x": 17, "spine_01.x": 17, "spine_02.x": 17, "spine_03.x": 17, "c_subneck_1.x": 14, "neck.x": 12,
              "head.x": 16, "c_tail_00.x": 10, "c_tail_01.x": 8, "c_tail_02.x": 8, "c_tail_03.x": 7, "c_tail_04.x": 6,
              "c_thigh_b": 9, "thigh_stretch": 6, "leg_stretch": 4, "foot": 3.5, "toes_01": 3,
              "c_thigh_b_dupli_001": 9, "thigh_stretch_dupli_001": 7, "leg_stretch_dupli_001": 6,
              "foot_dupli_001": 5.5, "toes_01_dupli_001": 5}
ARM = ("thigh_stretch_dupli_001", "leg_stretch_dupli_001", "foot_dupli_001", "toes_01_dupli_001")

def part_of(name):
    base, _, side = name.partition(".")
    return "head" if name == "head.x" else f"arm_{side}" if base in ARM else "body"

def radius(name):
    base, _, side = name.partition(".")
    return REF_RADIUS[base if side in ("l", "r") else name]

def nearest_bone(p, n=None):
    """Bone nearest to a point over its radius; with a normal, bones the surface faces away from count 3x as far
    (where two parts press together, each side's skin faces its own bone)."""
    best, who = 1e9, None
    for name in deform_bones:
        h, t, _ = REF[name]
        q, f = intersect_point_line(p, h, t)
        q = h if f < 0 else t if f > 1 else q
        off = p - q
        d = off.length / radius(name)
        if n is not None and off.length > 1e-6 and n.dot(off) < 0:
            d *= 3
        if d < best:
            best, who = d, name
    return who

bm = bmesh.new()
bm.from_mesh(ref.data)
bm.verts.ensure_lookup_table()
bm.normal_update()
label = [part_of(nearest_bone(v.co, v.normal)) for v in bm.verts]
for _ in range(3):  # majority of the neighbours, so the borders do not fray
    nxt = []
    for v in bm.verts:
        votes = {}
        for e in v.link_edges:
            k = label[e.other_vert(v).index]
            votes[k] = votes.get(k, 0) + 1
        votes[label[v.index]] = votes.get(label[v.index], 0) + 1.5
        nxt.append(max(votes, key=votes.get))
    label = nxt
cut = [f for f in bm.faces if len({label[v.index] for v in f.verts}) > 1]
bmesh.ops.delete(bm, geom=cut, context='FACES_ONLY')
for v in [v for v in bm.verts if not v.link_faces]:
    bm.verts.remove(v)
bm.verts.ensure_lookup_table()
label_of = {v: label[v.index] for v in bm.verts}
filled = bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)
bmesh.ops.triangulate(bm, faces=filled["faces"])
counts = {}
for k in label_of.values():
    counts[k] = counts.get(k, 0) + 1
print(f"parts: {counts}; cut {len(cut)} faces, capped {len(filled['faces'])} holes")
bm.verts.ensure_lookup_table()
bm.verts.index_update()
part_index = {k: i for i, k in enumerate(sorted(counts))}
labels = [label_of[v] for v in bm.verts]
dl = bm.verts.layers.int.new("part")
for v, k in zip(bm.verts, labels):
    v[dl] = part_index[k]
bm.to_mesh(ref.data)
bm.free()
print("part layer:", {k: sum(1 for a in ref.data.attributes["part"].data if a.value == i) for k, i in part_index.items()})

# ---------------------------------------------------------------- reference skeleton + bone heat
ref_arm = bpy.data.objects.new("RefArmature", bpy.data.armatures.new("RefArmature"))
bpy.context.scene.collection.objects.link(ref_arm)
bpy.context.view_layer.objects.active = ref_arm
for o in bpy.context.scene.objects:
    o.select_set(o == ref_arm)
bpy.ops.object.mode_set(mode='EDIT')
rebs = ref_arm.data.edit_bones
for name in deform_bones:
    h, t, s = REF[name]
    eb = rebs.new(name)
    eb.head, eb.tail = h, t
    eb.align_roll(s)
for name in deform_bones:
    p = arm.data.bones[name].parent
    while p is not None and not p.use_deform:
        p = p.parent
    if p is not None:
        rebs[name].parent = rebs[p.name]
bpy.ops.object.mode_set(mode='OBJECT')

def bone_heat(obj, armature):
    for m in [m for m in obj.modifiers if m.type == 'ARMATURE']:
        obj.modifiers.remove(m)
    obj.vertex_groups.clear()
    obj.parent = None
    for o in bpy.context.scene.objects:
        o.select_set(False)
    obj.select_set(True)
    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.parent_set(type='ARMATURE_AUTO')
    names = {g.index: g.name for g in obj.vertex_groups}
    out = []
    for v in obj.data.vertices:
        w = sorted(((names[g.group], g.weight) for g in v.groups if g.weight > 0.01), key=lambda kv: -kv[1])[:4]
        total = sum(x for _, x in w)
        out.append([(n, x / total) for n, x in w] if total > 0 else [])
    return out

def split_parts(obj):
    """One object per part (int layer 'part'), named by part."""
    names = {i: k for k, i in part_index.items()}
    out = {}
    for i, k in names.items():
        o = obj.copy()
        o.data = obj.data.copy()
        o.name = o.data.name = k
        bpy.context.scene.collection.objects.link(o)
        bm = bmesh.new()
        bm.from_mesh(o.data)
        lay = bm.verts.layers.int["part"]
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if v[lay] != i], context='VERTS')
        open_edges = [e for e in bm.edges if e.is_boundary]
        if open_edges:
            bmesh.ops.holes_fill(bm, edges=open_edges, sides=0)
        left = sum(1 for e in bm.edges if e.is_boundary)
        bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
        print(f"  part {k}: {len(bm.verts)} verts, {len(open_edges)} open edges, {left} still open, "
              f"{sum(1 for e in bm.edges if not e.is_manifold)} non-manifold")
        bm.to_mesh(o.data)
        bm.free()
        out[k] = o
    bpy.data.objects.remove(obj)
    return out

parts = split_parts(ref)
ref_w = {}
for k, o in parts.items():
    if k == "head":
        ref_w[k] = [[("head.x", 1.0)] for _ in o.data.vertices]
        continue
    for b in ref_arm.data.bones:
        b.use_deform = part_of(b.name) == k or (k.startswith("arm") and b.name == f"c_thigh_b_dupli_001.{k[-1]}")
    w = bone_heat(o, ref_arm)
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.verts.ensure_lookup_table()
    for _ in range(10):  # misses borrow from a neighbour
        miss = [i for i, x in enumerate(w) if not x]
        if not miss:
            break
        for i in miss:
            n = next((e.other_vert(bm.verts[i]).index for e in bm.verts[i].link_edges if w[e.other_vert(bm.verts[i]).index]), None)
            if n is not None:
                w[i] = list(w[n])
    bm.free()
    if any(not x for x in w):
        print(f"  {k}: {sum(1 for x in w if not x)} verts unweighted, left on the nearest bone")
        w = [x or [(nearest_bone(v.co), 1.0)] for x, v in zip(w, o.data.vertices)]
    ref_w[k] = w
print("reference skinned:", {k: len(w) for k, w in ref_w.items()})

# ============================================================================ 3. carry onto the bind pose
def frame(axis, side):
    y = axis.normalized()
    z = (side - y * side.dot(y)).normalized()
    return Matrix((y.cross(z), y, z)).transposed()

# Flesh shape on top of the carry, (across, along, thickness) in a bone's frame: the sculpt is print-thick, so its
# flippers are thinned into webs (and widened and lengthened back to the flippers the kick is tuned on) and its tail
# is flattened into a paddle.
SHAPE = {"foot": (1.08, 1.0, 0.65), "toes_01": (1.12, 1.2, 0.6),
         "c_tail_01.x": (1.0, 1.0, 0.8), "c_tail_02.x": (1.0, 1.0, 0.8), "c_tail_03.x": (1.0, 1.0, 0.8), "c_tail_04.x": (1.0, 1.0, 0.8)}

D = {}
for name in deform_bones:
    h0, t0, s0 = REF[name]
    h1, t1 = bind[name]
    F0, F1 = frame(t0 - h0, s0), frame(t1 - h1, bind_side(name))
    k = (t1 - h1).length / (t0 - h0).length
    base, _, side = name.partition(".")
    a, b, c = SHAPE.get(base if side in ("l", "r") else name, (1.0, 1.0, 1.0))
    S = Matrix.Diagonal((S0 * a, k * b, S0 * c))
    D[name] = Matrix.Translation(h1) @ (F1 @ S @ F0.transposed()).to_4x4() @ Matrix.Translation(-h0)
    if k / S0 < 0.7 or k / S0 > 1.4:
        print(f"  note: {name} stretched {k / S0:.2f}x along itself")

ref_rest = {}
for k, o in parts.items():
    for m in [m for m in o.modifiers if m.type == 'ARMATURE']:
        o.modifiers.remove(m)
    o.parent = None
    o.vertex_groups.clear()
    o.matrix_world = Matrix.Identity(4)
    ref_rest[k] = [v.co.copy() for v in o.data.vertices]
    for v, w in zip(o.data.vertices, ref_w[k]):
        v.co = sum((x * (D[n] @ v.co) for n, x in w), Vector())
# the forepaws rest side by side on the midline and must come apart to stroke: each arm is eased off the midline
# so the fused paws stay ARM_GAP clear of it
ARM_GAP, ARM_EASE = 0.005, 0.012
for k, sgn in (("arm_l", 1), ("arm_r", -1)):
    for v in parts[k].data.vertices:
        u = sgn * v.co.x
        if u < ARM_GAP + ARM_EASE:
            v.co.x = sgn * (ARM_GAP + ARM_EASE * math.exp((u - ARM_GAP - ARM_EASE) / ARM_EASE))

# ---------------------------------------------------------------- head
# The sculpt's head is swapped for an otter's on the head bone, in its frame (x the otter's left, f forward along the
# bone, u up across it; metres from the bone's head): a long rounded skull whose forehead slopes into a blunt snout,
# cheeks widening the face below the eyes, a short chin, two small whisker pads under the nose, and a throat running
# back into the neck. Ears, eyes, nose and mouth go on after the low-poly pass.
HEAD = {  # centre (x > 0: left, mirrored), half-axes (x, f, u), pitch (deg, nose down)
    "skull":  ((0, 0.030, 0.012), (0.072, 0.066, 0.056), 0),
    "snout":  ((0, 0.090, -0.002), (0.044, 0.062, 0.036), 15),
    "cheek":  ((0.034, 0.060, -0.016), (0.036, 0.045, 0.030), 0),
    "chin":   ((0, 0.075, -0.032), (0.032, 0.042, 0.020), 10),
    "throat": ((0, 0.010, -0.028), (0.056, 0.055, 0.038), 0),
    "pad":    ((0.014, 0.127, -0.020), (0.013, 0.012, 0.012), 0),
}
hh, ht = bind["head.x"]
HX, HF = Vector((1, 0, 0)), (ht - hh).normalized()
HU = HF.cross(HX).normalized()

def at_head(x, f, u):
    return hh + HX * x + HF * f + HU * u

def head_dir(x, f, u):
    return (HX * x + HF * f + HU * u).normalized()

def ellipsoid(bm, centre, radii, axes, segs, rings):
    m = Matrix([a * r for a, r in zip(axes, radii)]).transposed().to_4x4()
    m.translation = centre
    verts = bmesh.ops.create_uvsphere(bm, u_segments=segs, v_segments=rings, radius=1.0, matrix=m)["verts"]
    bmesh.ops.recalc_face_normals(bm, faces=list({f for v in verts for f in v.link_faces}))
    return verts

bpy.data.objects.remove(parts.pop("head"), do_unlink=True)
bm = bmesh.new()
for (x, f, u), radii, pitch in HEAD.values():
    a = math.radians(pitch)
    axes = (HX, HF * math.cos(a) - HU * math.sin(a), HU * math.cos(a) + HF * math.sin(a))
    for s in ((1, -1) if x else (1,)):
        ellipsoid(bm, at_head(s * x, f, u), radii, axes, 48, 24)
head = bpy.data.objects.new("Head", bpy.data.meshes.new("Head"))
bm.to_mesh(head.data)
bm.free()
bpy.context.scene.collection.objects.link(head)
parts["head"] = head

# ---------------------------------------------------------------- neck
# The head stands off the chest; the neck between is a thick tube lofted from the chest's cross-section to the back
# of the head's, along the neck bones.
NECK_RINGS, NECK_SEGS = 16, 32
def section(o, centre, axis, reach):
    """Centre, half-width and half-height of a part's flesh over a slab across an axis through a point."""
    xs, zs = [], []
    for v in o.data.vertices:
        if abs((v.co - centre).dot(axis)) < reach:
            xs.append(v.co.x); zs.append(v.co.z)
    xs.sort(); zs.sort()
    q = lambda a, f: a[int((len(a) - 1) * f)]
    x0, x1, z0, z1 = q(xs, 0.03), q(xs, 0.97), q(zs, 0.03), q(zs, 0.97)
    return Vector(((x0 + x1) / 2, centre.y, (z0 + z1) / 2)), (x1 - x0) / 2, (z1 - z0) / 2

chest_at, head_at = bind["spine_03.x"][0], at_head(0, 0.0, 0)
fwd = (head_at - chest_at).normalized()
chest_c, chest_w, chest_h = section(parts["body"], chest_at, fwd, 0.01)
head_c, head_w, head_h = section(parts["head"], head_at, fwd, 0.01)
print(f"neck from chest {chest_w:.3f}x{chest_h:.3f} to head {head_w:.3f}x{head_h:.3f} (half-width x half-height, m)")
up = frame(fwd, Vector((0, 0, 1))).col[2]
bm = bmesh.new()
rings = []
for i in range(NECK_RINGS):
    f = i / (NECK_RINGS - 1)
    s = f * f * (3 - 2 * f)
    c, w, h = chest_c.lerp(head_c, s), chest_w + (head_w - chest_w) * s, chest_h + (head_h - chest_h) * s
    c = c + fwd * ((chest_at.lerp(head_at, f) - c).dot(fwd))
    rings.append([bm.verts.new(c + Vector((w * math.cos(t), 0, 0)) + up * (h * math.sin(t)))
                  for t in (2 * math.pi * j / NECK_SEGS for j in range(NECK_SEGS))])
for a, b in zip(rings, rings[1:]):
    for j in range(NECK_SEGS):
        bm.faces.new((a[j], a[j - 1], b[j - 1], b[j]))
bm.faces.new(rings[0])
bm.faces.new(rings[-1][::-1])
bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
neck = bpy.data.objects.new("Neck", bpy.data.meshes.new("Neck"))
bm.to_mesh(neck.data)
bm.free()
bpy.context.scene.collection.objects.link(neck)
neck_parts = [neck]


# ============================================================================ 4. fuse, symmetrize, decimate
for o in neck_parts:
    bpy.context.view_layer.objects.active = o
    for x in bpy.context.scene.objects:
        x.select_set(x == o)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
pieces = list(parts.values()) + neck_parts
for o in bpy.context.scene.objects:
    o.select_set(o in pieces)
bpy.context.view_layer.objects.active = parts["body"]
bpy.ops.object.join()
obj = parts["body"]
obj.name = obj.data.name = "SeaOtter"
apply_modifier(obj, 'REMESH', mode='VOXEL', voxel_size=FUSE_VOXEL)
print(f"fused: {len(obj.data.vertices)} verts")
apply_modifier(obj, 'SMOOTH', factor=0.5, iterations=SMOOTH_ITER)

# exactly symmetric: the otter's SIDE half mirrored onto the other
bpy.context.view_layer.objects.active = obj
for o in bpy.context.scene.objects:
    o.select_set(o == obj)
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.symmetrize(direction='POSITIVE_X' if SIDE == "l" else 'NEGATIVE_X', threshold=1e-4)
bpy.ops.object.mode_set(mode='OBJECT')
apply_modifier(obj, 'DECIMATE', ratio=min(1.0, FACES / len(obj.data.polygons) / 2), use_collapse_triangulate=True,
               use_symmetry=True, symmetry_axis='X')
# the decimation is only nearly symmetric: keep the chosen half and mirror it
bm = bmesh.new()
bm.from_mesh(obj.data)
sgn = 1.0 if SIDE == "l" else -1.0
geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
res = bmesh.ops.bisect_plane(bm, geom=geom, plane_co=(0, 0, 0), plane_no=(sgn, 0, 0), clear_inner=True)
for v in res["geom_cut"]:
    if isinstance(v, bmesh.types.BMVert):
        v.co.x = 0.0
mirrored = bmesh.ops.duplicate(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:])
dup_verts = [g for g in mirrored["geom"] if isinstance(g, bmesh.types.BMVert)]
for v in dup_verts:
    v.co.x = -v.co.x
bmesh.ops.reverse_faces(bm, faces=[g for g in mirrored["geom"] if isinstance(g, bmesh.types.BMFace)])
bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-6)
bmesh.ops.triangulate(bm, faces=bm.faces[:])
bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
open_edges = sum(1 for e in bm.edges if not e.is_manifold)
bm.to_mesh(obj.data)
bm.free()
kd = KDTree(len(obj.data.vertices))
for i, v in enumerate(obj.data.vertices):
    kd.insert(v.co, i)
kd.balance()
asym = max(kd.find(Vector((-v.co.x, v.co.y, v.co.z)))[2] for v in obj.data.vertices)
print(f"low poly: {len(obj.data.vertices)} verts, {len(obj.data.polygons)} tris, mirror error {asym:.1e} m, "
      f"{open_edges} non-manifold edges")
for o in [o for o in bpy.data.objects if o not in (obj, arm)]:
    bpy.data.objects.remove(o, do_unlink=True)

# ---------------------------------------------------------------- face
# Crisp pieces set on the low-poly head: small oval ears on the sides at the back of the skull, standing out
# sideways and looking forward-out, with darker insides; big black eyes each with a white glint (lit from the same
# side on both); a rounded inverted triangle of a nose on the snout's tip and a small smile under it. Where each sits
# is a direction in the head frame from the skull's centre (ears, eyes) or the snout's (nose); left pieces are built
# and mirrored.
EAR_AT, EAR_FACE = (0.92, -0.35, 0.18), (0.6, 0.75, 0.1)   # where on the skull; which way the ear's front looks
EAR_R, EAR_OUT = (0.017, 0.021, 0.0065), 0.45              # half-axes (across, out of the skin, thick); centre's height over the skin (radii)
EAR_IN_R = (0.0095, 0.012, 0.0025)
EYE_AT, EYE_R, EYE_SINK = (0.55, 0.83, 0.2), 0.0105, 0.35  # ..., centre's depth under the skin (radii)
GLINT_AT, GLINT_R = (-0.35, 0.75, 0.55), 0.003
NOSE_AT, NOSE_R, NOSE_TAPER = (0, 1.0, 0.35), (0.015, 0.0105, 0.009), 0.6  # ..., half-axes (across, up, out)
SMILE = [(0, -0.005), (0.0035, -0.0085), (0.0075, -0.0095), (0.011, -0.008), (0.014, -0.0045)]  # (across, up) from under the nose
MOUTH_R = 0.0014
KINDS = {"eye": 1, "glint": 2, "nose": 3, "mouth": 4, "ear": 5, "earin": 6}    # 0: skin

bvh = BVHTree.FromObject(obj, bpy.context.evaluated_depsgraph_get())
def on_skin(centre, d):
    hit, n, _, _ = bvh.ray_cast(centre + d * 0.3 + HX * 1e-5, -d)   # a ray in the mirror plane slips between its edges
    if hit is None:
        raise RuntimeError(f"no skin along {tuple(d)} from {tuple(centre)}")
    return hit, n

bm = bmesh.new()
bm.from_mesh(obj.data)
kind = bm.faces.layers.int.new("kind")
def add(verts, name, mirror=True):
    faces = list({f for v in verts for f in v.link_faces})
    for f in faces:
        f[kind] = KINDS[name]
    if mirror:
        twin = bmesh.ops.duplicate(bm, geom=verts + faces)["geom"]
        for v in (g for g in twin if isinstance(g, bmesh.types.BMVert)):
            v.co.x = -v.co.x
        bmesh.ops.reverse_faces(bm, faces=[g for g in twin if isinstance(g, bmesh.types.BMFace)])

def tube(pts, r, across, sides=6):
    rings = []
    for i, p in enumerate(pts):
        t = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
        a = across.cross(t).normalized()
        b = t.cross(a)
        rings.append([bm.verts.new(p + (a * math.cos(2 * math.pi * j / sides) + b * math.sin(2 * math.pi * j / sides)) * r)
                      for j in range(sides)])
    for r0, r1 in zip(rings, rings[1:]):
        for j in range(sides):
            bm.faces.new((r0[j], r0[j - 1], r1[j - 1], r1[j]))
    bm.faces.new(rings[0][::-1])
    bm.faces.new(rings[-1])
    verts = [v for ring in rings for v in ring]
    bmesh.ops.recalc_face_normals(bm, faces=list({f for v in verts for f in v.link_faces}))
    return verts

skull_c, (sx, sf, su) = at_head(*HEAD["skull"][0]), HEAD["skull"][1]
p, _ = on_skin(skull_c, head_dir(*EAR_AT))
q = p - skull_c
n = head_dir(q.dot(HX) / sx ** 2, q.dot(HF) / sf ** 2, q.dot(HU) / su ** 2)
z = head_dir(*EAR_FACE)
y = (n - z * n.dot(z)).normalized()
ear_c = p + y * (EAR_R[1] * EAR_OUT)
add(ellipsoid(bm, ear_c, EAR_R, (y.cross(z), y, z), 14, 7), "ear")
add(ellipsoid(bm, ear_c + z * (EAR_R[2] * 0.75) - y * (EAR_R[1] * 0.1), EAR_IN_R, (y.cross(z), y, z), 12, 6), "earin")

d = head_dir(*EYE_AT)
p, _ = on_skin(skull_c, d)
eye_c = p - d * (EYE_R * EYE_SINK)
add(bmesh.ops.create_icosphere(bm, subdivisions=2, radius=EYE_R, matrix=Matrix.Translation(eye_c))["verts"], "eye")
for c in (eye_c, Vector((-eye_c.x, eye_c.y, eye_c.z))):
    add(bmesh.ops.create_icosphere(bm, subdivisions=1, radius=GLINT_R,
                                   matrix=Matrix.Translation(c + head_dir(*GLINT_AT) * EYE_R))["verts"], "glint", mirror=False)

z = head_dir(*NOSE_AT)
p, _ = on_skin(at_head(*HEAD["snout"][0]), z)
y = (HU - z * HU.dot(z)).normalized()
x = y.cross(z)
nose_c = Vector((0, p.y, p.z)) + z * (NOSE_R[2] * 0.3)
nose = ellipsoid(bm, nose_c, NOSE_R, (x, y, z), 12, 8)
for v in nose:
    lv = (v.co - nose_c).dot(y)
    if lv < 0:
        v.co -= x * ((v.co - nose_c).dot(x) * NOSE_TAPER * -lv / NOSE_R[1])
add(nose, "nose", mirror=False)

under = nose_c - y * (NOSE_R[1] * 0.8)
mouth = []
for a, b in [(0, 0)] + SMILE:
    hit, n = on_skin(under + x * a + y * b, z)
    mouth.append(hit + n * (MOUTH_R * 0.5))
    if a == 0:
        mouth[-1].x = 0.0
add(tube(mouth[:2], MOUTH_R, z), "mouth", mirror=False)
add(tube(mouth[1:], MOUTH_R, z), "mouth")
bm.to_mesh(obj.data)
bm.free()
face_verts = {v for p in obj.data.polygons if obj.data.attributes["kind"].data[p.index].value for v in p.vertices}
print(f"face: ears at {tuple(round(a, 3) for a in ear_c)}, eyes at {tuple(round(a, 3) for a in eye_c)}, "
      f"nose at {tuple(round(a, 3) for a in nose_c)}; {len(face_verts)} verts rigid on the head")

# ============================================================================ 5. skin weights
# Bone heat bleeds the haunch bones across the belly and the tail's root into the haunches and the skin between the
# legs, so the belly pulls with each kick and the rump with the tail. Each is cut back to the flesh around it: within
# REACH (full, gone: distance from the bone, m) of a hind leg bone and on its own side of the midline, MIDLINE (m)
# from it; within TAIL_REACH of a tail bone, its height counted TAIL_FLAT times its width as the tail is flat. What a
# leg bone loses goes to the hip bone; what a tail bone loses, to the bones left on the vertex (the rest of the tail),
# or to the hip bone when none are.
REACH = {"c_thigh_b": (0.035, 0.06), "thigh_stretch": (0.035, 0.06)}
MIDLINE = 0.015
TAIL_REACH, TAIL_FLAT = (0.05, 0.07), 1.7

def _fade(d, full, gone):
    if d <= full:
        return 1.0
    if d >= gone:
        return 0.0
    t = (d - full) / (gone - full)
    return 1.0 - t * t * (3.0 - 2.0 * t)

def limit_reach(bm, dl):
    rules = {}
    for g in obj.vertex_groups:
        b = arm.data.bones.get(g.name)
        base, _, side = g.name.partition(".")
        if b is None:
            continue
        a, c = M @ b.head_local, M @ b.tail_local
        if base in REACH and side in ("l", "r"):
            rules[g.index] = (a, c, REACH[base], 1.0 if side == "l" else -1.0, 1.0)
        elif g.name in TAIL_BONES:
            rules[g.index] = (a, c, TAIL_REACH, 0.0, TAIL_FLAT)
    hip = obj.vertex_groups["root.x"].index
    for v in bm.verts:
        p = v.co
        leg_cut = tail_cut = 0.0
        for gi, (a, c, (full, gone), side, flat) in rules.items():
            w = v[dl].get(gi, 0.0)
            if w <= 0.0:
                continue
            ab = c - a
            t = max(0.0, min(1.0, (p - a).dot(ab) / ab.length_squared))
            off = p - (a + ab * t)
            d = math.sqrt(off.x * off.x + flat * flat * max(0.0, off.length_squared - off.x * off.x))
            keep = _fade(d, full, gone)
            if side:
                keep *= _fade(MIDLINE - p.x * side, 0.0, MIDLINE)
            v[dl][gi] = w * keep
            if side:
                leg_cut += w * (1.0 - keep)
            else:
                tail_cut += w * (1.0 - keep)
        if tail_cut > 0.0 and sum(x for x in v[dl].values()) < 0.05:
            leg_cut += tail_cut
        if leg_cut > 0.0:
            v[dl][hip] = v[dl].get(hip, 0.0) + leg_cut

for o in bpy.context.scene.objects:
    o.select_set(False)
obj.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.parent_set(type='ARMATURE_AUTO')
head_group = obj.vertex_groups["head.x"].index
bm = bmesh.new()
bm.from_mesh(obj.data)
dl = bm.verts.layers.deform.verify()
bm.verts.ensure_lookup_table()
limit_reach(bm, dl)
for i in face_verts:
    bm.verts[i][dl].clear()
    bm.verts[i][dl][head_group] = 1.0
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
for _ in range(8):  # bone heat can miss a few vertices on tangled spots: they borrow a neighbour's weights
    left = []
    for v in unweighted:
        n_ = next((e.other_vert(v) for e in v.link_edges if len(e.other_vert(v)[dl])), None)
        if n_ is None:
            left.append(v)
            continue
        for g, x in n_[dl].items():
            v[dl][g] = x
    unweighted = left
bm.to_mesh(obj.data)
bm.free()
if unweighted:
    raise RuntimeError(f"{len(unweighted)} vertices have no skin weights")

# ============================================================================ 6. colours
# Dark brown fur, the head pale with a paler muzzle, the throat pale on down the neck while its nape turns brown just
# behind the head, dark brown ears with darker insides, paws and flippers near black, dark nose and mouth, black
# eyes. Each face takes one palette cell (flat shading); the shade within a colour and the head's ragged edges come
# from a hash of the face's mirrored position, so both sides match.
PALETTE = {  # name: shades (8-bit sRGB)
    "body":   [(96, 68, 50), (103, 73, 53), (90, 63, 46), (108, 77, 56)],
    "back":   [(82, 57, 42), (88, 61, 45), (77, 53, 39), (92, 64, 47)],
    "neck":   [(150, 122, 96), (158, 129, 101), (143, 116, 91), (164, 134, 106)],
    "head":   [(204, 184, 156), (212, 193, 165), (197, 176, 148), (218, 199, 172)],
    "muzzle": [(232, 220, 200), (238, 227, 208), (226, 213, 192), (242, 232, 214)],
    "ear":    [(80, 60, 49), (85, 64, 52), (76, 57, 46), (89, 67, 54)],
    "earin":  [(54, 40, 34)],
    "paw":    [(60, 43, 33), (65, 47, 36), (56, 40, 31), (69, 50, 38)],
    "nose":   [(46, 33, 29)],
    "mouth":  [(58, 42, 36)],
    "eye":    [(16, 13, 13)],
    "glint":  [(250, 250, 247)],
}
GRID, TEX_SIZE = 8, 256
KIND_NAME = {i: name for name, i in KINDS.items()}
PALE = {"head.x": 1.0, "neck.x": 0.6, "c_subneck_1.x": 0.25}    # how pale each bone's flesh is
THROAT = 0.45           # the neck's flesh facing down is this much paler, facing up this much darker
MUZZLE, MUZZLE_GROW = ("chin", "pad"), 1.15     # the pale muzzle reaches this far round these (their radii)
DARK = {f"{b}.{s}" for b in ("leg_stretch_dupli_001", "foot_dupli_001", "toes_01_dupli_001", "foot", "toes_01") for s in "lr"}
CELL_OF = {key: i for i, key in enumerate((name, k) for name, shades in PALETTE.items() for k in range(len(shades)))}

def cell_uv(name, k=0):
    i = CELL_OF[(name, k % len(PALETTE[name]))]
    return ((i % GRID + 0.5) / GRID, (i // GRID + 0.5) / GRID)

def mirrored_hash(c):
    return hash((round(abs(c.x), 3), round(c.y, 3), round(c.z, 3))) & 0xFFFF

def on_muzzle(c, grow):
    q = c - hh
    x, f, u = abs(q.dot(HX)), q.dot(HF), q.dot(HU)
    for (cx, cf, cu), (rx, rf, ru), pitch in (HEAD[m] for m in MUZZLE):
        a = math.radians(pitch)
        df, du = f - cf, u - cu
        lf, lu = df * math.cos(a) - du * math.sin(a), du * math.cos(a) + df * math.sin(a)
        if ((x - cx) / rx) ** 2 + (lf / rf) ** 2 + (lu / ru) ** 2 < grow * grow:
            return True
    return False

groups = {g.index: g.name for g in obj.vertex_groups}
me = obj.data
vw = [{groups[g.group]: g.weight for g in v.groups} for v in me.vertices]
kinds = [a.value for a in me.attributes["kind"].data]
bm = bmesh.new()
bm.from_mesh(me)
uv = bm.loops.layers.uv.verify()
region_counts = {}
for f in bm.faces:
    c = f.calc_center_median()
    k = mirrored_hash(c)
    if kinds[f.index]:
        name = KIND_NAME[kinds[f.index]]
    else:
        w = {}
        for v in f.verts:
            for b, x in vw[v.index].items():
                w[b] = w.get(b, 0.0) + x / len(f.verts)
        pale = (sum(PALE.get(b, 0.0) * x for b, x in w.items()) + ((k >> 4) % 100 - 50) * 0.003
                - THROAT * f.normal.z * (w.get("neck.x", 0.0) + w.get("c_subneck_1.x", 0.0)))
        if sum(w.get(b, 0.0) for b in DARK) > 0.5:
            name = "paw"
        elif pale > 0.7:
            name = "muzzle" if on_muzzle(c, MUZZLE_GROW + ((k >> 8) % 10 - 5) * 0.01) else "head"
        elif pale > 0.35:
            name = "neck"
        else:
            name = "back" if f.normal.z > 0.6 else "body"
    region_counts[name] = region_counts.get(name, 0) + 1
    for loop in f.loops:
        loop[uv].uv = cell_uv(name, k)
bm.to_mesh(me)
bm.free()
me.attributes.remove(me.attributes["kind"])
print("faces per colour:", region_counts)

img = bpy.data.images.new("sea_otter_basecolor", TEX_SIZE, TEX_SIZE, alpha=False)
cell = TEX_SIZE // GRID
px = [0.0] * (TEX_SIZE * TEX_SIZE * 4)
for (name, k), i in CELL_OF.items():
    col = [x / 255.0 for x in PALETTE[name][k]] + [1.0]  # byte image pixels are stored as sRGB
    cx, cy = i % GRID, i // GRID
    for y in range(cy * cell, (cy + 1) * cell):
        for x in range(cx * cell, (cx + 1) * cell):
            j = (y * TEX_SIZE + x) * 4
            px[j:j + 4] = col
img.pixels = px
img.filepath_raw = os.path.join(UNITY_DIR, "sea_otter_basecolor.jpg")
img.file_format = 'JPEG'
img.save(quality=95)
img.pack()

mat = bpy.data.materials.new("SeaOtter")
mat.use_nodes = True
nt = mat.node_tree
bsdf = next(n_ for n_ in nt.nodes if n_.type == 'BSDF_PRINCIPLED')
bsdf.inputs['Roughness'].default_value = 0.8
tn = nt.nodes.new('ShaderNodeTexImage')
tn.image = img
tn.interpolation = 'Closest'
nt.links.new(tn.outputs['Color'], bsdf.inputs['Base Color'])
nt.nodes.active = tn
me.materials.clear()
me.materials.append(mat)

# mesh data into armature space, identity parenting (like the cat)
me.transform(Minv @ obj.matrix_world)
obj.matrix_parent_inverse = Matrix.Identity(4)
obj.matrix_basis = Matrix.Identity(4)
if FLAT_SHADING:
    for p in me.polygons:
        p.use_smooth = False

# ============================================================================ 7. report
tris = sum(len(p.vertices) - 2 for p in me.polygons)
infl = [sum(1 for g in v.groups if g.weight > 1e-4) for v in me.vertices]
print(f"\n==== SEA OTTER verts={len(me.vertices)} tris={tris} bones={len(arm.data.bones)} deform={len(deform_bones)} "
      f"maxInfluences={max(infl)}")
world = [M @ v.co for v in me.vertices]
mins, maxs = Vector(map(min, *world)), Vector(map(max, *world))
print(f"bounds min=({mins.x:.3f},{mins.y:.3f},{mins.z:.3f}) max=({maxs.x:.3f},{maxs.y:.3f},{maxs.z:.3f})")
groups = {g.index: g.name for g in obj.vertex_groups}
counts = {}
for v in me.vertices:
    for g in v.groups:
        if g.weight > 0.3:
            counts[groups[g.group]] = counts.get(groups[g.group], 0) + 1
print("verts per bone (w>0.3):", ", ".join(f"{k}:{counts.get(k, 0)}" for k in deform_bones))

def extents(pts):
    """Half-extents along a point cloud's principal axes (power iteration with deflation), longest first."""
    c = sum(pts, Vector()) / len(pts)
    cov = Matrix(((0,) * 3,) * 3)
    for p in pts:
        d = p - c
        for i in range(3):
            for j in range(3):
                cov[i][j] += d[i] * d[j] / len(pts)
    out = []
    for _ in range(3):
        a = Vector((0.3, 0.5, 0.8))
        for _ in range(60):
            a = (cov @ a).normalized() if (cov @ a).length > 1e-12 else a
        out.append(max(abs((p - c).dot(a)) for p in pts))
        lam = a.dot(cov @ a)
        cov = cov - lam * Matrix([[a[i] * a[j] for j in range(3)] for i in range(3)])
    return out

for label, bones_ in (("hind flipper .l", ("foot.l", "toes_01.l")), ("forepaw .l", ("foot_dupli_001.l", "toes_01_dupli_001.l")),
                      ("tail", tuple(TAIL_BONES[1:]))):
    pts = [world[i] for i, v in enumerate(me.vertices) if sum(g.weight for g in v.groups if groups[g.group] in bones_) >= 0.5]
    if pts:
        e = extents(pts)
        print(f"{label}: {len(pts)} verts, extent {2 * e[0]:.3f} x {2 * e[1]:.3f} x {2 * e[2]:.3f} m")

# ============================================================================ 8. save + export
for o in bpy.data.objects:
    o.select_set(True)
bpy.ops.export_scene.fbx(filepath=os.path.join(UNITY_DIR, "sea_otter.fbx"), **FBX_EXPORT)
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(SOURCE_DIR, "sea_otter.blend"), relative_remap=True)
print("exported", os.path.join(UNITY_DIR, "sea_otter.fbx"))

# ============================================================================ 9. previews
if PREVIEW_DIR:
    scene = bpy.context.scene
    scene.display.shading.show_specular_highlight = False
    if scene.world is None:
        scene.world = bpy.data.worlds.new("w")
    scene.world.color = (0.62, 0.70, 0.64)

    def axis_local(bone, axis_world):
        a = Minv.to_3x3() @ Vector(axis_world)
        return (bone.matrix_local.to_3x3().inverted() @ a).normalized()

    def rot(name, axis_world, deg):
        pb = arm.pose.bones[name]
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = Quaternion(axis_local(arm.data.bones[name], axis_world), math.radians(deg)) @ pb.rotation_quaternion

    def reset_pose():
        for pb in arm.pose.bones:
            pb.matrix_basis = Matrix.Identity(4)

    face_at = bind["head.x"][0].lerp(bind["head.x"][1], 0.6)
    snap([obj], "rest", [("three_quarter", (1, -1.1, 0.7)), ("side", (1, 0, 0)), ("top", (0, 0, 1)), ("below", (0.3, -0.5, -1)),
                         ("back", (0.4, 1, 0.5))], color='TEXTURE')
    snap([obj], "rest", [("front", (0, -1, 0.15))], color='TEXTURE', scale=0.5, look=face_at)
    snap([obj], "rest", [("face", (0.6, -1, 0.35)), ("face_front", (0, -1, 0.12)), ("head_top", (0, -0.25, 1))],
         color='TEXTURE', scale=0.3, look=face_at)
    snap([obj], "rest", [("head_side", (1, 0, 0)), ("head_three", (1, -1.1, 0.5))], color='TEXTURE', scale=0.45,
         look=bind["neck.x"][1])

    # bones through a see-through skin
    sticks = bpy.data.meshes.new("sticks")
    sbm = bmesh.new()
    for b in arm.data.bones:
        h, t = M @ b.head_local, M @ b.tail_local
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
    snap([obj, stick_obj], "bones", [("side", (1, 0, 0)), ("top", (0, 0, 1))], xray=True)
    bpy.data.objects.remove(stick_obj, do_unlink=True)

    # C-shaped turn: front leads, back trails (yaw about world up)
    for n_, d in (("spine_01.x", 12), ("spine_02.x", 14), ("spine_03.x", 12), ("c_subneck_1.x", 10), ("neck.x", 12), ("head.x", 8)):
        rot(n_, (0, 0, 1), d)
    for n_ in TAIL_BONES:
        rot(n_, (0, 0, 1), -10)
    snap([obj], "pose", [("turn_top", (0, 0, 1))], color='TEXTURE')
    reset_pose()
    # kick: thighs drawn forward and out, shins folded, flippers turned; forepaws swept out to the sides
    for side, sgn in (("l", 1), ("r", -1)):
        rot(f"c_thigh_b.{side}", (1, 0, 0), -40)
        rot(f"c_thigh_b.{side}", (0, 0, 1), -25 * sgn)
        rot(f"thigh_stretch.{side}", (1, 0, 0), 50)
        rot(f"foot.{side}", (1, 0, 0), -30)
        rot(f"thigh_stretch_dupli_001.{side}", (0, 1, 0), 35 * sgn)
        rot(f"leg_stretch_dupli_001.{side}", (0, 1, 0), 25 * sgn)
    snap([obj], "pose", [("kick_side", (1, 0, 0)), ("kick_below", (0.3, -0.5, -1)), ("kick_three", (1, -1.1, 0.7))], color='TEXTURE')
    reset_pose()
    # paddle stroke + chest roll
    rot("spine_03.x", (0, -1, 0), 30)
    rot("spine_02.x", (0, -1, 0), 12)
    for side, sgn in (("l", 1), ("r", -1)):
        rot(f"thigh_stretch_dupli_001.{side}", (1, 0, 0), -55)
        rot(f"leg_stretch_dupli_001.{side}", (1, 0, 0), 35)
        rot(f"c_thigh_b.{side}", (1, 0, 0), 35 * sgn)
        rot(f"thigh_stretch.{side}", (1, 0, 0), -30)
    snap([obj], "pose", [("paddle", (1, -1.1, 0.7))], color='TEXTURE')
    reset_pose()
    # back-float: whole body rolled over
    rot("root.x", (0, -1, 0), 180)
    snap([obj], "pose", [("backfloat", (1, -0.6, 0.9))], color='TEXTURE')
    reset_pose()
    print("previews in", PREVIEW_DIR)
