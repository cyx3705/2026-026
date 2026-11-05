import {normalizeLayout,pageIds} from './layout.js';

const isRecord=value=>value!==null&&typeof value==='object'&&!Array.isArray(value);
const record=value=>Object.assign(Object.create(null),isRecord(value)?value:{});
const strings=value=>Array.isArray(value)?[...new Set(value.filter(item=>typeof item==='string'&&item))]:[];
function homesOf(value,available) {
  const homes=record();
  for(const [id,home]of Object.entries(record(value))){
    if(!isRecord(home)||typeof home.target!=='string'||!['left','right','top','bottom','center'].includes(home.side))continue;
    const targets=strings([home.target,...strings(home.targets)]).filter(target=>!available||available.has(target));
    if(available&&(!available.has(id)||!targets.length))continue;
    homes[id]={target:targets.includes(home.target)?home.target:targets[0]??home.target,targets,side:home.side,ratio:Math.max(.12,Math.min(.88,Number(home.ratio)||.3))};
  }
  return homes;
}
// Browser storage is input, not a trusted object graph. Normalize before rendering or migration.
export function workspaceState(value) {
  const source=record(value),layouts=record(),homes=record(),userScenes=record(),sceneUsage=record();
  for(const [scene,tree]of Object.entries(record(source.layouts))){
    const normalized=normalizeLayout(tree,null);
    if(tree===null||normalized)layouts[scene]=normalized;
  }
  for(const [scene,entries]of Object.entries(record(source.homes)))homes[scene]=homesOf(entries);
  for(const [id,scene]of Object.entries(record(source.userScenes))){
    if(isRecord(scene)&&typeof scene.title==='string'&&id&& !/[<>:"/\\|?*\u0000-\u001f]/.test(id))userScenes[id]={title:scene.title.trim()||id};
  }
  for(const [id,usage]of Object.entries(record(source.sceneUsage))){
    if(isRecord(usage)&&Number.isSafeInteger(usage.count)&&usage.count>=0)sceneUsage[id]={count:usage.count,lastUsed:typeof usage.lastUsed==='string'?usage.lastUsed:null};
  }
  return {layouts,homes,order:strings(source.order),hidden:strings(source.hidden),active:record(source.active),
    userScenes,sceneUsage,deletedScenes:strings(source.deletedScenes),resetScenes:strings(source.resetScenes),
    desktopHistory:record(source.desktopHistory),namedLayouts:record(source.namedLayouts),lastScene:typeof source.lastScene==='string'?source.lastScene:null};
}
export function layoutName(value) {
  const name=String(value??'').trim();
  if(!name||/[<>:"/\\|?*\u0000-\u001f]/.test(name))throw new Error('布局名不能为空或包含文件名不允许的字符');
  return name;
}
export function layoutSnapshot(tree,active,homes) {
  return {schemaVersion:1,tree:structuredClone(tree),active,homes:homesOf(homes)};
}
export function readLayoutSnapshot(snapshot,available,{retainMissing=false}={}) {
  if(!isRecord(snapshot)||snapshot.schemaVersion!==1||snapshot.tree!==null&&!normalizeLayout(snapshot.tree,null))throw new Error('布局方案的版本或结构无效');
  const tree=normalizeLayout(snapshot.tree,retainMissing?null:available);
  return {tree,active:available.has(snapshot.active)||retainMissing&&pageIds(tree).includes(snapshot.active)?snapshot.active:undefined,homes:homesOf(snapshot.homes,retainMissing?null:available)};
}
export function strictRatio(value,label='value') {
  const ratio=Number(value);
  if(value===undefined||!Number.isFinite(ratio)||ratio<=0||ratio>=1)throw new Error(`${label} 应严格位于 (0,1)`);
  return ratio;
}
// Resize the nearest split while interpreting value relative to the entire canvas, like desktop.
export function resizePage(tree,id,value) {
  const ratio=strictRatio(value);let found=false;
  const visit=(node,width,height)=>{
    if(node?.kind!=='split')return node;
    const first=node.first.kind==='page'&&node.first.page===id,second=node.second.kind==='page'&&node.second.page===id;
    if(first||second){
      found=true;const fraction=Math.max(.12,Math.min(.88,ratio/(node.axis==='x'?width:height)));
      return {...node,ratio:first?fraction:1-fraction};
    }
    return {...node,first:visit(node.first,node.axis==='x'?width*node.ratio:width,node.axis==='y'?height*node.ratio:height),
      second:visit(node.second,node.axis==='x'?width*(1-node.ratio):width,node.axis==='y'?height*(1-node.ratio):height)};
  };
  const next=visit(tree,1,1);if(!found)throw new Error('当前页面没有可调整的停靠分隔线');return next;
}
