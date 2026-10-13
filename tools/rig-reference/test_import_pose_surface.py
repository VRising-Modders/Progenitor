"""Blender-background regression checks for the static posed-surface importer."""
import copy
import importlib.util
import json
import math
import sys
import tempfile
from pathlib import Path

import bpy

script = Path(__file__).with_name('import_pose_surface.py')
spec = importlib.util.spec_from_file_location('import_pose_surface', script)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


def translation(x, y, z):
    return [1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1]


capture = {
    'schemaVersion': 1, 'kind': 'posed-surface', 'referenceOnly': True,
    'coordinateSystem': 'unity-left-handed-y-up', 'matrixLayout': 'row-major',
    'rootName': 'Model', 'capturedUtc': '2026-10-02T00:00:00Z', 'coreVersion': '1.0',
    'nodes': [
        {'name': 'Model', 'path': 'Model[0]', 'parent': -1, 'poseMatrix': translation(0, 0, 0)},
        {'name': 'Head_JNT', 'path': 'Model[0]/Head_JNT[0]', 'parent': 0,
         'poseMatrix': translation(0, 1, 0)},
    ],
    'meshes': [{'name': 'Head surface', 'rendererNode': 0, 'rootBone': 1, 'bones': [1],
                'posedVerticesRoot': [[0, 1, 0], [1, 1, 0], [0, 2, 0]],
                'triangles': [0, 1, 2]}],
}
reference = {'schemaVersion': 1, 'coordinateSystem': 'unity-left-handed-y-up',
             'matrixLayout': 'row-major',
             'meshes': [{'name': 'full rig reference'}],
             'nodes': [{'name': 'Head_JNT', 'path': 'Model[0]/Head_JNT[0]',
                        'restMatrix': translation(0, 2, 0)}]}

mod.validate_capture(capture)
bad = copy.deepcopy(capture)
bad['meshes'][0]['weights'] = []
try:
    mod.validate_capture(bad)
except ValueError:
    pass
else:
    raise AssertionError('Bind/weight fields must not be accepted')

for target in (None, reference):
    result = mod.import_capture(capture, target)
    assert result == [{'mesh': 'Head surface', 'vertices': 3, 'triangles': 1}]
    obj = next(o for o in bpy.context.scene.objects if o.type == 'MESH')
    assert obj['referenceOnly'] and obj['exportForbidden']
    assert len(obj.vertex_groups) == 0 and len(obj.modifiers) == 0
    assert not any(o.type == 'ARMATURE' for o in bpy.context.scene.objects)
    assert bpy.context.scene['poseSurfaceKind'] == 'posed-surface'
    assert bpy.context.scene['poseSurfaceCapturedUtc'] == capture['capturedUtc']
    expected_y = 1 if target is None else 2
    assert math.isclose(obj.data.vertices[0].co.z, expected_y, abs_tol=1e-6)
    assert bpy.context.scene['poseSurfaceAlignment'] == (
        'posed-root' if target is None else 'head-rest-rigid-approximation')

with tempfile.TemporaryDirectory() as folder:
    input_path = Path(folder) / 'capture.json'
    output_path = Path(folder) / 'existing.blend'
    fresh_path = Path(folder) / 'new-reference.blend'
    input_path.write_text(json.dumps(capture), encoding='utf-8')
    output_path.write_bytes(b'KEEP')
    original_argv = sys.argv
    try:
        sys.argv = [str(script), '--', str(input_path), '--out', str(output_path)]
        try:
            mod.main()
        except ValueError as error:
            assert 'nonexistent' in str(error)
        else:
            raise AssertionError('Existing .blend output must be refused')
    finally:
        sys.argv = original_argv
    assert output_path.read_bytes() == b'KEEP'
    try:
        sys.argv = [str(script), '--', str(input_path), '--out', str(fresh_path)]
        mod.main()
    finally:
        sys.argv = original_argv
    assert fresh_path.is_file() and fresh_path.stat().st_size > 0

print('posed-surface import checks passed')
