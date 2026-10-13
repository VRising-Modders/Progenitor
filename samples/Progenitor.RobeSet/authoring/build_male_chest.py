"""Build an original measured male robe chest from a retargeted chest scene.

Run with Blender in background mode and pass --scene, --reference, --garment,
and --out after Blender's ``--`` argument separator. The output directory
must be new. No native body triangles enter the garment mesh.
"""
import argparse, bpy, json, math, sys
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
repo=Path(__file__).resolve().parents[3]
sys.path.insert(0,str(repo/'tools'/'rig-reference'))
from import_reference import C
from fit_garment import barycentric
from export_garment import export_garment
from retarget_garment import build_scene
from check_poses import check_poses
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--scene',type=Path,required=True,help='Editable retargeted original chest scene')
parser.add_argument('--reference',type=Path,required=True,help='Captured target rest skeleton and body')
parser.add_argument('--garment',required=True,help='Exact exportable chest object name in --scene')
parser.add_argument('--out',type=Path,required=True,help='New output directory for chest.blend, chest.json and report.json')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
if args.out.exists():raise FileExistsError('Output directory already exists: '+str(args.out))
bpy.ops.wm.open_mainfile(filepath=str(args.scene.resolve()))
body=bpy.data.objects.get('Body_GEO')
garment=bpy.data.objects.get(args.garment)
if body is None or body.type!='MESH' or not body.get('referenceOnly',False):raise ValueError('Scene must contain referenceOnly Body_GEO')
if garment is None:raise ValueError('Garment object absent from scene: '+args.garment)
rig=next((m.object for m in garment.modifiers if m.type=='ARMATURE'),None)
if rig is None:raise ValueError('Garment must have target armature modifier')
reference=json.loads(args.reference.read_text(encoding='utf-8-sig'))
native=next(m for m in reference['meshes'] if m['name']=='Body_GEO')
# This checks rest vertices, triangle order, skin weights, captured bone paths,
# and bind matrices before body vertex indices are used for weight transfer.
check_poses(garment,body,[(str(args.reference),reference)])
original=export_garment(garment,reference)
output=json.loads(json.dumps(original));output['cloth']=[];output['gold']=[]
for surface in ('cloth','gold'):
 idx=original[surface]
 for k in range(0,len(idx),3):
  tri=idx[k:k+3]
  if max(original['vertices'][i][1] for i in tri)<=1.05:output[surface].extend(tri)
body_to_rig=rig.matrix_world.inverted() @ body.matrix_world
if body_to_rig.determinant()<=0:raise ValueError('Body-to-rig transform must not reflect geometry')
points=[body_to_rig @ v.co for v in body.data.vertices]
body.data.calc_loop_triangles();triangles=[tuple(t.vertices) for t in body.data.loop_triangles]
tree=BVHTree.FromPolygons(points,triangles,all_triangles=True)
node_by_path={n['path']:n for n in reference['nodes']}
palette={b['path']:i for i,b in enumerate(output['bones'])}
def slot_for_node(index):
 node=reference['nodes'][index];path=node['path']
 if path not in palette:
  if node['restMatrix'] is None:raise ValueError('Missing measured target bind '+node['name'])
  palette[path]=len(output['bones']);output['bones'].append({k:node[k] for k in ('name','path','restMatrix')})
 return palette[path]
def skin(point):
 hit,normal,face,distance=tree.find_nearest(point,.15)
 if hit is None:raise ValueError('No target body near generated garment point')
 nearest_skin_distances.append(distance)
 tri=triangles[face];factors=barycentric(hit,*(points[i] for i in tri));weights={}
 for body_index,factor in zip(tri,factors):
  source=native['weights'][body_index]
  for bone_index,value in zip(source['indices'],source['values']):
   if value:
    slot=slot_for_node(native['bones'][bone_index]);weights[slot]=weights.get(slot,0)+factor*value
 ordered=sorted(weights.items(),key=lambda x:-x[1])[:4];total=sum(v for _,v in ordered)
 if total<=0:raise ValueError('Missing body skin')
 return {'indices':[i for i,_ in ordered]+[0]*(4-len(ordered)), 'values':[v/total for _,v in ordered]+[0.]*(4-len(ordered))}
