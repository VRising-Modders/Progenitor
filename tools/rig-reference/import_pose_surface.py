"""Import a posed-surface capture as static, export-forbidden Blender reference meshes.

Run with Blender in background mode and pass script arguments after ``--``. The
optional head-rest alignment is a rigid approximation for comparing a captured
pose with an authored rig; it cannot recover bind geometry or skin weights.
"""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


# Unity (x, y, z) -> Blender (x, -z, y), a proper rotation preserving winding.
C = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
ROOT_KEYS = {'schemaVersion', 'kind', 'referenceOnly', 'coordinateSystem',
             'matrixLayout', 'rootName', 'capturedUtc', 'coreVersion', 'nodes', 'meshes'}
NODE_KEYS = {'name', 'path', 'parent', 'poseMatrix'}
MESH_KEYS = {'name', 'rendererNode', 'rootBone', 'bones', 'posedVerticesRoot', 'triangles'}


def exact_keys(value, expected, label):
    if not isinstance(value, dict) or set(value) != expected:
        raise ValueError(f'{label} must have exactly {sorted(expected)}')


def finite_number(value):
    return type(value) in (int, float) and math.isfinite(value)


def matrix(values, label):
    if not isinstance(values, list) or len(values) != 16 or not all(map(finite_number, values)):
        raise ValueError(f'{label} must have 16 finite row-major numbers')
    result = Matrix([values[i:i + 4] for i in range(0, 16, 4)])
    if max(abs(result[3][i] - expected) for i, expected in enumerate((0, 0, 0, 1))) > 1e-5:
        raise ValueError(f'{label} must be affine')
    if abs(result.to_3x3().determinant()) < 1e-8:
        raise ValueError(f'{label} is singular')
    return result


def validate_capture(data):
    exact_keys(data, ROOT_KEYS, 'Pose capture')
    if (data['schemaVersion'], data['kind'], data['referenceOnly'],
        data['coordinateSystem'], data['matrixLayout']) != (
            1, 'posed-surface', True, 'unity-left-handed-y-up', 'row-major'):
        raise ValueError('Unsupported posed-surface schema or coordinate convention')
    if not isinstance(data['rootName'], str) or not data['rootName']:
        raise ValueError('Missing model root name')
    if not isinstance(data['capturedUtc'], str) or not data['capturedUtc']:
        raise ValueError('Missing capture time')
    if data['coreVersion'] is not None and not isinstance(data['coreVersion'], str):
        raise ValueError('Invalid core version')
    nodes, meshes = data['nodes'], data['meshes']
    if not isinstance(nodes, list) or not nodes or not isinstance(meshes, list) or not 1 <= len(meshes) <= 16:
        raise ValueError('Capture requires nodes and 1..16 meshes')
    root_count = 0
    for i, node in enumerate(nodes):
        exact_keys(node, NODE_KEYS, f'Node {i}')
        if not isinstance(node['name'], str) or not node['name'] or not isinstance(node['path'], str):
            raise ValueError(f'Node {i} has invalid name or path')
        parent = node['parent']
        if type(parent) is not int or parent < -1 or parent >= i:
            raise ValueError(f'Node {i} has invalid parent')
        if parent == -1:
            root_count += 1
            if i != 0 or node['name'] != data['rootName']:
                raise ValueError('The first node must be the model root')
        elif not node['path'].startswith(nodes[parent]['path'] + '/'):
            raise ValueError(f'Node {i} path is outside its parent')
        matrix(node['poseMatrix'], f'Node {i} pose')
    if root_count != 1:
        raise ValueError('Capture must have exactly one model root')
    total_vertices = 0
    total_indices = 0
    for i, mesh in enumerate(meshes):
        exact_keys(mesh, MESH_KEYS, f'Mesh {i}')
        if not isinstance(mesh['name'], str) or not mesh['name']:
            raise ValueError(f'Mesh {i} has no name')
        renderer = mesh['rendererNode']
        root_bone = mesh['rootBone']
        bones = mesh['bones']
        if type(renderer) is not int or not 0 <= renderer < len(nodes):
            raise ValueError(f'Mesh {i} has invalid renderer node')
        if type(root_bone) is not int or not -1 <= root_bone < len(nodes):
            raise ValueError(f'Mesh {i} has invalid root bone node')
        if not isinstance(bones, list) or any(type(b) is not int or not 0 <= b < len(nodes) for b in bones):
            raise ValueError(f'Mesh {i} has invalid bone node indices')
        vertices, triangles = mesh['posedVerticesRoot'], mesh['triangles']
        if not isinstance(vertices, list) or not vertices or total_vertices + len(vertices) > 2000000:
            raise ValueError(f'Mesh {i} exceeds posed vertex limits')
        if any(not isinstance(v, list) or len(v) != 3 or not all(map(finite_number, v)) for v in vertices):
            raise ValueError(f'Mesh {i} has invalid posed vertices')
        if (not isinstance(triangles, list) or not triangles or len(triangles) % 3
                or total_indices + len(triangles) > 6000000
                or any(type(t) is not int or not 0 <= t < len(vertices)
                                                for t in triangles)):
            raise ValueError(f'Mesh {i} has invalid triangles')
        total_vertices += len(vertices)
        total_indices += len(triangles)


