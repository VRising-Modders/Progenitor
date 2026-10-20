import fs from 'node:fs';
import path from 'node:path';
import { analyze, key } from './planner.mjs';
const [directory, output] = process.argv.slice(2);
if (!directory || !output) throw Error('Usage: node compare.mjs snapshot-folder comparison.json');
const comparisons=[];
for(const file of fs.readdirSync(directory).filter(n=>/^plot-\d+\.json$/.test(n))){
  const original=JSON.parse(fs.readFileSync(path.join(directory,file),'utf8').replace(/^\uFEFF/,''));
  for(const empty of [false,true]){
    const s=empty?{...original,floors:[],buildings:[],floorCount:0}:original;
    const baseline=analyze(s), occupied=new Set(baseline.occupied);
    const cells=new Set(baseline.blocks.map(b=>key(b.x,b.y)));
    // Comparison anchors only. These have NOT been established as usable plot entrances.
    const anchors=baseline.blocks.filter(b=>!occupied.has(key(b.x,b.y)) &&
      [[0,1],[1,0],[0,-1],[-1,0]].some(([x,y])=>!cells.has(key(b.x+x,b.y+y))));
    for(const coverage of [.25,.5,.75]){
      let best;
      for(const a of anchors){
        const candidate=analyze(s,{coverage,entrances:[key(a.x,a.y)]});
        if(!best || candidate.newFloors>best.newFloors)best=candidate;
      }
      best ??= analyze(s,{coverage});
      comparisons.push({plot:s.plot,scenario:empty?'hypothetical-empty':'preserve-existing',
        coverage,comparisonAnchor:best.entrances[0]??null,anchorVerified:false,
        eligibleGround:best.eligibleGroundBlocks,floorTarget:best.floorTarget,newFloors:best.newFloors,
        rooms:best.rooms.length,remainingAllowance:best.remainingAfterPlan,
        constructionReady:false});
    }
  }
}
fs.writeFileSync(output,JSON.stringify(comparisons,null,2));
for(const row of comparisons)console.log(JSON.stringify(row));
