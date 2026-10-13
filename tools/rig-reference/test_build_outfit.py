"""Blender integration checks for manifest-driven original outfit builds."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import bpy
sys.path.insert(0,str(Path(__file__).resolve().parent))
import build_outfit as workflow
from build_outfit import build,validate
from import_reference import import_capture


def fixture():
    identity=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]
    vertices=[[0,0,0],[.1,0,0],[0,.1,0],[.1,.1,0]]
    return {'schemaVersion':1,'coordinateSystem':'unity-left-handed-y-up','matrixLayout':'row-major','rootName':'Synthetic rig',
      'nodes':[{'name':'Root','path':'Root[0]','parent':-1,'restMatrix':identity,'poseMatrix':identity}],
      'meshes':[{'name':'Synthetic body','rendererToRoot':identity,'vertices':vertices,'normals':[[0,0,1]]*4,
        'uv':[[0,0],[1,0],[0,1],[1,1]],'triangles':[0,1,2,2,1,3],'bones':[0],'bindPoses':[identity],
        'weights':[{'indices':[0,0,0,0],'values':[1,0,0,0]} for _ in vertices],
        'bakedVertices':vertices}]}


def rejects(action):
    try: action()
    except ValueError: return
    raise AssertionError('Invalid build accepted')


def main():
    with tempfile.TemporaryDirectory(prefix='progenitor-outfit-test-') as temp:
        root=Path(temp);data=fixture();import_capture(data,.001)
        body=bpy.data.objects['Synthetic body']
        garment=body.copy();garment.data=body.data.copy();garment.name='Original synthetic garment'
        bpy.context.collection.objects.link(garment);garment['referenceOnly']=False
        garment['wearablePaletteSource']='Chest'
        garment['wearablePaletteMode']='authored'
        # This is explicitly original synthetic test geometry, never native body triangles.
        garment.data.materials.append(bpy.data.materials.new('cloth'))
        for vertex in garment.data.vertices: vertex.co.y-=.003
        region=garment.vertex_groups.new(name='TransferRegion');region.add(list(range(4)),1,'REPLACE')
        (root/'reference.json').write_text(json.dumps(data))
        bpy.ops.wm.save_as_mainfile(filepath=str(root/'input.blend'))
        config={'schemaVersion':1,'scene':'input.blend','reference':'reference.json','body':'Synthetic body','poses':['reference.json'],
                'garments':[{'id':'unchanged','object':garment.name},
                            {'id':'loose','object':garment.name,'fit':{'weightsOnly':True,'weightRegion':'TransferRegion','maxDistanceMm':50}}]}
        manifest=root/'outfit.json';manifest.write_text(json.dumps(config))
        report=build(manifest,root/'result')
        assert report['status']=='requires-visual-review' and report['coverageApproved'] is False
        assert report['garments'][0]['fitRequiresReview'] is None
        assert report['garments'][1]['fitRequiresReview'] is False
        assert report['garments'][1]['fitDiagnostics']['requiresReview'] is False
        first=json.loads((root/'result/package/garments/unchanged.json').read_text())
        second=json.loads((root/'result/package/garments/loose.json').read_text())
        assert first['paletteSource']==second['paletteSource']=='Chest', 'Build lost palette source metadata'
        assert first['paletteMode']==second['paletteMode']=='authored', 'Build lost palette mode metadata'
        assert first['vertices']==second['vertices'],'Weights-only changed loose garment geometry'
        package=json.loads((root/'result/package/manifest.json').read_text())
        assert len(package['garments'])==2 and all(len(x['sha256'])==64 for x in package['garments'])
        assert not list((root/'result/package').rglob('*.blend'))
        assert (root/'result/authoring.blend').is_file()
        resume_path=root/'result/outfit.json'
        resume=json.loads(resume_path.read_text())
        assert resume['scene']=='authoring.blend' and resume['reference']==str((root/'reference.json').resolve())
        assert resume['poses']==[str((root/'reference.json').resolve())]
        assert resume['body']=='Synthetic body'
        assert resume['garments']==[{'id':item['id'],'object':item['outputObject']} for item in report['garments']]
        assert all('fit' not in item for item in resume['garments'])
        assert any(item['sourceObject']!=item['outputObject'] for item in report['garments'] if item['fitApplied'])
        assert {'file':'outfit.json','sha256':workflow.sha(resume_path)} in report['artifacts']
        resumed=build(resume_path,root/'resumed')
        assert all(item['fitRequiresReview'] is None for item in resumed['garments']), 'Resume does not reassess the original fitting operation'
        assert json.loads((root/'resumed/package/garments/loose.json').read_text())['paletteSource']=='Chest'
        assert json.loads((root/'resumed/package/garments/loose.json').read_text())['paletteMode']=='authored'
        assert [item['sha256'] for item in resumed['artifacts'] if item['file'].startswith('package/garments/')]==[
            item['sha256'] for item in report['artifacts'] if item['file'].startswith('package/garments/')]
        assert json.loads((root/'result/reports/loose.fit.json').read_text())['maxMovementMeters']<1e-9
        rejects(lambda:build(manifest,root/'result'))
        original_sha,original_check=workflow.sha,workflow.check_poses
        changed=[False]
        def changing_check(*args,**kwargs):
            result=original_check(*args,**kwargs);changed[0]=True
            return result
        def simulated_sha(path):
            if changed[0] and Path(path).name=='fit_garment.py': return '0'*64
            return original_sha(path)
        try:
            workflow.check_poses=changing_check;workflow.sha=simulated_sha
            rejects(lambda:build(manifest,root/'source-changed'))
            assert changed[0], 'The simulated source edit must occur during pose verification'
            assert not (root/'source-changed').exists() and not list(root.glob('.source-changed-building-*'))
        finally:
            workflow.sha=original_sha;workflow.check_poses=original_check

        for mutation in [lambda c:c.update(unknown=True),lambda c:c['garments'][0].update(id='../escape'),
                         lambda c:c['garments'][1].update(id='unchanged'),lambda c:c['garments'][1]['fit'].update(weightsOnly='true'),
                         lambda c:c['garments'][1]['fit'].update(maxDistanceMm=float('inf')),
                         lambda c:c['garments'][1]['fit'].update(unknown=1)]:
            bad=copy.deepcopy(config);mutation(bad);rejects(lambda:validate(bad))
        bad=copy.deepcopy(config);bad['garments'][0]['object']='Synthetic body';manifest.write_text(json.dumps(bad))
        rejects(lambda:build(manifest,root/'rejected'))
        assert not (root/'rejected').exists() and not list(root.glob('.rejected-building-*'))
        print('OUTFIT_BUILD_PASS: unchanged + weights-only, resumable output hashes, original-only guard, strict manifest, atomic failure')

if __name__=='__main__': main()
