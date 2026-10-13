"""Run: blender --background --python tools/rig-reference/test_check_poses.py"""
import copy
import sys
from pathlib import Path
import bpy
from mathutils import Matrix, Vector
sys.path.insert(0,str(Path(__file__).resolve().parent))
from import_reference import import_capture
from check_poses import check_poses, diagnostics


def flat(m):
    return [x for row in m for x in row]


def fixture(angle):
    pose = Matrix.Rotation(angle,4,'Z')
    vertices = [[-1,-1,0],[1,-1,0],[1,1,0],[-1,1,0]]
    return {'schemaVersion':1,'coordinateSystem':'unity-left-handed-y-up','matrixLayout':'row-major','rootName':'Rig',
            'nodes':[{'name':'Root','path':'Root','parent':-1,'restMatrix':flat(Matrix.Identity(4)),'poseMatrix':flat(pose)}],
            'meshes':[{'name':'Body','rendererToRoot':flat(Matrix.Identity(4)), 'vertices':vertices,
                       'normals':[[0,0,1]]*4,'uv':[],'triangles':[0,1,2,0,2,3],'bones':[0],
                       'bindPoses':[flat(Matrix.Identity(4))],
                       'weights':[{'indices':[0,0,0,0],'values':[1,0,0,0]} for _ in vertices],
                       'bakedVertices':[list(pose @ Vector(v)) for v in vertices]}]}


data = fixture(0)
import_capture(data,1e-5)
body = bpy.data.objects['Body']
cloth = body.copy()
cloth.data = body.data.copy()
cloth.name = 'Garment'
bpy.context.collection.objects.link(cloth)
for v in cloth.data.vertices:
    v.co.y -= 0.01
rig = body.modifiers[0].object
before = rig.pose.bones[0].matrix_basis.copy()
report = check_poses(cloth,body,[('rest',data),('attack',fixture(0.8))])
assert len(report['poses']) == 2
assert all(p['intersectionPairs'] == 0 and p['nearSurfaceGarmentVertices'] == 0 for p in report['poses'])
assert max(abs(x) for row in (rig.pose.bones[0].matrix_basis-before) for x in row) < 1e-6
rounded = fixture(0.8)
rounded['nodes'][0]['restMatrix'][3] += 1e-7
rounded['meshes'][0]['rendererToRoot'][3] += 1e-7
check_poses(cloth,body,[('rest',data),('harmless float cancellation',rounded)])
changed = fixture(0.8)
changed['nodes'][0]['restMatrix'][3] += 0.01
try:
    check_poses(cloth,body,[('rest',data),('different rest',changed)])
except ValueError:
    pass
else:
    raise AssertionError('Meaningful rest change accepted')
body.modifiers[0].use_deform_preserve_volume = True
try:
    check_poses(cloth,body,[('nonlinear',data)])
except ValueError:
    pass
else:
    raise AssertionError('Nonlinear skinning accepted')
body.modifiers[0].use_deform_preserve_volume = False
bad = copy.deepcopy(data)
bad['meshes'][0]['triangles'] = [0,2,1,0,2,3]
try:
    check_poses(cloth,body,[('bad',bad)])
except ValueError:
    pass
else:
    raise AssertionError('Topology mismatch accepted')
bad = fixture(0.8)
bad['meshes'][0]['bakedVertices'][0][0] += 0.1
try:
    check_poses(cloth,body,[('bad',bad)])
except ValueError:
    pass
else:
    raise AssertionError('Incorrect pose accepted')
bad = copy.deepcopy(data)
bad['nodes'][0]['path'] = 'DifferentRoot'
try:
    check_poses(cloth,body,[('bad',bad)])
except ValueError:
    pass
else:
    raise AssertionError('Different skeleton accepted')
assert max(abs(x) for row in (rig.pose.bones[0].matrix_basis-before) for x in row) < 1e-6
body.vertex_groups[0].add([0],0.5,'REPLACE')
try:
    check_poses(cloth,body,[('bad skin',data)])
except ValueError:
    pass
else:
    raise AssertionError('Changed body skin accepted')
body.vertex_groups[0].add([0],1,'REPLACE')
for unsupported in ('mask', 'modifier', 'shape'):
    if unsupported == 'mask':
        cloth.modifiers[0].vertex_group = cloth.vertex_groups[0].name
    elif unsupported == 'modifier':
        extra = cloth.modifiers.new('Unsupported cloth motion', 'CLOTH')
    else:
        cloth.shape_key_add(name='Basis')
    try:
        check_poses(cloth,body,[('unsupported export state',data)])
    except ValueError:
        pass
    else:
        raise AssertionError('Unsupported export state accepted: '+unsupported)
    if unsupported == 'mask':
        cloth.modifiers[0].vertex_group = ''
    elif unsupported == 'modifier':
        cloth.modifiers.remove(extra)
    else:
        cloth.shape_key_clear()
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode='EDIT')
rig.data.edit_bones[0].head.x += 0.02
rig.data.edit_bones[0].tail.x += 0.02
bpy.ops.object.mode_set(mode='OBJECT')
try:
    check_poses(cloth,body,[('edited rest bone',data)])
except ValueError:
    pass
else:
    raise AssertionError('Edited imported rest bone accepted')
# Non-coplanar triangles crossing the body must produce an overlap diagnostic.
report = diagnostics([Vector(v) for v in [(-1,-1,0),(1,-1,0),(0,1,0)]],[(0,1,2)],
                     [Vector(v) for v in [(0,0,-1),(0,0,1),(0.5,0,0)]],[(0,1,2)],0.002)
assert report['intersectionPairs'] > 0
assert report['bodyNonManifoldEdges'] == 3
assert report['inwardFacingGarmentVertices'] > 0
print('POSE_CHECKS_PASS: two poses, state restoration, topology rejection, pose rejection, intersection/open-surface diagnostics')
