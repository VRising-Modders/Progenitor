"""Export an original garment in bind/rest space for WearableGraphics.FromAuthoredJson."""
import argparse, json, math, sys
from pathlib import Path
import bpy
from mathutils import Matrix
C = Matrix(((1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))
# Match the public WearableSlot names in WearableGraphics.cs. Object metadata is
# validated here so a typo cannot silently become an own-slot fallback at runtime.
PALETTE_SOURCE_SLOTS = ('Headgear', 'Chest', 'Gloves', 'Legs', 'Footgear', 'Cloak', 'Weapon', 'Grimoire')

def export_garment(obj, reference):
    if obj.type != 'MESH' or obj.get('referenceOnly', False):
        raise ValueError('Only an original garment mesh may be exported; referenceOnly meshes are forbidden')
    palette_source = None
    if 'wearablePaletteSource' in obj:
        palette_source = obj['wearablePaletteSource']
        if type(palette_source) is not str or palette_source not in PALETTE_SOURCE_SLOTS:
            raise ValueError('wearablePaletteSource must name a WearableSlot')
    palette_mode = None
    if 'wearablePaletteMode' in obj:
        palette_mode = obj['wearablePaletteMode']
        if type(palette_mode) is not str or palette_mode not in ('native', 'authored'):
            raise ValueError('wearablePaletteMode must be native or authored')
    mods = [m for m in obj.modifiers if m.type == 'ARMATURE']
    if len(mods) != 1 or any(m.type != 'ARMATURE' for m in obj.modifiers):
        raise ValueError('Apply geometry modifiers first; exactly one linear armature modifier is required')
    rig = mods[0].object
    if rig is None or mods[0].use_deform_preserve_volume or not mods[0].use_vertex_groups or mods[0].use_bone_envelopes or mods[0].vertex_group:
        raise ValueError('A reference rig with linear skinning is required')
    if not mods[0].show_viewport or not mods[0].show_render:
        raise ValueError('Armature modifier must be enabled')
    if obj.constraints or rig.constraints or any(b.constraints for b in rig.pose.bones):
        raise ValueError('Constraints must be baked/removed before export')
    if any(x.animation_data and x.animation_data.drivers for x in (obj,rig,obj.data,rig.data)):
        raise ValueError('Drivers are unsupported')
    if obj.data.shape_keys:
        raise ValueError('Shape keys are unsupported')
    if reference['schemaVersion'] != 1 or reference['coordinateSystem'] != 'unity-left-handed-y-up' or reference['matrixLayout'] != 'row-major':
        raise ValueError('Unsupported reference schema')
    by_path = {n['path']: n for n in reference['nodes']}
    bones, mapping = [], {}
    for group in obj.vertex_groups:
        bone = rig.data.bones.get(group.name)
        if bone is None:
            continue # Non-deforming FitRegion masks are allowed.
        node = by_path.get(bone.get('sourcePath'))
        if node is None or node['restMatrix'] is None:
            raise ValueError('Deforming bone has no authoritative reference bind: '+group.name)
        if any(b['name'] == node['name'] for b in bones):
            raise ValueError('Runtime requires unique bone names')
        saved = bone.get('importedRestMatrix')
        if saved is None or len(saved) != 16 or max(abs(bone.matrix_local[r][c]-saved[r*4+c]) for r in range(4) for c in range(4)) > 1e-5:
            raise ValueError('Reference rig rest bones were edited; recapture/reimport before export')
        original = bone.get('sourceRestMatrix')
        if original is None or max(abs(a-b) for a,b in zip(original,node['restMatrix'])) > 1e-5:
            raise ValueError('Rig and reference capture rest matrices disagree')
        mapping[group.index] = len(bones)
        bones.append({k:node[k] for k in ('name','path','restMatrix')})
    if not bones:
        raise ValueError('Garment has no reference bone groups')
    # Geometry is exported unposed; rig/object world transforms are resolved into root space.
    transform = C.inverted() @ rig.matrix_world.inverted() @ obj.matrix_world
    normal = transform.to_3x3().inverted().transposed()
    if transform.determinant() <= 0:
        raise ValueError('Mirrored object transforms must be applied before export')
    mesh = obj.data
    if not mesh.uv_layers.active:
        raise ValueError('Garment requires a UV layer')
    mesh.calc_loop_triangles()
    vertices, normals, uv, weights = [], [], [], []
    surfaces = {'cloth':[], 'gold':[], 'void':[]}
    surface_colors = {}
    surface_palette = {}
    surface_palette_seen = {}
    # Split loop corners deliberately so UV seams and hard normals survive the runtime import.
    for tri in mesh.loop_triangles:
        material = mesh.materials[tri.material_index] if tri.material_index < len(mesh.materials) else None
        surface = str(material.get('wearableSurface', material.name.lower()) if material else '')
        if surface not in surfaces:
            raise ValueError('Each material must be named cloth/gold/void or define wearableSurface')
        # These are Blender's linear/shader RGBA values, not display/sRGB colors.
        # Runtime has one material per surface, so different colors cannot be merged.
        color = [float(value) for value in material.diffuse_color]
        if len(color) != 4 or any(not math.isfinite(value) or value < 0 or value > 1 for value in color):
            raise ValueError('Material diffuse_color for '+surface+' must contain four finite values in [0,1]')
        previous = surface_colors.get(surface)
        if previous is not None and previous != color:
            raise ValueError('Conflicting diffuse_color values for wearable surface '+surface+
                             '; use one RGBA color per surface')
        surface_colors[surface] = color
        palette = material.get('wearablePalette') if 'wearablePalette' in material else None
        if palette is not None and (not isinstance(palette, str) or palette not in ('primary','accent','neutral','metal')):
            raise ValueError('wearablePalette for '+surface+' must be primary/accent/neutral/metal')
        if surface in surface_palette_seen and surface_palette_seen[surface] != palette:
            raise ValueError('Conflicting wearablePalette values for wearable surface '+surface)
        surface_palette_seen[surface] = palette
        if palette is not None:
            surface_palette[surface] = palette
        for loop_index in tri.loops:
            vertex = mesh.vertices[mesh.loops[loop_index].vertex_index]
            influences = sorted(((mapping[g.group],g.weight) for g in vertex.groups if g.group in mapping and g.weight > 0), key=lambda p:-p[1])
            if not influences or len(influences) > 4:
                raise ValueError('Each vertex requires one to four positive bone weights; do not silently discard weights')
            total = sum(v for _,v in influences)
            indices = [i for i,_ in influences] + [0]*(4-len(influences))
            values = [v/total for _,v in influences] + [0.]*(4-len(influences))
            surfaces[surface].append(len(vertices))
            vertices.append(list(transform @ vertex.co))
            normals.append(list((normal @ mesh.corner_normals[loop_index].vector).normalized()))
            uv.append(list(mesh.uv_layers.active.data[loop_index].uv))
            weights.append({'indices':indices,'values':values})
    data = {'schemaVersion':1,'coordinateSystem':'unity-left-handed-y-up','matrixLayout':'row-major',
            'vertices':vertices,'normals':normals,'uv':uv,'bones':bones,'weights':weights,
            'surfaceColors':surface_colors,**surfaces}
    if surface_palette:
        data['surfacePalette'] = surface_palette
    if palette_source is not None:
        data['paletteSource'] = palette_source
    if palette_mode is not None:
        data['paletteMode'] = palette_mode
    if not vertices or len(vertices) > 60000:
        raise ValueError('Runtime garment export requires 1..60000 split vertices')
    # JSON itself must be portable and finite.
    json.dumps(data, allow_nan=False)
    return data

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--garment',required=True); p.add_argument('--reference',required=True); p.add_argument('--out',required=True)
    args=p.parse_args(sys.argv[sys.argv.index('--')+1:])
    obj=bpy.data.objects.get(args.garment)
    if obj is None: raise ValueError('Garment object does not exist')
    data=export_garment(obj,json.loads(Path(args.reference).read_text(encoding='utf-8-sig')))
    with Path(args.out).open('x',encoding='utf-8') as f: json.dump(data,f,allow_nan=False,separators=(',',':'))
    print('Exported',len(data['vertices']),'vertices')
if __name__=='__main__': main()