nearest_skin_distances=[]
def add_triangle(a,b,c,uvs,normals):
 for p,uv,n in zip((a,b,c),uvs,normals):
  i=len(output['vertices']);output['vertices'].append(list(C.inverted()@p));output['normals'].append(list((C.inverted().to_3x3()@n).normalized()));output['uv'].append(uv);output['weights'].append(skin(p));output['cloth'].append(i)
def body_radius(origin,direction,limit,fallback,farthest=False):
 if not farthest:
  point,normal,face,distance=tree.ray_cast(origin,direction,limit)
  return distance if point is not None and distance>0 else fallback
 travelled=0.;current=origin.copy();far=None
 for _ in range(12):
  point,normal,face,distance=tree.ray_cast(current,direction,limit-travelled)
  if point is None:break
  travelled+=distance;far=travelled
  current=point+direction*.0002;travelled+=.0002
  if travelled>=limit:break
 return far if far is not None and far>0 else fallback
# Body measured rings: 27 heights x 40 angles. A procedural quad grid is new cloth topology.
# Measured continuous torso ellipses, following the neck center as it moves back.
def smoothstep(x):
 x=max(0.,min(1.,x));return x*x*(3-2*x)
steps=48;hs=[.98+i*(.67/32) for i in range(33)]
raw=[]
for h in hs:
 sample=[p for p in points if abs(p.z-h)<.035 and abs(p.x)<.29]
 if len(sample)<8:raise ValueError('Insufficient body slice at '+str(h))
 depth_min=min(p.y for p in sample);depth_max=max(p.y for p in sample)
 cy=(depth_min+depth_max)/2
 cap=.215+.045*smoothstep((h-1.34)/.11)
 rx=min(max(abs(p.x) for p in sample),cap)+.008
 ry=(depth_max-depth_min)/2+.010
 shape=[p for p in sample if abs(p.x)<cap]
 scale=max(1.,min(1.22,max(math.sqrt((p.x/rx)**2+((p.y-cy)/ry)**2) for p in shape)))
 raw.append((cy,rx*scale+.002,ry*scale+.002))
params=[]
for i in range(len(raw)):
 lo=max(0,i-1);hi=min(len(raw),i+2)
 cy,rx,ry=(sum(raw[j][k] for j in range(lo,hi))/(hi-lo) for k in range(3))
 # Ease the narrow natural waist into the retained loose skirt/belt.
 flare=smoothstep((1.16-hs[i])/.18)
 params.append((cy,rx+.022*flare,ry+.008*flare))
rings=[]
for h,(cy,rx,ry) in zip(hs,params):
 ring=[]
 for j in range(steps):
  theta=2*math.pi*j/steps
  ring.append(Vector((rx*math.cos(theta),cy+ry*math.sin(theta),h)))
 rings.append(ring)
# Follow both tangents of the actual cloth surface. Horizontal ellipse normals
# omit the shoulder/chest slope and make those surfaces shade like vertical walls.
ring_normals=[]
for i,ring in enumerate(rings):
 row=[]
 for j in range(steps):
  around=ring[(j+1)%steps]-ring[(j-1)%steps]
  along=rings[min(i+1,len(rings)-1)][j]-rings[max(i-1,0)][j]
  normal=around.cross(along)
  if normal.length<1e-10:raise ValueError('Degenerate torso normal')
  row.append(normal.normalized())
 ring_normals.append(row)
for i in range(len(rings)-1):
 for j in range(steps):
  k=(j+1)%steps;a,b,c,d=rings[i][j],rings[i][k],rings[i+1][k],rings[i+1][j]
  na=ring_normals[i][j];nb=ring_normals[i][k]
  nc=ring_normals[i+1][k];nd=ring_normals[i+1][j]
  uc=j/steps;un=(j+1)/steps;vl=i/(len(rings)-1);vn=(i+1)/(len(rings)-1)
  add_triangle(a,b,c,[(uc,vl),(un,vl),(un,vn)],[na,nb,nc]);add_triangle(a,c,d,[(uc,vl),(un,vn),(uc,vn)],[na,nc,nd])
