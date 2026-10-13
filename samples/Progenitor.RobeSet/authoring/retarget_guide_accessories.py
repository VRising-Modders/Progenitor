"""Build original Guide shoe/face candidates against explicit captured bind references.

Run with Blender. Captures remain local; only the original garment JSON is distributable.
The face adjustments are measured male Guide settings, not an automatic hood fitter.
"""
import argparse
import json
import sys
from pathlib import Path

from mathutils import Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'tools' / 'rig-reference'))
from retarget_garment import retarget, build_scene


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-reference', required=True)
    parser.add_argument('--target-reference', required=True)
    parser.add_argument('--garments', default=str(Path(__file__).resolve().parents[1] / 'garments'))
    parser.add_argument('--out', required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    out = Path(args.out).resolve()
    if out.exists():
        raise ValueError('Use a new output directory')
    source = json.loads(Path(args.source_reference).read_text(encoding='utf-8-sig'))
    target = json.loads(Path(args.target_reference).read_text(encoding='utf-8-sig'))
    results = {}
    for name in ('boots', 'face'):
        garment = json.loads((Path(args.garments) / (name + '.json')).read_text(encoding='utf-8-sig'))
        result, report = retarget(garment, source, target)
        if name == 'face':
            # Small width/forward adjustments to the original black insert only.
            for vertex in result['vertices']:
                vertex[0] *= 1.02
                vertex[2] += .003
            for index, normal in enumerate(result['normals']):
                result['normals'][index] = list(Vector((normal[0] / 1.02, normal[1], normal[2])).normalized())
            report['insertAdjustment'] = {'widthScale': 1.02, 'forwardMm': 3}
        results[name] = (result, report)
    out.mkdir(parents=True)
    for name, (result, report) in results.items():
        (out / (name + '-male.json')).write_text(json.dumps(result, allow_nan=False, separators=(',', ':')), encoding='utf-8')
        build_scene(result, target, 'Original Guide ' + name + ' male', out / (name + '.blend'))
        (out / (name + '-report.json')).write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('GUIDE_ACCESSORIES_RETARGETED: candidates require visual and pose review')


if __name__ == '__main__':
    main()
