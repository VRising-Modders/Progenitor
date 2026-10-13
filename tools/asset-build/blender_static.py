"""Run with Blender --background --python blender_static.py -- <arguments>."""
import argparse
import sys
from pathlib import Path

import bpy


def arguments():
    parser = argparse.ArgumentParser(description="Export selected static mesh objects to GLB.")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--blend", type=Path)
    source.add_argument("--example", action="store_true")
    parser.add_argument("--objects", help="Comma-separated exact object names from the active scene")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    if args.blend and not args.objects:
        parser.error("--blend requires --objects")
    if args.example and args.objects:
        parser.error("--example does not accept --objects")
    return args


def static_objects(names):
    result = []
    for name in names:
        obj = bpy.context.scene.objects.get(name)
        if obj is None:
            raise ValueError(f"Object not found in active scene: {name!r}")
        if obj.type != "MESH":
            raise ValueError(f"Object is not a static mesh: {name!r} ({obj.type})")
        if obj.data.shape_keys is not None:
            raise ValueError(f"Morph/shape keys are unsupported: {name!r}")
        if obj.modifiers:
            raise ValueError(f"Modifiers are unsupported; apply static modifiers before export: {name!r}")
        current = obj
        while current is not None:
            if current.type == "ARMATURE":
                raise ValueError(f"Armature parent is unsupported: {name!r}")
            if current.animation_data is not None:
                raise ValueError(f"Object or ancestor animation/drivers are unsupported: {name!r}")
            if current.constraints:
                raise ValueError(f"Object or ancestor constraints are unsupported: {name!r}")
            if current.parent is not None and current.parent_type != "OBJECT":
                raise ValueError(f"Only static object parenting is supported: {name!r}")
            current = current.parent
        if obj.data.animation_data is not None:
            raise ValueError(f"Mesh animation/drivers are unsupported: {name!r}")
        result.append(obj)
    return result


def example():
    material = bpy.data.materials.new("PedestalStone")
    material.use_nodes = True
    shader = material.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (0.23, 0.38, 0.46, 1.0)
    shader.inputs["Roughness"].default_value = 0.85
    # Original geometric prop: three flat-shaded cuboids, no external assets.
    for name, location, scale in (
        ("PedestalBase", (0, 0, 0.1), (0.6, 0.6, 0.1)),
        ("PedestalColumn", (0, 0, 0.55), (0.25, 0.25, 0.35)),
        ("PedestalCap", (0, 0, 1.0), (0.45, 0.45, 0.1)),
    ):
        bpy.ops.mesh.primitive_cube_add(size=2, location=location)
        obj = bpy.context.object
        obj.name = name
        obj.scale = scale
        obj.data.materials.append(material)


def main():
    args = arguments()
    output = args.output.resolve()
    if output.suffix.lower() != ".glb":
        raise ValueError("--output must end in .glb")
    if output.exists():
        raise ValueError(f"Output already exists: {output}")
    if args.blend:
        source = args.blend.resolve(strict=True)
        if source.suffix.lower() != ".blend":
            raise ValueError("--blend must name a .blend file")
        bpy.ops.wm.open_mainfile(filepath=str(source))
        bpy.context.view_layer.update()
        names = args.objects.split(",")
        if any(not name for name in names) or len(set(names)) != len(names):
            raise ValueError("--objects must contain unique, nonempty exact names")
        objects = static_objects(names)
    else:
        objects = []

    # Isolate copies so parent nodes, cameras, lights and unrelated scene objects cannot leak.
    # Never save the opened .blend file; all scene changes are in memory only.
    scene = bpy.data.scenes.new("ProgenitorStaticExport")
    bpy.context.window.scene = scene
    if args.example:
        example()
    else:
        for original in objects:
            obj = original.copy()
            obj.data = original.data.copy()
            obj.parent = None
            obj.matrix_world = original.matrix_world.copy()
            obj.animation_data_clear()
            scene.collection.objects.link(obj)
    output.parent.mkdir(parents=True, exist_ok=True)
    result = bpy.ops.export_scene.gltf(
        filepath=str(output), export_format="GLB", use_selection=False,
        use_active_scene=True, export_cameras=False, export_lights=False,
        export_animations=False, export_skins=False, export_morph=False,
        export_materials="EXPORT", export_apply=True,
    )
    if result != {"FINISHED"} or not output.is_file():
        raise RuntimeError(f"GLB export failed: {result}")
    print(f"Exported static GLB: {output}")


if __name__ == "__main__":
    main()
