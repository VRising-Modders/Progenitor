"""Original synthetic two-bone fixture. Run with Blender, never the game."""
import json
import math
import sys
from pathlib import Path
from mathutils import Matrix, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from import_reference import import_capture

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

data = fixture()
result = import_capture(data, 0.000001)
print('SYNTHETIC_PASS',json.dumps(result))
# A transposed pose must not silently pass verification.
bad = json.loads(json.dumps(data))
bad['nodes'][0]['poseMatrix'] = flat(Matrix([bad['nodes'][0]['poseMatrix'][i:i+4] for i in range(0,16,4)]).transposed())
try:
    import_capture(bad,0.001)
except ValueError:
    print('TRANSPOSE_REJECTED')
else:
    raise AssertionError('Transposed pose was accepted')
if '--' in sys.argv:
    output = Path(sys.argv[sys.argv.index('--')+1])
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(data,indent=2),encoding='utf-8')
