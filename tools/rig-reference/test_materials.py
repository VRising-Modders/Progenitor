"""Offline material-group/UV contract test; no game or output files required."""
import sys
from pathlib import Path
import bpy
sys.path.insert(0,str(Path(__file__).resolve().parent))
from import_reference import import_surface_metadata

def mesh_object():
    mesh=bpy.data.meshes.new('Synthetic material triangles')
    mesh.from_pydata([(0,0,0),(1,0,0),(0,1,0),(1,1,0)],[],[(0,1,2),(1,3,2),(0,2,3),(0,3,1)])
    return bpy.data.objects.new('Synthetic original',mesh)

obj=mesh_object()
source={'triangles':[0,1,2,1,3,2,0,2,3,0,3,1],'uv':[],
        'submeshes':[{'indexCount':3,'materialName':'progenitor-wear-'+s} for s in ('cloth','gold','void')]
        + [{'indexCount':3,'materialName':'Native armor'}]}
import_surface_metadata(obj,source)
assert [p.material_index for p in obj.data.polygons]==[0,1,2,3]
assert [m.get('wearableSurface') for m in obj.data.materials]==['cloth','gold','void',None]
assert obj['sourceUvMissing'] and all(tuple(v.uv)==(0.,0.) for v in obj.data.uv_layers.active.data)
legacy=mesh_object(); import_surface_metadata(legacy,{'triangles':source['triangles'],'uv':[[.1,.2]]*4})
assert not legacy['sourceUvMissing'] and len(legacy.data.materials)==0
for count in (2,-3,True,6):
    bad={**source,'submeshes':[{'indexCount':count,'materialName':'progenitor-wear-cloth'}]}
    try: import_surface_metadata(mesh_object(),bad)
    except ValueError: pass
    else: raise AssertionError('Invalid partition accepted')
colored={**source,'submeshes':[dict(g,color=[.12,.34,.56,.78]) for g in source['submeshes']]}
obj=mesh_object(); import_surface_metadata(obj,colored)
for material in list(obj.data.materials)[:3]:
    assert max(abs(a-b) for a,b in zip(material.diffuse_color,[.12,.34,.56,.78]))<1e-6
assert obj.data.materials[3].get('wearableSurface') is None
for color in ([1,0,0], [1,0,0,2], [float('nan'),0,0,1], [True,0,0,1], 'red'):
    bad={**source,'submeshes':[dict(g,color=color) for g in source['submeshes']]}
    try: import_surface_metadata(mesh_object(),bad)
    except ValueError: pass
    else: raise AssertionError('Invalid color accepted')
print('MATERIALS_PASS: triangle assignments, known surfaces, native isolation, UV fallback, legacy schema, invalid counts')
