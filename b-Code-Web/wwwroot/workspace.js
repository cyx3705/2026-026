import {defaultLayout, pageIds, dockPage, removePage, normalizeLayout, pageHome} from './layout.js';

const el = (tag,text,cls) => { const node=document.createElement(tag); if(text!==undefined) node.textContent=text; if(cls) node.className=cls; return node; };
const $ = id => document.getElementById(id);
const storageKey = 'aurora.web.workspace.v1';
export class Workspace {
  constructor(hooks) {
    this.hooks=hooks; this.pages=[]; this.panes=new Map(); this.adjust=false; this.scene=null;
    this.saved={layouts:{},order:[],hidden:[],active:{},homes:{}};
    try { const saved=JSON.parse(localStorage.getItem(storageKey));
      if (saved?.layouts && typeof saved.layouts==='object' && !Array.isArray(saved.layouts)) this.saved={...this.saved,...saved};
    } catch { hooks.report('保存的布局无法读取，已使用默认布局'); }
    this.saved.order=Array.isArray(this.saved.order)?this.saved.order:[];
    this.saved.hidden=Array.isArray(this.saved.hidden)?this.saved.hidden:[];
    this.saved.active=this.saved.active && typeof this.saved.active==='object'?this.saved.active:{};
    this.saved.homes=this.saved.homes && typeof this.saved.homes==='object'?this.saved.homes:{};
    $('adjust').onclick=()=>this.setAdjust(!this.adjust);
    $('layout-reset').onclick=()=> { this.tree=defaultLayout(this.scenePages()); this.saved.homes[this.scene]={}; this.active=pageIds(this.tree)[0]; this.persist(); this.draw(); };
    $('nav-query').oninput=()=>this.drawRail();
    $('nav-query').onkeydown=e=> { if(e.key==='Enter') $('pages button, #page-library button')?.click(); };
    $('rail-toggle').onclick=()=>document.body.classList.toggle('rail-open');
    $('scene-tray').ondragover=e=> { if(this.drag?.kind==='scene') e.preventDefault(); };
    $('scene-tray').ondrop=e=> { e.preventDefault(); if(this.drag?.kind==='scene') this.hideScene(this.drag.id,true); };
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
  control(text,handler,cls) {
    const button=el('button',text,cls); button.type='button';
    button.onclick=async()=> { try { await handler(); } catch(error) { this.hooks.report(error); } };
    return button;
  }
  setPages(pages) {
    this.pages=pages;
    const scenes=[...new Set(pages.map(p=>p.scene||p.owner))];
    this.scenes=[...this.saved.order.filter(id=>scenes.includes(id)),...scenes.filter(id=>!this.saved.order.includes(id))];
    this.drawRail();
  }
  scenePages() { return this.pages.filter(p=>(p.scene||p.owner)===this.scene); }
  async open(scene,force=false) {
    if(!force && this.scene===scene && $('scene-canvas')) { document.body.classList.remove('rail-open'); return; }
    this.scene=scene; this.panes.clear(); this.hooks.onSwitch(scene);
    const available=new Set(this.pages.map(p=>p.id));
    this.tree=Object.hasOwn(this.saved.layouts,scene) ? normalizeLayout(this.saved.layouts[scene],available)
      : defaultLayout(this.scenePages());
    this.active=this.saved.active[scene] || pageIds(this.tree)[0] || this.scenePages()[0]?.id;
    this.saved.lastScene=scene; this.persist(); this.draw(); document.body.classList.remove('rail-open');
  }
  persist() {
    this.saved.layouts[this.scene]=this.tree; this.saved.order=this.scenes; this.saved.active[this.scene]=this.active;
    try { localStorage.setItem(storageKey,JSON.stringify(this.saved)); }
    catch { this.hooks.report('浏览器无法保存布局，当前调整仅在本次打开期间有效'); }
  }
  setAdjust(value) {
    this.adjust=value; document.body.classList.toggle('adjusting',value);
    $('adjust').setAttribute('aria-pressed',String(value));
    $('adjust').querySelector('.wide').textContent=`页面调整：${value?'开':'关'}`;
    this.drawRail();
    for(const [id,pane] of this.panes) pane.querySelector('.page-label').draggable=value;
  }
  select(id) {
    this.active=id;
    const page=this.pages.find(p=>p.id===id); if(!page) return;
    // Mobile can visit a hidden page without rewriting its desktop docking position.
    if(!this.panes.has(id)) this.pane(id);
    this.persist(); this.draw();
  }
  show(id) {
    if(matchMedia('(max-width:700px)').matches) return this.select(id);
    if(pageIds(this.tree).includes(id)) { this.active=id; this.draw(); return; }
    const page=this.pages.find(p=>p.id===id); if(!page) return;
    const home=this.saved.homes[this.scene]?.[id];
    if(home && pageIds(this.tree).includes(home.target)) { this.place(id,home.target,home.side,home.ratio); return; }
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
    this.tree=dockPage(this.tree,id,target,side,ratio); this.active=id; this.persist(); this.draw();
  }
  hide(id) {
    this.saved.homes[this.scene]??={};
    this.saved.homes[this.scene][id]=pageHome(this.tree,id);
    this.tree=removePage(this.tree,id);
    if(this.active===id) this.active=pageIds(this.tree)[0];
    this.persist(); this.draw();
  }
  hideScene(id,hidden) {
    this.saved.hidden=this.saved.hidden.filter(value=>value!==id);
    if(hidden) this.saved.hidden.push(id);
    this.persist(); this.drawRail();
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
    for(const scene of this.scenes||[]) {
      if(!matches(scene)) continue;
      const hidden=this.saved.hidden.includes(scene);
      if(hidden&&!this.adjust&&!query) continue;
      const item=el('div',undefined,'scene-item');
      const button=this.control(scene.replace(/^History/,''),()=>this.open(scene));
      button.setAttribute('aria-current',this.scene===scene?'page':'false'); button.dataset.scene=scene;
      this.dragSource(button,'scene',scene);
      item.append(button);
      if(this.adjust) {
        item.append(this.control(hidden?'放回':'收起',()=>this.hideScene(scene,!hidden),'scene-hide'));
        button.ondragover=e=> { if(this.drag?.kind==='scene'&&this.drag.id!==scene) e.preventDefault(); };
        button.ondrop=e=> { e.preventDefault();
          if(this.drag?.kind!=='scene') return;
          const id=this.drag.id; this.scenes=this.scenes.filter(value=>value!==id);
          this.scenes.splice(this.scenes.indexOf(scene),0,id); this.saved.hidden=this.saved.hidden.filter(value=>value!==id);
          this.persist(); this.drawRail();
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
    this.hooks.renderPage(page,content).catch(error=>this.hooks.report(error));
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
    const resize=value=> { tree.ratio=Math.max(.12,Math.min(.88,value)); first.style.flex=`${tree.ratio} 1 0%`;
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
    let start;
    canvas.addEventListener('touchstart',e=> { const touch=e.touches[0];
      start=matchMedia('(max-width:700px)').matches&&e.touches.length===1
        && !e.target.closest('input,textarea,select,button,.table-scroll,.dock-separator')
        ? {x:touch.clientX,y:touch.clientY}:null;
    },{passive:true});
    canvas.addEventListener('touchend',e=> { if(!start) return;
      const touch=e.changedTouches[0], dx=touch.clientX-start.x, dy=touch.clientY-start.y; start=null;
      if(Math.abs(dx)<60||Math.abs(dx)<Math.abs(dy)*1.5) return;
      const next=ids[ids.indexOf(this.active)+(dx<0?1:-1)]; if(next) this.select(next);
    },{passive:true});
  }
}
