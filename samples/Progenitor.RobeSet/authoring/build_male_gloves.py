"""Build an original male Guide glove candidate from an explicit retarget scene.

Run in Blender with --scene, --garment, --body, --reference, --blend-out,
--json-out and --report-out. The target body remains reference-only. The
garment receives radial limb corrections and new, original thumb/web grids;
no native body triangles or materials enter the exported garment JSON.
"""
import argparse
import importlib.util
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree


parser = argparse.ArgumentParser(description=__doc__)
for name in ("scene", "garment", "body", "reference", "blend-out", "json-out", "report-out"):
    parser.add_argument("--" + name, required=True)
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
SCENE = Path(args.scene).resolve(strict=True)
REFERENCE = Path(args.reference).resolve(strict=True)
OUT = Path(args.blend_out).resolve()
JSON_OUT = Path(args.json_out).resolve()
REPORT = Path(args.report_out).resolve()
if len({SCENE, REFERENCE, OUT, JSON_OUT, REPORT}) != 5:
    raise ValueError("Input and output paths must be distinct")
if OUT.suffix.lower() != ".blend" or JSON_OUT.suffix.lower() != ".json":
    raise ValueError("Expected .blend and .json outputs")
if any(path.exists() for path in (OUT, JSON_OUT, REPORT)):
    raise ValueError("Outputs must be new")
TOOLS = Path(__file__).resolve().parents[3] / "tools" / "rig-reference"
sys.path.insert(0, str(TOOLS))
from check_poses import check_poses
FIT_SCRIPT = TOOLS / "fit_garment.py"
spec = importlib.util.spec_from_file_location("fit_garment", FIT_SCRIPT)
fit_tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fit_tool)

bpy.ops.wm.open_mainfile(filepath=str(SCENE), load_ui=False, use_scripts=False)
source = bpy.data.objects[args.garment]
body = bpy.data.objects[args.body]
rig = next(m.object for m in source.modifiers if m.type == "ARMATURE")
if next(m.object for m in body.modifiers if m.type == "ARMATURE") != rig:
    raise ValueError("Garment and body must use the same armature")
if not body.get("referenceOnly"):
    raise ValueError("Body must be marked referenceOnly")
reference = json.loads(REFERENCE.read_text(encoding="utf-8-sig"))
# A shared armature alone does not establish that this is the captured body.
check_poses(source, body, [(str(REFERENCE), reference)])
for bone in rig.pose.bones:
    bone.matrix_basis.identity()
bpy.context.view_layer.update()

source_to_root = rig.matrix_world.inverted() @ source.matrix_world
body_to_root = rig.matrix_world.inverted() @ body.matrix_world
inverse = source_to_root.inverted()
body_points = [body_to_root @ v.co for v in body.data.vertices]
source_points = [source_to_root @ v.co for v in source.data.vertices]
body.data.calc_loop_triangles()
source.data.calc_loop_triangles()


def group_name(name):
    return name.split(":", 1)[-1]


def family(name):
    if not name.startswith(("Left_", "Right_")):
        return None
    side = name.split("_", 1)[0]
    for finger in ("Index", "Middle", "Ring", "Pinky", "Thumb"):
        if finger in name or (finger == "Thumb" and "Thum" in name):
            return side + "_" + finger
    if any(part in name for part in ("Wrist", "Elbow", "Shoulder", "Collar")):
        return side + "_Arm"
    return None


def scores(obj):
    groups = {g.index: family(group_name(g.name)) for g in obj.vertex_groups}
    result = []
    for vertex in obj.data.vertices:
        row = {}
        for assignment in vertex.groups:
            key = groups.get(assignment.group)
            if key:
                row[key] = row.get(key, 0) + assignment.weight
        result.append(row)
    return result


body_scores = scores(body)
source_scores = scores(source)
keys = sorted({key for row in source_scores for key in row})
trees = {}
triangle_counts = {}
body_triangles = [tuple(t.vertices) for t in body.data.loop_triangles]
for key in keys:
    side = key.split("_", 1)[0]
    if key.endswith("_Arm"):
        def eligible(v):
            row = body_scores[v]
            return sum(w for k, w in row.items() if k.startswith(side + "_") and
                       (k.endswith("_Arm") or k.endswith("_Thumb"))) > 0.03
    else:
        def eligible(v):
            return body_scores[v].get(key, 0) > 0.02
    triangles = [tri for tri in body_triangles if sum(eligible(i) for i in tri) >= 2]
    if not triangles:
        raise ValueError("No body triangles for " + key)
    trees[key] = BVHTree.FromPolygons(body_points, triangles, all_triangles=True)
    triangle_counts[key] = len(triangles)

