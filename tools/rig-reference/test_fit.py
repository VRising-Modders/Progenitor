"""Run with Blender --background --python-exit-code 1 --python this_file."""
import sys
from pathlib import Path
import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from fit_garment import fit, skin, geometry_diagnostics

square=[Vector(p) for p in [(0,0,0),(1,0,0),(0,1,0),(1,1,0)]]
faces=[(0,1,2),(2,1,3)]
clean=geometry_diagnostics(square,square,faces,[1,1,1,0])
assert not clean['requiresReview'] and clean['selectedFitRegion']['triangles']==2
assert clean['wholeGarment']['flippedTriangles']==0
boundary=geometry_diagnostics(square,square,faces,[1,0,0,0])
assert boundary['selectedFitRegion']['triangles']==1, 'A partly selected seam triangle was omitted'
folded=[p.copy() for p in square];folded[2]=Vector((0,-.2,0))
distorted=geometry_diagnostics(square,folded,faces,[1,1,1,0])
assert distorted['requiresReview'] and distorted['selectedFitRegion']['flippedTriangles']==1
assert distorted['wholeGarment']['triangles']==2
pinched=[p.copy() for p in square];pinched[2]=Vector((0,.1,0))
pinch=geometry_diagnostics(square,pinched,faces,[1,1,1,0])
assert pinch['selectedFitRegion']['collapsedTriangles']==1
assert pinch['selectedFitRegion']['compressedEdges']>=1
creased=[p.copy() for p in square];creased[3]=Vector((1,1,1))
crease=geometry_diagnostics(square,creased,faces,[1,1,1,1])
assert crease['wholeGarment']['dihedralOver45Before']==0
assert crease['wholeGarment']['dihedralOver45After']==1
point=[Vector((0,0,0)) for _ in square]
flattened=geometry_diagnostics(square,point,faces,[1,1,1,1])
assert flattened['requiresReview']
assert flattened['wholeGarment']['minimumEdgeRatio']==0
assert flattened['wholeGarment']['maximumEdgeRatio']==0

bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
arm = bpy.data.armatures.new('Test rig')
rig = bpy.data.objects.new('Test rig', arm)
bpy.context.collection.objects.link(rig)
bpy.context.view_layer.objects.active = rig
rig.select_set(True)
bpy.ops.object.mode_set(mode='EDIT')
for name, x in [('A', 0), ('B', 1)]:
    bone = arm.edit_bones.new(name)
    bone.head, bone.tail = (x, 0, 0), (x, 1, 0)
bpy.ops.object.mode_set(mode='OBJECT')

def mesh(name, vertices, triangles):
    data = bpy.data.meshes.new(name)
    data.from_pydata(vertices, [], triangles)
    data.update()
    obj = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(obj)
    obj.modifiers.new('Skin', 'ARMATURE').object = rig
    return obj

body = mesh('Body', [(0,0,0),(1,0,0),(0,1,0)], [(0,1,2)])
body.vertex_groups.new(name='A').add([0,2], 1, 'REPLACE')
body.vertex_groups.new(name='B').add([1], 1, 'REPLACE')
garment = mesh('Original', [(0.25,0.25,0.02),(0.4,0.2,0.02),(0.2,0.4,0.02),(0,0,-1)], [(0,1,2)])
garment.vertex_groups.new(name='A').add([0,1,2,3], 1, 'REPLACE')
garment.vertex_groups.new(name='FitRegion').add([0,1,2], 1, 'REPLACE')
garment['wearablePaletteSource'] = 'Chest'
garment['wearablePaletteMode'] = 'authored'
uv = garment.data.uv_layers.new(name='UVMap')
for loop in garment.data.loops:
    uv.data[loop.index].uv = (loop.index/3, 0.5)
