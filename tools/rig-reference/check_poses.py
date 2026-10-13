"""Replay captured poses and report intersections; diagnostics are not fit approval."""
import argparse
import json
import math
import sys
from pathlib import Path
from collections import Counter
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree
sys.path.insert(0, str(Path(__file__).resolve().parent))
from import_reference import C, matrix


def close_numbers(a, b, tolerance):
    if len(a) != len(b):
        return False
    return all(close_numbers(x,y,tolerance) if isinstance(x,(list,tuple)) else
               math.isfinite(x) and math.isfinite(y) and abs(x-y) <= tolerance for x,y in zip(a,b))


def same_reference(first_nodes, first, nodes, source):
    if [(n['path'],n['parent'],n['restMatrix'] is None) for n in first_nodes] != [(n['path'],n['parent'],n['restMatrix'] is None) for n in nodes]:
        return False
    if any(a['restMatrix'] is not None and not close_numbers(a['restMatrix'],b['restMatrix'],1e-4) for a,b in zip(first_nodes,nodes)):
        return False
    if first['triangles'] != source['triangles'] or first['bones'] != source['bones']:
        return False
    for field,tolerance in [('vertices',1e-5),('bindPoses',1e-4),('rendererToRoot',1e-4)]:
        if not close_numbers(first[field],source[field],tolerance):
            return False
    return (len(first['weights']) == len(source['weights']) and
            all(a['indices'] == b['indices'] and close_numbers(a['values'],b['values'],1e-5)
                for a,b in zip(first['weights'],source['weights'])))


def geometry(obj, rig):
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        mesh.calc_loop_triangles()
        transform = rig.matrix_world.inverted() @ evaluated.matrix_world
        return ([transform @ v.co for v in mesh.vertices],
                [tuple(t.vertices) for t in mesh.loop_triangles])
    finally:
        evaluated.to_mesh_clear()


def diagnostics(body_vertices, body_triangles, cloth_vertices, cloth_triangles, clearance):
    if not body_triangles or not cloth_triangles or any(not math.isfinite(x) for v in body_vertices+cloth_vertices for x in v):
        raise ValueError('Diagnostic meshes require finite surfaces')
    body_tree = BVHTree.FromPolygons(body_vertices, body_triangles, all_triangles=True)
    cloth_tree = BVHTree.FromPolygons(cloth_vertices, cloth_triangles, all_triangles=True)
    intersections = body_tree.overlap(cloth_tree)
    distances, inward, near = [], [], []
    for index, vertex in enumerate(cloth_vertices):
        location, normal, _, distance = body_tree.find_nearest(vertex)
        if location is None:
            raise ValueError('Body has no searchable surface')
        distances.append(distance)
        if distance <= clearance:
            near.append(index)
        # Local nearest-face sign is not a watertight inside/outside test.
        if (vertex-location).dot(normal) < -1e-6:
            inward.append(index)
    edges = Counter(tuple(sorted((a,b))) for face in body_triangles for a,b in zip(face,face[1:]+face[:1]))
    return {'intersectionPairs':len(intersections),
            'intersectingBodyTriangles':len({a for a,b in intersections}),
            'intersectingGarmentTriangles':len({b for a,b in intersections}),
            'nearSurfaceGarmentVertices':len(near), 'inwardFacingGarmentVertices':len(inward),
            'minVertexSurfaceDistanceMm':min(distances,default=0)*1000,
            'bodyNonManifoldEdges':sum(count != 2 for count in edges.values()),
            'sampleInwardVertexIndices':inward[:32], 'sampleNearVertexIndices':near[:32],
            'status':'review' if intersections or inward or near else 'no-findings-in-sampled-pose'}