def rigid(matrix_value):
    """Discard scale and shear; the capture cannot establish a bind transform."""
    result = matrix_value.to_quaternion().normalized().to_matrix().to_4x4()
    result.translation = matrix_value.translation
    return result


def head_alignment(capture, reference):
    if reference.get('kind') == 'posed-surface' or not isinstance(reference.get('meshes'), list):
        raise ValueError('Head-rest alignment requires a full rig reference, not a posed surface')
    if (reference.get('schemaVersion'), reference.get('coordinateSystem'),
        reference.get('matrixLayout')) != (1, 'unity-left-handed-y-up', 'row-major'):
        raise ValueError('Unsupported target rig reference schema')
    target_nodes = reference.get('nodes')
    if not isinstance(target_nodes, list):
        raise ValueError('Target rig reference has no nodes')
    source_bones = {bone for mesh in capture['meshes']
                    for bone in [*mesh['bones'], mesh['rootBone']] if bone >= 0}
    source = [(i, n) for i, n in enumerate(capture['nodes'])
              if n['name'] == 'Head_JNT' and i in source_bones]
    target = [n for n in target_nodes if isinstance(n, dict) and n.get('name') == 'Head_JNT']
    if len(source) != 1 or len(target) != 1:
        raise ValueError('Head-rest alignment requires one captured Head_JNT bone and one target Head_JNT')
    if target[0].get('restMatrix') is None:
        raise ValueError('Target Head_JNT has no explicit rest matrix')
    source_pose = rigid(matrix(source[0][1]['poseMatrix'], 'Captured Head_JNT pose'))
    target_rest = rigid(matrix(target[0]['restMatrix'], 'Target Head_JNT rest'))
    return target_rest @ source_pose.inverted(), source[0][1]['path'], target[0].get('path', '')


def import_capture(data, reference=None):
    validate_capture(data)
    alignment = Matrix.Identity(4)
    source_path = target_path = ''
    if reference is not None:
        alignment, source_path, target_path = head_alignment(data, reference)
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene
    scene['referenceOnly'] = True
    scene['exportForbidden'] = True
    scene['poseSurfaceSchemaVersion'] = data['schemaVersion']
    scene['poseSurfaceKind'] = data['kind']
    scene['poseSurfaceCoordinateSystem'] = data['coordinateSystem']
    scene['poseSurfaceMatrixLayout'] = data['matrixLayout']
    scene['poseSurfaceRootName'] = data['rootName']
    scene['poseSurfaceCapturedUtc'] = data['capturedUtc']
    scene['poseSurfaceCoreVersion'] = data['coreVersion'] or ''
    scene['poseSurfaceAlignment'] = 'head-rest-rigid-approximation' if reference else 'posed-root'
    scene['poseSurfaceSourceHeadPath'] = source_path
    scene['poseSurfaceTargetHeadPath'] = target_path
    collection = bpy.data.collections.new('POSED SURFACE — REFERENCE ONLY')
    scene.collection.children.link(collection)
    result = []
    transform = C @ alignment
    for source in data['meshes']:
        vertices = [transform @ Vector(v) for v in source['posedVerticesRoot']]
        faces = [source['triangles'][i:i + 3] for i in range(0, len(source['triangles']), 3)]
        mesh = bpy.data.meshes.new(source['name'] + ' posed reference')
        mesh.from_pydata(vertices, [], faces)
        mesh.update()
        obj = bpy.data.objects.new(source['name'] + ' (posed reference only)', mesh)
        collection.objects.link(obj)
        obj['referenceOnly'] = True
        obj['exportForbidden'] = True
        obj['poseSurfaceKind'] = data['kind']
        obj['poseSurfaceSpace'] = scene['poseSurfaceAlignment']
        obj['sourceRendererPath'] = data['nodes'][source['rendererNode']]['path']
        obj['sourceBonePaths'] = json.dumps([data['nodes'][i]['path'] for i in source['bones']])
        obj['sourceRootBonePath'] = (data['nodes'][source['rootBone']]['path']
                                     if source['rootBone'] >= 0 else '')
        obj.display_type = 'WIRE'
        result.append({'mesh': source['name'], 'vertices': len(vertices), 'triangles': len(faces)})
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('input', help='posed-surface capture JSON')
    parser.add_argument('--out', required=True, help='new .blend output path; existing files are refused')
    parser.add_argument('--align-head-rest', action='store_true',
                        help='rigid approximation using captured Head_JNT pose and target rest')
    parser.add_argument('--reference', help='full rig-reference JSON, required for head-rest alignment')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    if args.align_head_rest != bool(args.reference):
        raise ValueError('--align-head-rest and --reference must be supplied together')
    output = Path(args.out).resolve()
    if output.suffix.lower() != '.blend' or output.exists():
        raise ValueError('Output must be a new, nonexistent .blend file')
    capture = json.loads(Path(args.input).read_text(encoding='utf-8-sig'))
    reference = json.loads(Path(args.reference).read_text(encoding='utf-8-sig')) if args.reference else None
    result = import_capture(capture, reference)
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise ValueError('Output appeared during import; refusing to overwrite it')
    bpy.ops.wm.save_as_mainfile(filepath=str(output))
    print(json.dumps({'alignment': bpy.context.scene['poseSurfaceAlignment'], 'meshes': result}))


if __name__ == '__main__':
    main()
