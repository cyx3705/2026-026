import {bindArgs, rowsOf} from './protocol.js';
import {showMenu, closeMenu} from './menu.js';
import {widgetKind, booleanValue, normalizeOptions, chooseOption, panelLines, validatePanel} from './panel-model.js';

const el=(tag,text,cls) => { const e=document.createElement(tag); if(text!==undefined) e.textContent=text; if(cls) e.className=cls; return e; };
export function renderPanel(node, page, hooks) {
  const section=el('section',undefined,'panel control-panel'); section.dataset.panel=node.id ?? 'page-panel';
  try { validatePanel(node); } catch(error) { section.append(el('p',error.message,'notice')); hooks.report(error); return section; }
  const scope={...page,inputs:new Map(),panelId:node.id ?? 'page-panel',panelElement:section};
  page.panels.push(scope);
  let disposed=false, frame;
  const active=() => !disposed && hooks.active();
  const rows=[], pending=new Set();
  function publish(spec,value) { if (spec.channel) hooks.publish(spec.channel,{value:String(value)}); }
  async function run(id, additions={}, control) {
    const key=control ?? id;
    if (!active() || pending.has(key)) return;
    pending.add(key); updateBusy(control,true);
    try { await hooks.action(id,scope,additions); }
    catch(error) { hooks.report(error); }
    finally { pending.delete(key); updateBusy(control,false); }
  }
  function updateBusy(control,busy) {
    if (!control) return;
    control.setAttribute('aria-busy',String(busy));
    control.classList.toggle('is-running',busy);
    if ('disabled' in control) control.disabled=busy || !!control.gated;
  }
  function register(spec,input,set,get=() => input.value) {
    if (input.tagName !== 'INPUT' && input.tagName !== 'TEXTAREA') Object.defineProperty(input,'value',{get,set:value => set(String(value))});
    input.setValue=value => set(String(value)); scope.inputs.set(spec.id,input);
    input.dataset.control=spec.id;
    publish(spec,get());
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
      return {title:item.title ?? declaration?.title ?? item.action,disabled:!declaration || pending.has(item.action),
        reason:declaration?.summary ?? `未声明的动作：${item.action}`,danger:item.style?.toLowerCase()==='danger' || declaration?.danger,
        run:() => run(item.action,{value})};
    }),opener,{x:event?.clientX,y:event?.clientY,owner:scope,report:hooks.report});
  }
  function build(spec) {
    const kind=widgetKind(spec.kind);
    if(kind==='text') return [{element:el('span',spec.text ?? '', 'panel-text'),min:spec.minWidth,flex:spec.flex}];
    if(kind==='button') {
      const declaration=hooks.declaration(spec.action);
      if(!declaration) return [{element:el('span',`未声明的动作：${spec.action ?? ''}`,'notice')}];
      const b=el('button',spec.text ?? declaration.title ?? spec.action, declaration.danger ? 'danger' : ''); b.type='button';
      b.title=declaration.summary || `${spec.action} → ${declaration.command}`;
      if(spec.icon==='refresh-cw') { const icon=el('span','↻','panel-icon'); icon.setAttribute('aria-hidden','true'); b.prepend(icon); }
      b.onclick=() => void run(spec.action,{},b);
      if(spec.enabledWhen?.selected) {
        const sync=() => {
          b.gated=!hooks.selection()[spec.enabledWhen.selected]; b.disabled=b.gated || pending.has(b);
          b.title=b.gated ? `请先选择条目（${spec.enabledWhen.selected}）` : declaration.summary || spec.action;
        }; scope.followers.push(sync); sync();
      }
      return [{element:b,min:spec.minWidth,flex:spec.flex}];
    }
    if(kind==='switch') {
      let value=booleanValue(spec.value); const b=el('button',spec.label ?? spec.id ?? '', 'panel-switch'); b.type='button'; b.setAttribute('role','switch');
      const set=next => { value=booleanValue(next); b.setAttribute('aria-checked',String(value)); publish(spec,String(value)); };
      register(spec,b,set,() => String(value)); set(String(value));
      b.onclick=() => { set(String(!value)); void run(spec.action || spec.commitAction,{value:String(value)},b); };
      return [{element:b,min:spec.minWidth ?? (b.textContent ? undefined : 48),flex:spec.flex}];
    }
    if(kind==='choices' || spec.mode?.toLowerCase()==='select') {
      let options=normalizeOptions(spec.options ?? []), value=chooseOption(options,undefined,spec.value), request=0, previousArgs, queued;
      const input=el(kind==='choices' ? 'div' : 'button',undefined,kind==='choices' ? 'panel-choices' : 'panel-option');
      if(kind==='choices') { input.setAttribute('role','group'); } else { input.type='button'; input.setAttribute('aria-haspopup','menu'); }
      const set=(next,user=false) => {
        const found=options.find(o => o.value.toLowerCase()===String(next).toLowerCase());
        const changed=value!==(found?.value ?? ''); value=found?.value ?? ''; paint(); publish(spec,value);
        if(user && changed && value && spec.commitAction) void run(spec.commitAction,{value},input);
      };
      function paint() {
        if(kind!=='choices') { input.textContent=value; input.title=value; return; }
        input.replaceChildren(...options.map(option => {
          const b=el('button',option.text,'panel-choice'); b.type='button'; b.dataset.value=option.value;
          b.setAttribute('aria-pressed',String(option.value===value)); b.title=option.value;
          b.onclick=() => set(option.value,true);
          b.oncontextmenu=e => menu(spec,b,option.value,e);
          b.onkeydown=e => { if(e.key==='ContextMenu' || e.key==='F10' && e.shiftKey) { e.preventDefault(); menu(spec,b,option.value); } };
          return b;
        }));
        scheduleLayout();
      }
      register(spec,input,set,() => value); paint();
      if(kind!=='choices') {
        const move=delta => { if(options.length && !pending.has(input)) { const index=options.findIndex(o=>o.value===value); set(options[(Math.max(0,index)+delta+options.length)%options.length].value,true); } };
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
        const sync=() => { const split=spec.follows.lastIndexOf('.'), next=hooks.selection()[spec.follows.slice(0,split)]?.[spec.follows.slice(split+1)] ?? '';
          if(last!==next) { last=next; set(next); } };
        scope.followers.push(sync); sync();
      }
      if(spec.optionsSource) {
        const load=async () => {
          const ticket=++request; let args;
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
        scope.initializers.push(queue); scope.refreshers.push(queue);
        if(JSON.stringify(spec.optionsSource.args ?? {}).includes('{selection.')) scope.followers.push(() => {
          let key; try { key=JSON.stringify(bindArgs(spec.optionsSource.args,{}, {},hooks.selection())); } catch { key=null; }
          if(key!==previousArgs) { previousArgs=key; ++request; void queue(); }
        });
      }
      return controlCells(spec,input);
    }
    const input=el(spec.multiline && kind==='textbox' ? 'textarea' : 'input'); if(input.tagName==='INPUT') input.type='text';
    input.value=spec.value ?? ''; input.placeholder=spec.placeholder ?? '';
    const set=value => { input.value=value; publish(spec,input.value); };
    register(spec,input,set); input.oninput=() => publish(spec,input.value);
    if(spec.follows && kind==='textbox') {
      let last=Symbol(); const sync=() => { const split=spec.follows.lastIndexOf('.');
        const next=hooks.selection()[spec.follows.slice(0,split)]?.[spec.follows.slice(split+1)] ?? '';
        if(last!==next) { last=next; set(next); } };
      scope.followers.push(sync); sync();
    }
    let selecting=false, committed;
    const commit=async () => {
      if(selecting || !spec.commitAction || spec.multiline || !active()) return;
      if(kind==='sourcepicker') { if(!input.value.trim() || input.value===committed) return; committed=input.value; }
      await run(spec.commitAction,{value:input.value},input);
    };
    input.onkeydown=e => { if(e.key==='Enter' && !e.isComposing && !spec.multiline) { e.preventDefault(); void commit(); } };
    input.onblur=() => void commit();
    if(kind==='sourcepicker') {
      input.title='双击选择文件或目录，也可以直接输入路径';
      input.ondblclick=async () => {
        if(selecting || pending.has(input)) return;
        selecting=true; updateBusy(input,true);
        try {
          const value=await hooks.selectSource(spec.selectCommand,scope,input.value);
          if(value && active()) { set(value); selecting=false; await commit(); }
        } catch(error) { hooks.report(error); }
        finally { selecting=false; updateBusy(input,false); }
      };
    }
    return controlCells(spec,input);
  }
  function layout() {
    frame=null; if(!active() || !section.isConnected || !section.clientWidth) return;
    const width=Math.max(1,section.clientWidth-16);
    for(const row of rows) {
      for(const cell of row.cells) if(cell.min===undefined) {
        const clone=cell.element.cloneNode(true); clone.removeAttribute('id'); clone.inert=true;
        clone.style.position='fixed'; clone.style.visibility='hidden'; clone.style.width='max-content'; clone.style.maxWidth='260px';
        section.append(clone); cell.min=Math.max(1,Math.min(260,clone.getBoundingClientRect().width)); clone.remove();
      }
      let top=0;
      for(const line of panelLines(row.cells,width,row.mode)) {
        let left=0;
        for(const {index,width} of line) { const c=row.cells[index]; c.wrapper.style.width=`${width}px`; c.wrapper.style.left=`${left}px`; c.wrapper.classList.toggle('panel-cell-separated',left>0); left+=width+12; }
        const height=Math.max(28,...line.map(({index}) => row.cells[index].wrapper.offsetHeight));
        for(const {index} of line) { const c=row.cells[index]; c.wrapper.style.top=`${top+(height-c.wrapper.offsetHeight)/2}px`; }
        top+=height+3;
      }
      row.element.style.height=`${Math.max(0,top-3)}px`;
    }
  }
  function scheduleLayout() { if(frame==null) frame=requestAnimationFrame(layout); }
  for(const row of node.rows) {
    const element=el('div',undefined,'panel-row'), cells=row.widgets.flatMap(build);
    for(const cell of cells) { cell.wrapper=el('div',undefined,'panel-cell'); cell.wrapper.append(cell.element); element.append(cell.wrapper); }
    rows.push({element,cells,mode:row.mode ?? 'flex'}); section.append(element);
  }
  const observer=new ResizeObserver(scheduleLayout); observer.observe(section);
  scope.disposers.push(() => { disposed=true; observer.disconnect(); cancelAnimationFrame(frame); closeMenu(scope); });
  scheduleLayout(); return section;
}