def bone(name):return next(b.matrix_local.translation.copy() for b in rig.data.bones if b.name.endswith(':'+name))
# Radial tubes follow measured shoulder, elbow, wrist axes; leave hands uncovered.
for side in ('Left','Right'):
 axis=[bone(side+'_Shoulder_JNT'),bone(side+'_Elbow_JNT'),bone(side+'_Wrist_JNT')]
 axis[0]+=Vector((.035 if side=='Left' else -.035,0,.07))
 sleeve=[];ns=20
 for segment in range(2):
  start,end=axis[segment:segment+2];direction=(end-start).normalized();u=direction.cross(Vector((0,0,1))).normalized();v=direction.cross(u).normalized()
  for step in range(12):
   t=step/12;origin=start.lerp(end,t);ring=[]
   for j in range(ns):
    theta=2*math.pi*j/ns;radial=(u*math.cos(theta)+v*math.sin(theta)).normalized()
    near=[p for p in points if (p-origin).length<.13 and (p-origin).dot(radial)>0 and abs((p-origin).dot(direction))<.05]
    fallback=max((p-origin).dot(radial) for p in near) if near else .07
    radius=min(.15,body_radius(origin,radial,.16,fallback)+.011)
    ring.append((origin+radial*radius,radial))
   sleeve.append(ring)
 # endpoint ring at wrist
 origin=axis[-1];direction=(axis[-1]-axis[-2]).normalized();u=direction.cross(Vector((0,0,1))).normalized();v=direction.cross(u).normalized();ring=[]
 for j in range(ns):
  theta=2*math.pi*j/ns;radial=(u*math.cos(theta)+v*math.sin(theta)).normalized();near=[p for p in points if (p-origin).length<.13 and (p-origin).dot(radial)>0];fallback=max((p-origin).dot(radial) for p in near) if near else .06
  ring.append((origin+radial*min(.12,body_radius(origin,radial,.13,fallback)+.009),radial))
 sleeve.append(ring)
 for i in range(len(sleeve)-1):
  for j in range(ns):
   k=(j+1)%ns;a,na=sleeve[i][j];b,nb=sleeve[i][k];c,nc=sleeve[i+1][k];d,nd=sleeve[i+1][j]
   uc=j/ns;un=(j+1)/ns;vl=i/(len(sleeve)-1);vn=(i+1)/(len(sleeve)-1)
   add_triangle(a,b,c,[(uc,vl),(un,vl),(un,vn)],[na,nb,nc]);add_triangle(a,c,d,[(uc,vl),(un,vn),(uc,vn)],[na,nc,nd])
if len(output['vertices'])>=60000:raise ValueError('Generated garment exceeds runtime vertex limit')
skirt_triangles=len(output['cloth'])//3-(len(rings)-1)*steps*2-2*(24*ns*2)
args.out.mkdir(parents=True)
object_name='Original male measured ellipse chest'
build_scene(output,reference,object_name,args.out/'chest.blend')
# The scene exporter splits loop corners and discards unused source upper-body
# vertices, so the JSON is compact and matches the editable scene exactly.
compact=export_garment(bpy.data.objects[object_name],reference)
with (args.out/'chest.json').open('x',encoding='utf-8') as stream:
 json.dump(compact,stream,allow_nan=False,separators=(',',':'))
report={'objectName':object_name,'originalSkirtTriangles':skirt_triangles,
        'newBodiceTriangles':(len(rings)-1)*steps*2,
        'newSleeveTriangles':2*24*ns*2,'vertices':len(compact['vertices']),
        'maxTransferredSkinDistanceMm':round(max(nearest_skin_distances)*1000,3),
        'method':'original analytic measured rings; no native triangles'}
(args.out/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('LOFT_PASS',len(compact['vertices']),len(compact['cloth'])//3)
