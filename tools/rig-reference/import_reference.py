"""Import a Progenitor v1 rig capture; fail unless Blender reproduces its baked pose."""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

# Unity (x,y,z) -> Blender (x,-z,y). This proper rotation preserves winding.
C = Matrix(((1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))

def matrix(values):
    if len(values) != 16 or not all(math.isfinite(x) for x in values):
        raise ValueError('Expected 16 finite row-major matrix elements')
    return Matrix([values[i:i+4] for i in range(0,16,4)])

def import_surface_metadata(obj, source):
    """Preserve triangle groups; known framework materials get only solid preview colors."""
    mesh = obj.data
    groups = source.get('submeshes')
    if groups is not None:
        counts = [g['indexCount'] for g in groups]
        if any(type(n) is not int or n < 0 or n % 3 for n in counts) or sum(counts) != len(source['triangles']):
            raise ValueError('Submesh index counts must exactly partition the triangles')
        colors = {'cloth':(0,84/255,182/255,1), 'gold':(0.8,0.47,0.06,1), 'void':(0,0,0,1)}
        first = 0
        for slot, group in enumerate(groups):
            name = group['materialName']
            if not isinstance(name, str):
                raise ValueError('Submesh material name must be a string')
            color = group.get('color')
            if color is not None and (not isinstance(color, list) or len(color) != 4
                    or any(type(v) not in (int, float) or not math.isfinite(v) or not 0 <= v <= 1 for v in color)):
                raise ValueError('Captured color must contain four finite channels in [0,1]')
            material = bpy.data.materials.new('Reference: ' + (name or 'unassigned'))
            material['sourceMaterialName'] = name
            surface = next((s for s in colors if name == 'progenitor-wear-' + s), None)
            if surface is not None:
                material['wearableSurface'] = surface
                material.diffuse_color = color if color is not None else colors[surface]
            mesh.materials.append(material)
            end = first + group['indexCount']//3
            for polygon in mesh.polygons[first:end]:
                polygon.material_index = slot
            first = end
    if len(source['uv']) not in (0, len(mesh.vertices)):
        raise ValueError('UVs must be absent or complete')
    uv = mesh.uv_layers.new(name='UVMap')
    obj['sourceUvMissing'] = len(source['uv']) == 0
    for loop in mesh.loops:
        uv.data[loop.index].uv = source['uv'][loop.vertex_index] if source['uv'] else (0,0)

def import_capture(data, tolerance):
    if (data['schemaVersion'], data['coordinateSystem'], data['matrixLayout']) != (1, 'unity-left-handed-y-up', 'row-major'):
        raise ValueError('Unsupported rig reference schema or coordinate convention')
    nodes = data['nodes']
    if not nodes or not data['meshes']:
        raise ValueError('Capture must contain a skeleton and meshes')
    order, pending = [], set(range(len(nodes)))
    while pending:
        ready = sorted(i for i in pending if nodes[i]['parent'] == -1 or nodes[i]['parent'] in order)
        if not ready:
            raise ValueError('Invalid or cyclic node hierarchy')
        order.extend(ready)
        pending.difference_update(ready)
    rests = [C @ matrix(n['restMatrix'] or n['poseMatrix']) @ C.inverted() for n in nodes]
    poses = [C @ matrix(n['poseMatrix']) @ C.inverted() for n in nodes]
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    arm = bpy.data.armatures.new('Reference skeleton')
    rig = bpy.data.objects.new(data['rootName'], arm)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')
    names = {}
    for i in order:
        bone = arm.edit_bones.new(f'{i}:{nodes[i]["name"]}')
        bone.head, bone.tail = (0,0,0), (0,0.06,0)
        bone.matrix = rests[i]
        bone.use_connect = False
        if nodes[i]['parent'] != -1:
            bone.parent = arm.edit_bones[names[nodes[i]['parent']]]
        names[i] = bone.name
    bpy.ops.object.mode_set(mode='OBJECT')
    for i in order:
        bone = arm.bones[names[i]]
        bone['sourcePath'] = nodes[i]['path']
        bone['restFromPose'] = nodes[i]['restMatrix'] is None
        bone['sourceRestMatrix'] = nodes[i]['restMatrix'] or nodes[i]['poseMatrix']
        bone['importedRestMatrix'] = [v for row in bone.matrix_local for v in row]
        # Edit bones cannot encode arbitrary rest scale. Preserve the exact delta
        # against the bone matrix Blender actually created instead.
        rig.pose.bones[names[i]].matrix = poses[i] @ rests[i].inverted() @ bone.matrix_local
        bpy.context.view_layer.update()
    results = []
    for source in data['meshes']:
        renderer = matrix(source['rendererToRoot'])
        transform = C @ renderer
        vertices = [transform @ Vector(v) for v in source['vertices']]
        if not vertices or any(not math.isfinite(x) for v in vertices for x in v):
            raise ValueError('Mesh must contain finite vertices')
        triangles = source['triangles']
        if len(triangles) % 3 or any(i < 0 or i >= len(vertices) for i in triangles):
            raise ValueError('Invalid triangle indices')
        if len(source['weights']) != len(vertices) or len(source['bakedVertices']) != len(vertices):
            raise ValueError('Missing weights or baked verification vertices')
        if any(len(v) != 3 or not all(math.isfinite(x) for x in v) for v in source['bakedVertices']):
            raise ValueError('Invalid baked verification vertices')
        mesh = bpy.data.meshes.new(source['name'])
        mesh.from_pydata(vertices, [], [triangles[i:i+3] for i in range(0,len(triangles),3)])
        mesh.update()
        obj = bpy.data.objects.new(source['name'], mesh)
        bpy.context.collection.objects.link(obj)
        obj['referenceOnly'] = True
        import_surface_metadata(obj, source)
        if len(source['normals']) == len(vertices):
            normal_transform = transform.to_3x3().inverted().transposed()
            mesh.normals_split_custom_set_from_vertices([(normal_transform @ Vector(n)).normalized() for n in source['normals']])
        if len(source['bindPoses']) != len(source['bones']) or len(set(source['bones'])) != len(source['bones']):
            raise ValueError('Invalid bone palette')
        groups = [obj.vertex_groups.new(name=names[i]) for i in source['bones']]
        for slot, node in enumerate(source['bones']):
            if nodes[node]['restMatrix'] is None:
                raise ValueError('Weighted bones require an explicit bind rest matrix')
            expected = rests[node].inverted() @ transform
            actual = C @ matrix(source['bindPoses'][slot])
            if max(abs(expected[r][c]-actual[r][c]) for r in range(4) for c in range(4)) > 1e-4:
                raise ValueError(f'{source["name"]}: inconsistent bind pose for bone {node}')
        for index, weight in enumerate(source['weights']):
            if len(weight['indices']) != 4 or len(weight['values']) != 4:
                raise ValueError('Expected four bone indices and weights')
            if abs(sum(weight['values'])-1) > 1e-4:
                raise ValueError('Weights must sum to one')
            for slot, value in zip(weight['indices'], weight['values']):
                if not math.isfinite(value) or value < 0:
                    raise ValueError('Invalid weight')
                if value:
                    if slot < 0 or slot >= len(groups):
                        raise ValueError('Weight references missing bone')
                    groups[slot].add([index], value, 'ADD')
        modifier = obj.modifiers.new('Captured linear skinning', 'ARMATURE')
        modifier.object = rig
        modifier.use_deform_preserve_volume = False
        bpy.context.view_layer.update()
        evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
        baked = evaluated.to_mesh()
        try:
            if len(baked.vertices) != len(vertices):
                raise ValueError('Evaluation changed vertex count')
            errors = [(v.co - transform @ Vector(target)).length for v,target in zip(baked.vertices,source['bakedVertices'])]
        finally:
            evaluated.to_mesh_clear()
        result = {'mesh':source['name'], 'vertices':len(vertices), 'triangles':len(triangles)//3,
                  'maxErrorMeters':max(errors,default=0), 'rmsErrorMeters':math.sqrt(sum(e*e for e in errors)/max(1,len(errors)))}
        results.append(result)
        if not all(math.isfinite(e) for e in errors) or result['maxErrorMeters'] > tolerance:
            raise ValueError(f'Pose mismatch: {result}')
    return results

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('input')
    parser.add_argument('--out', required=True)
    parser.add_argument('--tolerance-mm', type=float, default=1)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    if not math.isfinite(args.tolerance_mm) or args.tolerance_mm <= 0:
        raise ValueError('Tolerance must be finite and positive')
    results = import_capture(json.loads(Path(args.input).read_text(encoding='utf-8-sig')), args.tolerance_mm/1000)
    output = Path(args.out).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(output))
    output.with_suffix('.verification.json').write_text(json.dumps({'toleranceMm':args.tolerance_mm,'meshes':results},indent=2), encoding='utf-8')
    print(json.dumps(results))

if __name__ == '__main__':
    main()