def check_poses(garment, body, captures, clearance=0.002, tolerance=0.001):
    if garment == body or garment.type != 'MESH' or body.type != 'MESH':
        raise ValueError('Select distinct garment and body meshes')
    if not math.isfinite(clearance) or clearance < 0 or not math.isfinite(tolerance) or tolerance <= 0:
        raise ValueError('Invalid clearance or verification tolerance')
    arms = [m.object for m in body.modifiers if m.type == 'ARMATURE' and m.show_viewport]
    if len(arms) != 1 or arms[0] is None:
        raise ValueError('Body needs exactly one enabled armature')
    rig = arms[0]
    for obj in (body,garment):
        if obj.data.shape_keys or len(obj.modifiers) != 1 or obj.modifiers[0].type != 'ARMATURE':
            raise ValueError('Apply shape keys and non-armature modifiers before pose checks')
        enabled = [m for m in obj.modifiers if m.show_viewport and m.type == 'ARMATURE']
        if len(enabled) != 1 or enabled[0].object != rig or enabled[0].use_deform_preserve_volume or enabled[0].use_bone_envelopes or not enabled[0].use_vertex_groups or enabled[0].vertex_group:
            raise ValueError('Body and garment must each use one linear vertex-group reference armature')
    for owner in (rig,rig.data,body,garment,body.data,garment.data):
        if owner.animation_data is not None or getattr(owner,'constraints',None):
            raise ValueError('Remove animation data and constraints before captured pose checks')
    if any(b.constraints for b in rig.pose.bones):
        raise ValueError('Remove pose constraints before captured pose checks')
    paths = {b.get('sourcePath'):b.name for b in rig.data.bones}
    if None in paths or len(paths) != len(rig.data.bones):
        raise ValueError('Rig requires unique imported sourcePath values')
    saved = {b.name:b.matrix_basis.copy() for b in rig.pose.bones}
    saved_position = rig.data.pose_position
    results = []
    baseline = None
    try:
        rig.data.pose_position = 'POSE'
        for label,data in captures:
            if (data['schemaVersion'],data['coordinateSystem'],data['matrixLayout']) != (1,'unity-left-handed-y-up','row-major'):
                raise ValueError('Unsupported capture schema')
            nodes = data['nodes']
            if len({n['path'] for n in nodes}) != len(nodes) or set(paths) != {n['path'] for n in nodes}:
                raise ValueError('Capture skeleton paths differ from imported rig')
            sources = [m for m in data['meshes'] if m['name'] == body.data.name or m['name'] == body.name]
            if len(sources) != 1:
                raise ValueError('Capture must contain exactly one matching body mesh')
            source = sources[0]
            if baseline is not None and not same_reference(*baseline,nodes,source):
                raise ValueError('Capture rest mesh, topology, skeleton or skin differs between poses')
            if baseline is None:
                baseline = (nodes,source)
            transform = C @ matrix(source['rendererToRoot'])
            rest_transform = rig.matrix_world.inverted() @ body.matrix_world
            if len(body.data.vertices) != len(source['vertices']) or any((rest_transform @ v.co-transform @ Vector(s)).length > tolerance for v,s in zip(body.data.vertices,source['vertices'])):
                raise ValueError('Body rest vertices do not match capture')
            body.data.calc_loop_triangles()
            if [i for t in body.data.loop_triangles for i in t.vertices] != source['triangles']:
                raise ValueError('Body topology differs from capture')
            if len(source['weights']) != len(body.data.vertices):
                raise ValueError('Body skin correspondence is incomplete')
            for vertex, weight in zip(body.data.vertices, source['weights']):
                expected_weights = {}
                if len(weight['indices']) != 4 or len(weight['values']) != 4 or abs(sum(weight['values'])-1) > 1e-4:
                    raise ValueError('Captured weights must contain four normalized influences')
                for slot,value in zip(weight['indices'],weight['values']):
                    if not math.isfinite(value) or value < 0:
                        raise ValueError('Invalid captured weight')
                    if value:
                        if slot < 0 or slot >= len(source['bones']):
                            raise ValueError('Captured skin palette index is invalid')
                        name = paths[nodes[source['bones'][slot]]['path']]
                        expected_weights[name] = expected_weights.get(name,0)+value
                actual_weights = {body.vertex_groups[g.group].name:g.weight for g in vertex.groups
                                  if body.vertex_groups[g.group].name in rig.data.bones and g.weight > 0}
                if any(abs(actual_weights.get(name,0)-expected_weights.get(name,0)) > 1e-5
                       for name in set(actual_weights)|set(expected_weights)):
                    raise ValueError('Body skin weights differ from capture')
            pending = set(range(len(nodes)))
            done = set()
            intended = {}
            while pending:
                ready = [i for i in pending if nodes[i]['parent'] == -1 or nodes[i]['parent'] in done]
                if not ready:
                    raise ValueError('Invalid capture hierarchy')
                for i in ready:
                    node = nodes[i]
                    bone = rig.data.bones[paths[node['path']]]
                    parent = None if node['parent'] == -1 else paths[nodes[node['parent']]['path']]
                    if (bone.parent.name if bone.parent else None) != parent:
                        raise ValueError('Imported hierarchy differs from capture')
                    original = bone.get('sourceRestMatrix')
                    imported = bone.get('importedRestMatrix')
                    if original is None or imported is None:
                        raise ValueError('Reimport reference with rest-matrix provenance before pose checks')
                    if node['restMatrix'] is not None and not close_numbers(original,node['restMatrix'],1e-4):
                        raise ValueError('Capture bind rest differs from imported reference')
                    if not close_numbers(imported,[v for row in bone.matrix_local for v in row],1e-5):
                        raise ValueError('Imported armature rest bones were edited')
                    rest = C @ matrix(original) @ C.inverted()
                    pose = C @ matrix(node['poseMatrix']) @ C.inverted()
                    intended[bone.name] = pose @ rest.inverted() @ bone.matrix_local
                    rig.pose.bones[bone.name].matrix = intended[bone.name]
                    bpy.context.view_layer.update()
                done.update(ready)
                pending.difference_update(ready)
            for name,target in intended.items():
                if not close_numbers([v for row in target for v in row],
                                     [v for row in rig.pose.bones[name].matrix for v in row],1e-4):
                    raise ValueError('Actual rig matrix differs from requested captured pose')
            body_v,body_t = geometry(body,rig)
            expected = source['bakedVertices']
            if len(body_v) != len(expected):
                raise ValueError('Body evaluation changed vertex count')
            errors = [(v-transform @ Vector(target)).length for v,target in zip(body_v,expected)]
            if not errors or any(not math.isfinite(e) for e in errors) or max(errors) > tolerance:
                raise ValueError('Body pose does not reproduce game capture')
            cloth_v,cloth_t = geometry(garment,rig)
            if not cloth_v or not cloth_t:
                raise ValueError('Garment requires a surface')
            result = diagnostics(body_v,body_t,cloth_v,cloth_t,clearance)
            result.update({'capture':str(label),'bodyPoseMaxErrorMm':max(errors)*1000})
            results.append(result)
    finally:
        for name,value in saved.items():
            rig.pose.bones[name].matrix_basis = value
        rig.data.pose_position = saved_position
        bpy.context.view_layer.update()
    if not results:
        raise ValueError('Provide at least one capture')
    return {'schemaVersion':1,'clearanceMm':clearance*1000,'poses':results,
            'limitations':'Sampled poses only; no cloth simulation or continuous collision detection. Loose fabric and large body clearance are intentional design choices, not defects. Near-surface counts are diagnostics only. Triangle overlap and nearest-face signs are not proof of coverage. Open surfaces, inconsistent normals, sparse vertices, unsampled motion and self-intersections require visual review. No automatic fit approval.'}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--garment',required=True)
    parser.add_argument('--body',required=True)
    parser.add_argument('--captures',nargs='+',required=True)
    parser.add_argument('--out',required=True)
    parser.add_argument('--clearance-mm',type=float,default=2)
    parser.add_argument('--tolerance-mm',type=float,default=1)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    captures = [(p,json.loads(Path(p).read_text(encoding='utf-8-sig'))) for p in args.captures]
    report = check_poses(bpy.data.objects[args.garment],bpy.data.objects[args.body],captures,args.clearance_mm/1000,args.tolerance_mm/1000)
    Path(args.out).write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps(report))

if __name__ == '__main__':
    main()
