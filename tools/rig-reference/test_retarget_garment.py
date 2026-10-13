"""Offline synthetic checks for authored rest-space retargeting; run in Blender."""
import copy
import importlib.util
import tempfile
from pathlib import Path


spec = importlib.util.spec_from_file_location('retarget_garment', Path(__file__).with_name('retarget_garment.py'))
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


def matrix(x=0, y=0, z=0):
    return [1,0,0,x, 0,1,0,y, 0,0,1,z, 0,0,0,1]


def capture(sex, pelvis_x, knee_x):
    prefix = sex + '[0]/' + sex + '_rig[0]/Root_JNT[0]'
    return {'schemaVersion':1, 'coordinateSystem':'unity-left-handed-y-up',
            'matrixLayout':'row-major', 'rootName':sex,
            'nodes':[
                {'name':'Root_JNT','path':prefix,'parent':-1,'restMatrix':matrix(), 'poseMatrix':matrix()},
                {'name':'Pelvis_JNT','path':prefix+'/Pelvis_JNT[0]','parent':0,
                 'restMatrix':matrix(pelvis_x), 'poseMatrix':matrix(pelvis_x)},
                {'name':'Left_Knee_JNT','path':prefix+'/Pelvis_JNT[0]/Left_Knee_JNT[0]','parent':1,
                 'restMatrix':matrix(knee_x,-1), 'poseMatrix':matrix(knee_x,-1)}]}


source = capture('Female', 0, -.2)
target = capture('Male', .1, -.4)
garment = {'schemaVersion':1, 'coordinateSystem':'unity-left-handed-y-up',
           'matrixLayout':'row-major',
           'vertices':[[.2, .2, 0], [.5,-1.2,0], [-.5,-1.2,0]],
           'normals':[[0,0,1]]*3, 'uv':[[0,0]]*3,
           'bones':[{'name':n['name'],'path':n['path'],'restMatrix':n['restMatrix']}
                    for n in source['nodes'][1:]],
           'weights':[{'indices':[0,1,0,0], 'values':[.5,.5,0,0]}]*3,
           'cloth':[0,1,2], 'gold':[], 'void':[]}


def fails(g, s, t, fragment):
    try:
        tool.retarget(g, s, t)
    except ValueError as error:
        assert fragment in str(error), str(error)
    else:
        raise AssertionError('Expected failure: '+fragment)


result, report = tool.retarget(garment, source, target, preserve_below_y=-.5)
assert report['preservedLooseVertices'] == 2
assert abs(result['vertices'][0][0] - .15) < 1e-6  # weighted .1 and -.2 deltas
assert abs(result['vertices'][1][0] - .6) < 1e-6
assert abs((result['vertices'][1][0]-result['vertices'][2][0]) - 1) < 1e-6
assert result['weights'] == garment['weights']
assert result['cloth'] == garment['cloth'] and result['uv'] == garment['uv']
assert result['bones'][0]['path'] == target['nodes'][1]['path']
assert result['bones'][1]['restMatrix'] == target['nodes'][2]['restMatrix']
assert all(abs(n[2]-1) < 1e-6 for n in result['normals'])
mapped, mapped_report = tool.retarget(garment, source, target,
                                      bone_map={'Left_Knee_JNT':'Pelvis_JNT'})
assert mapped_report['targetBones'] == 1
assert mapped['bones'][0]['name'] == 'Pelvis_JNT'
assert mapped['weights'][0] == {'indices':[0,0,0,0], 'values':[1.,0.,0.,0.]}
assert abs(mapped['vertices'][0][0] - .4) < 1e-6  # both source deltas remain distinct
try:
    tool.retarget(garment, source, target, bone_map={'Missing_JNT':'Pelvis_JNT'})
except ValueError as error:
    assert 'Bone map keys' in str(error)
else:
    raise AssertionError('Unknown source bone map key was accepted')

changed = copy.deepcopy(garment)
changed['bones'][0]['restMatrix'][3] = 3
fails(changed, source, target, 'differs from source capture')
changed = copy.deepcopy(target)
changed['nodes'][2]['name'] = 'OtherKnee'
fails(garment, source, changed, 'Missing or ambiguous target bone')
changed = copy.deepcopy(target)
changed['nodes'].append(dict(changed['nodes'][2], path=changed['nodes'][1]['path']+'/Left_Knee_JNT[1]'))
fails(garment, source, changed, 'Missing or ambiguous target bone')
changed = copy.deepcopy(target)
changed['nodes'][2]['parent'] = 0
fails(garment, source, changed, 'path disagrees with parent index')
changed = copy.deepcopy(source)
changed['nodes'][2]['parent'] = 0  # exact path still matches; parent metadata must fail
fails(garment, source, changed, 'path disagrees with parent index')
changed = copy.deepcopy(garment)
changed['weights'][0]['indices'][0] = 99
fails(changed, source, target, 'Invalid bone influence')
changed = copy.deepcopy(garment)
changed['uv'].pop()
fails(changed, source, target, 'garment UVs')
changed = copy.deepcopy(garment)
changed['cloth'][0] = 99
fails(changed, source, target, 'triangle indices')
changed = copy.deepcopy(target)
changed['nodes'][2]['restMatrix'] = matrix()
changed['nodes'][2]['restMatrix'][0] = 0
fails(garment, source, changed, 'singular')

# A tiny original garment must survive Blender scene creation and the existing
# exporter with target rest metadata, UVs, surfaces, and palette settings intact.
import bpy
export_spec = importlib.util.spec_from_file_location('export_garment', Path(__file__).with_name('export_garment.py'))
exporter = importlib.util.module_from_spec(export_spec)
export_spec.loader.exec_module(exporter)
scene_reference = copy.deepcopy(target)
scene_reference['meshes'] = [{
    'name':'Synthetic target body', 'rendererToRoot':matrix(),
    'vertices':[[0,0,0],[1,0,0],[0,1,0]], 'normals':[[0,0,1]]*3,
    'uv':[[0,0],[1,0],[0,1]], 'triangles':[0,1,2], 'bones':[1],
    'bindPoses':[matrix(-.1)],
    'weights':[{'indices':[0,0,0,0],'values':[1,0,0,0]}]*3,
    'bakedVertices':[[0,0,0],[1,0,0],[0,1,0]]}]
original = copy.deepcopy(garment)
original['surfaceColors'] = {'cloth':[.1,.2,.3,1]}
original['surfacePalette'] = {'cloth':'primary'}
original['paletteSource'] = 'Chest'
original['paletteMode'] = 'native'
result, _ = tool.retarget(original, source, scene_reference)
with tempfile.TemporaryDirectory() as tmp:
    tool.build_scene(result, scene_reference, 'Original synthetic garment', Path(tmp)/'scene.blend')
    exported = exporter.export_garment(bpy.data.objects['Original synthetic garment'], scene_reference)
    for key in ('bones','surfacePalette','paletteSource','paletteMode'):
        assert exported[key] == result[key], key
    assert all(abs(a-b) < 1e-6 for a,b in zip(exported['surfaceColors']['cloth'], result['surfaceColors']['cloth']))
    assert all(abs(a-b) < 1e-6 for ea,ra in zip(exported['uv'],result['uv']) for a,b in zip(ea,ra))
    assert len(exported['vertices']) == len(result['vertices'])
print('RETARGET_PASS')
