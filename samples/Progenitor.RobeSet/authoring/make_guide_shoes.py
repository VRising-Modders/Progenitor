"""Original procedural low shoes fitted to a female Body_GEO reference scene.

Run in Blender with a captured rig/body scene containing "Original robe boots"
for its armature/material metadata. Native body triangles are never exported.
The source reference, output JSON and saved project paths are explicit options.
"""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

REPO = Path(__file__).resolve().parents[3]
parser=argparse.ArgumentParser()
parser.add_argument('--reference',type=Path,required=True)
parser.add_argument('--out',type=Path,required=True)
parser.add_argument('--scene-out',type=Path,required=True)
parser.add_argument('--weight-report',type=Path,required=True)
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
for output in (args.out,args.scene_out,args.weight_report):
    if output.exists():
        raise FileExistsError(f'Refusing to overwrite existing shoe output: {output}')

# Z is fore/aft in the captured Unity root space. Width and crown are shaped
# independently of the reference mesh. Measurements put the bare toes at z=.141,
# width=.105 total, and y=0..~.035. The rounded shoe extends to z=.158.
SECTIONS = [
    (-.119, .012, .034), (-.108, .040, .074), (-.095, .056, .105),
    (-.075, .062, .113), (-.050, .064, .117), (-.025, .065, .119),
    ( .000, .065, .116), ( .025, .065, .106), ( .050, .064, .077),
    ( .075, .064, .064), ( .100, .063, .052), ( .120, .058, .041),
    ( .138, .049, .029), ( .151, .030, .020), ( .160, .008, .009),
]
N = 24
BOTTOM = -.012
OPEN_BACK_UNTIL = .025
SIDE_NAMES = [('Left', -1), ('Right', 1)]

old = bpy.data.objects['Original robe boots']
rig = next(m.object for m in old.modifiers if m.type == 'ARMATURE')
bone_group = {bone.name.split(':',1)[-1]:bone.name for bone in rig.data.bones}
C = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
to_unity = C.inverted() @ rig.matrix_world.inverted() @ old.matrix_world
to_blender = to_unity.inverted()

vertices = []
faces = []
face_material = []
uvs = []
weights = []

def push(unity, side, z):
    idx = len(vertices)
    vertices.append(tuple(to_blender @ Vector(unity)))
    # The body capture shows heel/arch weight on ankle and toe weight on ball.
    # Matching this transition matters during the raised-foot attack frame.
    ball = max(0.0, min(1.0, (z - .052) / .065))
    weights.append({bone_group[side+'_Ankle_JNT']: 1.0-ball,
                    bone_group[side+'_Ball_JNT']: ball})
    return idx

for side, sign in SIDE_NAMES:
    rings = []
    cx = sign * .1018
    for z, half_width, crown in SECTIONS:
        half_width += .002
        if z >= .10:
            half_width += .002
            crown += .003
        ring = []
        for j in range(N):
            theta = 2 * math.pi * j / N
            # Full, flat outsole with a rounded upper. The y profile is raised
            # near the instep and tapers at the end of the covered toes.
            x = cx + half_width * math.cos(theta)
            s = math.sin(theta)
            # Nearly flat, genuinely closed sole under the full foot width.
            # A purely elliptical cross-section lifts at the sides and lets
            # the foot break through the underside when it rolls in combat.
            y = BOTTOM + .005 * (1+s) if s <= 0 else -.007 + (crown+.007)*s
            ring.append(push((x, y, z), side, z))
        rings.append(ring)
    for i in range(len(rings)-1):
        z0, z1 = SECTIONS[i][0], SECTIONS[i+1][0]
        for j in range(N):
            # Quarter arc removed above the ankle and heel: an actual shoe
            # opening, leaving the lower leg exposed rather than a torn boot.
            upper_arc = N//8 <= j < 3*N//8
            if upper_arc and z0 < OPEN_BACK_UNTIL:
                continue
            jp = (j+1) % N
            faces.append((rings[i][j], rings[i][jp], rings[i+1][jp], rings[i+1][j]))
            face_material.append(0)
    # Rounded caps close the heel and toes, including the entire underside.
    for i, direction in [(0, -1), (len(rings)-1, 1)]:
        z = SECTIONS[i][0]
        center = push((cx, (BOTTOM+SECTIONS[i][2])/2, z), side, z)
        for j in range(N):
            jp = (j+1) % N
            faces.append((center, rings[i][j], rings[i][jp]) if direction == 1
                         else (center, rings[i][jp], rings[i][j]))
            face_material.append(0)
    # Two fine gold welts follow the outsole edges; they are original strips,
    # never borrowed native geometry. They inherit the metal palette channel.
    for edge_sign in (-1,1):
        lower=[];upper=[]
        for z,half_width,_ in SECTIONS:
            half_width += .002
            if z >= .10:
                half_width += .002
            x=cx+edge_sign*(half_width+.0012)
            lower.append(push((x,-.006,z),side,z))
            upper.append(push((x,.000,z),side,z))
        for i in range(len(SECTIONS)-1):
            quad=(lower[i],lower[i+1],upper[i+1],upper[i])
            faces.append(quad if edge_sign>0 else tuple(reversed(quad)))
            face_material.append(1)

