import test from 'node:test';
import assert from 'node:assert/strict';
import { analyze, flood, key } from './planner.mjs';
function plot(w=12,h=10) {
  return { plot:1,heartLevel:2,floorLimit:150,floorCount:0,buildings:[],floors:[],
    blocks:Array.from({length:w*h},(_,i)=>({x:i%w,y:Math.floor(i/w),heights:[5,5,5,5,5]})) };
}
test('capacity and coverage are ceilings; only complete rooms',()=>{
  const s=plot(); s.floorCount=139;
  const p=analyze(s,{coverage:.8,entrances:['0,0']});
  assert.ok(p.newFloors<=11); assert.ok(p.rooms.length>0);
  assert.equal(p.newFloors,p.rooms.reduce((a,r)=>a+r.width*r.height,0));
  assert.equal(p.constructionReady,false);
});
test('no inferred entrance, no construction candidate',()=>{
  assert.equal(analyze(plot()).newFloors,0);
});
test('missing limits do not silently use defaults',()=>{
  assert.throws(()=>analyze({...plot(),floorLimit:null}));
});
test('a narrow corridor remains connected between approaches',()=>{
  const s=plot();
  s.blocks=s.blocks.filter(b=>b.x<4 || b.x>7 || b.y===4);
  const p=analyze(s,{coverage:1,entrances:['0,4','11,4']});
  const excluded=new Set(p.rooms.flatMap(r=>r.cells));
  const cells=new Map(p.blocks.map(c=>[key(c.x,c.y),c]));
  const reachable=flood(cells,'0,4',excluded);
  assert.ok(reachable.has('11,4'));
  for(const r of p.rooms)assert.ok(reachable.has(r.door.outside));
});
test('occupied footprints and upper floors are preserved',()=>{
  const s=plot();
  s.buildings=[{minX:40,minY:40,maxX:60,maxY:60,height:15}];
  const p=analyze(s,{coverage:1,entrances:['0,0']});
  assert.ok(p.rooms.every(r=>r.cells.every(c=>!['4,4','4,5','5,4','5,5'].includes(c))));
});
test('heart remains reserved even when absent from the buildings list',()=>{
  const s=plot(); s.heart={x:4,y:4};
  const p=analyze(s,{coverage:1,entrances:['0,0']});
  assert.ok(p.occupied.includes('4,4'));
  assert.ok(p.rooms.every(r=>!r.cells.includes('4,4')));
});
test('rooms do not bridge a cliff or use missing samples',()=>{
  const s=plot();
  for(const b of s.blocks) if(b.x>=6)b.heights=[10,10,10,10,10];
  s.blocks.find(b=>b.x===2&&b.y===2).heights=[null,5,5,5,5];
  const p=analyze(s,{coverage:1,entrances:['0,0']});
  assert.ok(p.rooms.every(r=>r.x+r.width<=6&&!r.cells.includes('2,2')));
});
test('too little space produces no partial room',()=>{
  const s=plot();s.floorLimit=3;
  assert.equal(analyze(s,{coverage:1,entrances:['0,0']}).newFloors,0);
});
test('invalid and occupied entrance refused',()=>{
  const s=plot();s.floors=[{x:5,y:5}];
  assert.throws(()=>analyze(s,{entrances:['0,0']}));
  assert.throws(()=>analyze(s,{coverage:NaN}));
});
test('deterministic plan across shuffled source order',()=>{
  const s=plot(),args={coverage:.6,entrances:['0,0','11,9']};
  assert.deepEqual(analyze(s,args).rooms,analyze({...s,blocks:[...s.blocks].reverse()},args).rooms);
});
test('every room cell connects to its doorway across allowed height steps',()=>{
  const s=plot(6,6);s.floorLimit=16;
  for(const b of s.blocks) {
    const h=b.y<4?[5,5.2,4.8,4.8,5,5][b.x]:5;
    b.heights=[h,h,h,h,h];
  }
  const p=analyze(s,{coverage:1,entrances:['5,5']});
  const cells=new Map(p.blocks.map(c=>[key(c.x,c.y),c]));
  for(const r of p.rooms) {
    const inside=new Map(r.cells.map(k=>[k,cells.get(k)]));
    assert.equal(flood(inside,r.door.inside).size,r.cells.length);
  }
});
