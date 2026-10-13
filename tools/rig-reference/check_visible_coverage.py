"""Sample body-first rays in an explicit projected region; diagnostic only."""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree


DIRECTIONS = {
    'front': (1, -1),  # look along +Y; projected axes are X,Z
    'back': (1, 1),    # look along -Y; projected axes are X,Z
    'left': (0, -1),   # look along +X; projected axes are Y,Z
    'right': (0, 1),   # look along -X; projected axes are Y,Z
    'bottom': (2, -1), # look along +Z; projected axes are X,Y
    'top': (2, 1),     # look along -Z; projected axes are X,Y
}


def mesh_geometry(obj, frame):
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        mesh.calc_loop_triangles()
        transform = frame.inverted() @ evaluated.matrix_world
        vertices = [transform @ vertex.co for vertex in mesh.vertices]
        triangles = [tuple(face.vertices) for face in mesh.loop_triangles]
        if not vertices or not triangles or any(not math.isfinite(c) for v in vertices for c in v):
            raise ValueError(f'{obj.name} needs a finite evaluated surface')
        return vertices, triangles
    finally:
        evaluated.to_mesh_clear()


def sample(body_vertices, body_triangles, garment_vertices, garment_triangles,
           directions, bounds, step, epsilon, max_cells):
    if not all(math.isfinite(x) for x in (*bounds, step, epsilon)) or not (bounds[0] < bounds[1] and bounds[2] < bounds[3] and step > 0 and epsilon >= 0):
        raise ValueError('Expected finite ordered bounds, positive step and nonnegative tolerance')
    columns = math.floor((bounds[1] - bounds[0]) / step) + 1
    rows = math.floor((bounds[3] - bounds[2]) / step) + 1
    if columns * rows * len(directions) > max_cells:
        raise ValueError('Grid exceeds --max-cells; narrow the region or increase the step')
    body_tree = BVHTree.FromPolygons(body_vertices, body_triangles, all_triangles=True)
    garment_tree = BVHTree.FromPolygons(garment_vertices, garment_triangles, all_triangles=True)
    all_vertices = body_vertices + garment_vertices
    result = {}
    for name in directions:
        axis, sign = DIRECTIONS[name]
        projected = [i for i in range(3) if i != axis]
        start = (min(v[axis] for v in all_vertices) - 1) if sign < 0 else (max(v[axis] for v in all_vertices) + 1)
        ray = Vector((0, 0, 0)); ray[axis] = -sign
        cells = []
        body_hits = garment_misses = body_first = 0
        gaps = []
        for col in range(columns):
            u = bounds[0] + col * step
            for row in range(rows):
                v = bounds[2] + row * step
                origin = Vector((0, 0, 0))
                origin[axis] = start
                origin[projected[0]], origin[projected[1]] = u, v
                body_hit = body_tree.ray_cast(origin, ray)
                if body_hit[0] is None:
                    continue
                body_hits += 1
                garment_hit = garment_tree.ray_cast(origin, ray)
                missing = garment_hit[0] is None
                gap = None if missing else garment_hit[3] - body_hit[3]
                if missing:
                    garment_misses += 1
                if missing or gap > epsilon:
                    body_first += 1
                    if gap is not None:
                        gaps.append(gap * 1000)
                    if len(cells) < 64:
                        cells.append({'u': round(u, 6), 'v': round(v, 6),
                                      'gapMm': None if gap is None else round(gap * 1000, 3),
                                      'bodyTriangle': body_hit[2],
                                      'garmentTriangle': garment_hit[2]})
        result[name] = {'gridCells': columns * rows, 'bodyHits': body_hits,
                        'bodyFirst': body_first, 'garmentMisses': garment_misses,
                        'maxBodyFirstGapMm': max(gaps, default=None), 'examples': cells}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--scene', type=Path, required=True)
    parser.add_argument('--garment', required=True)
    parser.add_argument('--body', required=True)
    parser.add_argument('--directions', nargs='+', choices=DIRECTIONS, required=True)
    parser.add_argument('--u-min', type=float, required=True)
    parser.add_argument('--u-max', type=float, required=True)
    parser.add_argument('--v-min', type=float, required=True)
    parser.add_argument('--v-max', type=float, required=True)
    parser.add_argument('--step-mm', type=float, default=2)
    parser.add_argument('--epsilon-mm', type=float, default=.5)
    parser.add_argument('--max-cells', type=int, default=100000)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    if args.max_cells <= 0 or args.out.exists():
        raise ValueError('Output must be new and --max-cells positive')
    scene = args.scene.resolve(strict=True)
    bpy.ops.wm.open_mainfile(filepath=str(scene), load_ui=False, use_scripts=False)
    garment = bpy.data.objects.get(args.garment)
    body = bpy.data.objects.get(args.body)
    if garment is None or body is None or garment == body or garment.type != 'MESH' or body.type != 'MESH':
        raise ValueError('Select distinct existing garment and body meshes')
    body_vertices, body_triangles = mesh_geometry(body, body.matrix_world)
    garment_vertices, garment_triangles = mesh_geometry(garment, body.matrix_world)
    bounds = (args.u_min, args.u_max, args.v_min, args.v_max)
    results = sample(body_vertices, body_triangles, garment_vertices, garment_triangles,
                     args.directions, bounds, args.step_mm / 1000, args.epsilon_mm / 1000, args.max_cells)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps({
        'scene': str(scene), 'garment': garment.name, 'body': body.name,
        'coordinateFrame': 'body-object local metres',
        'axes': 'front/back: u=X,v=Z; left/right: u=Y,v=Z; top/bottom: u=X,v=Y',
        'bounds': bounds, 'stepMm': args.step_mm, 'epsilonMm': args.epsilon_mm,
        'directions': results,
        'qualification': 'Sampled view rays only; openings, self-occlusion, animation, shaders and live appearance need review. Body-first is not an approval failure by itself.',
    }, indent=2), encoding='utf-8')
    print('VISIBLE_COVERAGE_PASS', json.dumps({d: {'bodyHits': r['bodyHits'], 'bodyFirst': r['bodyFirst']} for d, r in results.items()}))


if __name__ == '__main__':
    main()
