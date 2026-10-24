import {bindArgs, rowsOf} from './protocol.js';
import {columnWeight, columnOrder, allocateWidths, resizeWidths} from './table-layout.js';

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
  box.dataset.table = node.id ?? ''; box.append(notice, scroll);
  const storageKey = node.id ? `aurora.web.table.v1:${JSON.stringify([context.owner, context.pageId, node.id])}` : null;
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(storageKey)) ?? {}; } catch { /* Unavailable storage uses defaults. */ }
  let columns = node.columns ?? [], order = [], weights = {}, widths = [], colgroup;
  let rows = [], selectedKey, selected, sourceKey, request = 0, disposed = false, menu, cancelGesture, viewportWidth=0;
  const pending = new Set(), inline = (node.rowActions ?? []).filter(a => a.inline !== false);
  const identity = (row, index) => row[node.dataSource.rowKey || 'id'] ?? index;
  const active = () => !disposed && hooks.active();
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
    selectedKey = identity(row, index); selected = row;
    for (const sibling of scroll.querySelectorAll('tbody tr')) sibling.setAttribute('aria-selected', String(sibling === tr));
    if (node.channel) hooks.publish(node.channel, row);
  }
  const resolveOverrides = spec => Object.fromEntries(Object.entries(spec.args ?? {}).map(([key, binding]) => {
    if (Object.hasOwn(binding, 'value')) return [key, binding.value];
    const match = /^(.+)\.selected\.(.+)$/.exec(binding.from ?? '');
    const value = match && context.tables.get(match[1])?.selected()?.[match[2]];
    if (value == null) throw new Error(`无法取得参数：${binding.from}`);
    return [key, value];
  }));
  async function run(spec, row, index) {
    const key = JSON.stringify([identity(row, index), spec.action]);
    if (pending.has(key) || !active()) return;
    const snapshot = {...row}, additions = resolveOverrides(spec);
    pending.add(key);
    try { await hooks.action(spec.action, snapshot, additions); }
    finally {
      pending.delete(key);
      // Successful actions can rebuild the rows while this action is still pending.
      for (const button of scroll.querySelectorAll('button[data-pending-key]'))
        if (button.dataset.pendingKey === key) button.disabled=false;
    }
  }
  function rowMenu(row, index, tr, x, y) {
    select(row, index, tr);
    const snapshot = {...row};
    if (!node.rowActions?.length) return;
    // Freeze explicit cross-table arguments when opening, too.
    const items = node.rowActions.map(spec => {
      let additions, error;
      try { additions = resolveOverrides(spec); } catch (e) { error = e; }
      return {title:spec.title || '执行', danger:spec.danger || hooks.danger(spec.action),
        disabled:pending.has(JSON.stringify([identity(row,index),spec.action])),
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
    const i = order.indexOf(column.key), star = columns.find(c => c.width === '*')?.key;
    openMenu([
      {title:'向左移动列', disabled:i === 0 || column.key === star, run:() => moveColumn(column.key, order[i-1])},
      {title:'向右移动列', disabled:i >= order.length-1 || column.key === star || order[i+1] === star,
        run:() => { const next = [...order]; [next[i], next[i+1]] = [next[i+1], next[i]]; order = next; save(); draw(); }},
      {title:'恢复默认列宽和顺序', run:reset}
    ], x, y, th);
  }
  function gesture(e, th, column, resizing) {
    if (e.button !== 0) return;
    if (viewportWidth !== scroll.clientWidth || widths.length !== order.length) fit();
    e.preventDefault(); e.stopPropagation(); closeMenu(); cancelGesture?.();
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
  function draw() {
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
    if (inline.length) { header.append(el('th','操作')); colgroup.append(el('col')); }
    head.append(header); table.append(colgroup,head,body);
    rows.forEach((row,index) => {
      const tr = el('tr'); tr.tabIndex = 0; tr.setAttribute('aria-selected',String(identity(row,index) === selectedKey));
      tr.onclick = () => select(row,index,tr);
      tr.oncontextmenu = e => { e.preventDefault(); rowMenu(row,index,tr,e.clientX,e.clientY); };
      tr.onkeydown = e => {
        if (e.target !== tr) return;
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); select(row,index,tr); }
        if (e.key === 'ContextMenu' || e.key === 'F10' && e.shiftKey) { e.preventDefault(); const r=tr.getBoundingClientRect(); rowMenu(row,index,tr,r.x+8,r.bottom); }
      };
      for (const key of order) {
        const column = columns.find(c => c.key === key), value = textOf(row[key]), td = el('td'); td.title = value;
        if (column.cellEditAction && (!column.editableWhen || String(row[column.editableWhen]).toLowerCase() === 'true')) {
          td.textContent = value; td.classList.add('editable-cell'); td.ondblclick = () => edit(td,column,row,index);
        } else if (column.cellAction && value && column.cellStyle !== 'double') {
          const b = el('button',value,column.cellStyle === 'button' ? '' : 'cell-link'); b.type='button';
          b.onclick = () => { select(row,index,tr); void run({action:column.cellAction},row,index).catch(hooks.report); }; td.append(b);
        } else { td.textContent = value; if (column.cellAction && value) td.ondblclick = () => void run({action:column.cellAction},row,index).catch(hooks.report); }
        tr.append(td);
      }
      if (inline.length) {
        const td = el('td',undefined,'row-actions');
        for (const spec of inline) {
          const b = el('button',spec.title || '执行',spec.danger || hooks.danger(spec.action) ? 'danger' : ''); b.type='button';
          b.dataset.pendingKey=JSON.stringify([identity(row,index),spec.action]);
          b.disabled = pending.has(JSON.stringify([identity(row,index),spec.action]));
          b.onclick = async () => { select(row,index,tr); b.disabled=true; try { await run(spec,row,index); } catch(e) { hooks.report(e); } finally { b.disabled=false; } }; td.append(b);
        }
        tr.append(td);
      }
      body.append(tr);
    });
    const top = scroll.scrollTop; scroll.replaceChildren(table); scroll.scrollTop = top; fit();
  }
  function edit(td,column,row,index) {
    if (td.querySelector('input')) return;
    const snapshot = {...row}, original = textOf(row[column.key]), input = el('input'); input.value = original;
    input.setAttribute('aria-label',`编辑${column.title || column.key}`); td.replaceChildren(input); input.focus(); input.select();
    let finished = false;
    const cancel = () => { if (!finished) { finished=true; td.textContent=original; } };
    input.onblur = cancel;
    input.onkeydown = async e => {
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancel(); }
      if (e.key !== 'Enter' || finished || e.isComposing) return;
      e.preventDefault(); e.stopPropagation(); finished=true; input.disabled=true;
      try { await run({action:column.cellEditAction},{...snapshot,[column.key]:input.value},index); if (td.isConnected) td.textContent=input.value; }
      catch (error) { td.textContent=original; hooks.report(error); }
    };
  }
  async function refresh() {
    const ticket = ++request;
    try {
      const args = bindArgs(node.dataSource.args, {}, {}, hooks.selection()), nextSource = JSON.stringify(args);
      previous = nextSource;
      if (sourceKey !== nextSource) {
        closeMenu(); cancelGesture?.();
        sourceKey = nextSource; selectedKey = undefined; selected=undefined; rows=[]; scroll.replaceChildren();
        if (node.channel) hooks.publish(node.channel,undefined);
      }
      const result = rowsOf(await hooks.invoke(node.dataSource.command,args));
      if (ticket !== request || !active()) return;
      rows = result;
      columns = node.columns?.length ? node.columns : [...new Set(rows.flatMap(r => Object.keys(r)))].map(key => ({key}));
      order = columnOrder(columns, order.length ? order : Array.isArray(saved.order) ? saved.order : []);
      weights = Object.fromEntries(columns.map(c => [c.key, columnWeight(weights[c.key] ?? saved.weights?.[c.key] ?? c.width)]));
      selected = rows.find((r,i) => identity(r,i) === selectedKey);
      notice.hidden = true; draw();
      if (node.channel) hooks.publish(node.channel, selected);
    } catch (error) {
      if (ticket !== request || !active()) return;
      if (error.message.startsWith('无法取得参数')) {
        closeMenu(); cancelGesture?.();
        sourceKey = undefined; selectedKey=undefined; selected=undefined; rows=[]; scroll.replaceChildren();
        if (node.channel) hooks.publish(node.channel,undefined);
      }
      notice.textContent = error.message.startsWith('无法取得参数') ? '请先选择相关条目' : error.message;
      notice.hidden = false;
    }
  }
  if (node.id) context.tables.set(node.id, {selected:() => selected});
  const observer = new ResizeObserver(() => {
    if (scroll.clientWidth === viewportWidth) return;
    cancelGesture?.(); fit();
  }); observer.observe(scroll);
  context.disposers.push(() => { disposed=true; ++request; observer.disconnect(); cancelGesture?.(); closeMenu(); });
  let previous;
  if (JSON.stringify(node.dataSource?.args ?? {}).includes('{selection.')) context.followers.push(() => {
    let next;
    try { next=JSON.stringify(bindArgs(node.dataSource.args,{}, {},hooks.selection())); } catch { next=null; }
    if (next !== previous) { previous=next; void refresh(); }
  });
  context.refreshers.push(refresh);
  await refresh(); return box;
}
