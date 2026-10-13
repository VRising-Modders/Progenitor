"""Fit an original garment's explicitly selected region to a reference in rest space."""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils.bvhtree import BVHTree


def mask(obj, name):
    group = obj.vertex_groups.get(name)
    if group is None:
        raise ValueError(f'{obj.name}: missing region {name}')
    values = [next((g.weight for g in v.groups if g.group == group.index), 0.0)
              for v in obj.data.vertices]
    if not any(values):
        raise ValueError(f'{name}: empty region')
    return values


def skin(obj, rig):
    names = {g.index: g.name for g in obj.vertex_groups if g.name in rig.data.bones}
    return [{names[g.group]: g.weight for g in v.groups if g.group in names and g.weight > 0}
            for v in obj.data.vertices]


def normalized(weights):
    pairs = sorted(((n, w) for n, w in weights.items() if w > 1e-8), key=lambda p: (-p[1], p[0]))
    total = sum(w for _, w in pairs[:4])
    if total <= 0 or not math.isfinite(total):
        raise ValueError('Missing or invalid skin weights')
    return {n: w / total for n, w in pairs[:4]}, max(0, len(pairs) - 4)


def barycentric(p, a, b, c):
    u, v, q = b-a, c-a, p-a
    uu, uv, vv = u.dot(u), u.dot(v), v.dot(v)
    denominator = uu*vv-uv*uv
    if denominator <= 1e-20:
        raise ValueError('Degenerate reference triangle')
    y = (vv*q.dot(u)-uv*q.dot(v))/denominator
    z = (uu*q.dot(v)-uv*q.dot(u))/denominator
    values = [max(0.0, min(1.0, w)) for w in (1-y-z, y, z)]
    total = sum(values)
    return [w/total for w in values]


def geometry_diagnostics(before, after, triangles, fit_mask):
    """Compare fitted rest geometry; local shape evidence, not coverage proof."""
    if len(before) != len(after) or len(before) != len(fit_mask):
        raise ValueError('Geometry diagnostic vertex counts differ')

    def measure(faces):
        edge_faces = {}
        old_normals, new_normals = [], []
        flipped = collapsed = 0
        for tri in faces:
            a,b,c = tri
            old = (before[b]-before[a]).cross(before[c]-before[a])
            new = (after[b]-after[a]).cross(after[c]-after[a])
            old_normals.append(old); new_normals.append(new)
            if old.length > 1e-12:
                if new.dot(old) <= 0: flipped += 1
                if new.length < old.length*.25: collapsed += 1
            index = len(old_normals)-1
            for u,v in ((a,b),(b,c),(c,a)):
                edge_faces.setdefault((min(u,v),max(u,v)),[]).append(index)
        compressed = stretched = 0
        minimum, maximum = math.inf, 0.0
        old_angles, new_angles = [], []
        for (u,v), incident in edge_faces.items():
            length = (before[u]-before[v]).length
            if length > 1e-8:
                ratio = (after[u]-after[v]).length/length
                minimum = min(minimum,ratio); maximum = max(maximum,ratio)
                compressed += ratio < .5
                stretched += ratio > 2
            if len(incident) == 2:
                i,j = incident
                if min(old_normals[i].length,old_normals[j].length,
                       new_normals[i].length,new_normals[j].length) > 1e-12:
                    old_angles.append(math.degrees(old_normals[i].angle(old_normals[j])))
                    new_angles.append(math.degrees(new_normals[i].angle(new_normals[j])))
        def percentile(values):
            values.sort()
            return values[int(.9*(len(values)-1))] if values else 0.0
        old_over45=sum(angle>45 for angle in old_angles)
        new_over45=sum(angle>45 for angle in new_angles)
        return {'triangles':len(faces),'edges':len(edge_faces),
                'adjacentFacePairs':len(old_angles),'flippedTriangles':flipped,
                'collapsedTriangles':collapsed,'compressedEdges':compressed,
                'stretchedEdges':stretched,'minimumEdgeRatio':minimum if minimum<math.inf else 1.0,
                'maximumEdgeRatio':maximum if minimum<math.inf else 1.0,
                'dihedralP90BeforeDegrees':percentile(old_angles),
                'dihedralP90AfterDegrees':percentile(new_angles),
                'dihedralOver45Before':old_over45,'dihedralOver45After':new_over45}

    triangles=list(triangles)
    # Include transition triangles: moving one selected vertex can distort a
    # face whose other vertices are fixed outside the mask.
    selected=[tri for tri in triangles if any(fit_mask[i]>0 for i in tri)]
    whole=measure(triangles); fitted=measure(selected)
    severe=bool(fitted['flippedTriangles'] or fitted['collapsedTriangles']
                or fitted['compressedEdges'] or fitted['stretchedEdges']
                or (fitted['dihedralP90AfterDegrees']>45 and
                    fitted['dihedralP90AfterDegrees']-fitted['dihedralP90BeforeDegrees']>20))
    return {'selectedFitRegion':fitted,'wholeGarment':whole,'requiresReview':severe,
            'definitions':'Selected region includes triangles touching any positive fit-mask vertex. Flipped counts a nonpositive before/after face-normal dot product, including zero-area results; it does not prove topological inversion. Collapsed means less than 25% of original triangle area; compressed/stretched edges are below 0.5/above 2 times original length.',
            'limitations':'Rest geometry only; does not prove body clearance, coverage, or animation safety.'}


