"""Retarget ORIGINAL authored garment JSON between explicit v1 rest captures.

Run with Blender --background --python-exit-code 1 --python this_file --
    --garment female.json --source-reference female-rig.json
    --target-reference male-rig.json --out male.json

This is a rest-space starting point, not body fitting or animation qualification.
Use --preserve-below-y with --anchor-bone Pelvis_JNT for loose skirt panels:
those vertices receive one rigid pelvis rest transform while retaining their
mapped skin weights. Fit the resulting asset deliberately against the target body.
--bone-map accepts an explicit JSON object of source bone names to target bone
names for absent joints; mapped influences that share a target are merged.
--scene-out saves an editable rest-space .blend beside reference-only body meshes.
Reflected or singular rest transforms are rejected; this tool does not change
triangle winding to accommodate a reflection.
"""
import argparse
import importlib.util
import json
import math
import sys
from pathlib import Path

from mathutils import Matrix, Vector


def _matrix(values, label):
    if not isinstance(values, list) or len(values) != 16 or any(
            type(x) not in (float, int) or not math.isfinite(x) for x in values):
        raise ValueError(label + ': expected 16 finite row-major values')
    m = Matrix([values[i:i + 4] for i in range(0, 16, 4)])
    if max(abs(m[3][i] - (1 if i == 3 else 0)) for i in range(4)) > 1e-5:
        raise ValueError(label + ': non-affine rest matrix')
    if not math.isfinite(m.determinant()) or m.determinant() <= 1e-10:
        raise ValueError(label + ': singular or reflected rest matrix')
    return m


def _schema(data, label):
    if (data.get('schemaVersion'), data.get('coordinateSystem'), data.get('matrixLayout')) != (
            1, 'unity-left-handed-y-up', 'row-major'):
        raise ValueError(label + ': unsupported schema or coordinates')


def _nodes(reference, label):
    _schema(reference, label)
    nodes = reference.get('nodes')
    if not isinstance(nodes, list) or not nodes:
        raise ValueError(label + ': missing nodes')
    by_path, by_name = {}, {}
    for i, node in enumerate(nodes):
        name, path = node.get('name'), node.get('path')
        if not name or not path or path in by_path:
            raise ValueError(label + ': missing or duplicate bone path')
        parent = node.get('parent')
        if type(parent) is not int or parent < -1 or parent >= len(nodes) or parent == i:
            raise ValueError(label + ': invalid parent index')
        if parent != -1:
            prefix = nodes[parent]['path'] + '/'
            if not path.startswith(prefix) or '/' in path[len(prefix):]:
                raise ValueError(label + ': path disagrees with parent index')
        by_path[path] = i
        by_name.setdefault(name, []).append(i)
    return nodes, by_path, by_name


def _chain(nodes, index):
    """Names from the skeleton root through a bone, with cycle detection."""
    result, seen = [], set()
    while index != -1:
        if index in seen:
            raise ValueError('Cyclic bone hierarchy')
        seen.add(index)
        result.append(nodes[index]['name'])
        index = nodes[index]['parent']
    return result[::-1]


def _match(source_nodes, source_index, target_nodes, target_paths, target_names,
           mapped_name=None):
    source = source_nodes[source_index]
    name = mapped_name or source['name']
    candidates = target_names.get(name, [])
    if len(candidates) != 1:
        raise ValueError('Missing or ambiguous target bone name: ' + name)
    if mapped_name is not None:
        # An explicit author decision may cross source/target hierarchy branches.
        return candidates[0]
    # Exact paths are authoritative when both captures use the same root.
    if source['path'] in target_paths:
        candidate = target_paths[source['path']]
        if target_nodes[candidate]['name'] != source['name']:
            raise ValueError('Path resolves to a different bone: ' + source['path'])
    else:
        candidate = candidates[0]
    # Female/male prefab and rig root names may differ. The joint ancestry must
    # still match from the first common Root_JNT/Pelvis_JNT onward.
    a, b = _chain(source_nodes, source_index), _chain(target_nodes, candidate)
    if 'Root_JNT' in a and 'Root_JNT' in b:
        a, b = a[a.index('Root_JNT'):], b[b.index('Root_JNT'):]
    elif 'Pelvis_JNT' in a and 'Pelvis_JNT' in b:
        a, b = a[a.index('Pelvis_JNT'):], b[b.index('Pelvis_JNT'):]
    if a != b:
        raise ValueError('Target bone hierarchy differs for ' + source['name'])
    return candidate