normal_matrix = body_to_root.to_3x3().inverted().transposed()
body_normals = [(normal_matrix @ v.normal).normalized() for v in body.data.vertices]
target_clearance = 0.003
max_search = 0.055
max_move = 0.012
bone_heads = {group_name(b.name): b.head_local.copy() for b in rig.data.bones}
arm_axes = {
    side: (bone_heads[side + "_ElbowArmor_JNT"], bone_heads[side + "_Wrist_JNT"])
    for side in ("Left", "Right")
}
finger_axes = {
    side + "_" + finger: (
        bone_heads[side + "_" + finger + "01_JNT"],
        bone_heads[side + "_" + finger + "03_JNT"])
    for side in ("Left", "Right")
    for finger in ("Index", "Middle", "Ring", "Pinky", "Thumb")
}
raw_displacements = []
raw_signed = []
selected_groups = []
missing = 0
for index, point in enumerate(source_points):
    row = source_scores[index]
    if not row:
        raise ValueError("Garment vertex has no mapped anatomy: " + str(index))
    # A finger needs at least 20% finger influence; the web/palm then stays
    # with the arm instead of jumping to another finger.
    finger = [(key, value) for key, value in row.items()
              if not key.endswith("_Arm") and value >= .20]
    key = max(finger if finger else list(row.items()), key=lambda pair: pair[1])[0]
    selected_groups.append(key)
    found = trees[key].find_nearest(point, max_search)
    anchor, face_normal, face_index, distance = found
    if anchor is None:
        raw_displacements.append(Vector())
        raw_signed.append(None)
        missing += 1
        continue
    if key.endswith("_Arm"):
        side = key.split("_", 1)[0]
        elbow, wrist = arm_axes[side]
        axis = wrist - elbow
        raw_t = (point - elbow).dot(axis) / axis.length_squared
        t = max(0.0, min(1.0, raw_t))
        center = elbow + axis * t
        radial = point - center
        if radial.length > 1e-6:
            direction = radial.normalized()
            raw_signed.append((point - anchor).dot(face_normal.normalized()))
            scale = .30 if raw_t <= 1 else .20
            cap = .015 if raw_t <= 1 else .008
            raw_displacements.append(direction * min(cap, radial.length * scale))
            continue
    else:
        start, end = finger_axes[key]
        axis = end - start
        t = (point - start).dot(axis) / axis.length_squared
        center = start + axis * t
        radial = point - center
        if radial.length > 1e-6:
            raw_signed.append((point - anchor).dot(face_normal.normalized()))
            raw_displacements.append(radial.normalized() * min(.007, radial.length * .80))
            continue
    direction = face_normal.normalized()
    signed = (point - anchor).dot(direction)
    raw_signed.append(signed)
    needed = max(0, target_clearance - signed)
    raw_displacements.append(direction * min(max_move, needed))

# Split corners sharing a point must remain coincident after fitting.
shared = {}
for i, point in enumerate(source_points):
    label = tuple(round(x, 6) for x in point)
    shared.setdefault(label, []).append(i)
displacements = list(raw_displacements)
for indices in shared.values():
    delta = sum((raw_displacements[i] for i in indices), Vector()) / len(indices)
    for i in indices:
        displacements[i] = delta

# A narrow wrist-edge correction closes the exposed strip above the hand.
# Keep the gold band and the rest of the palm untouched.
for indices in shared.values():
    point = source_points[indices[0]]
    if not (.95 <= point.z <= 1.01 and .54 <= abs(point.x) <= .61):
        continue
    if not any(selected_groups[i].endswith("_Arm") for i in indices):
        continue
    side = "Right" if point.x > 0 else "Left"
    elbow, wrist = arm_axes[side]
    axis = wrist - elbow
    t = (point - elbow).dot(axis) / axis.length_squared
    radial = point - (elbow + axis * t)
    if radial.length > 1e-6:
        correction = radial.normalized() * .002
        for i in indices:
            displacements[i] = displacements[i] + correction

result = source.copy()
result.data = source.data.copy()
result.name = "Guide gloves male constrained"
bpy.context.collection.objects.link(result)
for i, delta in enumerate(displacements):
    result.data.vertices[i].co = inverse @ (source_points[i] + delta)
if result.data.has_custom_normals:
    result.data.normals_split_custom_set([(0, 0, 0)] * len(result.data.loops))
