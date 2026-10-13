"""Blender background exporter for an authored GLB rig; never saves the source file."""
import argparse
import json
import runpy
import struct
import sys
from pathlib import Path

import bpy


def main():
    parser = argparse.ArgumentParser(description="Export selected meshes and their authored armature to GLB.")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--blend", type=Path)
    source.add_argument("--example", action="store_true")
    parser.add_argument("--objects", help="Comma-separated exact names, including armature and every ancestor")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    if bool(args.objects) != bool(args.blend):
        parser.error("--blend requires --objects; --example does not accept --objects")
    output = args.output.resolve()
    if output.suffix.lower() != ".glb" or output.exists():
        raise ValueError("--output must be a new .glb file")
    if args.blend:
        bpy.ops.wm.open_mainfile(filepath=str(args.blend.resolve(strict=True)))
        names = args.objects.split(",")
        if any(not n for n in names) or len(set(names)) != len(names):
            raise ValueError("Object names must be nonempty and unique")
        objects = [bpy.context.scene.objects.get(n) for n in names]
        if any(o is None for o in objects):
            raise ValueError("An object is absent from the active scene")
    else:
        scene = bpy.data.scenes.new("ProgenitorAnimatedExport")
        bpy.context.window.scene = scene
        recipe = Path(__file__).parent / "examples" / "animated-banner.py"
        objects = runpy.run_path(str(recipe))["create"]()
    chosen = set(objects)
    for obj in objects:
        if obj.type not in {"MESH", "ARMATURE", "EMPTY"}:
            raise ValueError(f"Unsupported selected object type: {obj.name}: {obj.type}")
        if obj.parent is not None and obj.parent not in chosen:
            raise ValueError(f"Include every ancestor in --objects: {obj.name}")
        if obj.type == "MESH":
            if obj.data.shape_keys is not None:
                raise ValueError(f"Shape keys are unsupported: {obj.name}")
            for mod in obj.modifiers:
                if mod.type != "ARMATURE" or mod.object not in chosen:
                    raise ValueError(f"Only a selected armature modifier is supported: {obj.name}")
            deform_names = {b.name for rig in objects if rig.type == "ARMATURE" for b in rig.data.bones}
            groups = {g.index for g in obj.vertex_groups if g.name in deform_names}
            for vertex in obj.data.vertices:
                if sum(g.weight > 0 and g.group in groups for g in vertex.groups) > 4:
                    raise ValueError(f"More than four joint influences: {obj.name} vertex {vertex.index}")
    for obj in bpy.context.scene.objects:
        obj.select_set(obj in chosen)
    bpy.context.view_layer.objects.active = next((o for o in objects if o.type == "ARMATURE"), objects[0])
    properties = bpy.ops.export_scene.gltf.get_rna_type().properties
    options = dict(filepath=str(output), export_format="GLB", use_selection=True,
                   use_active_scene=True, export_cameras=False, export_lights=False,
                   export_animations=True, export_force_sampling=True, export_skins=True,
                   export_morph=False, export_materials="EXPORT", export_apply=False)
    optional = dict(export_animation_mode="ACTIONS", export_sampling_interpolation_fallback="LINEAR",
                    export_influence_nb=4, export_all_influences=False,
                    export_morph_animation=False, export_extra_animations=False,
                    export_pointer_animation=False, export_optimize_animation_size=False)
    options.update({key: value for key, value in optional.items() if key in properties})
    output.parent.mkdir(parents=True, exist_ok=True)
    try:
        result = bpy.ops.export_scene.gltf(**options)
        if result != {"FINISHED"} or not output.is_file():
            raise RuntimeError(f"GLB export failed: {result}")
        data = output.read_bytes()
        json_length, kind = struct.unpack_from("<II", data, 12)
        if kind != 0x4E4F534A:
            raise ValueError("Missing GLB JSON chunk")
        document = json.loads(data[20:20 + json_length])
        if not document.get("skins") or not document.get("animations"):
            raise ValueError("Export needs a skin and at least one animation")
        for animation in document["animations"]:
            if any(s.get("interpolation", "LINEAR") not in {"LINEAR", "STEP"} for s in animation["samplers"]):
                raise ValueError("Export contains unsupported interpolation; bake to linear keys")
            if any(c["target"].get("path") not in {"translation", "rotation", "scale"} for c in animation["channels"]):
                raise ValueError("Only node TRS animation is supported")
        if document.get("extensionsRequired"):
            raise ValueError("Required GLTF extensions are unsupported")
        if any("targets" in primitive for mesh in document.get("meshes", []) for primitive in mesh["primitives"]):
            raise ValueError("Morph targets are unsupported")
        if any("bufferView" not in image for image in document.get("images", [])):
            raise ValueError("Images must be embedded")
        print("Exported authored rig GLB: " + str(output))
        print("Clips: " + ", ".join(a.get("name", "<unnamed>") for a in document["animations"]))
    except Exception:
        if output.is_file():
            output.unlink()
        raise


if __name__ == "__main__":
    main()
