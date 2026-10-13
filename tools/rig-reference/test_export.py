import sys,json
from pathlib import Path
import bpy
from mathutils import Matrix,Vector
sys.path.insert(0,str(Path(__file__).resolve().parent))
from import_reference import import_capture,C,matrix
from export_garment import export_garment
def flat(m):
    return [v for row in m for v in row]

def fixture():
    root = Matrix.Translation((0.3,0.2,-0.4)) @ Matrix.Rotation(0.23,4,'Y')
    upper = root @ Matrix.Translation((0.1,0.8,0.05))
    lower = upper @ Matrix.Translation((0,0.5,0))
    upper_pose = upper @ Matrix.Rotation(-0.41,4,'Z')
    lower_pose = upper_pose @ Matrix.Translation((0,0.5,0)) @ Matrix.Rotation(1.1,4,'X') @ Matrix.Diagonal((1.03,0.97,1.02,1))
    # Intentionally child-first; hierarchy ordering must not depend on array order.
    rest = [lower,root,upper]
    pose = [lower_pose,root,upper_pose]
    renderer = Matrix.Translation((0.12,-0.08,0.21)) @ Matrix.Rotation(-0.3,4,'Y')
    vertices = [[0.2,1.2,-0.3],[0.4,1.2,-0.3],[0.2,1.7,-0.2],[0.4,1.7,-0.2]]
    weights = [{'indices':[0,1,0,0],'values':[w,1-w,0,0]} for w in (0,0.2,0.7,1)]
    bones = [0,2]
    binds = [rest[i].inverted() @ renderer for i in bones]
    baked = []
    for vertex,weight in zip(vertices,weights):
        v = Vector((*vertex,1))
        result = Vector((0,0,0,0))
        for slot,w in zip(weight['indices'],weight['values']):
            result += w * (renderer.inverted() @ pose[bones[slot]] @ binds[slot] @ v)
        baked.append(list(result[:3]))
    return {'schemaVersion':1,'coordinateSystem':'unity-left-handed-y-up','matrixLayout':'row-major','rootName':'Synthetic rig',
            'nodes':[{'name':n,'path':p,'parent':parent,'restMatrix':flat(r),'poseMatrix':flat(q)}
                     for n,p,parent,r,q in zip(['Elbow','Root','Shoulder'],['Root/Shoulder/Elbow','Root','Root/Shoulder'],[2,-1,1],rest,pose)],
            'meshes':[{'name':'Original synthetic arm strip','rendererToRoot':flat(renderer),'vertices':vertices,
                       'normals':[[0,0,1]]*4,'uv':[[0,0],[1,0],[0,1],[1,1]],'triangles':[0,1,2,2,1,3],
                       'bones':bones,'bindPoses':[flat(m) for m in binds],'weights':weights,'bakedVertices':baked}]}

data=fixture()
import_capture(data,.001)
body=next(o for o in bpy.data.objects if o.type=='MESH')
try: export_garment(body,data)
except ValueError: pass
else: raise AssertionError('reference export accepted')
# Original synthetic fixture only; duplicate to exercise export transform and split seams.
obj=body.copy();obj.data=body.data.copy();bpy.context.collection.objects.link(obj);obj['referenceOnly']=False
mat=bpy.data.materials.new('cloth');mat.diffuse_color=(.03,.2,.7,1);obj.data.materials.append(mat)
transform=Matrix.Translation((.2,-.1,.3)) @ Matrix.Rotation(.6,4,'Z')
obj.matrix_world=transform
for vertex in obj.data.vertices: vertex.co=transform.inverted() @ vertex.co
obj.data.uv_layers.active.data[3].uv=(.123,.456)
result=export_garment(obj,data)
assert len(result['vertices'])==6 and abs(result['uv'][3][0]-.123)<1e-6
bpy.context.view_layer.update()
evaluated=obj.evaluated_get(bpy.context.evaluated_depsgraph_get()); mesh=evaluated.to_mesh()
errors=[]
for i,loop in enumerate(obj.data.loops):
    p=Vector((*result['vertices'][i],1));actual=Vector((0,0,0,0))
    for slot,w in zip(result['weights'][i]['indices'],result['weights'][i]['values']):
        node=next(n for n in data['nodes'] if n['path']==result['bones'][slot]['path'])
        actual+=w*(matrix(node['poseMatrix']) @ matrix(node['restMatrix']).inverted() @ p)
    expected=C.inverted() @ obj.matrix_world @ mesh.vertices[loop.vertex_index].co
    errors.append((Vector(actual[:3])-expected).length)
