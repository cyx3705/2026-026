import {pageIds, dockPage, removePage, pageHome, restorePage, seedDesktopLayouts,desktopLayout,normalizeLayout,visibleLayout} from './layout.js';
import {workspaceState,layoutName,layoutSnapshot,readLayoutSnapshot,resizePage} from './workspace-state.js';
import {SceneManager} from './scenes.js';

const el = (tag,text,cls) => { const node=document.createElement(tag); if(text!==undefined) node.textContent=text; if(cls) node.className=cls; return node; };
const $ = id => document.getElementById(id);
const storageKey = 'aurora.web.workspace.v1';
export class Workspace {
  constructor(hooks) {
    this.hooks=hooks; this.pages=[]; this.panes=new Map(); this.adjust=false; this.scene=null;
    this.saved=workspaceState();this.desktopLayouts={};this.sceneManager=new SceneManager(this);
    try { this.saved=workspaceState(JSON.parse(localStorage.getItem(storageKey))); }
    catch { hooks.report('保存的布局无法读取，已使用默认布局'); }
    $('adjust').onclick=()=>this.setAdjust(!this.adjust);
    $('layout-reset').onclick=()=>{try {this.resetLayout();}catch(error){hooks.report(error);}};
    $('layout-desktop').onclick=()=>{try {this.restoreDesktopLayout();hooks.report('已恢复桌面布局');}catch(error){hooks.report(error);}};
    $('nav-query').oninput=()=>this.drawRail();
    $('nav-search').onclick=()=>this.setSearch(true);
    $('nav-search-close').onclick=()=>this.setSearch(false);
    const dismissSearch=event=>{
      if(!$('nav-search-bar').hidden&&!event.target.closest('.rail-header'))this.setSearch(false,{restoreFocus:false});
    };
    document.addEventListener('pointerdown',event=>{
      // Result buttons must receive their click before clearing the filter rebuilds them.
      // Background clicks close immediately, before the shell starts its native drag loop.
      if(!event.target.closest('#pages button,#page-library button,#scene-tray button'))dismissSearch(event);
    });
    document.addEventListener('click',dismissSearch);
    $('nav-query').onkeydown=e=> {
      if(e.key==='Enter')document.querySelector('#pages button, #page-library button')?.click();
      if(e.key==='Escape'){e.preventDefault();e.stopPropagation();this.setSearch(false);}
    };
    $('rail-toggle').onclick=()=>document.body.classList.toggle('rail-open');
    $('scene-tray').ondragover=e=> { if(this.drag?.kind==='scene') e.preventDefault(); };
    $('scene-tray').ondrop=e=> { e.preventDefault();try {if(this.drag?.kind==='scene')this.hideScene(this.drag.id,true);}catch(error){this.hooks.report(error);} };
    $('page-library').ondragover=e=> { if(this.adjust&&this.drag?.kind==='page') e.preventDefault(); };
    $('page-library').ondrop=e=> { e.preventDefault(); if(this.adjust&&this.drag?.kind==='page') this.hide(this.drag.id); };
    window.addEventListener('dragend',()=> { this.drag=null; document.body.classList.remove('dragging'); },true);
    window.addEventListener('keydown',e=> {
      if(e.key==='Control' && !e.repeat && !e.altKey && !e.metaKey && !e.shiftKey
        && !e.target.closest('input,textarea,select,[contenteditable]') && !this.adjust) {
        this.temporary=true; this.setAdjust(true);
      } else if(e.key!=='Control' && this.temporary) { this.temporary=false; this.setAdjust(false); }
      if(e.key==='Escape' && this.adjust) this.setAdjust(false);
    });
    window.addEventListener('keyup',e=> { if(e.key==='Control' && this.temporary && !this.drag) { this.temporary=false; this.setAdjust(false); } });
    window.addEventListener('blur',()=> { if(this.temporary&&!this.drag) { this.temporary=false; this.setAdjust(false); } });
    $('main').addEventListener('dragover',e=> { if(this.adjust && this.drag?.kind==='page') e.preventDefault(); });
    $('main').addEventListener('drop',e=> {
      if(e.target.closest('.dock-target') || !this.adjust || this.drag?.kind!=='page') return;
      if (!pageIds(this.tree).length) { e.preventDefault(); this.place(this.drag.id,null,'center'); }
    });
  }
  setSearch(open,{restoreFocus=true}={}) {
    document.body.classList.toggle('rail-searching',open);
    if(open)document.body.classList.add('rail-open');
    $('nav-search-bar').hidden=!open;
    $('nav-search').setAttribute('aria-expanded',String(open));
    if(open){$('nav-query').focus();$('nav-query').select();}
    else {$('nav-query').value='';this.drawRail();if(restoreFocus)$('nav-search').focus();}
  }
  control(text,handler,cls) {
    const button=el('button',text,cls); button.type='button';
    button.onclick=async()=> { try { await handler(); } catch(error) { this.hooks.report(error); } };
    return button;
  }
  setPages(pages,{replaceIds}={}) {
    this.hooks.onPagesChanged?.(replaceIds);
    if(replaceIds)for(const id of replaceIds)this.panes.delete(id);else this.panes.clear();
    this.pages=pages;
    this.scenes=this.sceneManager.list().map(scene=>scene.id);
    this.drawRail();
  }
  scenePages() { return this.pages.filter(p=>(p.scene||p.owner)===this.scene); }
  restoreDesktopHistory(history) {
    this.desktopLayouts=history?.layouts??{};
    return seedDesktopLayouts(this.saved,history,this.pages,Math.max(320,innerWidth-200),innerHeight);
  }
  // Explicit recovery bypasses automatic migration markers and browser-named copies.
  restoreDesktopLayout(value=this.scene) {
    const scene=this.sceneManager.require(value);
    const snapshot=this.desktopLayouts[scene.id];
    if(!snapshot)throw new Error(`场景 [${scene.title}] 没有桌面布局历史`);
    const tree=desktopLayout(snapshot,null,Math.max(320,innerWidth-200),innerHeight);
    if(!tree)throw new Error(`场景 [${scene.title}] 的桌面布局历史为空`);
    const ids=pageIds(tree),active=ids.includes(snapshot.activeContentId)?snapshot.activeContentId:ids[0];
    this.commitSaved(saved=>{
      this.sceneManager.store(saved,scene.id,tree,active,{});
      saved.desktopHistory[scene.id]=true;
      saved.resetScenes=saved.resetScenes.filter(id=>id!==scene.id);
      saved.lastScene=scene.id;
    });
    if(this.scene===scene.id)this.sceneManager.paint(scene.id);
    return scene.id;
  }
  async open(scene,force=false,recordUse=true) {this.sceneManager.open(scene,force,recordUse);}
  setLayout(tree,active=this.layoutActive) {
    this.fullTree=normalizeLayout(tree,null);this.projectedNodes=new WeakMap();
    const available=new Set(this.pages.map(page=>page.id));
    this.tree=visibleLayout(this.fullTree,available,this.projectedNodes);this.layoutActive=active;
    this.active=available.has(active)?active:pageIds(this.tree)[0];
  }
  persist() {
    this.saved.layouts[this.scene]=this.fullTree;this.saved.active[this.scene]=this.layoutActive;
    if(Object.hasOwn(this.saved.userScenes,this.scene))this.saved.namedLayouts[this.scene]=layoutSnapshot(this.fullTree,this.layoutActive,this.saved.homes[this.scene]);
    try { localStorage.setItem(storageKey,JSON.stringify(this.saved)); }
    catch { this.hooks.report('浏览器无法保存布局，当前调整仅在本次打开期间有效'); }
  }
  commitSaved(update) {
    const next=workspaceState(structuredClone(this.saved));update(next);
    try { localStorage.setItem(storageKey,JSON.stringify(next)); }
    catch { throw new Error('浏览器无法保存布局，本次操作未保存'); }
    this.saved=next;
  }
  listLayouts() {return [...new Set([...Object.keys(this.desktopLayouts),...Object.keys(this.saved.namedLayouts)])].sort((a,b)=>a.localeCompare(b));}
  saveLayout(value) {
    const requested=layoutName(value),name=this.listLayouts().find(name=>name.toLowerCase()===requested.toLowerCase())??requested;
    const snapshot=layoutSnapshot(this.fullTree,this.layoutActive,this.saved.homes[this.scene]);
    this.commitSaved(next=>{
      next.namedLayouts[name]=snapshot;
      if(Object.hasOwn(next.userScenes,name))this.sceneManager.store(next,name,snapshot.tree,snapshot.active,snapshot.homes);
    });return name;
  }
  loadLayout(value) {
    const requested=layoutName(value),name=this.listLayouts().find(name=>name.toLowerCase()===requested.toLowerCase());
    if(!name)throw new Error(`布局方案 [${requested}] 不存在`);
    const available=new Set(this.pages.map(page=>page.id));
    const snapshot=Object.hasOwn(this.saved.namedLayouts,name)?this.saved.namedLayouts[name]:layoutSnapshot(
      desktopLayout(this.desktopLayouts[name],null,Math.max(320,innerWidth-200),innerHeight),this.desktopLayouts[name].activeContentId,{});
    const next=readLayoutSnapshot(snapshot,available,{retainMissing:true});next.active=pageIds(next.tree).includes(next.active)?next.active:pageIds(next.tree)[0];
    this.commitSaved(saved=>this.sceneManager.store(saved,this.scene,next.tree,next.active,next.homes));
    this.setLayout(next.tree,next.active);this.draw();return name;
  }
  resetLayout() {
    this.sceneManager.reset();
  }
  setRatio(id,value) {
    const updated=resizePage(this.tree,id,value);
    const apply=(old,next)=>{if(old?.kind!=='split')return;const original=this.projectedNodes.get(old);if(original)original.ratio=next.ratio;apply(old.first,next.first);apply(old.second,next.second);};
    apply(this.tree,updated);this.setLayout(this.fullTree);this.persist();this.draw();
  }
  setAdjust(value) {
    this.adjust=value; document.body.classList.toggle('adjusting',value);
    $('adjust').setAttribute('aria-pressed',String(value));
    $('adjust').querySelector('.wide').textContent=`页面调整：${value?'开':'关'}`;
    this.drawRail();
    for(const [id,pane] of this.panes) pane.querySelector('.page-label').draggable=value;
    if(!value)this.hooks.onReveal?.();
  }
  select(id) {
    const page=this.pages.find(p=>p.id===id); if(!page) return;
    this.active=id;this.layoutActive=id;
    // Mobile can visit a hidden page without rewriting its desktop docking position.
    if(!this.panes.has(id)) this.pane(id);
    this.persist(); this.draw();
  }
  show(id) {
    if(matchMedia('(max-width:700px)').matches) return this.select(id);
    if(pageIds(this.tree).includes(id)) { this.active=id;this.layoutActive=id;this.persist();this.draw();return; }
    const page=this.pages.find(p=>p.id===id); if(!page) return;
    const home=this.saved.homes[this.scene]?.[id];
    if(home&&(home.targets??[home.target]).some(target=>pageIds(this.fullTree).includes(target))) {
      if(home.side==='center')this.place(id,home.target,home.side,home.ratio);
      else {this.setLayout(restorePage(this.fullTree,id,home),id);this.persist();this.draw();}
      return;
    }
    const side=page.placement?.side==='tab'?'center':page.placement?.side||'right';
    const target=page.placement?.tabTarget || this.active;
    this.place(id,target,side);
  }
  place(id,target,side,ratio=.3) {
    if(side==='center'&&!target) target=this.scenePages().find(p=>p.placement?.side==='center'
      &&pageIds(this.tree).includes(p.id))?.id || this.active || pageIds(this.tree)[0];
    if(side==='center'&&target&&target!==id) {
      this.saved.homes[this.scene]??={};
      this.saved.homes[this.scene][target]={target:id,side:'center',ratio};
    }
    this.setLayout(dockPage(this.fullTree,id,target,side,ratio),id);this.persist();this.draw();
  }
  hide(id) {
    this.saved.homes[this.scene]??={};
    this.saved.homes[this.scene][id]=pageHome(this.fullTree,id);
    const tree=removePage(this.fullTree,id);
    this.setLayout(tree,this.layoutActive===id?pageIds(visibleLayout(tree,new Set(this.pages.map(page=>page.id))))[0]:this.layoutActive);
    this.persist(); this.draw();
  }
  hideScene(id,hidden) {
    this.sceneManager.hide(id,hidden);
  }
  dragSource(node,kind,id) {
    node.draggable=this.adjust;
    node.ondragstart=e=> {
      if(!this.adjust) return e.preventDefault();
      this.drag={kind,id}; e.dataTransfer.setData('text/plain',id); e.dataTransfer.effectAllowed='move';
      document.body.classList.add('dragging');
    };
    node.ondragend=()=> { this.drag=null; document.body.classList.remove('dragging');
      if(this.temporary) { this.temporary=false; this.setAdjust(false); }
    };
  }
  drawRail() {
    const query=$('nav-query').value.trim().toLowerCase();
    const matches=text=>text.toLowerCase().includes(query);
    $('pages').replaceChildren(); $('scene-tray').replaceChildren();
    for(const info of this.sceneManager.list()) {
      const scene=info.id;
      if(!matches(`${scene} ${info.title}`)) continue;
      const hidden=this.saved.hidden.includes(scene);
      if(hidden&&!this.adjust&&!query) continue;
      const item=el('div',undefined,'scene-item');
      const button=this.control(info.title,()=>this.open(scene));
      button.setAttribute('aria-current',this.scene===scene?'page':'false'); button.dataset.scene=scene;
      this.dragSource(button,'scene',scene);
      item.append(button);
      if(this.adjust) {
        item.append(this.control(hidden?'放回':'收起',()=>this.hideScene(scene,!hidden),'scene-hide'));
        button.ondragover=e=> { if(this.drag?.kind==='scene'&&this.drag.id!==scene) e.preventDefault(); };
        button.ondrop=e=> { e.preventDefault();
          if(this.drag?.kind!=='scene') return;
          try {this.sceneManager.move(this.drag.id,scene,{unhide:true});}catch(error){this.hooks.report(error);}
        };
      }
      (hidden&&!query?$('scene-tray'):$('pages')).append(item);
    }
    $('page-library').replaceChildren();
    const visible=new Set(pageIds(this.tree));
    const library=this.pages.filter(p=>!visible.has(p.id)&&matches(`${p.title} ${p.owner}`));
    for(const page of library) {
      const chip=this.control(page.title,()=>this.show(page.id),'page-chip'); chip.title=`${page.title} · ${page.owner}`;
      chip.dataset.page=page.id; this.dragSource(chip,'page',page.id); $('page-library').append(chip);
    }
    if(!library.length) $('page-library').append(el('p',query?'没有匹配的页面':'所有页面都已打开','rail-hint'));
    $('scene-tray').hidden=!this.adjust;
    $('layout-reset').hidden=!this.adjust;
    $('layout-desktop').hidden=!this.adjust||!Object.hasOwn(this.desktopLayouts,this.scene);
  }
  pane(id) {
    if(this.panes.has(id)) return this.panes.get(id);
    const page=this.pages.find(p=>p.id===id); if(!page) return el('div');
    const pane=el('section',undefined,'scene-page'); pane.dataset.page=id; pane.setAttribute('aria-label',page.title);
    const content=el('div',undefined,'page-content'), overlay=el('div',undefined,'page-adjustment');
    const label=el('div',page.title,'page-label'); label.tabIndex=0; this.dragSource(label,'page',id);
    const controls=el('div',undefined,'page-adjust-tools');
    const positions=el('select'); positions.setAttribute('aria-label',`${page.title}停靠位置`);
    positions.append(el('option','移动到…'));
    for(const [side,title] of [['left','左侧'],['right','右侧'],['top','上方'],['bottom','下方'],['center','中央']]) {
      const option=el('option',title); option.value=side; positions.append(option);
    }
    positions.onchange=()=> { if(positions.selectedIndex) { this.place(id,null,positions.value); positions.selectedIndex=0; } };
    controls.append(positions,this.control('弹出页面',()=>this.hooks.floatPage(page)),this.control('隐藏',()=>this.hide(id)));
    overlay.append(label,controls); pane.append(content,overlay);
    const targets=el('div',undefined,'dock-targets');
    for(const [side,title] of [['left','左'],['right','右'],['top','上'],['bottom','下'],['center','替换']]) {
      const target=el('div',title,`dock-target dock-${side}`); target.dataset.side=side;
      target.ondragover=e=> { if(this.adjust&&this.drag?.kind==='page') { e.preventDefault(); target.classList.add('over'); } };
      target.ondragleave=()=>target.classList.remove('over');
      target.ondrop=e=> { e.preventDefault(); e.stopPropagation(); target.classList.remove('over');
        if(this.adjust&&this.drag?.kind==='page') this.place(this.drag.id,id,side);
      };
      targets.append(target);
    }
    pane.append(targets); this.panes.set(id,pane);
    pane.rendered=this.hooks.renderPage(page,content).catch(error=>this.hooks.report(error));
    return pane;
  }
  node(tree) {
    if(tree.kind==='page') { const pane=this.pane(tree.page); pane.classList.remove('mobile-extra'); return pane; }
    const group=el('div',undefined,`dock-split dock-${tree.axis}`), first=el('div',undefined,'dock-branch'), second=el('div',undefined,'dock-branch');
    first.style.flex=`${tree.ratio} 1 0%`; second.style.flex=`${1-tree.ratio} 1 0%`;
    first.append(this.node(tree.first)); second.append(this.node(tree.second));
    const grip=el('div',undefined,'dock-separator'); grip.tabIndex=0; grip.setAttribute('role','separator');
    grip.setAttribute('aria-label',tree.axis==='x'?'调整左右页面比例':'调整上下页面比例');
    grip.setAttribute('aria-orientation',tree.axis==='x'?'vertical':'horizontal');
    grip.setAttribute('aria-valuemin','12'); grip.setAttribute('aria-valuemax','88');
    grip.setAttribute('aria-valuenow',String(Math.round(tree.ratio*100)));
    const resize=value=> { tree.ratio=Math.max(.12,Math.min(.88,value));const original=this.projectedNodes.get(tree);if(original)original.ratio=tree.ratio;first.style.flex=`${tree.ratio} 1 0%`;
      second.style.flex=`${1-tree.ratio} 1 0%`; grip.setAttribute('aria-valuenow',String(Math.round(tree.ratio*100))); };
    grip.onpointerdown=e=> {
      if(e.button!==0) return;
      e.preventDefault(); grip.setPointerCapture(e.pointerId);
      grip.onpointermove=event=> { const bounds=group.getBoundingClientRect();
        resize(tree.axis==='x'?(event.clientX-bounds.left)/bounds.width:(event.clientY-bounds.top)/bounds.height); };
      const end=()=> { grip.onpointermove=null; this.persist(); };
      grip.onpointerup=end; grip.onlostpointercapture=end;
    };
    grip.onkeydown=e=> { const minus=tree.axis==='x'?'ArrowLeft':'ArrowUp', plus=tree.axis==='x'?'ArrowRight':'ArrowDown';
      if(e.key===minus||e.key===plus) { e.preventDefault(); resize(tree.ratio+(e.key===minus?-.025:.025)); this.persist(); } };
    group.append(first,grip,second); return group;
  }
  draw() {
    if(!this.scene) return;
    const main=$('main'), canvas=el('div',undefined,'scene-canvas'); canvas.id='scene-canvas';
    const capsules=el('div',undefined,'page-capsules'); capsules.setAttribute('aria-label','场景内页面');
    const ids=[...new Set([...pageIds(this.tree),...this.scenePages().map(p=>p.id)])];
    if(!ids.includes(this.active)) this.active=ids[0];
    for(const id of ids) {
      const page=this.pages.find(p=>p.id===id); if(!page) continue;
      const chip=this.control(page.title,()=>this.select(id)); chip.dataset.page=id; chip.setAttribute('aria-pressed',String(id===this.active)); capsules.append(chip);
    }
    if(this.tree) canvas.append(this.node(this.tree));
    else canvas.append(el('p','从右侧常用页面打开一页，或在页面调整中拖入。','empty-layout'));
    const activeInTree=pageIds(this.tree).includes(this.active);
    if(this.active&&!activeInTree) { const extra=this.pane(this.active); extra.classList.add('mobile-extra'); canvas.append(extra); }
    for(const [id,pane] of this.panes) { pane.dataset.active=String(id===this.active); pane.querySelector('.page-label').draggable=this.adjust; }
    main.replaceChildren(capsules,canvas); this.drawRail();
    this.hooks.onReveal?.();
    let start;
    canvas.addEventListener('touchstart',e=> { const touch=e.touches[0];
      start=matchMedia('(max-width:700px)').matches&&e.touches.length===1
        && !e.target.closest('input,textarea,select,button,.table-scroll,.dock-separator,.graph-viewport,.chat-viewport,.conversation-list')
        ? {x:touch.clientX,y:touch.clientY}:null;
    },{passive:true});
    canvas.addEventListener('touchend',e=> { if(!start) return;
      const touch=e.changedTouches[0], dx=touch.clientX-start.x, dy=touch.clientY-start.y; start=null;
      if(Math.abs(dx)<60||Math.abs(dx)<Math.abs(dy)*1.5) return;
      const next=ids[ids.indexOf(this.active)+(dx<0?1:-1)]; if(next) this.select(next);
    },{passive:true});
  }
}