def retarget(garment, source_reference, target_reference, preserve_below_y=None,
             anchor_bone='Pelvis_JNT', bone_map=None):
    _schema(garment, 'Garment')
    source_nodes, source_paths, _ = _nodes(source_reference, 'Source capture')
    target_nodes, target_paths, target_names = _nodes(target_reference, 'Target capture')
    bones = garment.get('bones')
    if not isinstance(bones, list) or not bones:
        raise ValueError('Garment has no authored bones')
    bone_map = {} if bone_map is None else bone_map
    if not isinstance(bone_map, dict) or any(
            type(k) is not str or not k or type(v) is not str or not v for k,v in bone_map.items()):
        raise ValueError('Bone map must contain source-name to target-name strings')
    authored_names = [bone.get('name') for bone in bones]
    if len(set(authored_names)) != len(authored_names) or any(name not in authored_names for name in bone_map):
        raise ValueError('Bone map keys must name unique authored source bones')
    if preserve_below_y is not None and not math.isfinite(preserve_below_y):
        raise ValueError('Preservation threshold must be finite')
    deltas, output_bones, target_slot_by_index, source_to_output = [], [], {}, []
    for bone in bones:
        name, path = bone.get('name'), bone.get('path')
        source_index = source_paths.get(path)
        if source_index is None or source_nodes[source_index]['name'] != name:
            raise ValueError('Garment bone is absent from source capture: ' + str(path))
        source_rest = _matrix(source_nodes[source_index].get('restMatrix'), 'Source '+name)
        authored_rest = _matrix(bone.get('restMatrix'), 'Authored '+name)
        if max(abs(source_rest[r][c] - authored_rest[r][c]) for r in range(4) for c in range(4)) > 1e-4:
            raise ValueError('Authored rest differs from source capture: ' + name)
        target_index = _match(source_nodes, source_index, target_nodes, target_paths, target_names,
                              bone_map.get(name))
        target = target_nodes[target_index]
        target_rest = _matrix(target.get('restMatrix'), 'Target '+name)
        delta = target_rest @ source_rest.inverted()
        if delta.determinant() <= 1e-10:
            raise ValueError('Invalid rest-space transform for ' + name)
        deltas.append(delta)
        if target_index not in target_slot_by_index:
            target_slot_by_index[target_index] = len(output_bones)
            output_bones.append({'name': target['name'], 'path': target['path'],
                                 'restMatrix': list(target['restMatrix'])})
        source_to_output.append(target_slot_by_index[target_index])
    anchor = None
    if preserve_below_y is not None:
        source_anchor = [i for i, node in enumerate(source_nodes) if node['name'] == anchor_bone]
        if len(source_anchor) != 1:
            raise ValueError('Missing or ambiguous source anchor: ' + anchor_bone)
        target_anchor = _match(source_nodes, source_anchor[0], target_nodes, target_paths, target_names)
        anchor = (_matrix(target_nodes[target_anchor].get('restMatrix'), 'Target anchor') @
                  _matrix(source_nodes[source_anchor[0]].get('restMatrix'), 'Source anchor').inverted())
    vertices, normals, weights = garment.get('vertices'), garment.get('normals'), garment.get('weights')
    if not isinstance(vertices, list) or not vertices or not isinstance(normals, list) or not isinstance(weights, list) or len(vertices) != len(normals) or len(vertices) != len(weights):
        raise ValueError('Incomplete garment vertices, normals or weights')
    if len(vertices) > 60000:
        raise ValueError('Garment exceeds runtime vertex limit')
    uv = garment.get('uv')
    if not isinstance(uv, list) or len(uv) != len(vertices) or any(
            not isinstance(v, list) or len(v) != 2 or any(type(x) not in (int, float) or not math.isfinite(x) for x in v)
            for v in uv):
        raise ValueError('Incomplete or invalid garment UVs')
    for surface in ('cloth', 'gold', 'void'):
        indices = garment.get(surface)
        if not isinstance(indices, list) or len(indices) % 3 or any(
                type(i) is not int or i < 0 or i >= len(vertices) for i in indices):
            raise ValueError('Invalid '+surface+' triangle indices')
    result = dict(garment)
    result['bones'] = output_bones
    result['vertices'], result['normals'], result['weights'] = [], [], []
    preserved = 0
    for vertex_index, (point, normal, weight) in enumerate(zip(vertices, normals, weights)):
        if any(not isinstance(v, list) or len(v) != 3 or any(type(x) not in (int, float) or not math.isfinite(x) for x in v) for v in (point, normal)):
            raise ValueError('Invalid garment point or normal at '+str(vertex_index))
        p, n = Vector(point), Vector(normal)
        if n.length < 1e-8:
            raise ValueError('Zero garment normal at '+str(vertex_index))
        indices, values = weight.get('indices'), weight.get('values')
        if not isinstance(indices, list) or not isinstance(values, list) or len(indices) != 4 or len(values) != 4:
            raise ValueError('Invalid weights at '+str(vertex_index))
        if any(type(i) is not int or i < 0 or i >= len(bones) for i in indices) or any(type(v) not in (int, float) or not math.isfinite(v) or v < 0 for v in values) or abs(sum(values)-1) > 1e-4:
            raise ValueError('Invalid bone influence at '+str(vertex_index))
        merged = {}
        for source_slot, value in zip(indices, values):
            if value:
                target_slot = source_to_output[source_slot]
                merged[target_slot] = merged.get(target_slot, 0.) + value
        if not merged or len(merged) > 4:
            raise ValueError('Invalid merged influences at '+str(vertex_index))
        merged_items = sorted(merged.items(), key=lambda item: (-item[1], item[0]))
        total = sum(value for _, value in merged_items)
        result['weights'].append({'indices':[slot for slot,_ in merged_items] + [0]*(4-len(merged_items)),
                                  'values':[value/total for _,value in merged_items] + [0.]*(4-len(merged_items))})
        if anchor is not None and p.y < preserve_below_y:
            active = [(anchor, 1.)]
            preserved += 1
        else:
            active = [(deltas[i], value) for i, value in zip(indices, values) if value]
        moved = Vector((0, 0, 0))
        moved_normal = Vector((0, 0, 0))
        for delta, value in active:
            moved += value * (delta @ p)
            moved_normal += value * (delta.to_3x3().inverted().transposed() @ n)
        if moved_normal.length < 1e-8 or any(not math.isfinite(x) for x in moved):
            raise ValueError('Degenerate retarget result at '+str(vertex_index))
        result['vertices'].append(list(moved))
        result['normals'].append(list(moved_normal.normalized()))
    json.dumps(result, allow_nan=False)
    return result, {'vertices': len(vertices), 'sourceBones': len(bones),
                    'targetBones': len(output_bones), 'explicitBoneMap': bone_map,
                    'preservedLooseVertices': preserved,
                    'sourceRootName': source_reference.get('rootName'),
                    'targetRootName': target_reference.get('rootName')}


