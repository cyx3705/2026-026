import {defaultLayout,normalizeLayout,pageIds,sceneLayout,residentPage} from './layout.js';
import {layoutName,layoutSnapshot} from './workspace-state.js';

const key=value=>String(value??'').trim().toLowerCase();
const same=(a,b)=>key(a)===key(b);
const includes=(values,value)=>values.some(item=>same(item,value));
const without=(values,value)=>values.filter(item=>!same(item,value));
const titleOf=id=>id.replace(/^History/,'');

// REQ-WEB-021: scenes name layouts; every scene reuses the workspace's page instances.
export class SceneManager {
  constructor(workspace){this.workspace=workspace;}
  list(saved=this.workspace.saved) {
    const rows=[],seen=new Set();
    const add=(id,title,source)=>{
      if(seen.has(key(id)))return;seen.add(key(id));
      rows.push({id,title,source,active:same(this.workspace.scene,id),hidden:includes(saved.hidden,id),uses:saved.sceneUsage[id]?.count??0});
    };
    for(const page of this.workspace.pages){
      const id=page.scene||page.owner;
      if(!includes(saved.deletedScenes,id))add(id,titleOf(id),'模块');
    }
    for(const [id,scene]of Object.entries(saved.userScenes))add(id,scene.title,'另存');
    const order=id=>{const index=saved.order.findIndex(item=>same(item,id));return index<0?Infinity:index;};
    return rows.sort((a,b)=>(order(a.id)-order(b.id))||a.title.localeCompare(b.title,undefined,{sensitivity:'base'}));
  }
  find(value,{exact=false,saved=this.workspace.saved}={}) {
    const scenes=this.list(saved);
    return scenes.find(scene=>same(scene.id,value))??(!exact&&scenes.find(scene=>same(scene.title,value)))
      ??(!exact&&scenes.find(scene=>same(scene.id,`History${String(value??'').trim()}`)));
  }
  require(value,options) {
    const scene=this.find(value,options);if(!scene)throw new Error(`场景不存在：${value??''}`);return scene;
  }
  store(saved,id,tree,active,homes) {
    saved.layouts[id]=structuredClone(tree);saved.active[id]=active;saved.homes[id]=structuredClone(homes??{});
    if(Object.hasOwn(saved.userScenes,id))saved.namedLayouts[id]=layoutSnapshot(tree,active,homes);
  }
  remember(saved) {
    const w=this.workspace;
    if(w.scene&&w.fullTree!==undefined&&this.find(w.scene,{exact:true,saved}))this.store(saved,w.scene,w.fullTree,w.layoutActive,saved.homes[w.scene]);
  }
  record(saved,id) {
    saved.sceneUsage[id]={count:Math.min(Number.MAX_SAFE_INTEGER,(saved.sceneUsage[id]?.count??0)+1),lastUsed:new Date().toISOString()};
  }
  prepare(saved,scene) {
    const w=this.workspace,available=new Set(w.pages.map(page=>page.id));
    const reset=includes(saved.resetScenes,scene.id);
    const tree=reset||!Object.hasOwn(saved.layouts,scene.id)?sceneLayout(w.pages,scene.id):normalizeLayout(saved.layouts[scene.id],null);
    const ids=pageIds(tree),initial=ids.find(id=>w.pages.some(page=>page.id===id&&(page.scene||page.owner)===scene.id&&!residentPage(id)))??ids[0];
    const active=reset||!available.has(saved.active[scene.id])&&!ids.includes(saved.active[scene.id])?initial:saved.active[scene.id];
    this.store(saved,scene.id,tree,active,reset?{}:saved.homes[scene.id]);
    saved.resetScenes=without(saved.resetScenes,scene.id);saved.lastScene=scene.id;
  }
  paint(id) {
    const w=this.workspace;w.scene=id;w.setLayout(w.saved.layouts[id],w.saved.active[id]);
    w.scenes=this.list().map(scene=>scene.id);w.hooks.onSwitch(id);w.draw();document.body.classList.remove('rail-open');
  }
  open(value,force=false,recordUse=true) {
    const w=this.workspace,scene=this.require(value),redraw=force||!same(w.scene,scene.id)||!document.getElementById('scene-canvas');
    // Reconnect may have just imported a desktop snapshot. The old canvas must
    // not overwrite it while the newly registered descriptions are being opened.
    w.commitSaved(saved=>{
      const requested=saved.lastScene;
      if(!force||recordUse)this.remember(saved);if(redraw)this.prepare(saved,scene);
      if(recordUse){saved.lastScene=scene.id;this.record(saved,scene.id);}
      else if(requested&&!this.find(requested,{exact:true,saved}))saved.lastScene=requested;
    });
    if(redraw)this.paint(scene.id);else document.body.classList.remove('rail-open');
  }
  save(value,title) {
    const w=this.workspace,requested=layoutName(value),existing=this.find(requested,{exact:true});
    if(existing?.source==='模块')throw new Error(`[${existing.id}] 是模块场景，不能覆盖`);
    const id=existing?.id??requested;
    const label=String(title??'').trim()||w.saved.userScenes[id]?.title||id;
    w.commitSaved(saved=>{
      this.remember(saved);saved.userScenes[id]={title:label};
      this.store(saved,id,w.fullTree,w.layoutActive,saved.homes[w.scene]);
      saved.resetScenes=without(saved.resetScenes,id);saved.lastScene=id;this.record(saved,id);
    });
    this.paint(id);return label;
  }
  reset(value=this.workspace.scene) {
    const w=this.workspace,scene=this.find(value);
    if(!scene){
      const revived=w.saved.deletedScenes.find(id=>same(id,value)||same(id,`History${String(value??'').trim()}`));
      if(!revived)throw new Error(`场景不存在：${value??''}`);
      w.commitSaved(saved=>{
        saved.deletedScenes=without(saved.deletedScenes,revived);saved.resetScenes=[...without(saved.resetScenes,revived),revived];
        saved.order=[...this.list().map(scene=>scene.id),revived];saved.desktopHistory[revived]=true;
      });
      w.scenes=this.list().map(scene=>scene.id);w.drawRail();return `场景 [${titleOf(revived)}] 已恢复，下次进入时按默认形态重建`;
    }
    if(scene.source==='另存'&&!scene.active)throw new Error(`[${scene.title}] 是另存的场景，请先切到它再重置`);
    w.commitSaved(saved=>{
      saved.desktopHistory[scene.id]=true;
      if(scene.active){
        const visible=new Set(pageIds(w.tree));
        const pages=w.pages.filter(page=>scene.source==='另存'?visible.has(page.id):(page.scene||page.owner)===scene.id);
        const tree=scene.source==='另存'?defaultLayout(pages):sceneLayout(w.pages,scene.id);this.store(saved,scene.id,tree,pageIds(tree)[0],{});
        saved.resetScenes=without(saved.resetScenes,scene.id);
      }else saved.resetScenes=[...without(saved.resetScenes,scene.id),scene.id];
    });
    if(scene.active)this.paint(scene.id);
    return `场景 [${scene.title}] ${scene.active?'已回到默认形态':'将在下次进入时恢复默认形态'}`;
  }
  delete(value) {
    const w=this.workspace,scene=this.require(value,{exact:true}),rows=this.list();
    if(rows.length<=1)throw new Error(`[${scene.title}] 是最后一个场景，不能删除`);
    const remaining=rows.filter(row=>row.id!==scene.id),next=remaining.find(row=>!row.hidden)??remaining[0];
    w.commitSaved(saved=>{
      if(scene.source==='模块')saved.deletedScenes=[...without(saved.deletedScenes,scene.id),scene.id];
      else {delete saved.userScenes[scene.id];delete saved.namedLayouts[scene.id];}
      for(const entries of [saved.layouts,saved.homes,saved.active,saved.sceneUsage])delete entries[scene.id];
      saved.desktopHistory[scene.id]=true;saved.resetScenes=without(saved.resetScenes,scene.id);
      saved.hidden=without(saved.hidden,scene.id);saved.order=without(saved.order,scene.id);
      if(scene.active)this.prepare(saved,next);
    });
    w.scenes=this.list().map(row=>row.id);if(scene.active)this.paint(next.id);else w.drawRail();return scene.title;
  }
  move(value,before,{unhide=false}={}) {
    const w=this.workspace,scene=this.require(value,{exact:true}),anchor=key(before)?this.require(before,{exact:true}):null;
    if(anchor?.id===scene.id)return;
    const order=this.list().map(scene=>scene.id).filter(id=>id!==scene.id);
    order.splice(anchor?order.indexOf(anchor.id):order.length,0,scene.id);
    w.commitSaved(saved=>{saved.order=order;if(unhide)saved.hidden=without(saved.hidden,scene.id);});
    w.scenes=order;w.drawRail();
  }
  hide(value,hidden) {
    const w=this.workspace,scene=this.require(value,{exact:true});
    w.commitSaved(saved=>{saved.hidden=without(saved.hidden,scene.id);if(hidden)saved.hidden.push(scene.id);});w.drawRail();
  }
}
