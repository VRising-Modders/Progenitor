"""Render isolated garment views with back-facing triangles transparent for winding review."""
import argparse
import json
import sys
from pathlib import Path

import bpy
from mathutils import Vector


VIEWS = {
    'front': Vector((0, -1, 0)),
    'side': Vector((1, 0, 0)),
    'elevated': Vector((0.7, -1, 0.55)).normalized(),
}


def evaluated_corners(obj, depsgraph):
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        if not mesh.vertices:
            raise ValueError('Garment has no evaluated vertices')
        return [evaluated.matrix_world @ vertex.co for vertex in mesh.vertices]
    finally:
        evaluated.to_mesh_clear()


def culled_material(source, label):
    # Source diffuse_color is authored shader/linear RGBA. Blender's AgX view
    # transform maps it to the display; this is not the game's HDRP shader.
    material = bpy.data.materials.new('Preview cull: ' + label)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    nodes.clear()
    links = material.node_tree.links
    output = nodes.new('ShaderNodeOutputMaterial')
    geometry = nodes.new('ShaderNodeNewGeometry')
    is_void = source is not None and source.get('wearableSurface', source.name.lower()) == 'void'
    if is_void:
        surface = nodes.new('ShaderNodeEmission')
        surface.inputs['Color'].default_value = (0, 0, 0, 1)
        surface.inputs['Strength'].default_value = 1
    else:
        surface = nodes.new('ShaderNodeBsdfPrincipled')
        surface.inputs['Base Color'].default_value = source.diffuse_color if source else (0.7, 0.7, 0.7, 1)
        surface.inputs['Roughness'].default_value = 0.8
    transparent = nodes.new('ShaderNodeBsdfTransparent')
    mix = nodes.new('ShaderNodeMixShader')
    links.new(geometry.outputs['Backfacing'], mix.inputs[0])
    links.new(surface.outputs[0], mix.inputs[1])
    links.new(transparent.outputs[0], mix.inputs[2])
    links.new(mix.outputs[0], output.inputs['Surface'])
    return material


def prepare_garment(obj):
    # Copy mesh and materials in memory. Never alter or save the authored scene.
    material_indices = tuple(polygon.material_index for polygon in obj.data.polygons)
    copy = obj.data.copy()
    obj.data = copy
    originals = list(copy.materials)
    for index, original in enumerate(originals):
        copy.materials[index] = culled_material(original, original.name if original else str(index))
    if not originals:
        copy.materials.append(culled_material(None, 'unassigned'))
    if tuple(polygon.material_index for polygon in copy.polygons) != material_indices:
        raise AssertionError('Preview preparation changed polygon material indices')


def add_area(name, location, target, power, size):
    data = bpy.data.lights.new(name, 'AREA')
    data.energy = power
    data.shape = 'DISK'
    data.size = size
    obj = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(obj)
    obj.location = location
    obj.rotation_euler = (target - obj.location).to_track_quat('-Z', 'Y').to_euler()


def render_view(scene, camera, corners, center, name, direction, output):
    direction = direction.normalized()
    distance = max((point - center).length for point in corners) * 4 + 1
    camera.location = center + direction * distance
    camera.rotation_euler = (center - camera.location).to_track_quat('-Z', 'Y').to_euler()
    bpy.context.view_layer.update()
    inverse = camera.matrix_world.inverted()
    projected = [inverse @ point for point in corners]
    width = max(point.x for point in projected) - min(point.x for point in projected)
    height = max(point.y for point in projected) - min(point.y for point in projected)
    camera.data.ortho_scale = max(width, height, 0.1) * 1.25
    scene.render.filepath = str(output / (name + '.png'))
    bpy.ops.render.render(write_still=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--scene', type=Path, required=True)
    parser.add_argument('--object', dest='garment', required=True)
    parser.add_argument('--body', help='Optional reference body object to show behind the garment')
    parser.add_argument('--out', type=Path, required=True, help='New output directory')
    parser.add_argument('--size', type=int, default=768, help='Square image pixels (default: 768)')
    parser.add_argument('--samples', type=int, default=24, help='Cycles samples (default: 24)')
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    if args.size < 128 or args.samples < 1:
        raise ValueError('Size must be at least 128 and samples must be positive')
    scene_path = args.scene.resolve()
    output = args.out.resolve()
    if not scene_path.is_file() or scene_path.suffix.lower() != '.blend':
        raise ValueError('Scene must be an existing .blend file')
    if output.exists():
        raise ValueError('Output directory must not already exist')
    bpy.ops.wm.open_mainfile(filepath=str(scene_path), load_ui=False, use_scripts=False)
    garment = bpy.data.objects.get(args.garment)
    body = bpy.data.objects.get(args.body) if args.body else None
    if garment is None or garment.type != 'MESH':
        raise ValueError('Named garment mesh is missing')
    if args.body and (body is None or body.type != 'MESH' or body == garment):
        raise ValueError('Named reference body mesh is missing or equals the garment')
    for obj in bpy.data.objects:
        if obj.type == 'MESH':
            obj.hide_render = obj not in (garment, body)
    garment.hide_render = False
    if body:
        body.hide_render = False
    corners = evaluated_corners(garment, bpy.context.evaluated_depsgraph_get())
    center = sum(corners, Vector()) / len(corners)
    prepare_garment(garment)
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = args.samples
    scene.render.resolution_x = args.size
    scene.render.resolution_y = args.size
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.film_transparent = False
    scene.view_settings.view_transform = 'AgX'
    world = bpy.data.worlds.new('Preview world')
    world.use_nodes = True
    world.node_tree.nodes.get('Background').inputs['Color'].default_value = (0.18, 0.18, 0.18, 1)
    world.node_tree.nodes.get('Background').inputs['Strength'].default_value = 0.7
    scene.world = world
    camera_data = bpy.data.cameras.new('Preview orthographic')
    camera_data.type = 'ORTHO'
    camera = bpy.data.objects.new('Preview camera', camera_data)
    bpy.context.collection.objects.link(camera)
    scene.camera = camera
    span = max((point - center).length for point in corners) * 3 + 1
    add_area('Preview key', center + Vector((-span, -span, span)), center, 450, span)
    add_area('Preview fill', center + Vector((span, -span / 2, span / 2)), center, 220, span)
    output.mkdir(parents=True)
    for name, direction in VIEWS.items():
        render_view(scene, camera, corners, center, name, direction, output)
    (output / 'preview.json').write_text(json.dumps({
        'source': str(scene_path), 'garment': garment.name,
        'body': body.name if body else None, 'views': list(VIEWS),
        'render': 'Cycles; Geometry.Backfacing makes rear faces transparent',
        'color': 'material diffuse_color in shader/linear space displayed through Blender AgX; void is unlit black',
        'qualification': 'Orientation diagnostic only; not Unity/HDRP or live-game appearance approval',
    }, indent=2), encoding='utf-8')
    print('PREVIEW_PASS', output)


if __name__ == '__main__':
    main()
