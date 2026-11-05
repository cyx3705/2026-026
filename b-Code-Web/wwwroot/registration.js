import {commandText} from './protocol.js';
import {validateNodeTree} from './page-tree.js';

const same=(a,b)=>String(a??'').toLowerCase()===String(b??'').toLowerCase();
export const expectedOwner=domain=>`History${domain[0].toUpperCase()}${domain.slice(1).toLowerCase()}`;
export function moduleDomain(value) {
  const domain=String(value??'').trim().replace(/^history/i,'').toLowerCase();
  if(!/^[a-z][a-z0-9_]*$/.test(domain))throw new Error('owner 必须是模块名或指令域');
  return domain;
}
export function contractDomains(names,suffix) {
  return [...new Set(names.filter(name=>typeof name==='string'&&name.toLowerCase().endsWith(suffix))
    .map(name=>name.slice(0,-suffix.length).toLowerCase()).filter(domain=>domain&&domain!=='aurora'))].sort();
}
export async function readActionDescription(description,owner) {
  const set=await readDescription('action',description,owner);
  // Dispatch syntax is browser-owned; schema, IDs and field types belong to the native reader.
  for(const action of set.actions)commandText(action.command,action.args??{});
  return set.actions.map(action=>({...action,owner}));
}
// Discovery is independent of page descriptions: a module can provide actions without any page.
export async function loadModuleActions(names,invoke,{domain,existing=new Map(),read=readActionDescription}={}) {
  const domains=domain?[domain]:contractDomains(names,'.ui.actions'),actions=new Map(existing),errors=[];
  for(const source of domains) {
    const owner=expectedOwner(source);
    for(const [id,action]of actions)if(same(action.owner,owner))actions.delete(id);
    if(!names.some(name=>same(name,`${source}.ui.actions`)))continue;
    try {
      const declarations=await read(await invoke(`${source}.ui.actions`),owner);
      for(const action of declarations) {
        if(actions.has(action.id)){errors.push(`动作 id 冲突：${action.id}（保留 ${actions.get(action.id).owner}）`);continue;}
        actions.set(action.id,action);
      }
    }catch(error){errors.push(`${source}.ui.actions：${error.message}`);}
  }
  return {actions,errors,asked:domains.length};
}
async function readDescription(kind,description,owner) {
  const response=await fetch(`/api/${kind}-description`,{method:'POST',headers:{'Content-Type':'application/json','X-Aurora-Web':'1'},body:JSON.stringify({owner,description})});
  const result=await response.json();
  if(!response.ok||result.success!==true)throw new Error(result.message||'无法解析模块声明');
  return result.description;
}
export async function readPageDescription(description,owner) {
  const set=await readDescription('page',description,owner);
  for(const page of set.pages){
    validateNodeTree(page.content,`${page.id}.content`);
    const side=page.placement.side?.toLowerCase();
    page.placement.side=['left','right','top','bottom','center','tab'].includes(side)?side:'right';
  }
  return set;
}
export async function loadModulePages(domain,invoke,read=readPageDescription) {
  const owner=expectedOwner(domain),set=await read(await invoke(`${domain}.ui.describe`),owner);
  return set.pages.map(page=>({...page,owner,...(page.scene?{scene:owner}:{})}));
}
export function replaceOwnerPages(current,incoming,owner) {
  const retained=current.filter(page=>!same(page.owner,owner));
  for(const page of incoming)if(retained.some(old=>same(old.id,page.id)))throw new Error(`页面 id 冲突：${page.id}`);
  const index=current.findIndex(page=>same(page.owner,owner));
  retained.splice(index<0?retained.length:index,0,...incoming);return retained;
}