original = [v.co.copy() for v in garment.data.vertices]
fitted, report = fit(garment, body, clearance=0.006)
assert fitted['wearablePaletteSource'] == 'Chest', 'Fit lost object palette source metadata'
assert fitted['wearablePaletteMode'] == 'authored', 'Fit lost object palette mode metadata'
assert abs(fitted.data.vertices[0].co.z-0.006) < 1e-7
assert (fitted.data.vertices[3].co-original[3]).length == 0, 'Outside-region skirt changed'
assert all((v.co-p).length == 0 for v,p in zip(garment.data.vertices,original)), 'Source modified'
weights = skin(fitted, rig)[0]
assert abs(weights['A']-0.75) < 1e-6 and abs(weights['B']-0.25) < 1e-6, weights
assert [tuple(p.vertices) for p in fitted.data.polygons] == [tuple(p.vertices) for p in garment.data.polygons]
assert [tuple(v.uv) for v in fitted.data.uv_layers[0].data] == [tuple(v.uv) for v in garment.data.uv_layers[0].data]
loose, loose_report = fit(garment, body, region=None, weight_region='FitRegion', output_name='Loose')
assert loose['wearablePaletteSource'] == 'Chest', 'Weights-only fit lost palette source metadata'
assert loose['wearablePaletteMode'] == 'authored', 'Weights-only fit lost palette mode metadata'
assert all((v.co-p).length == 0 for v,p in zip(loose.data.vertices,original)), 'Weights-only changed fabric shape'
assert abs(skin(loose, rig)[0]['B']-0.25) < 1e-6
assert loose_report['weightsOnly'] and loose_report['maxMovementMeters'] == 0

def rejects(call, text):
    try:
        call()
    except ValueError:
        return
    raise AssertionError(text)

rejects(lambda: fit(garment, body, max_distance=0.001, output_name='Too far'), 'Distant vertices accepted')
garment['referenceOnly'] = True
rejects(lambda: fit(garment, body, output_name='Native copy'), 'Reference accepted as garment')
garment['referenceOnly'] = False
rejects(lambda: fit(garment, body, region='Missing', output_name='Missing mask'), 'Missing region accepted')
garment.modifiers[0].use_deform_preserve_volume = True
rejects(lambda: fit(garment, body, output_name='Dual quaternion'), 'Unsupported deformation accepted')
garment.modifiers[0].use_deform_preserve_volume = False

# Curved reference, transformed objects and a feathered fit/weight mask.
curved = mesh('Curved body', [(0,0,1),(1,0,0),(0,1,0),(-1,0,0),(0,-1,0),(0,0,-1)],
              [(0,1,2),(0,2,3),(0,3,4),(0,4,1),(5,2,1),(5,3,2),(5,4,3),(5,1,4)])
curved.vertex_groups.new(name='B').add(list(range(6)), 1, 'REPLACE')
curved.matrix_world = Matrix.Translation((1,2,3)) @ Matrix.Rotation(0.4,4,'X')
world_normal = curved.matrix_world.to_3x3() @ Vector((0,0,1))
world_anchor = curved.matrix_world @ Vector((0,0,1))
partial = mesh('Partial garment', [(0,0,0),(0.1,0,0),(0,0.1,0)], [(0,1,2)])
partial.matrix_world = Matrix.Translation((-2,1,0)) @ Matrix.Rotation(-0.3,4,'Y')
partial.data.vertices[0].co = partial.matrix_world.inverted() @ (world_anchor+world_normal*0.02)
partial.vertex_groups.new(name='A').add([0,1,2], 1, 'REPLACE')
partial.vertex_groups.new(name='FitRegion').add([0], 0.5, 'REPLACE')
curved_fit, _ = fit(partial, curved, output_name='Partial fitted')
expected = world_anchor+world_normal*0.013
assert (curved_fit.matrix_world @ curved_fit.data.vertices[0].co-expected).length < 1e-6
assert abs(skin(curved_fit,rig)[0]['B']-0.5) < 1e-5
# Scene staging must not change clearance in the exported model's meter units.
stage = Matrix.Translation((3,-2,1)) @ Matrix.Diagonal((2,2,2,1))
rig.matrix_world = stage
body.matrix_world = stage
garment.matrix_world = stage
staged, _ = fit(garment,body,output_name='Staged fit')
assert abs(staged.data.vertices[0].co.z-0.006) < 1e-6
print('FIT_PASS', report)