evaluated.to_mesh_clear()
assert max(errors)<.000001,errors
print('EXPORT_PASS max_m=',max(errors),'vertices=',len(result['vertices']))

# Palette is explicit for used surfaces only and copies shader RGBA without gamma conversion.
assert set(result) == {'schemaVersion','coordinateSystem','matrixLayout','vertices','normals',
                       'uv','bones','weights','cloth','gold','void','surfaceColors'}
assert result['surfaceColors'] == {'cloth':list(mat.diffuse_color)}
mat.diffuse_color=(.6,.12,.04,.75)
recolored=export_garment(obj,data)
assert recolored['surfaceColors'] == {'cloth':list(mat.diffuse_color)}
assert recolored['surfaceColors'] != result['surfaceColors']
assert {k:v for k,v in recolored.items() if k!='surfaceColors'} == {
    k:v for k,v in result.items() if k!='surfaceColors'}, 'Color edit changed mesh/skin data'
# Same surface can occupy multiple slots only if their colors agree.
second=bpy.data.materials.new('second cloth');second['wearableSurface']='cloth'
second.diffuse_color=mat.diffuse_color;obj.data.materials.append(second)
obj.data.polygons[1].material_index=1
assert export_garment(obj,data)['surfaceColors'] == recolored['surfaceColors']
second.diffuse_color=(.1,.3,.5,1)
try: export_garment(obj,data)
except ValueError as error: assert 'Conflicting diffuse_color' in str(error)
else: raise AssertionError('Conflicting colors for the same surface accepted')
# An explicit second surface retains its independent RGBA and triangle assignment.
second['wearableSurface']='gold'
two=export_garment(obj,data)
assert two['surfaceColors']=={'cloth':list(mat.diffuse_color),'gold':list(second.diffuse_color)}
assert len(two['cloth'])==3 and len(two['gold'])==3 and not two['void']
assert json.loads(json.dumps(two))['surfaceColors']==two['surfaceColors']
# Palette mappings are opt-in per material; fixed RGBA remains in the export.
mat['wearablePalette']='primary';second['wearablePalette']='metal'
mapped=export_garment(obj,data)
assert mapped['surfacePalette']=={'cloth':'primary','gold':'metal'}
assert mapped['surfaceColors']==two['surfaceColors']
assert {k:v for k,v in mapped.items() if k!='surfacePalette'}==two
obj['wearablePaletteSource'] = 'Chest'
inherited = export_garment(obj, data)
assert inherited['paletteSource'] == 'Chest'
assert {k:v for k,v in inherited.items() if k!='paletteSource'} == mapped
obj['wearablePaletteMode'] = 'authored'
fixed = export_garment(obj, data)
assert fixed['paletteMode'] == 'authored' and fixed['surfacePalette'] == mapped['surfacePalette']
assert {k:v for k,v in fixed.items() if k!='paletteMode'} == inherited
for invalid in ('Native', 'reset', '1', 1):
    obj['wearablePaletteMode'] = invalid
    try: export_garment(obj, data)
    except ValueError as error: assert 'wearablePaletteMode' in str(error)
    else: raise AssertionError('Invalid palette mode accepted: '+str(invalid))
obj['wearablePaletteMode'] = 'authored'
for invalid in ('chest', 'Unknown', '1', 1):
    obj['wearablePaletteSource'] = invalid
    try: export_garment(obj, data)
    except ValueError as error: assert 'wearablePaletteSource' in str(error)
    else: raise AssertionError('Invalid palette source accepted: '+str(invalid))
obj['wearablePaletteSource'] = 'Chest'
second['wearablePalette']='invalid'
try: export_garment(obj,data)
except ValueError as error: assert 'wearablePalette' in str(error)
else: raise AssertionError('Unknown palette channel accepted')
second['wearablePalette']='metal'
second['wearableSurface']='cloth';second.diffuse_color=mat.diffuse_color
try: export_garment(obj,data)
except ValueError as error: assert 'Conflicting wearablePalette' in str(error)
else: raise AssertionError('Conflicting palette channels accepted')
del second['wearablePalette']
try: export_garment(obj,data)
except ValueError as error: assert 'Conflicting wearablePalette' in str(error)
else: raise AssertionError('Mapped and fixed materials for one surface accepted')
second['wearableSurface']='unknown'
try: export_garment(obj,data)
except ValueError as error: assert 'Each material must' in str(error)
else: raise AssertionError('Unknown wearable surface accepted')
print('SURFACE_COLORS_PASS linear_rgba and independent_geometry')