def build_scene(garment, reference, object_name, scene_path, posed_surface=None):
    """Import the target body, then add only the retargeted ORIGINAL as exportable mesh."""
    import bpy
    spec = importlib.util.spec_from_file_location('rig_import_reference', Path(__file__).with_name('import_reference.py'))
    importer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(importer)
    importer.import_capture(reference, .001)
    rigs = [o for o in bpy.data.objects if o.type == 'ARMATURE']
    if len(rigs) != 1:
        raise ValueError('Expected one imported target armature')
    rig = rigs[0]
    # The importer first verifies the capture at its sampled animation pose.
    # Authoring and the optional head-rest-aligned posed surface use rest space.
    for pose_bone in rig.pose.bones:
        pose_bone.matrix_basis.identity()
    bpy.context.view_layer.update()
    by_path = {}
    for bone in rig.data.bones:
        path = bone.get('sourcePath')
        if path in by_path:
            raise ValueError('Duplicate imported target bone path')
        by_path[path] = bone.name
    triangles = []
    triangle_materials = []
    for slot, surface in enumerate(('cloth', 'gold', 'void')):
        indices = garment[surface]
        if len(indices) % 3 or any(type(i) is not int or i < 0 or i >= len(garment['vertices']) for i in indices):
            raise ValueError('Invalid '+surface+' triangle list')
        triangles.extend(tuple(indices[i:i+3]) for i in range(0, len(indices), 3))
        triangle_materials.extend([slot] * (len(indices)//3))
    mesh = bpy.data.meshes.new(object_name)
    mesh.from_pydata([importer.C @ Vector(v) for v in garment['vertices']], [], triangles)
    mesh.update()
    obj = bpy.data.objects.new(object_name, mesh)
    bpy.context.collection.objects.link(obj)
    colors = garment.get('surfaceColors', {})
    palette = garment.get('surfacePalette', {})
    defaults = {'cloth':(0,84/255,182/255,1), 'gold':(.8,.47,.06,1), 'void':(0,0,0,1)}
    for surface in ('cloth', 'gold', 'void'):
        material = bpy.data.materials.new(surface)
        material['wearableSurface'] = surface
        material.diffuse_color = colors.get(surface, defaults[surface])
        if surface in palette:
            material['wearablePalette'] = palette[surface]
        mesh.materials.append(material)
    for polygon, slot in zip(mesh.polygons, triangle_materials):
        polygon.material_index = slot
        polygon.use_smooth = True
    layer = mesh.uv_layers.new(name='UVMap')
    if len(garment['uv']) != len(mesh.vertices):
        raise ValueError('Incomplete garment UVs')
    for loop in mesh.loops:
        layer.data[loop.index].uv = garment['uv'][loop.vertex_index]
    normal_conversion = importer.C.to_3x3()
    mesh.normals_split_custom_set_from_vertices([(normal_conversion @ Vector(n)).normalized() for n in garment['normals']])
    groups = []
    for bone in garment['bones']:
        imported_name = by_path.get(bone['path'])
        if imported_name is None:
            raise ValueError('Target garment bone absent from imported armature: '+bone['path'])
        groups.append(obj.vertex_groups.new(name=imported_name))
    for i, weight in enumerate(garment['weights']):
        for bone_index, value in zip(weight['indices'], weight['values']):
            if value:
                groups[bone_index].add([i], value, 'ADD')
    modifier = obj.modifiers.new('Target linear skinning', 'ARMATURE')
    modifier.object = rig
    modifier.use_deform_preserve_volume = False
    if 'paletteSource' in garment:
        obj['wearablePaletteSource'] = garment['paletteSource']
    if 'paletteMode' in garment:
        obj['wearablePaletteMode'] = garment['paletteMode']
    obj['retargetedOriginal'] = True
    if posed_surface is not None:
        surface_spec = importlib.util.spec_from_file_location('rig_pose_surface', Path(__file__).with_name('import_pose_surface.py'))
        surface_importer = importlib.util.module_from_spec(surface_spec)
        surface_spec.loader.exec_module(surface_importer)
        surface_importer.validate_capture(posed_surface)
        alignment, _, _ = surface_importer.head_alignment(posed_surface, reference)
        collection = bpy.data.collections.new('POSED SURFACE - REFERENCE ONLY')
        bpy.context.scene.collection.children.link(collection)
        for surface in posed_surface['meshes']:
            points = [importer.C @ alignment @ Vector(v) for v in surface['posedVerticesRoot']]
            indices = surface['triangles']
            faces = [indices[i:i+3] for i in range(0, len(indices), 3)]
            posed_mesh = bpy.data.meshes.new(surface['name']+' posed reference')
            posed_mesh.from_pydata(points, [], faces)
            posed_mesh.update()
            posed_obj = bpy.data.objects.new(surface['name']+' (posed reference only)', posed_mesh)
            collection.objects.link(posed_obj)
            posed_obj['referenceOnly'] = True
            posed_obj['exportForbidden'] = True
            posed_obj['poseSurfaceAlignment'] = 'head-rest-rigid-approximation'
    scene_path.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(scene_path))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('garment', 'source-reference', 'target-reference', 'out'):
        parser.add_argument('--'+name, required=True)
    parser.add_argument('--preserve-below-y', type=float)
    parser.add_argument('--anchor-bone', default='Pelvis_JNT')
    parser.add_argument('--bone-map', help='JSON object mapping authored source bone names to chosen target bone names')
    parser.add_argument('--scene-out', help='New .blend with target reference body and editable original garment')
    parser.add_argument('--object-name', default='Retargeted original garment')
    parser.add_argument('--posed-surface', help='Optional posed surface for visual reference, rigidly aligned by Head_JNT')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    paths = [Path(getattr(args, n)).resolve() for n in ('garment', 'source_reference', 'target_reference', 'out')]
    scene_path = Path(args.scene_out).resolve() if args.scene_out else None
    if len(set(paths + ([scene_path] if scene_path else []))) != len(paths) + bool(scene_path) or paths[-1].exists() or (scene_path and scene_path.exists()):
        raise ValueError('Input and output paths must be distinct; outputs must be new')
    if scene_path and scene_path.suffix.lower() != '.blend':
        raise ValueError('Scene output must be a .blend file')
    if args.posed_surface and not scene_path:
        raise ValueError('--posed-surface requires --scene-out')
    inputs = [json.loads(p.read_text(encoding='utf-8-sig')) for p in paths[:3]]
    bone_map = json.loads(Path(args.bone_map).read_text(encoding='utf-8-sig')) if args.bone_map else None
    result, report = retarget(*inputs, args.preserve_below_y, args.anchor_bone, bone_map)
    if scene_path:
        surface = json.loads(Path(args.posed_surface).read_text(encoding='utf-8-sig')) if args.posed_surface else None
        build_scene(result, inputs[2], args.object_name, scene_path, surface)
    paths[-1].parent.mkdir(parents=True, exist_ok=True)
    paths[-1].write_text(json.dumps(result, allow_nan=False, separators=(',', ':')), encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__':
    main()
