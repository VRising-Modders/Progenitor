import bpy, math, json, argparse, sys
from pathlib import Path
parser=argparse.ArgumentParser(description='Original female robe hood authoring example; requires the matching captured outfit scene.')
parser.add_argument('--scene',type=Path,required=True)
parser.add_argument('--reference',type=Path,required=True)
parser.add_argument('--out',type=Path,required=True)
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
P=args.out.resolve()
if P.exists(): raise ValueError('Output directory already exists')
P.mkdir(parents=True)

bpy.ops.wm.open_mainfile(filepath=str(args.scene.resolve()),load_ui=False,use_scripts=False)
# Previous builder scenes contain the source and the fitted result. Preserve the
# result rather than silently exporting the pre-fit chest during a hood-only edit.
fitted=bpy.data.objects.get('Outfit build chest')
if fitted is not None:
    source=bpy.data.objects.get('Original robe chest')
    if source is not None:bpy.data.objects.remove(source,do_unlink=True)
    fitted.name='Original robe chest'
old=bpy.data.objects['Original robe hood']; rig=next(m.object for m in old.modifiers if m.type=='ARMATURE'); coll=old.users_collection[0]
head=next(g.name for g in old.vertex_groups if 'Head_JNT' in g.name)
neck=next(b.name for b in rig.data.bones if 'NeckBase_JNT' in b.name)
bpy.data.objects.remove(old,do_unlink=True)
cloth=next(m for m in bpy.data.materials if m.get('wearableSurface')=='cloth').copy()
void=next(m for m in bpy.data.materials if m.get('wearableSurface')=='void').copy()
cloth.name='Hood cloth';void.name='Hood black recess'
cloth.diffuse_color=(0,84/255,182/255,1);void.diffuse_color=(0,0,0,1)
cloth['wearablePalette']='primary'
if 'wearablePalette' in void:del void['wearablePalette']
# A compact shaded face ends under the chin. The gathered cloth below it is
# separate from the void; it must not stretch the face down onto the chest.
opening=[(.003,1.690),(.036,1.683),(.067,1.671),(.074,1.638),(.060,1.595),(.037,1.574),(.009,1.568),(-.027,1.579),(-.058,1.602),(-.072,1.636),(-.068,1.674),(-.039,1.687),(-.018,1.695)]
outer=[(.003,1.824),(.040,1.804),(.078,1.750),(.091,1.650),(.107,1.547),(.105,1.505),(.061,1.478),(-.025,1.472),(-.098,1.502),(-.108,1.550),(-.094,1.633),(-.079,1.752),(-.039,1.806)]
N=104;vs=[];fs=[];mats=[]
def curve(points,u):
    a=u*len(points);i=int(a)%len(points);t=a-int(a)
    ps=[points[(i+k)%len(points)] for k in (-1,0,1,2)]
    return tuple(.5*(2*ps[1][d]+(-ps[0][d]+ps[2][d])*t+(2*ps[0][d]-5*ps[1][d]+4*ps[2][d]-ps[3][d])*t*t+(-ps[0][d]+3*ps[1][d]-3*ps[2][d]+ps[3][d])*t*t*t) for d in range(2))
def vertex(x,y,z):vs.append((x,y,z));return len(vs)-1
def connect(a,b,mat=0):
    for i in range(N):fs.append((a[(i+1)%N],b[(i+1)%N],b[i],a[i]));mats.append(mat)
rings=[]
# Face-to-crown. A low brow overhang and crease valleys replace the thick oval rim.
for t in [0,.012]+[i/24 for i in range(1,25)]:
    r=[]
    for i in range(N):
        u=i/N;x0,z0=curve(opening,u);x1,z1=curve(outer,u)
        x=x0*(1-t)+x1*t;z=z0*(1-t)+z1*t
        side=max(0,1-abs(z-1.54)/.19)
        fold=math.sin(6*math.pi*u+1.1*t)*.010+math.sin(10*math.pi*u-.7*t)*.004
        # The brow falls almost vertically before returning over the crown.
        upper_drape=max(0,min(1,(z0-1.64)/.045))
        sweep=t*(1-upper_drape)+t*t*upper_drape
        y=-.088+.143*sweep + math.sin(math.pi*t)*fold*(.35+.65*side)
        # Let the lower fabric fall forward across the neck, not form a chin torus.
        y-=max(0,(1.61-z)/.21)*.075*t
        lower=max(0,min(1,(1.68-z)/.18))
        crease=math.sin(math.pi*t*2.4+u*8)*math.sin(math.pi*t)
        y+=lower*.023*crease
        z-=lower*.018*math.sin(math.pi*t)**2
        # Broad hanging U-folds, not fine crumpling or a padded rim.
        hanging=0.0
        for height,depth in ((1.505,.012),(1.537,.009),(1.571,.006)):
            delta=z-(height+.32*abs(x))
            hanging+=depth*math.exp(-(delta/.009)**2)
        y+=hanging*math.sin(math.pi*t)
        # Shallow temple/crown creases break the smooth helmet-like shell.
        upper=max(0,min(1,(z-1.61)/.08))
        y+=upper*.007*math.sin(u*10*math.pi+.4)*math.sin(math.pi*t)**2
        r.append(vertex(x,y,z))
    if rings:connect(rings[-1],r)
    rings.append(r)