result.data.update()
result["referenceOnly"] = False
result["fitBody"] = body.name
result["fitMethod"] = "same-side forearm 30 percent and finger 80 percent radial envelopes; palm 20 percent"

after = [source_points[i] + displacements[i] for i in range(len(source_points))]
triangles = [tuple(t.vertices) for t in source.data.loop_triangles]
distortion = fit_tool.geometry_diagnostics(source_points, after, triangles,
                                           [1] * len(source_points))
valid = [x for x in raw_signed if x is not None]
movement = [d.length for d in displacements]
report = {
    "source": str(SCENE), "object": result.name, "body": body.name,
    "method": result["fitMethod"], "targetClearanceMm": target_clearance * 1000,
    "fallbackNormalMoveCapMm": max_move * 1000, "bodyTrianglesByGroup": triangle_counts,
    "garmentVertices": len(source_points), "unmatchedVertices": missing,
    "movedVertices": sum(x > 1e-8 for x in movement),
    "maximumActualMoveMm": max(movement) * 1000,
    "meanActualMoveMm": sum(movement) / len(movement) * 1000,
    "beforeSignedBelowZero": sum(x < 0 for x in valid),
    "beforeSignedBelowTarget": sum(x < target_clearance for x in valid),
    "beforeSignedMinimumMm": min(valid) * 1000,
    "afterApproxSignedBelowZero": sum(
        x + d.dot(raw_displacements[i].normalized() if raw_displacements[i].length else Vector()) < 0
        for i, (x, d) in enumerate(zip(raw_signed, displacements)) if x is not None),
    "distortion": distortion,
    "limitations": "Rest-space nearest normal estimate only. No dynamic collisions, self-intersection, visibility, or live-game approval."
}


def surface_tree(obj):
    to_root = rig.matrix_world.inverted() @ obj.matrix_world
    points = [to_root @ v.co for v in obj.data.vertices]
    obj.data.calc_loop_triangles()
    faces = [tuple(tri.vertices) for tri in obj.data.loop_triangles]
    return BVHTree.FromPolygons(points, faces, all_triangles=True)


def add_patch(label, points, faces, uv, skin_rows):
    if not faces:
        raise ValueError("No authored " + label + " faces")
    mesh = bpy.data.meshes.new("Original Guide glove " + label)
    mesh.from_pydata(points, [], faces)
    mesh.update()
    mesh.materials.append(result.data.materials[0])
    for polygon in mesh.polygons:
        polygon.material_index = 0
        polygon.use_smooth = True
    layer = mesh.uv_layers.new(name="UVMap")
    for loop in mesh.loops:
        layer.data[loop.index].uv = uv[loop.vertex_index]
    patch = bpy.data.objects.new("Original Guide glove " + label + " patch", mesh)
    bpy.context.collection.objects.link(patch)
    patch.matrix_world = result.matrix_world.copy()

    def skin_group(name):
        matches = [group.name for group in result.vertex_groups
                   if group_name(group.name) == name]
        if len(matches) != 1:
            raise ValueError("Ambiguous skin group " + name)
        return patch.vertex_groups.new(name=matches[0])

    groups = {side: {
        "Wrist": skin_group(side + "_Wrist_JNT"),
        "Thumb": skin_group(side + "_Thumb01_JNT"),
        "Index": skin_group(side + "_Index01_JNT")}
        for side in ("Left", "Right")}
    for index, (side, wrist, thumb, index_weight) in enumerate(skin_rows):
        selected = groups["Right" if side == 1 else "Left"]
        for name, weight in (("Wrist", wrist), ("Thumb", thumb),
                             ("Index", index_weight)):
            if weight > 0:
                selected[name].add([index], weight, "REPLACE")
    modifier = patch.modifiers.new("Target linear skinning", "ARMATURE")
    modifier.object = rig
    modifier.use_deform_preserve_volume = False
    bpy.ops.object.select_all(action="DESELECT")
    result.select_set(True)
    patch.select_set(True)
    bpy.context.view_layer.objects.active = result
    bpy.ops.object.join()
    return {"vertices": len(points), "triangles": len(faces)}