mesh = bpy.data.meshes.new('Original Guide low shoes mesh')
mesh.from_pydata(vertices, [], faces)
mesh.update()
obj = bpy.data.objects.new('Original Guide low shoes', mesh)
bpy.context.collection.objects.link(obj)
obj.matrix_world = old.matrix_world.copy()
for surface, channel in [('cloth','primary'),('gold','metal')]:
    matches=[m for m in old.data.materials if m is not None
             and m.get('wearableSurface',m.name.lower()) == surface]
    if len(matches) != 1:
        raise ValueError(f'Expected one {surface} source material; got {len(matches)}')
    owned=matches[0].copy()
    owned['wearableSurface']=surface
    owned['wearablePalette']=channel
    mesh.materials.append(owned)
for face, material in zip(mesh.polygons, face_material):
    face.material_index = material
    face.use_smooth = material == 0
for name in sorted(set(k for w in weights for k in w)):
    obj.vertex_groups.new(name=name)
for i, groupweights in enumerate(weights):
    for name, value in groupweights.items():
        if value > 1e-6:
            obj.vertex_groups[name].add([i], value, 'REPLACE')
modifier = obj.modifiers.new('Captured reference rig', 'ARMATURE')
modifier.object = rig
modifier.use_deform_preserve_volume = False
modifier.use_vertex_groups = True
modifier.use_bone_envelopes = False

uv = mesh.uv_layers.new(name='Guide shoe UV')
for poly in mesh.polygons:
    for li in poly.loop_indices:
        v = mesh.vertices[mesh.loops[li].vertex_index].co
        uv.data[li].uv = ((v.x + .25) / .5, (v.y + .15) / .35)

# Calculate explicit outward normals. The input face winding is authored in
# root coordinates, then transformed through the same positive-determinant
# mapping used by export_garment.py.
import bmesh
bm = bmesh.new()
bm.from_mesh(mesh)
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
bm.to_mesh(mesh)
bm.free()
mesh.update()

sys.path.insert(0, str(REPO/'tools/rig-reference'))
from fit_garment import fit
from export_garment import export_garment
# Preserve the original hand-authored shape while transferring the captured
# foot's actual linear skin weights. This follows knee-roll and ball flexion in
# motion, and avoids using copied body triangles as the rendered shoe.
body=bpy.data.objects['Body_GEO']
region=body.vertex_groups.get('Guide foot reference') or body.vertex_groups.new(name='Guide foot reference')
region.add([v.index for v in body.data.vertices if v.co.z < .2],1.0,'REPLACE')
selected=obj.vertex_groups.new(name='Guide shoe weight transfer')
selected.add(list(range(len(mesh.vertices))),1.0,'REPLACE')
unweighted=obj
obj,weight_report=fit(obj,body,region=None,weight_region=selected.name,
                      max_distance=.08,output_name='Original Guide low shoes fitted weights',
                      body_region=region.name)
unweighted.name='Original Guide low shoes unweighted'
obj.name='Original Guide low shoes'
args.weight_report.parent.mkdir(parents=True,exist_ok=True)
args.weight_report.write_text(json.dumps(weight_report,indent=2),encoding='utf-8')
data = export_garment(obj, json.loads(args.reference.read_text(encoding='utf-8-sig')))
args.out.parent.mkdir(parents=True,exist_ok=True)
args.out.write_text(json.dumps(data, separators=(',',':'), allow_nan=False), encoding='utf-8')
args.scene_out.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(args.scene_out))
print('GUIDE_SHOES', len(mesh.vertices), len(mesh.polygons), len(data['vertices']), args.out)
