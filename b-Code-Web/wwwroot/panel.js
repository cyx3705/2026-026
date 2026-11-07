import {isVisible} from './dom.js';
import {bindArgs, rowsOf} from './protocol.js';
import {showMenu, closeMenu} from './menu.js';
import {widgetKind, booleanValue, normalizeOptions, chooseOption, panelLines, validatePanel} from './panel-model.js';
import {selectionRow} from './selection.js';

const el=(tag,text,cls) => { const e=document.createElement(tag); if(text!==undefined) e.textContent=text; if(cls) e.className=cls; return e; };
export function renderPanel(node, page, hooks) {
  const section=el('section',undefined,'panel control-panel'); section.dataset.panel=node.id ?? 'page-panel';
  try { validatePanel(node); } catch(error) { section.append(el('p',error.message,'notice')); hooks.report(error); return section; }
  const scope={...page,pageContext:page,inputs:new Map(),panelId:node.id ?? 'page-panel',panelSpec:node,panelElement:section,bindingRevision:0};
  page.panels.push(scope);
  let disposed=false, frame,frameWindow,fontDocument;
  const active=() => !disposed && hooks.active();
  const rows=[], pending=new Set(), activators=[],bindings=[];
  scope.rebindActions=changed=>{
    const affected=bindings.filter(binding=>!changed||changed.has(binding.id?.toLowerCase()));
    if(changed&&!affected.length)return false;
    scope.bindingRevision++;for(const {bind}of affected)bind();scheduleLayout();return true;
  };
  function publish(spec,value) { if (spec.channel) hooks.publish(spec.channel,{value:String(value)},spec); }
  // Keep activity with the binding, even when a missing action replaces its DOM control.
  async function run(id, additions={}, activity) {
    const key=activity ?? id;
    if (!active() || pending.has(key)) return false;
    pending.add(key); updateBusy(activity?.element,true);
    try { await hooks.action(id,scope,additions); return true; }
    catch(error) { if(active())hooks.report(error); return false; }
    finally { pending.delete(key); updateBusy(activity?.element,false); }
  }
  function updateBusy(control,busy) {
    if (!control) return;
    control.setAttribute('aria-busy',String(busy));
    control.classList.toggle('is-running',busy);
    if ('disabled' in control) control.disabled=busy || !!control.gated;
    else for(const button of control.querySelectorAll('button'))button.disabled=busy;
  }
  function updateActionTitle(cell) {
    const button=cell.element;
    button.title=[button.scrollWidth>button.clientWidth?button.textContent:null,cell.hint].filter(Boolean).join('\n');
  }
  function register(spec,input,set,get=() => input.value,sync=set) {
    if (input.tagName !== 'INPUT' && input.tagName !== 'TEXTAREA') Object.defineProperty(input,'value',{get,set:value => set(String(value))});
    input.setValue=value => set(String(value)); scope.inputs.set(spec.id,input);
    input.syncValue=value=>sync(String(value));
    input.dataset.control=spec.id;
    if(spec.channel)input.dataset.channel=spec.channel;
    const initial=hooks.initialValue?.(spec.channel);
    if(initial!==undefined)input.syncValue(initial);else publish(spec,get());
  }
  function labelled(spec,input) {
    const label=spec.label ?? spec.id ?? '';
    input.setAttribute('aria-label',label || spec.id || '输入');
    const id=`panel-control-${crypto.randomUUID()}`; input.id=id;
    return label ? {label:el('label',label,'panel-label'),id} : {id};
  }
  function controlCells(spec,input,minimum=120) {
    const {label,id}=labelled(spec,input), cells=[];
    if(label) { label.htmlFor=id; cells.push({element:label}); }
    cells.push({element:input,min:spec.minWidth ?? minimum,flex:spec.flex}); return cells;
  }
  function menu(spec,opener,value,event) {
    if(!spec.menu?.length) return;
    event?.preventDefault(); event?.stopPropagation();
    showMenu(spec.menu.map(item => {
      const declaration=hooks.declaration(item.action);
      return {title:item.title ?? (declaration?.title || item.action),disabled:!declaration || pending.has(item.action),
        reason:declaration?.summary ?? `未声明的动作：${item.action}`,danger:item.style?.toLowerCase()==='danger' || declaration?.danger,
        run:() => run(item.action,{value})};
    }),opener,{x:event?.clientX,y:event?.clientY,owner:scope,report:hooks.report});
  }
  function build(spec) {
    const kind=widgetKind(spec.kind);
    if(kind==='text') return [{element:el('span',spec.text ?? '', 'panel-text'),min:spec.minWidth,flex:spec.flex}];
    if(kind==='button') {
      let declaration,b;
      const cell={min:spec.minWidth,flex:spec.flex};
      const sync=()=>{
        if(!declaration)return;
        b.gated=!!spec.enabledWhen?.selected&&!selectionRow(hooks.selection(),spec.enabledWhen.selected);updateBusy(b,pending.has(cell));
        cell.hint=b.gated?`请先选择条目（${spec.enabledWhen.selected}）`:declaration.summary||`${spec.action} → ${declaration.command}`;
        updateActionTitle(cell);
      };
      const bind=()=>{
        declaration=hooks.declaration(spec.action);
        const tag=declaration?'BUTTON':'SPAN';
        if(b?.tagName!==tag){const next=el(tag.toLowerCase());b?.replaceWith(next);b=next;}
        cell.element=b;cell.min=spec.minWidth;
        if(declaration){
          b.type='button';b.classList.remove('notice');b.classList.add('panel-action');b.classList.toggle('danger',!!declaration.danger);
          b.textContent=spec.text??(declaration.title||spec.action);
          if(spec.icon==='refresh-cw'){const icon=el('span','↻','panel-icon');icon.setAttribute('aria-hidden','true');b.prepend(icon);}
          b.onclick=()=>void run(spec.action,{},cell);sync();
        }else {b.className='notice';b.textContent=`未声明的动作：${spec.action??''}`;}
      };
      bindings.push({id:spec.action,bind});if(spec.enabledWhen?.selected)scope.followers.push(sync);bind();return [cell];
    }
    if(kind==='switch') {
      let value=booleanValue(spec.value); const b=el('button',spec.label ?? spec.id ?? '', 'panel-switch'); b.type='button'; b.setAttribute('role','switch');
      b.title=b.textContent;
      const activity={element:b};
      const set=next => { value=booleanValue(next); b.setAttribute('aria-checked',String(value)); publish(spec,String(value)); };
      register(spec,b,set,() => String(value)); set(String(value));
      b.onclick=() => { if(!active()||pending.has(activity))return;set(String(!value)); void run(spec.action || spec.commitAction,{value:String(value)},activity); };
      return [{element:b,min:spec.minWidth ?? (b.textContent ? undefined : 48),flex:spec.flex}];
    }
    if(kind==='choices' || spec.mode?.toLowerCase()==='select') {
      let options=normalizeOptions(spec.options ?? []), value=chooseOption(options,undefined,spec.value), request=0, previousArgs, queued;
      const input=el(kind==='choices' ? 'div' : 'button',undefined,kind==='choices' ? 'panel-choices' : 'panel-option');
      const activity={element:input};
      if(kind==='choices') { input.setAttribute('role','group'); } else { input.type='button'; input.setAttribute('aria-haspopup','menu'); }
      const set=(next,user=false,external=false) => {
        if(user&&(!active()||pending.has(activity)))return;
        const found=options.find(o => o.value.toLowerCase()===String(next).toLowerCase());
        const selected=found?.value??(external&&spec.optionsSource?String(next):'');
        const changed=value!==selected;value=selected;paint();publish(spec,value);
        if(user && changed && value && spec.commitAction) void run(spec.commitAction,{value},activity);
      };
      function paint() {
        if(kind!=='choices') { input.textContent=value; input.title=value; return; }
        input.replaceChildren(...options.map(option => {
          const b=el('button',option.text,'panel-choice'); b.type='button'; b.dataset.value=option.value;
          b.setAttribute('aria-pressed',String(option.value===value)); b.title=option.value;
          b.disabled=pending.has(activity);
          b.onclick=() => set(option.value,true);
          b.oncontextmenu=e => menu(spec,b,option.value,e);
          b.onkeydown=e => { if(e.key==='ContextMenu' || e.key==='F10' && e.shiftKey) { e.preventDefault(); menu(spec,b,option.value); } };
          return b;
        }));
        scheduleLayout();
      }
      register(spec,input,set,() => value,next=>set(next,false,true));paint();
      if(kind!=='choices') {
        const move=delta => { if(options.length) { const index=options.findIndex(o=>o.value===value); set(options[(Math.max(0,index)+delta+options.length)%options.length].value,true); } };
        const open=event => {
          event?.preventDefault(); event?.stopPropagation();
          if(!options.length) return;
          showMenu(options.map(option => ({title:option.value,checked:option.value===value,run:() => set(option.value,true)})),input,
            {x:event?.clientX,y:event?.clientY,owner:scope,report:hooks.report});
        };
        input.onclick=() => move(1); input.oncontextmenu=open;
        input.onkeydown=e => {
          if([' ','Enter','ArrowDown','ArrowRight','ArrowUp','ArrowLeft','Home','End','ContextMenu','F10'].includes(e.key)) {
            if(e.key==='F10' && !e.shiftKey) return;
            e.preventDefault(); e.stopPropagation();
            if(e.key==='ContextMenu' || e.key==='F10') open();
            else if(e.key==='Home' || e.key==='End') { if(options.length) set(options[e.key==='Home' ? 0 : options.length-1].value,true); }
            else move(['ArrowUp','ArrowLeft'].includes(e.key) ? -1 : 1);
          }
        };
      }
      if(spec.follows) {
        let last=Symbol();
        const sync=() => { const split=spec.follows.lastIndexOf('.'), next=selectionRow(hooks.selection(),spec.follows.slice(0,split))?.[spec.follows.slice(split+1)] ?? '';
          if(last!==next) { last=next; set(next); } };
        scope.followers.push(sync); sync();
      }
      if(spec.optionsSource) {
        let needsLoad=true;
        const load=async () => {
          const ticket=++request; let args;
          if(!isVisible(section)){needsLoad=true;return;}
          needsLoad=false;
          try { args=bindArgs(spec.optionsSource.args,{}, {},hooks.selection()); }
          catch { previousArgs=null; return; } // An unavailable parent keeps the prior candidates.
          const key=JSON.stringify(args); previousArgs=key;
          try {
            const result=normalizeOptions(rowsOf(await hooks.invoke(spec.optionsSource.command,args)));
            if(ticket!==request || !active()) return;
            // Also reject a target changed while no other load has started yet.
            try { if(JSON.stringify(bindArgs(spec.optionsSource.args,{}, {},hooks.selection()))!==key) return; } catch { return; }
            options=result; value=chooseOption(options,value,spec.value); paint(); publish(spec,value); input.removeAttribute('data-error');
          } catch(error) { if(ticket===request && active()) { input.dataset.error=error.message; input.title=error.message; hooks.report(error); } }
        };
        const queue=() => {
          if(!queued) queued=Promise.resolve().then(async () => { queued=null; await load(); });
          return queued;
        };
        activators.push(()=>{if(needsLoad && isVisible(section))void queue();});
        scope.initializers.push(queue); scope.refreshers.push(queue);
        if(JSON.stringify(spec.optionsSource.args ?? {}).includes('{selection.')) scope.followers.push(() => {
          let key; try { key=JSON.stringify(bindArgs(spec.optionsSource.args,{}, {},hooks.selection())); } catch { key=null; }
          if(key!==previousArgs) { previousArgs=key; ++request; void queue(); }
        });
      }
      return controlCells(spec,input);
    }
    const input=el(spec.multiline && kind==='textbox' ? 'textarea' : 'input'); if(input.tagName==='INPUT') input.type='text';
    const activity={element:input};
    input.value=spec.value ?? ''; input.placeholder=spec.placeholder ?? '';
    const set=value => { input.value=value; publish(spec,input.value); };
    register(spec,input,set); input.oninput=() => publish(spec,input.value);
    if(spec.follows && kind==='textbox') {
      let last=Symbol(); const sync=() => { const split=spec.follows.lastIndexOf('.');
        const next=selectionRow(hooks.selection(),spec.follows.slice(0,split))?.[spec.follows.slice(split+1)] ?? '';
        if(last!==next) { last=next; set(next); } };
      scope.followers.push(sync); sync();
    }
    let selecting=false, committed;
    const commit=async () => {
      if(selecting || !spec.commitAction || spec.multiline || !active()) return;
      const value=input.value;
      if(kind==='sourcepicker' && (!value.trim() || value===committed))return;
      if(await run(spec.commitAction,{value},activity) && kind==='sourcepicker')committed=value;
    };
    input.onkeydown=e => { if(e.key==='Enter' && !e.isComposing && !spec.multiline) { e.preventDefault(); void commit(); } };
    input.onblur=() => void commit();
    if(kind==='sourcepicker') {
      input.title='双击选择文件或目录，也可以直接输入路径';
      input.ondblclick=async () => {
        if(!active() || selecting || pending.has(activity)) return;
        const initial=input.value;
        selecting=true; updateBusy(input,true);
        try {
          const value=await hooks.selectSource(spec.selectCommand,scope,initial);
          if(value && active() && input.value===initial) { set(value); selecting=false; await commit(); }
        } catch(error) { if(active())hooks.report(error); }
        finally { selecting=false; updateBusy(input,false); }
      };
    }
    return controlCells(spec,input);
  }
  function layout() {
    frame=null; if(!active() || !section.isConnected || !section.clientWidth) return;
    const view=section.ownerDocument.defaultView;
    for(const row of rows) {
      // REQ-WEB-047: measure in the actual document and wrapper, after fonts/styles have settled.
      // Keep declared minima separate; every layout can follow font changes and document adoption.
      const width=Math.max(1,parseFloat(view.getComputedStyle(row.element).width));
      const cells=row.cells.map(cell=>{
        const singleLine=cell.element.matches('.panel-action,.panel-switch');
        if(cell.min!==undefined&&!singleLine)return cell;
        const probe=section.ownerDocument.createElement('div');probe.className='panel-cell';probe.inert=true;probe.setAttribute('aria-hidden','true');
        Object.assign(probe.style,{position:'fixed',visibility:'hidden',width:'max-content',left:'0',top:'0',pointerEvents:'none'});
        const clone=cell.element.cloneNode(true);clone.removeAttribute('id');
        Object.assign(clone.style,{width:'max-content',maxWidth:singleLine?'none':'260px'});
        probe.append(clone);row.element.append(probe);
        const measured=Math.ceil(clone.getBoundingClientRect().width);probe.remove();
        return {...cell,min:Math.max(1,cell.min??0,singleLine?measured:Math.min(260,measured))};
      });
      let top=0;
      for(const line of panelLines(cells,width,row.mode)) {
        let left=0;
        for(const {index,width} of line) { const c=row.cells[index]; c.wrapper.style.width=`${width}px`; c.wrapper.style.left=`${left}px`; c.wrapper.classList.toggle('panel-cell-separated',left>0);if(c.element.matches('.panel-action'))updateActionTitle(c);left+=width+12; }
        const height=Math.max(28,...line.map(({index}) => row.cells[index].wrapper.offsetHeight));
        for(const {index} of line) { const c=row.cells[index]; c.wrapper.style.top=`${top+(height-c.wrapper.offsetHeight)/2}px`; }
        top+=height+3;
      }
      row.element.style.height=`${Math.max(0,top-3)}px`;
    }
  }
  function bindFonts() {
    const doc=section.ownerDocument;if(fontDocument===doc)return;
    fontDocument?.fonts.removeEventListener('loadingdone',scheduleLayout);
    fontDocument?.fonts.removeEventListener('loadingerror',scheduleLayout);
    fontDocument=doc;
    doc.fonts.addEventListener('loadingdone',scheduleLayout);doc.fonts.addEventListener('loadingerror',scheduleLayout);
    void doc.fonts.ready.then(()=>{if(!disposed&&fontDocument===doc)scheduleLayout();});
  }
  function scheduleLayout() { if(disposed)return;bindFonts();if(frame==null) {frameWindow=section.ownerDocument.defaultView;frame=frameWindow.requestAnimationFrame(layout);} }
  for(const row of node.rows) {
    const element=el('div',undefined,'panel-row'), cells=row.widgets.flatMap(build);
    for(const cell of cells) { cell.wrapper=el('div',undefined,'panel-cell'); cell.wrapper.append(cell.element); element.append(cell.wrapper); }
    rows.push({element,cells,mode:row.mode ?? 'flex'}); section.append(element);
  }
  const observer=new ResizeObserver(()=>{scheduleLayout();for(const activate of activators)activate();}); observer.observe(section);
  (page.revealers??=[]).push(()=>{if(isVisible(section)){scheduleLayout();for(const activate of activators)activate();}});
  (page.relocators??=[]).push(()=>{frameWindow?.cancelAnimationFrame(frame);frame=null;closeMenu(scope);scheduleLayout();for(const activate of activators)activate();});
  scope.disposers.push(() => { disposed=true; observer.disconnect(); frameWindow?.cancelAnimationFrame(frame);fontDocument?.fonts.removeEventListener('loadingdone',scheduleLayout);fontDocument?.fonts.removeEventListener('loadingerror',scheduleLayout);closeMenu(scope); });
  scheduleLayout(); return section;
}