# These two small grids use only original polygons. The body BVH supplies a
# clearance coordinate; it is never copied into the authored garment.
body_tree = surface_tree(body)
root_to_glove = result.matrix_world.inverted() @ rig.matrix_world
glove_tree = surface_tree(result)
points, faces, uv, skin_rows = [], [], [], []
nx, nz = 9, 15
for side in (-1, 1):
    slots = {}
    for ix in range(nx):
        x = side * (.58 + ix * .005)
        for iz in range(nz):
            z = .86 + iz * .005
            origin = Vector((x, -1, z))
            body_hit = body_tree.ray_cast(origin, Vector((0, 1, 0)))
            if body_hit[0] is None:
                continue
            glove_hit = glove_tree.ray_cast(origin, Vector((0, 1, 0)))
            body_y = body_hit[0].y
            glove_y = glove_hit[0].y if glove_hit[0] is not None else body_y + .02
            y = min(body_y - .005, glove_y + .001)
            slots[ix, iz] = len(points)
            points.append(root_to_glove @ Vector((x, y, z)))
            uv.append((ix / (nx - 1), iz / (nz - 1)))
            t = ix / (nx - 1)
            thumb = .28 * (1 - t)
            index_weight = .38 + .22 * t
            skin_rows.append((side, 1 - thumb - index_weight, thumb, index_weight))
    for ix in range(nx - 1):
        for iz in range(nz - 1):
            corners = [slots.get((ix, iz)), slots.get((ix + 1, iz)),
                       slots.get((ix + 1, iz + 1)), slots.get((ix, iz + 1))]
            if any(c is None for c in corners):
                continue
            a, b, c, d = corners
            faces.extend(((a, b, c), (a, c, d)) if side == 1
                         else ((a, c, b), (a, d, c)))
report["frontWebPatch"] = add_patch("front web", points, faces, uv, skin_rows)

glove_tree = surface_tree(result)
points, faces, uv, skin_rows = [], [], [], []
ny, nz = 15, 23
for side in (-1, 1):
    slots = {}
    for iy in range(ny):
        y = -.125 + iy * .005
        for iz in range(nz):
            z = .845 + iz * .005
            origin = Vector((side, y, z))
            body_hit = body_tree.ray_cast(origin, Vector((-side, 0, 0)))
            if body_hit[0] is None or not .50 <= abs(body_hit[0].x) <= .72:
                continue
            glove_hit = glove_tree.ray_cast(origin, Vector((-side, 0, 0)))
            body_x = body_hit[0].x
            glove_x = (glove_hit[0].x if glove_hit[0] is not None and
                       .50 <= abs(glove_hit[0].x) <= .72 else body_x - side * .02)
            x = (max(body_x + .005, glove_x - .001) if side == 1
                 else min(body_x - .005, glove_x + .001))
            slots[iy, iz] = len(points)
            points.append(root_to_glove @ Vector((x, y, z)))
            uv.append((iy / (ny - 1), iz / (nz - 1)))
            low = max(0., min(1., (.955 - z) / .11))
            index_weight = .38 + .18 * low
            thumb = .27 - .10 * low
            skin_rows.append((side, 1 - index_weight - thumb, thumb, index_weight))
    for iy in range(ny - 1):
        for iz in range(nz - 1):
            corners = [slots.get((iy, iz)), slots.get((iy + 1, iz)),
                       slots.get((iy + 1, iz + 1)), slots.get((iy, iz + 1))]
            if any(c is None for c in corners):
                continue
            a, b, c, d = corners
            faces.extend(((a, b, c), (a, c, d)) if side == 1
                         else ((a, c, b), (a, d, c)))
report["sideWebPatch"] = add_patch("side web", points, faces, uv, skin_rows)
result["authoredWebPatch"] = True
result["authoredSideWebPatch"] = True
result["webPatchMethod"] = "original front and side grids; body-clearance samples; wrist/thumb/index skin"

spec = importlib.util.spec_from_file_location("export_garment", TOOLS / "export_garment.py")
export_tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(export_tool)
data = export_tool.export_garment(result, reference)
report["exportedSplitVertices"] = len(data["vertices"])
report["exportedTriangles"] = {name: len(data[name]) // 3
                               for name in ("cloth", "gold", "void")}
report["referenceUse"] = "Body clearance coordinates only; no body triangles or materials exported"
for path in (OUT, JSON_OUT, REPORT):
    path.parent.mkdir(parents=True, exist_ok=True)
with JSON_OUT.open("x", encoding="utf-8") as output:
    json.dump(data, output, allow_nan=False, separators=(",", ":"))
bpy.ops.wm.save_as_mainfile(filepath=str(OUT))
with REPORT.open("x", encoding="utf-8") as output:
    json.dump(report, output, indent=2)
print(json.dumps(report))
