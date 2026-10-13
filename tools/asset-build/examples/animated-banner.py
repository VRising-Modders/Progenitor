"""Original two-joint banner, generated entirely from geometry and keyed rotations."""
import math

import bpy


def create():
    scene = bpy.context.scene
    scene.render.fps = 24
    scene.frame_start, scene.frame_end = 1, 49
    armature = bpy.data.armatures.new("BannerSkeleton")
    rig = bpy.data.objects.new("BannerRig", armature)
    scene.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    root = armature.edit_bones.new("BannerRoot")
    root.head, root.tail = (0, 0, 1), (0.7, 0, 1)
    tip = armature.edit_bones.new("BannerTip")
    tip.head, tip.tail = (0.7, 0, 1), (1.4, 0, 1)
    tip.parent = root
    tip.use_connect = True
    bpy.ops.object.mode_set(mode="OBJECT")
    # A subdivided vertical cloth, with two normalized influences at the middle.
    vertices = [(x * 0.175, 0, z) for x in range(9) for z in (0.6, 1.4)]
    faces = [(2*x, 2*x+2, 2*x+3, 2*x+1) for x in range(8)]
    mesh = bpy.data.meshes.new("BannerCloth")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    cloth = bpy.data.objects.new("BannerCloth", mesh)
    scene.collection.objects.link(cloth)
    cloth.parent = rig
    modifier = cloth.modifiers.new("BannerSkin", "ARMATURE")
    modifier.object = rig
    groups = [cloth.vertex_groups.new(name=name) for name in ("BannerRoot", "BannerTip")]
    for index, (x, _, _) in enumerate(vertices):
        weight = min(1.0, max(0.0, (x - 0.35) / 0.7))
        if weight < 1:
            groups[0].add([index], 1 - weight, "REPLACE")
        if weight > 0:
            groups[1].add([index], weight, "REPLACE")
    material = bpy.data.materials.new("BannerTeal")
    material.use_nodes = True
    shader = material.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (0.03, 0.55, 0.45, 1)
    shader.inputs["Roughness"].default_value = 0.85
    # Zero these to avoid unnecessary optional material extensions in Blender 4.5.
    for name in ("Specular IOR Level", "Sheen Weight", "Coat Weight"):
        if name in shader.inputs:
            shader.inputs[name].default_value = 0
    mesh.materials.append(material)
    rig.animation_data_create()
    for name, amplitude, cycles in (("Breeze", 0.18, 1), ("Gust", 0.5, 2)):
        action = bpy.data.actions.new(name)
        action.use_fake_user = True
        rig.animation_data.action = action
        for frame in range(1, 50):
            phase = (frame - 1) / 48 * math.tau * cycles
            for bone_name, amount, offset in (("BannerRoot", amplitude * 0.3, 0),
                                              ("BannerTip", amplitude, 0.6)):
                bone = rig.pose.bones[bone_name]
                bone.rotation_mode = "QUATERNION"
                angle = amount * math.sin(phase + offset)
                bone.rotation_quaternion = (math.cos(angle / 2), 0, math.sin(angle / 2), 0)
                bone.keyframe_insert(data_path="rotation_quaternion", frame=frame, group=bone_name)
        # Blender 4.5 supports legacy fcurves for this single-slot action.
        for curve in action.fcurves:
            for key in curve.keyframe_points:
                key.interpolation = "LINEAR"
    rig.animation_data.action = None
    scene.frame_set(1)
    for bone in rig.pose.bones:
        bone.rotation_quaternion = (1, 0, 0, 0)
    return [rig, cloth]
