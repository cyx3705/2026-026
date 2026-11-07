import {isVisible,setRunning} from './dom.js';
import {bindArgs} from './protocol.js';
import {applyTableUpdate,validateTableSource} from './table-data.js';
import {columnWeight, columnOrder, allocateWidths, resizeWidths} from './table-layout.js';
import {createTableViewport} from './table-viewport.js';
import {tableActions,tableArguments} from './table-actions.js';

const el = (tag, text, cls) => {
  const e = document.createElement(tag);
  if (text !== undefined) e.textContent = text;
  if (cls) e.className = cls;
  return e;
};
const textOf = value => value == null ? '' : typeof value === 'object' ? JSON.stringify(value) : String(value);

export async function renderTable(node, context, hooks) {
  const box = el('section', undefined, 'table-section');
  const scroll = el('div', undefined, 'table-scroll'), notice = el('p', '', 'notice'); notice.hidden = true;
  const empty=el('p','暂无数据','table-empty');empty.hidden=true;
  const actionNotice=el('p','','notice table-action-notice');actionNotice.hidden=true;
  box.dataset.table = node.id ?? ''; box.append(actionNotice,notice, scroll);
  try {validateTableSource(node.dataSource);}catch(error){notice.textContent=error.message;notice.hidden=false;return box;}
  const storageKey = node.id ? `aurora.web.table.v1:${JSON.stringify([context.owner, context.pageId, node.id])}` : null;
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(storageKey)) ?? {}; } catch { /* Unavailable storage uses defaults. */ }
  let columns = node.columns ?? [], order = [], weights = {}, widths = [], colgroup;
  let data={rows:[],revision:null},rows=data.rows,selectedKey,selected,sourceKey,request=0,disposed=false,pendingVisible=true,menu,cancelGesture,viewportWidth=0;
  let running,latestArgs,dirty=false,abort,renderedSchema,updating=false;
  const pending = new Set();
  const declaration=id=>hooks.declaration?hooks.declaration(id):{danger:hooks.danger?.(id)};
  let actions=tableActions(node,declaration),inline=actions.rows.filter(a=>a.inline!==false);
  const pendingKey=(row,index,id)=>JSON.stringify([identity(row,index),id.toLowerCase()]);
  const identity = (row, index) => node.dataSource.rowKey ? row[node.dataSource.rowKey] : index;
  const active = () => !disposed && hooks.active();
  const viewport=createTableViewport({scroll,identity,makeRow,selectedKey:()=>selectedKey,
    onNavigate:view=>select(view.row,view.index,view.tr),onChange:()=>{cancelGesture?.();closeMenu();},
    mutate:work=>{updating=true;try{work();}finally{updating=false;}}});
  const save = () => {
    if (!storageKey) return;
    try { localStorage.setItem(storageKey, JSON.stringify({order, weights})); }
    catch { hooks.report(new Error('浏览器无法保存表格列布局')); }
  };
  const closeMenu = (restore = false) => {
    if (!menu) return;
    const opener = menu.opener; menu.abort.abort(); menu.remove(); menu = null;
    if (restore && opener?.isConnected) opener.focus();
  };
  function openMenu(items, x, y, opener) {
    closeMenu();
    const document=box.ownerDocument,window=document.defaultView,{innerWidth,innerHeight}=window;
    menu = el('div', undefined, 'table-menu'); menu.setAttribute('role', 'menu');
    menu.opener = opener; menu.abort = new AbortController();
    const signal = menu.abort.signal;
    for (const item of items) {
      const b = el('button', item.title, item.danger ? 'danger' : ''); b.type = 'button';
      b.setAttribute('role', 'menuitem'); b.disabled = !!item.disabled;
      b.onclick = () => { closeMenu(true); Promise.resolve().then(item.run).catch(hooks.report); };
      menu.append(b);
    }
    (box.closest('dialog') ?? document.body).append(menu);
    menu.style.left = `${Math.max(0, Math.min(x, innerWidth - menu.offsetWidth))}px`;
    menu.style.top = `${Math.max(0, Math.min(y, innerHeight - menu.offsetHeight))}px`;
    menu.querySelector('button:not(:disabled)')?.focus();
    document.addEventListener('pointerdown', e => { if (menu && !menu.contains(e.target)) closeMenu(); }, {signal, capture:true});
    document.addEventListener('keydown', e => {
      if (!menu) return;
      if (e.key === 'Escape' || e.key === 'Tab') { e.preventDefault(); e.stopPropagation(); closeMenu(true); }
      else if (['ArrowDown','ArrowUp','Home','End'].includes(e.key)) {
        e.preventDefault(); const buttons = [...menu.querySelectorAll('button:not(:disabled)')];
        const i = buttons.indexOf(document.activeElement);
        buttons[e.key === 'Home' ? 0 : e.key === 'End' ? buttons.length - 1 : (i + (e.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length]?.focus();
      }
    }, {signal, capture:true});
    window.addEventListener('resize', () => closeMenu(), {signal});
    scroll.addEventListener('scroll', () => closeMenu(), {signal});
  }
  function select(row, index, tr) {
    if(!active())return;
    selectedKey = identity(row, index); selected = row;
    viewport.selection();
    if (node.channel) hooks.publish(node.channel, row);
  }
  const resolveOverrides = spec => tableArguments(spec.args,context.tables);
  async function run(spec, row, index) {
    const key = pendingKey(row,index,spec.action);
    if (pending.has(key) || !active()) return false;
    if(!declaration(spec.action))throw new Error(`未声明的动作：${spec.action}`);
    const snapshot = {...row}, additions = resolveOverrides(spec);
    pending.add(key);
    for(const button of scroll.querySelectorAll('button[data-pending-key]'))
      if(button.dataset.pendingKey===key)setRunning(button,true);
    try { await hooks.action(spec.action, snapshot, additions);return true; }
    finally {
      pending.delete(key);
      // Successful actions can rebuild the rows while this action is still pending.
      for (const button of scroll.querySelectorAll('button[data-pending-key]'))
        if (button.dataset.pendingKey === key) setRunning(button,false);
    }
  }
  function rowMenu(row, index, tr, x, y) {
    if(!active())return;
    select(row, index, tr);
    const snapshot = {...row};
    if (!actions.rows.length) return;
    // Freeze explicit cross-table arguments when opening, too.
    const items = actions.rows.map(spec => {
      let additions, error;
      try { additions = resolveOverrides(spec); } catch (e) { error = e; }
      return {title:spec.title, danger:spec.danger,
        disabled:pending.has(pendingKey(row,index,spec.action)),
        run:async () => {
          if (error) throw error;
          const frozen = {...spec, args:Object.fromEntries(Object.entries(additions).map(([k,v]) => [k,{value:v}]))};
          await run(frozen, snapshot, index);
        }};
    });
    openMenu(items, x, y, tr);
  }
  function fit() {
    if (!colgroup || !scroll.clientWidth) return;
    viewportWidth = scroll.clientWidth;
    const actionWidth = inline.length ? Math.min(scroll.clientWidth * .45,
      Math.max(48, inline.reduce((sum, a) => sum + Math.max(36, (a.title || '执行').length * 13 + 20), 16))) : 0;
    widths = allocateWidths(order.map(key => weights[key]), scroll.clientWidth - actionWidth);
    [...colgroup.children].forEach((col, i) => { col.style.width = `${i < widths.length ? widths[i] : actionWidth}px`; });
  }
  function moveColumn(key, target) {
    const star = columns.find(c => c.width === '*')?.key;
    if (key === star || key === target) return;
    const next = order.filter(k => k !== key); next.splice(next.indexOf(target), 0, key);
    order = columnOrder(columns, next); save(); draw();
  }
  function reset() { saved = {}; weights = Object.fromEntries(columns.map(c => [c.key, columnWeight(c.width)])); order = columnOrder(columns); save(); draw(); }
  function headerMenu(column, th, x, y) {
    if(!active())return;
    const i = order.indexOf(column.key), star = columns.find(c => c.width === '*')?.key;
    openMenu([
      {title:'向左移动列', disabled:i === 0 || column.key === star, run:() => moveColumn(column.key, order[i-1])},
      {title:'向右移动列', disabled:i >= order.length-1 || column.key === star || order[i+1] === star,
        run:() => { const next = [...order]; [next[i], next[i+1]] = [next[i+1], next[i]]; order = next; save(); draw(); }},
      {title:'恢复默认列宽和顺序', run:reset}
    ], x, y, th);
  }
  function gesture(e, th, column, resizing) {
    if (e.button !== 0||!active()) return;
    if (viewportWidth !== scroll.clientWidth || widths.length !== order.length) fit();
    e.preventDefault(); e.stopPropagation(); closeMenu(); cancelGesture?.();
    const document=box.ownerDocument,window=document.defaultView;
    const start = e.clientX, initial = [...widths], i = order.indexOf(column.key), abort = new AbortController();
    const signal = abort.signal; let moved = false, target;
    const finish = cancelled => {
      abort.abort(); th.classList.remove('column-dragging');
      scroll.querySelectorAll('.column-drop').forEach(n => n.classList.remove('column-drop'));
      if (resizing) {
        if (cancelled) { widths = initial; [...colgroup.children].slice(0, widths.length).forEach((c,j) => c.style.width = `${widths[j]}px`); }
        else if (moved) { order.forEach((key,j) => weights[key] = widths[j]); save(); }
      } else if (moved && target && !cancelled) moveColumn(column.key, target);
      cancelGesture = null;
    };
    cancelGesture = () => finish(true);
    document.addEventListener('pointermove', event => {
      if (Math.abs(event.clientX-start) < 4 && !moved) return;
      moved = true;
      if (resizing) {
        widths = resizeWidths(initial, i, event.clientX-start);
        [...colgroup.children].slice(0,widths.length).forEach((c,j) => c.style.width = `${widths[j]}px`);
      } else {
        th.classList.add('column-dragging');
        const hit = document.elementFromPoint(event.clientX,event.clientY)?.closest('th[data-column]');
        scroll.querySelectorAll('.column-drop').forEach(n => n.classList.remove('column-drop'));
        if (hit && scroll.contains(hit)) { target = hit.dataset.column; hit.classList.add('column-drop'); }
        else target = null;
      }
    }, {signal});
    document.addEventListener('pointerup', () => finish(false), {signal,once:true});
    document.addEventListener('pointercancel', () => finish(true), {signal,once:true});
    window.addEventListener('blur', () => finish(true), {signal,once:true});
  }
  function draw({dataOnly=false}={}) {
    const schema=JSON.stringify([columns,order]),existing=scroll.querySelector('table');
    empty.hidden=rows.length!==0;
    if(dataOnly&&existing&&schema===renderedSchema){viewport.reconcile(rows,existing.tBodies[0]);return;}
    cancelGesture?.(); closeMenu();
    const table = el('table', undefined, 'aurora-table'), head = el('thead'), header = el('tr'), body = el('tbody');
    colgroup = el('colgroup');
    order.forEach((key, index) => {
      const column = columns.find(c => c.key === key), th = el('th');
      th.dataset.column = key; th.tabIndex = 0; th.scope = 'col';
      const title = el('span', column.title || key, 'column-title'); th.append(title); th.title = `${column.title || key}：拖动调整位置；右键调整列`;
      th.oncontextmenu = e => { e.preventDefault(); headerMenu(column, th, e.clientX, e.clientY); };
      th.onpointerdown = e => { if (column !== columns.find(c => c.width === '*')) gesture(e, th, column, false); };
      th.onkeydown = e => {
        if (e.key === 'ContextMenu' || e.key === 'F10' && e.shiftKey) { e.preventDefault(); const r = th.getBoundingClientRect(); headerMenu(column,th,r.x,r.bottom); }
        if (e.altKey && ['ArrowLeft','ArrowRight'].includes(e.key)) {
          e.preventDefault(); const next = index + (e.key === 'ArrowLeft' ? -1 : 1), star = columns.find(c => c.width === '*')?.key;
          if (next >= 0 && next < order.length && key !== star && order[next] !== star) {
            [order[index], order[next]] = [order[next],order[index]]; save(); draw();
            [...scroll.querySelectorAll('th')].find(h => h.dataset.column === key)?.focus();
          }
        }
      };
      if (index < order.length-1) {
        const handle = el('span', undefined, 'column-resize'); handle.tabIndex = 0; handle.setAttribute('role','separator');
        handle.setAttribute('aria-label', `调整${column.title || key}列宽`); handle.setAttribute('aria-orientation','vertical');
        handle.onpointerdown = e => gesture(e,th,column,true);
        handle.ondblclick = e => { e.stopPropagation(); reset(); };
        handle.onkeydown = e => {
          if (['ArrowLeft','ArrowRight'].includes(e.key)) {
            e.preventDefault(); e.stopPropagation(); widths = resizeWidths(widths,index,e.key === 'ArrowLeft' ? -8 : 8);
            order.forEach((k,j) => weights[k] = widths[j]); save(); fit();
          }
        };
        th.append(handle);
      }
      header.append(th); colgroup.append(el('col'));
    });
    if (inline.length) { header.append(el('th','操作','row-action-header')); colgroup.append(el('col')); }
    head.append(header); table.append(colgroup,head,body);
    renderedSchema=schema;
    viewport.reconcile(rows,body);
    const top = scroll.scrollTop,left=scroll.scrollLeft; scroll.replaceChildren(table,empty); fit(); scroll.scrollTop=top;scroll.scrollLeft=left;
  }
  function makeRow(row,index) {
    const tr = el('tr'); tr.tabIndex = 0; tr.setAttribute('aria-selected',String(identity(row,index) === selectedKey));
    const view={row,index,tr};
    tr.onclick = () => select(row,view.index,tr);
    tr.oncontextmenu = e => { e.preventDefault(); rowMenu(row,view.index,tr,e.clientX,e.clientY); };
    tr.onkeydown = e => {
      if (e.target !== tr) return;
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); select(row,view.index,tr); }
      if (e.key === 'ContextMenu' || e.key === 'F10' && e.shiftKey) { e.preventDefault(); const r=tr.getBoundingClientRect(); rowMenu(row,view.index,tr,r.x+8,r.bottom); }
      viewport.keydown(e,view);
    };
    const cells=order.map(key=>{
      const column=columns.find(c=>c.key===key),td=el('td'),value=textOf(row[key]);td.title=value;tr.append(td);
      let signature;
      return ()=>{
        const binding=actions.columns.get(key),editable=binding?.edit&&(!column.editableWhen||String(row[column.editableWhen]).trim().toLowerCase()==='true');
        const mode=editable?'edit':binding&&!binding.edit&&value.trim()?(binding.style==='double'?'double':'button'):'text';
        const next=JSON.stringify([mode,binding?.id,binding?.style]);
        if(signature!==next){
          view.cancelEdit?.(td);td.replaceChildren();td.classList.toggle('editable-cell',mode==='edit');td.ondblclick=null;
          if(mode==='button'){
            const b=el('button',value,binding.style==='button'?'':'cell-link');b.type='button';
            b.onclick=()=>{select(row,view.index,tr);void run({action:binding.id},row,view.index).catch(hooks.report);};td.append(b);
          }else {
            td.textContent=value;
            if(mode==='edit')td.ondblclick=()=>edit(td,column,view);
            if(mode==='double')td.ondblclick=()=>{select(row,view.index,tr);void run({action:binding.id},row,view.index).catch(hooks.report);};
          }
          signature=next;
        }
        const b=td.querySelector('button');
        if(b){b.classList.toggle('danger',!!binding.danger);b.title=binding.summary;
          b.dataset.pendingKey=pendingKey(row,view.index,binding.id);setRunning(b,pending.has(b.dataset.pendingKey));}
      };
    });
    view.rebind=()=>{
      cells.forEach(bind=>bind());
      let td=tr.querySelector('.row-actions');
      if(!inline.length){td?.remove();return;}
      if(!td){td=el('td',undefined,'row-actions');tr.append(td);}
      const existing=new Map([...td.children].map(b=>[b.dataset.binding,b]));
      const buttons=inline.map((spec,index)=>{
        const key=JSON.stringify([index,spec.action.toLowerCase()]);
        const b=existing.get(key)??el('button');b.type='button';b.dataset.binding=key;
        b.dataset.action=spec.action.toLowerCase();b.textContent=spec.title;b.title=spec.summary||spec.action;
        b.classList.toggle('danger',!!spec.danger);b.dataset.pendingKey=pendingKey(row,view.index,spec.action);
        setRunning(b,pending.has(b.dataset.pendingKey));
        b.onclick=async()=>{select(row,view.index,tr);try{await run(spec,row,view.index);}catch(e){hooks.report(e);}};
        return b;
      });
      for(const button of [...td.children])if(!buttons.includes(button))button.remove();
      buttons.forEach((button,index)=>{if(td.children[index]!==button)td.insertBefore(button,td.children[index]??null);});
    };
    view.rebind();
    return view;
  }
  function edit(td,column,view) {
    if (!active()||td.querySelector('input')||pending.has(pendingKey(view.row,view.index,column.cellEditAction))) return;
    const {row,index}=view;view.editing=true;
    const ticket=Symbol('edit');td.editTicket=ticket;
    const snapshot = {...row}, original = textOf(row[column.key]), input = el('input'); input.value = original;
    input.setAttribute('aria-label',`编辑${column.title || column.key}`); td.replaceChildren(input); input.focus(); input.select();
    let finished = false;
    const cancel = () => { if (!finished) { finished=true;view.editing=false;td.textContent=original;viewport.schedule(); } };
    view.cancelEdit=cell=>{if(cell===td){td.editTicket=undefined;cancel();}};
    input.onblur = () => {if(!updating)cancel();};
    input.onkeydown = async e => {
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancel(); }
      if (e.key !== 'Enter' || finished || e.isComposing) return;
      e.preventDefault(); e.stopPropagation();
      if(input.value===original){cancel();return;}
      finished=true;view.editing=false;input.disabled=true;viewport.schedule();
      try { const saved=await run({action:column.cellEditAction},{...snapshot,[column.key]:input.value},index); if (td.isConnected&&td.editTicket===ticket) td.textContent=saved?input.value:original; }
      catch (error) { if(td.isConnected&&td.editTicket===ticket)td.textContent=original;if(active())hooks.report(error); }
      finally {view.editing=false;viewport.schedule();}
    };
  }
  function clearRows() {
    closeMenu();cancelGesture?.();data={rows:[],revision:null};rows=data.rows;selectedKey=undefined;selected=undefined;
    viewport.clear();renderedSchema=undefined;colgroup=undefined;scroll.replaceChildren();
    if(node.channel)hooks.publish(node.channel,undefined);
  }
  // One in-flight request per table. Further requests invalidate that response and
  // collapse into one follow-up using the latest source and last accepted revision.
  function refresh() {
    if(!active())return Promise.resolve();
    ++request;dirty=true;
    try {
      latestArgs=bindArgs(node.dataSource.args,{},{},hooks.selection());
      const next=JSON.stringify(latestArgs);previous=next;
      if(sourceKey!==next){sourceKey=next;clearRows();}
    }catch(error){
      latestArgs=undefined;previous=null;sourceKey=undefined;dirty=false;clearRows();
      notice.textContent=error.message.startsWith('无法取得参数')?'请先选择相关条目':error.message;notice.hidden=false;
      return running??Promise.resolve();
    }
    if(!isVisible(box)){pendingVisible=true;return running??Promise.resolve();}
    pendingVisible=false;
    return startReload();
  }
  function startReload() {
    if(!running)running=Promise.resolve().then(reload).finally(()=>{
      running=null;
      // A channel follower can request again in a microtask after reload exits
      // but before this finalizer runs. Keep that request in the same wait chain.
      if(dirty&&active()&&latestArgs){
        if(isVisible(box))return startReload();
        pendingVisible=true;
      }
    });
    return running;
  }
  async function reload() {
    while(dirty&&active()&&latestArgs) {
      if(!isVisible(box)){pendingVisible=true;return;}
      dirty=false;
      const ticket=request,args=latestArgs,key=sourceKey,delta=!!(data.revision&&node.dataSource.deltaCommand);
      abort=new AbortController();
      try {
        const result=await hooks.invoke(delta?node.dataSource.deltaCommand:node.dataSource.command,
          delta?{...args,since:data.revision}:args,{signal:abort.signal});
        if(ticket!==request||key!==sourceKey||!active())continue;
        const next=applyTableUpdate(data,result,{rowKey:node.dataSource.rowKey,deltaAllowed:delta,
          columnKeys:node.columns?.length?node.columns.map(column=>column.key):undefined});
        rows=next.rows;
        if(!next.delta||!columns.length)columns=node.columns?.length?node.columns:next.columnKeys.map(key=>({key}));
        order=columnOrder(columns,order.length?order:Array.isArray(saved.order)?saved.order:[]);
        weights=Object.fromEntries(columns.map(column=>[column.key,columnWeight(weights[column.key]??saved.weights?.[column.key]??column.width)]));
        selected=rows.find((row,index)=>identity(row,index)===selectedKey);
        if(!selected)selectedKey=undefined;
        notice.hidden=true;draw({dataOnly:true});data=next;
        if(node.channel)hooks.publish(node.channel,selected);
      }catch(error){
        if(ticket!==request||!active())continue;
        notice.textContent=error.message;notice.hidden=false;
      }finally{abort=null;}
    }
  }
  function showActionIssues() {
    actionNotice.textContent=actions.errors.length?'表格操作未绑定：'+actions.errors.join('；'):'';
    actionNotice.hidden=!actions.errors.length;
  }
  const actionIds=new Set([...(node.columns??[]).flatMap(column=>[column.cellAction,column.cellEditAction]),
    ...(node.rowActions??[]).map(spec=>spec.action)].filter(id=>typeof id==='string').map(id=>id.toLowerCase()));
  (context.actionBindings??=[]).push(changed=>{
    if(!active()||changed&&![...changed].some(id=>actionIds.has(id.toLowerCase())))return false;
    const hadInline=inline.length>0;
    actions=tableActions(node,declaration);inline=actions.rows.filter(spec=>spec.inline!==false);
    closeMenu();showActionIssues();
    const header=scroll.querySelector('thead tr');
    if(header&&hadInline!==!!inline.length){
      if(inline.length){header.append(el('th','操作','row-action-header'));colgroup.append(el('col'));}
      else {header.querySelector('.row-action-header')?.remove();colgroup.lastElementChild?.remove();}
    }
    viewport.updateViews(view=>view.rebind());fit();return true;
  });
  showActionIssues();
  if (node.id) context.tables.set(node.id, {selected:() => selected,refresh});
  (context.dataBindings??=[]).push({node:node.id??'',refresh});
  const reveal=()=>{if(pendingVisible&&isVisible(box))return refresh();};
  (context.revealers??=[]).push(()=>{viewport.schedule();return reveal();});
  const observer = new ResizeObserver(() => {
    void reveal();
    viewport.schedule();
    if (scroll.clientWidth === viewportWidth) return;
    cancelGesture?.(); fit();
  }); observer.observe(scroll);
  (context.relocators??=[]).push(()=>{cancelGesture?.();closeMenu();fit();viewport.relocate();void reveal();});
  context.disposers.push(() => { disposed=true; ++request;dirty=false;abort?.abort();observer.disconnect();cancelGesture?.();closeMenu();viewport.dispose(); });
  let previous;
  if (JSON.stringify(node.dataSource?.args ?? {}).includes('{selection.')) context.followers.push(() => {
    let next;
    try { next=JSON.stringify(bindArgs(node.dataSource.args,{}, {},hooks.selection())); } catch { next=null; }
    if (next !== previous) { previous=next; void refresh(); }
  });
  context.refreshers.push(refresh);
  (context.initializers ??= []).push(refresh); return box;
}