for scale,depth in ((.98,.083),(.87,.118),(.61,.140),(.27,.146)):
    r=[]
    for i in range(N):
        u=i/N;x,z=curve(outer,u)
        x*=scale;z=1.626+(z-1.626)*scale
        y=depth+.006*math.sin(u*8*math.pi)*(1-scale)
        r.append(vertex(x,y,z))
    connect(rings[-1],r);rings.append(r)
c=vertex(0,.149,1.626)
crown_cap_start=len(fs)
for i in range(N):fs.append((rings[-1][(i+1)%N],c,rings[-1][i]));mats.append(0)
# Turned cloth edge, only 2 mm thick; a continuous unlit recess shares its outline.
lip=[]
for index in rings[0]:
    x,y,z=vs[index];lip.append(vertex(x*.988,y+.002,1.63+(z-1.63)*.988))
lip_start=len(fs)
connect(lip,rings[0])
previous=lip
void_start=len(fs)
for scale,depth in ((1,.002),(.85,.004),(.45,.006)):
    r=[]
    for index in lip:
        x,y,z=vs[index];r.append(vertex(x*scale,y+depth,1.63+(z-1.63)*scale))
    connect(r,previous,1);previous=r
c=vertex(0,-.080,1.63)
for i in range(N):fs.append((previous[i],c,previous[(i+1)%N]));mats.append(1)
def face_normal(face):
    a,b,c=(vs[index] for index in face[:3])
    ab=tuple(b[k]-a[k] for k in range(3));ac=tuple(c[k]-a[k] for k in range(3))
    return (ab[1]*ac[2]-ab[2]*ac[1],ab[2]*ac[0]-ab[0]*ac[2],ab[0]*ac[1]-ab[1]*ac[0])
# Blender -Y is the viewing side; the back of the crown faces +Y.
assert face_normal(fs[0])[1]<0, 'Hood shell must face outward at the brow'
assert face_normal(fs[crown_cap_start])[1]>0, 'Hood crown cap must face backward'
assert face_normal(fs[lip_start])[1]<0, 'Hood lip must face the viewer'
assert face_normal(fs[void_start+2*N-8])[1]<0, 'Hood recess must face the viewer'
assert face_normal(fs[-1])[1]<0, 'Hood recess cap must face the viewer'
mesh=bpy.data.meshes.new('Draped cloth hood');mesh.from_pydata(vs,[],fs);mesh.update()
mesh.materials.append(cloth);mesh.materials.append(void)
for face,mat in zip(mesh.polygons,mats):face.material_index=mat;face.use_smooth=True
uv=mesh.uv_layers.new(name='Hood UV')
for face in mesh.polygons:
    for li in face.loop_indices:
        x,y,z=vs[mesh.loops[li].vertex_index];uv.data[li].uv=(.5+x*2,(z-1.35)*2)
obj=bpy.data.objects.new('Original robe hood',mesh);coll.objects.link(obj);obj['referenceOnly']=False;obj['wearablePaletteSource']='Chest'
vg=obj.vertex_groups.new(name=head);ng=obj.vertex_groups.new(name=neck)
for i,(x,y,z) in enumerate(vs):
    nw=max(0,min(.85,(1.565-z)/.18))
    vg.add([i],1-nw,'REPLACE')
    if nw>0:ng.add([i],nw,'REPLACE')
mod=obj.modifiers.new('Captured rig','ARMATURE');mod.object=rig
bpy.ops.wm.save_as_mainfile(filepath=str(P/'source.blend'))
manifest={'schemaVersion':1,'scene':'source.blend','reference':str(args.reference.resolve()),'body':'Body_GEO','poses':[str(args.reference.resolve())],'garments':[{'id':n,'object':'Original robe '+o} for n,o in [('chest','chest'),('gloves','gloves'),('hood','hood'),('boots','boots')]]}
(P/'outfit.json').write_text(json.dumps(manifest,indent=2))
print('ROOT_HOOD',len(vs),'vertices',len(fs),'faces')
