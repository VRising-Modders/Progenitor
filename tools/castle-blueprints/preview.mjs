import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const [input, output] = process.argv.slice(2);
if (!input || !output) throw Error('Usage: node preview.mjs snapshot-folder output.html');
const snapshots = fs.readdirSync(input).filter(n=>/^plot-\d+\.json$/.test(n))
  .map(n=>JSON.parse(fs.readFileSync(path.join(input,n),'utf8').replace(/^\uFEFF/,'')));
if(!snapshots.length)throw Error('No plot snapshots found');
const planner=fs.readFileSync(new URL('./planner.mjs',import.meta.url),'utf8').replaceAll('export ','');
const data=JSON.stringify(snapshots).replaceAll('<','\\u003c');
const html=`<!doctype html><html lang="en"><meta charset="utf-8">
<title>Castle blueprint investigation</title>
<style>
body{margin:0;background:#101923;color:#e5eaf0;font:16px system-ui}main{max-width:1400px;margin:auto;padding:28px}
h1{font-size:26px;margin:0 0 8px}p{line-height:1.5;color:#bccbd8}section{display:flex;gap:24px}
aside{width:320px;flex-shrink:0}canvas{width:100%;background:#17232e;border:1px solid #465869}
.drawing{flex:1;min-width:0}label{display:block;margin:18px 0 6px}select,input,button{font:inherit}
button,select{background:#243749;color:white;border:1px solid #678197;padding:8px}
#facts{white-space:pre-line;line-height:1.7}small{color:#b7c6d1}.warning{color:#ffd487}
</style><main><h1>Castle blueprint investigation</h1>
<p>Read-only planning preview. Click free ground to choose a candidate entrance; Shift-click to reserve another access block.
These selections need an in-game accessibility check before construction.</p>
<section><aside><label for="plot">Plot</label><select id="plot"></select>
<label for="coverage">Use up to <span id="pct">40</span>% of eligible free ground</label>
<input id="coverage" type="range" min="0" max="100" value="40">
<label><input id="empty" type="checkbox"> Hypothetical empty plot</label>
<small>Comparison only. Does not clear existing structures or modify the save.</small>
<p id="facts"></p><button id="download">Save candidate plan</button>
<p class="warning">A candidate plan is not permission or proof of safe placement.</p>
<details><summary>Checks still needed</summary><ul id="missing"></ul></details></aside>
<div class="drawing"><canvas id="map" width="1000" height="800"></canvas>
<p>Gray: existing construction · Green: room candidates · Gold: entrance/access reservations ·
Blue: sampled ground · Red: uneven or unreadable ground · White dot: doorway</p></div></section></main>
<script type="module">
${planner}
const snapshots=${data};
const select=document.querySelector('#plot'),canvas=document.querySelector('#map'),ctx=canvas.getContext('2d');
let entrances=[],reserve=[],current,view;
for(const s of snapshots){const o=document.createElement('option');o.value=s.plot;o.textContent='Plot '+s.plot;select.append(o);}
function render(){
 const original=snapshots.find(s=>s.plot==select.value),empty=document.querySelector('#empty').checked;
 let s=empty?{...original,buildings:[],floors:[],floorCount:0}:original;
 const coverage=Number(document.querySelector('#coverage').value)/100;
 document.querySelector('#pct').textContent=Math.round(coverage*100);
 current=undefined;document.querySelector('#download').disabled=true;
 try{current={...analyze(s,{coverage,entrances,reserve}),
   scenario:empty?'hypothetical-empty':'preserve-existing',originalFloorCount:original.floorCount,
   assumptions:empty?['Existing construction hypothetically removed; save has not been changed.']:[]};}
 catch(e){ctx.clearRect(0,0,1000,800);document.querySelector('#missing').replaceChildren();
   document.querySelector('#facts').textContent=e.message;return;}
 document.querySelector('#download').disabled=false;
 const p=current,all=s.blocks,minX=Math.min(...all.map(b=>b.x)),minY=Math.min(...all.map(b=>b.y));
 const maxX=Math.max(...all.map(b=>b.x)),maxY=Math.max(...all.map(b=>b.y));
 const scale=Math.min(940/(maxX-minX+1),740/(maxY-minY+1));view={minX,minY,scale};
 ctx.clearRect(0,0,1000,800);
 const occupied=new Set(p.occupied),blocked=new Set(p.rejected.map(b=>key(b.x,b.y))),rooms=new Map();
 p.rooms.forEach((r,i)=>r.cells.forEach(k=>rooms.set(k,i)));
 for(const b of all){
  const k=key(b.x,b.y),x=30+(b.x-minX)*scale,y=30+(b.y-minY)*scale;
  ctx.fillStyle=blocked.has(k)?'#6d343c':occupied.has(k)?'#697582':rooms.has(k)?
    ['#397d66','#43947e','#56866b'][rooms.get(k)%3]:'#28475f';
  if(p.reserved.includes(k))ctx.fillStyle='#d6a947';
  ctx.fillRect(x+1,y+1,scale-2,scale-2);
 }
 for(const r of p.rooms){const [x,y]=r.door.inside.split(',').map(Number);
 ctx.fillStyle='white';ctx.beginPath();ctx.arc(30+(x-minX+.5)*scale,30+(y-minY+.5)*scale,3,0,Math.PI*2);ctx.fill();}
 document.querySelector('#facts').textContent='Heart level '+p.heartLevel+'\\nFloor allowance: '+p.existingFloors+' / '+p.floorLimit+
 '\\nEligible free ground: '+p.eligibleGroundBlocks+' blocks\\nNew floors: '+p.newFloors+' / target '+p.floorTarget+
 '\\nComplete room candidates: '+p.rooms.length+'\\nRemaining allowance: '+p.remainingAfterPlan+
 '\\n'+p.explanation;
 document.querySelector('#missing').replaceChildren(...p.notChecked.map(t=>{const li=document.createElement('li');li.textContent=t;return li;}));
}
canvas.onclick=e=>{if(!view)return;const r=canvas.getBoundingClientRect(),x=Math.floor(((e.clientX-r.left)*1000/r.width-30)/view.scale)+view.minX,
 y=Math.floor(((e.clientY-r.top)*800/r.height-30)/view.scale)+view.minY,k=key(x,y);
 if(e.shiftKey)reserve=reserve.includes(k)?reserve.filter(v=>v!==k):[...reserve,k];else entrances=[k];render();};
select.onchange=()=>{entrances=[];reserve=[];render();};
document.querySelector('#coverage').oninput=render;
document.querySelector('#empty').onchange=()=>{entrances=[];reserve=[];render();};
document.querySelector('#download').onclick=()=>{if(!current)return;const a=document.createElement('a'),url=URL.createObjectURL(new Blob([JSON.stringify(current,null,2)],{type:'application/json'}));a.href=url;a.download='castle-plan-'+current.plot+'.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
render();
</script></html>`;
fs.writeFileSync(output,html);
console.log(fileURLToPath(new URL('file:///'+path.resolve(output).replaceAll('\\','/'))));