def fit(garment, body, region='FitRegion', weight_region=None, clearance=0.006,
        max_distance=0.05, output_name='Fitted garment', body_region=None):
    if garment == body or garment.type != 'MESH' or body.type != 'MESH':
        raise ValueError('Select distinct garment and body meshes')
    if garment.get('referenceOnly', False):
        raise ValueError('Reference mesh cannot become clothing; explicitly author an original garment first')
    if bpy.data.objects.get(output_name):
        raise ValueError('Output object already exists')
    if not (math.isfinite(clearance) and clearance >= 0 and math.isfinite(max_distance) and max_distance > 0):
        raise ValueError('Distances must be finite; clearance >= 0 and max distance > 0')
    for obj in (garment, body):
        if obj.data.shape_keys or any(m.type != 'ARMATURE' for m in obj.modifiers):
            raise ValueError('Apply shape keys and non-armature modifiers before fitting')
        if abs(obj.matrix_world.determinant()) < 1e-10:
            raise ValueError('Singular object transform')
        for modifier in obj.modifiers:
            if (not modifier.show_viewport or modifier.use_deform_preserve_volume
                    or modifier.use_bone_envelopes or not modifier.use_vertex_groups or modifier.vertex_group):
                raise ValueError('Use enabled linear vertex-weight skinning without envelopes or modifier masks')
    rigs = [[m.object for m in obj.modifiers if m.type == 'ARMATURE'] for obj in (garment, body)]
    if len(rigs[0]) != 1 or rigs[0] != rigs[1] or rigs[0][0] is None:
        raise ValueError('Garment and body must use the same single reference armature')
    rig = rigs[0][0]
    if region is None and not weight_region:
        raise ValueError('Weights-only transfer requires an explicit weight region')
    fit_mask = mask(garment, region) if region else [0.0]*len(garment.data.vertices)
    weight_mask = mask(garment, weight_region) if weight_region else fit_mask
    body_weights, old_weights = skin(body, rig), skin(garment, rig)
    body.data.calc_loop_triangles()
    if abs(rig.matrix_world.determinant()) < 1e-10:
        raise ValueError('Singular rig transform')
    body_to_root = rig.matrix_world.inverted() @ body.matrix_world
    garment_to_root = rig.matrix_world.inverted() @ garment.matrix_world
    points = [body_to_root @ v.co for v in body.data.vertices]
    allowed = mask(body, body_region) if body_region else [1.0]*len(points)
    triangles = [tuple(t.vertices) for t in body.data.loop_triangles
                 if all(allowed[i] > 0 for i in t.vertices)
                 and (points[t.vertices[1]]-points[t.vertices[0]]).cross(points[t.vertices[2]]-points[t.vertices[0]]).length > 1e-10]
    if not triangles:
        raise ValueError('Reference contains no usable triangles')
    tree = BVHTree.FromPolygons(points, triangles, all_triangles=True)
    normal_matrix = body_to_root.to_3x3().inverted().transposed()
    normals = [(normal_matrix @ v.normal).normalized() for v in body.data.vertices]
    inverse = garment_to_root.inverted()
    edits, truncated = [], 0
    for vertex, amount, weight_amount in zip(garment.data.vertices, fit_mask, weight_mask):
        if not amount and not weight_amount:
            continue
        position = garment_to_root @ vertex.co
        anchor, face_normal, face, distance = tree.find_nearest(position, max_distance)
        if anchor is None:
            raise ValueError(f'Vertex {vertex.index}: no reference surface within {max_distance:g} m; restrict region or choose the correct body part')
        tri = triangles[face]
        factors = barycentric(anchor, *(points[i] for i in tri))
        normal = sum((normals[i]*w for i, w in zip(tri, factors)), face_normal*0)
        normal = normal.normalized() if normal.length > 1e-8 else face_normal
        if normal.dot(face_normal) < 0:
            normal.negate()
        target = position.lerp(anchor + normal*clearance, amount)
        # Transfer at the body anchor before adding clearance, never at displaced cloth.
        transferred = {}
        if weight_amount:
            for i, factor in zip(tri, factors):
                source, _ = normalized(body_weights[i])
                for name, value in source.items():
                    transferred[name] = transferred.get(name, 0) + factor*value*weight_amount
            if weight_amount < 1:
                previous, _ = normalized(old_weights[vertex.index])
                for name, value in previous.items():
                    transferred[name] = transferred.get(name, 0) + value*(1-weight_amount)
            transferred, removed = normalized(transferred)
            truncated += removed
        edits.append((vertex.index, inverse @ target, transferred, (target-position).length))
    result = garment.copy()
    result.data = garment.data.copy()
    result.name = output_name
    bpy.context.collection.objects.link(result)
    for index, position, weights, _ in edits:
        result.data.vertices[index].co = position
        if weights:
            for group in list(result.vertex_groups):
                if group.name in rig.data.bones:
                    group.remove([index])
            for name, value in weights.items():
                group = result.vertex_groups.get(name) or result.vertex_groups.new(name=name)
                group.add([index], value, 'REPLACE')
    # Authored normals no longer describe the edited surface; derive from new geometry.
    if result.data.has_custom_normals and any(e[3] > 1e-9 for e in edits):
        result.data.normals_split_custom_set([(0, 0, 0)] * len(result.data.loops))
    result.data.update()
    garment.data.calc_loop_triangles()
    distortion = geometry_diagnostics(
        [garment_to_root @ v.co for v in garment.data.vertices],
        [garment_to_root @ v.co for v in result.data.vertices],
        (tuple(tri.vertices) for tri in garment.data.loop_triangles),fit_mask)
    result['referenceOnly'] = False
    result['fitBody'] = body.name
    result['fitClearanceMeters'] = clearance
    report = {'garment': result.name, 'body': body.name, 'editedVertices': len(edits),
              'unchangedVertices': len(garment.data.vertices)-len(edits),
              'maxMovementMeters': max((e[3] for e in edits), default=0),
              'discardedInfluences': truncated, 'clearanceMeters': clearance,
              'coverageVerified': False, 'weightsOnly': region is None,
              'method': 'rest-surface barycentric weights; explicit region',
              'requiresReview':distortion['requiresReview'],'distortion':distortion}
    return result, report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--garment', required=True)
    parser.add_argument('--body', required=True)
    parser.add_argument('--region', default='FitRegion')
    parser.add_argument('--weight-region')
    parser.add_argument('--weights-only', action='store_true', help='Preserve loose fabric shape; requires --weight-region')
    parser.add_argument('--body-region', help='Limit reference triangles to an explicit body vertex group')
    parser.add_argument('--clearance-mm', type=float, default=6)
    parser.add_argument('--max-distance-mm', type=float, default=50)
    parser.add_argument('--name', default='Fitted garment')
    parser.add_argument('--out', required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    output = Path(args.out).resolve()
    if output.exists() or output.with_suffix('.fit.json').exists():
        raise ValueError('Output already exists; use a new versioned filename')
    _, report = fit(bpy.data.objects[args.garment], bpy.data.objects[args.body], None if args.weights_only else args.region,
                    args.weight_region, args.clearance_mm/1000, args.max_distance_mm/1000, args.name, args.body_region)
    output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(output))
    output.with_suffix('.fit.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__':
    main()
