"""Build an original outfit package offline; every successful build still needs visual review."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import bpy
# Snapshot source before importing workflow functions, so reports identify the code
# loaded into this Blender process rather than a later edit on disk.
TOOL_PATHS = tuple(Path(__file__).with_name(name) for name in
                   ('build_outfit.py','fit_garment.py','check_poses.py','export_garment.py','import_reference.py'))
LOADED_TOOL_HASHES = tuple((path, hashlib.sha256(path.read_bytes()).hexdigest()) for path in TOOL_PATHS)
sys.path.insert(0, str(Path(__file__).resolve().parent))
from fit_garment import fit
from check_poses import check_poses
from export_garment import export_garment


def keys(value, allowed, required):
    if not isinstance(value, dict) or set(value)-set(allowed) or set(required)-set(value):
        raise ValueError('Missing or unknown configuration keys: '+str(value))


def string(value):
    if not isinstance(value, str) or not value.strip():
        raise ValueError('Expected a nonempty string')
    return value


def number(value, positive=False):
    if type(value) not in (int,float) or not math.isfinite(value) or value < 0 or (positive and value == 0):
        raise ValueError('Expected a finite '+('positive' if positive else 'nonnegative')+' number')
    return value


def read_json(path):
    def unique(pairs):
        result={}
        for k,v in pairs:
            if k in result: raise ValueError('Duplicate JSON key: '+k)
            result[k]=v
        return result
    return json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=unique,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError('Nonfinite JSON: '+value)))


def validate(config):
    keys(config, ('schemaVersion','scene','reference','body','poses','garments'), ('schemaVersion','scene','reference','body','poses','garments'))
    if type(config['schemaVersion']) is not int or config['schemaVersion'] != 1: raise ValueError('Unsupported manifest version')
    for k in ('scene','reference','body'): string(config[k])
    if not isinstance(config['poses'],list) or not config['poses']: raise ValueError('At least one captured pose is required')
    for p in config['poses']: string(p)
    if not isinstance(config['garments'],list) or not config['garments']: raise ValueError('At least one original garment is required')
    ids=set()
    for garment in config['garments']:
        keys(garment,('id','object','fit'),('id','object'))
        identifier=string(garment['id']);string(garment['object'])
        if not re.fullmatch(r'[a-z][a-z0-9_-]{0,63}',identifier) or identifier in ids or identifier.upper() in {'CON','PRN','AUX','NUL',*(f'COM{i}' for i in range(1,10)),*(f'LPT{i}' for i in range(1,10))}:
            raise ValueError('Garment IDs must be unique portable filenames')
        ids.add(identifier)
        if 'fit' in garment:
            f=garment['fit'];keys(f,('region','weightRegion','weightsOnly','bodyRegion','clearanceMm','maxDistanceMm'),())
            for key in ('region','weightRegion','bodyRegion'):
                if key in f: string(f[key])
            if 'weightsOnly' in f and type(f['weightsOnly']) is not bool: raise ValueError('weightsOnly must be boolean')
            if f.get('weightsOnly',False) and ('weightRegion' not in f or 'region' in f):
                raise ValueError('Weights-only requires weightRegion and forbids an unused region')
            if not f.get('weightsOnly',False) and 'region' not in f: raise ValueError('Surface fitting requires an explicit region')
            number(f.get('clearanceMm',6));number(f.get('maxDistanceMm',50),True)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, data):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(data,indent=2,allow_nan=False),encoding='utf-8')


def build(manifest, output):
    manifest=Path(manifest).resolve();output=Path(output).resolve()
    if any(sha(path) != digest for path,digest in LOADED_TOOL_HASHES):
        raise ValueError('Workflow source changed since loading; restart Blender before building')
    if output.exists(): raise ValueError('Output directory must not already exist')
    config=read_json(manifest);validate(config)
    resolve=lambda value:(manifest.parent/value).resolve()
    scene=resolve(config['scene']);reference=resolve(config['reference']);pose_paths=[resolve(p) for p in config['poses']]
    if scene.suffix.lower() != '.blend': raise ValueError('Scene must be a .blend file')
    inputs=[manifest,scene,reference,*pose_paths]
    for path in inputs:
        if not path.is_file(): raise ValueError('Missing input: '+str(path))
    provenance=[{'path':str(p),'sha256':sha(p)} for p in dict.fromkeys(inputs)]
    reference_data=read_json(reference);captures=[(str(p),read_json(p)) for p in pose_paths]
    bpy.ops.wm.open_mainfile(filepath=str(scene),load_ui=False,use_scripts=False)
    body=bpy.data.objects.get(config['body'])
    if body is None or body.type != 'MESH': raise ValueError('Body reference object is missing')
    output.parent.mkdir(parents=True,exist_ok=True)
    stage=Path(tempfile.mkdtemp(prefix='.'+output.name+'-building-',dir=str(output.parent))).resolve()
    try:
        entries=[];reports=[]
        for spec in config['garments']:
            identifier=spec['id'];obj=bpy.data.objects.get(spec['object'])
            if obj is None or obj == body or obj.type != 'MESH' or obj.get('referenceOnly',False):
                raise ValueError(identifier+': select an original-owned garment, never a body/reference mesh')
            # Run all export provenance/skinning guards even if fitting is requested.
            export_garment(obj,reference_data)
            fit_report=None
            if 'fit' in spec:
                f=spec['fit']
                obj,fit_report=fit(obj,body,None if f.get('weightsOnly',False) else f['region'],f.get('weightRegion'),
                    f.get('clearanceMm',6)/1000,f.get('maxDistanceMm',50)/1000,
                    'Outfit build '+identifier,f.get('bodyRegion'))
                write_json(stage/'reports'/f'{identifier}.fit.json',fit_report)
            poses=check_poses(obj,body,captures)
            write_json(stage/'reports'/f'{identifier}.poses.json',poses)
            garment=stage/'package'/'garments'/f'{identifier}.json'
            write_json(garment,export_garment(obj,reference_data))
            entries.append({'id':identifier,'file':f'garments/{identifier}.json','sha256':sha(garment)})
            reports.append({'id':identifier,'sourceObject':spec['object'],'outputObject':obj.name,
                            'fitApplied':fit_report is not None,'poseCount':len(poses['poses']),
                            'fitRequiresReview':fit_report.get('requiresReview',False) if fit_report is not None else None,
                            'fitDiagnostics':fit_report.get('distortion') if fit_report is not None else None})
        write_json(stage/'package'/'manifest.json',{'schemaVersion':1,'garments':entries})
        bpy.ops.wm.save_as_mainfile(filepath=str(stage/'authoring.blend'))
        # The saved scene contains the fitted output objects. Resume from those
        # objects so a later build cannot accidentally fit or export the source.
        write_json(stage/'outfit.json',{
            'schemaVersion':1,
            'scene':'authoring.blend',
            'reference':str(reference),
            'body':config['body'],
            'poses':[str(path) for path in pose_paths],
            'garments':[{'id':item['id'],'object':item['outputObject']} for item in reports],
        })
        # Detect inputs changed during the build instead of publishing misleading provenance.
        if any(sha(Path(p['path'])) != p['sha256'] for p in provenance): raise ValueError('An input changed during build')
        artifacts=[{'file':str(p.relative_to(stage)).replace('\\','/'),'sha256':sha(p)} for p in sorted(stage.rglob('*')) if p.is_file()]
        if any(sha(path) != digest for path,digest in LOADED_TOOL_HASHES):
            raise ValueError('Workflow source changed during build; refusing publication')
        report={'schemaVersion':1,'status':'requires-fit-review' if any(item['fitRequiresReview'] for item in reports) else 'requires-visual-review',
                'coverageApproved':False,'blenderVersion':bpy.app.version_string,
                'inputs':provenance,'tools':[{'file':p.name,'sha256':digest} for p,digest in LOADED_TOOL_HASHES], 'garments':reports,'artifacts':artifacts,
                'limitations':'Captured-pose diagnostics do not prove combat coverage. Only original garment JSON belongs in the package. Preserve the input scene and local authoring.blend with the original sources; these scenes contain body references and are not package redistribution assets.'}
        write_json(stage/'build-report.json',report)
        if output.exists(): raise ValueError('Output appeared during build; refusing replacement')
        os.rename(stage,output)
        return report
    except BaseException:
        # Only remove the newly allocated staging directory, never caller input/output.
        if stage.parent == output.parent and stage.name.startswith('.'+output.name+'-building-'):
            shutil.rmtree(stage)
        raise


def main():
    parser=argparse.ArgumentParser();parser.add_argument('manifest');parser.add_argument('--out',required=True)
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    print(json.dumps(build(args.manifest,args.out),allow_nan=False))

if __name__=='__main__': main()
