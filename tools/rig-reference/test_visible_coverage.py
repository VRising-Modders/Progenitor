"""Small synthetic checks for directional body-first sampling."""
import sys
from pathlib import Path
from mathutils import Vector
sys.path.insert(0, str(Path(__file__).resolve().parent))
from check_visible_coverage import sample


def plane(x0, x1, y):
    vertices = [Vector((x0,y,0)), Vector((x1,y,0)),
                Vector((x1,y,1)), Vector((x0,y,1))]
    return vertices, [(0,1,2), (0,2,3)]


body, body_faces = plane(0,1,0)
front, front_faces = plane(0,.5,-.1)
result = sample(body,body_faces,front,front_faces,['front'],(.25,.75,.25,.75),.5,.0005,10)['front']
assert result['gridCells'] == 4 and result['bodyHits'] == 4
assert result['bodyFirst'] == 2 and result['garmentMisses'] == 2

back, back_faces = plane(0,1,.1)
result = sample(body,body_faces,back,back_faces,['front'],(.25,.75,.25,.75),.5,.0005,10)['front']
assert result['bodyFirst'] == 4 and result['garmentMisses'] == 0
assert abs(result['maxBodyFirstGapMm'] - 100) < .001

try:
    sample(body,body_faces,front,front_faces,['front'],(.25,.75,.25,.75),.5,.0005,3)
except ValueError as error:
    assert 'max-cells' in str(error)
else:
    raise AssertionError('Expected grid limit rejection')

# A partial sole must reveal uncovered foot samples from below, while a
# surface above the foot cannot pass as sole coverage.
foot = [Vector((v.x, v.z, 0)) for v in body]
sole = [Vector((v.x, v.z, -.1)) for v in front]
result = sample(foot,body_faces,sole,front_faces,['bottom'],(.25,.75,.25,.75),.5,.0005,10)['bottom']
assert result['bodyHits'] == 4 and result['bodyFirst'] == 2 and result['garmentMisses'] == 2
upper = [Vector((v.x, v.y, .1)) for v in foot]
result = sample(foot,body_faces,upper,body_faces,['bottom','top'],(.25,.75,.25,.75),.5,.0005,10)
assert result['bottom']['bodyFirst'] == 4 and result['bottom']['garmentMisses'] == 0
assert result['top']['bodyFirst'] == 0 and result['top']['bodyHits'] == 4
assert abs(result['bottom']['maxBodyFirstGapMm'] - 100) < .001

print('VISIBLE_COVERAGE_TEST_PASS')
