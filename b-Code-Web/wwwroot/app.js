import {payload, commandText, bindArgs, rowsOf, validateEnvelope} from './protocol.js';
import {Workspace} from './workspace.js';
import {renderTable} from './table.js';
import {renderPanel} from './panel.js';
import {selectSource} from './source-picker.js';

const $ = id => document.getElementById(id);
const state = {pages: [], actions: new Map(), selection: {}, generation: 0, contexts: new Set(), scene: null, activePage: null};
const mobile = matchMedia('(max-width:700px)');
const workspace = new Workspace({renderPage, floatPage, report, onSwitch(scene) {
  state.generation++; state.selection={}; clearContexts(); state.scene=scene;
  for(const modal of document.querySelectorAll('dialog')) modal.close();
}});
function clearContexts() {
  for (const context of state.contexts) for (const dispose of context.disposers) dispose();
  state.contexts.clear();
}
function publish(channel, value, context) {
  if (JSON.stringify(state.selection[channel]) === JSON.stringify(value)) return;
  state.selection[channel] = value;
  for (const current of state.contexts) for (const update of current.followers) update();
}
const el = (tag, text, cls) => {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = text;
  if (cls) element.className = cls;
  return element;
};
function report(error) { $('status').textContent = error.message ?? String(error); }
function button(text, handler) {
  const b = el('button', text); b.type = 'button';
  b.onclick = async () => {
    b.disabled = true;
    try { await handler(); } catch (error) { report(error); }
    finally { b.disabled = false; }
  };
  return b;
}
async function execute(text) {
  const response = await fetch('/api/command', {method:'POST',
    headers:{'Content-Type':'application/json', 'X-Aurora-Web':'1'}, body:JSON.stringify({text})});
  const result = await response.json();
  if (!response.ok || result.success === false) throw new Error(result.message || `连接失败 ${response.status}`);
  return result;
}
async function invoke(name, args) { return payload(await execute(commandText(name, args))); }

// REQ-WEB-002: modal state belongs to this browser document; focus returns to its opener.
function dialog(title, body) {
  const opener = document.activeElement;
  const modal = el('dialog'), heading = el('div', undefined, 'dialog-heading');
  const titleElement = el('h2', title); titleElement.id = `dialog-${crypto.randomUUID()}`;
  modal.setAttribute('aria-labelledby', titleElement.id);
  heading.append(titleElement, button(mobile.matches ? '返回' : '关闭', () => modal.close()));
  const content = el('div', undefined, 'dialog-body'); content.append(body);
  modal.append(heading, content);
  modal.addEventListener('click', event => { if (event.target === modal) {
    const rect = modal.getBoundingClientRect();
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) modal.close();
  }});
  const historyKey = crypto.randomUUID();
  if (mobile.matches) history.pushState({auroraDialog:historyKey}, '');
  const onBack = () => { if (modal.open) modal.close(); };
  window.addEventListener('popstate', onBack, {once:true});
  modal.addEventListener('close', () => {
    window.removeEventListener('popstate', onBack);
    if (history.state?.auroraDialog === historyKey) history.back();
    modal.remove();
    for (const current of state.contexts) if (current.modal === modal) { for (const dispose of current.disposers) dispose(); state.contexts.delete(current); }
    if (opener?.isConnected) opener.focus();
  }, {once:true});
  document.body.append(modal); modal.showModal();
  return modal;
}
async function floatPage(page) {
  if (!mobile.matches) {
    const url = new URL(location.href); url.searchParams.set('page',page.id);
    const child = window.open(url, '_blank', 'popup,width=1000,height=760');
    if (!child) throw new Error('浏览器阻止了新窗口，请允许本站弹出窗口');
    child.opener = null;
    return;
  }
  const body = el('div'), modal = dialog(page.title, body);
  await renderPage(page, body, modal);
}
async function action(id, context, row = {}, additions = {}) {
  const declaration = state.actions.get(id);
  if (!declaration) throw new Error(`未声明的动作：${id}`);
  const readValues = () => ({...Object.fromEntries([...context.inputs].map(([key,input]) => [key,
    input.type === 'checkbox' ? String(input.checked) : input.value])), ...additions});
  const values = readValues();
  const args = bindArgs(declaration.args, values, row, state.selection);
  const clear = (declaration.clearOnSuccess ?? []).map(id => {
    const input = context.inputs.get(id);
    if (!input) throw new Error(`clearOnSuccess 引用了不存在的控件：${id}`);
    return {input,value:input.value};
  });
  // UI operations must affect the requesting browser, never the server's WPF window.
  if (['aurora.ui.float','aurora.ui.show','aurora.scene.open'].includes(declaration.command)) {
    const page = state.pages.find(p => p.id === (args.name ?? args.page));
    if (!page) throw new Error('目标网页尚未注册');
    if (declaration.command === 'aurora.ui.float') await floatPage(page);
    else workspace.show(page.id);
    return;
  }
  if (declaration.command === 'aurora.ui.panelset') {
    const scopes=[...state.contexts].flatMap(current => current.panels).filter(scope => scope.panelId === args.panel);
    if (!scopes.length) throw new Error(`找不到面板：${args.panel}`);
    const inputs=scopes.map(scope => scope.inputs.get(args.control));
    if (inputs.some(input => !input)) throw new Error(`找不到面板控件：${args.control}`);
    for (const input of inputs) input.setValue(args.value);
    return;
  }
  if (declaration.command.startsWith('aurora.ui.') || declaration.command.startsWith('aurora.app.'))
    throw new Error('此桌面界面操作尚未适配 Web');
  const text = commandText(declaration.command,args);
  const result = await execute(text);
  report(result.message || '操作完成');
  // An old completion may clear only the original unchanged submission and target.
  let unchanged=false;
  try { unchanged=commandText(declaration.command,bindArgs(declaration.args,readValues(),row,state.selection)) === text; } catch { /* Target no longer exists. */ }
  if (context.generation === state.generation && unchanged && clear.every(({input,value}) => input.isConnected && input.value === value))
    for (const {input} of clear) input.setValue ? input.setValue('') : input.value = '';
  await Promise.all([...state.contexts].flatMap(current => current.refreshers.map(refresh => refresh())));
}
function panel(node, context) {
  return renderPanel(node,context,{
    invoke,report,selection:() => state.selection,
    active:() => context.generation === state.generation && state.contexts.has(context),
    publish:(channel,row) => publish(channel,row,context),
    declaration:id => state.actions.get(id),
    action:(id,scope,additions) => action(id,scope,{},additions),
    selectSource:(command,scope,initial) => selectSource(command,scope,initial,{
      dialog,selection:() => state.selection,execute:async text => payload(await execute(text))
    })
  });
}
async function render(node, context) {
  if (!node) return el('p', '此页面没有内容');
  const type = node.type?.toLowerCase();
  if (type === 'text') return el('p', node.text ?? '');
  if (type === 'panel') return panel(node, context);
  if (type === 'popup') {
    const content = panel(node, context);
    return button(node.text || '更多', () => dialog(node.text || '更多', content));
  }
  if (['stack','grid'].includes(type)) {
    const box = el('section', undefined, type === 'grid' ? 'grid' : node.orientation === 'horizontal' ? 'horizontal' : 'stack');
    for (const child of node.children ?? []) box.append(await render(child, context));
    return box;
  }
  if (type === 'switch' && !node.source) {
    const box = el('section'), tabs = el('div', undefined, 'tabs'), content = el('div');
    const children = await Promise.all((node.children ?? []).map(n => render(n, context)));
    (node.children ?? []).forEach((child, index) => tabs.append(button(child.case || String(index + 1),
      () => content.replaceChildren(children[index]))));
    if (children[0]) content.append(children[0]); box.append(tabs, content); return box;
  }
  if (type === 'table') return renderTable(node, context, {
    invoke, report, selection:() => state.selection,
    active:() => context.generation === state.generation && state.contexts.has(context),
    publish:(channel,row) => publish(channel,row,context),
    action:(id,row,additions) => action(id,context,row,additions),
    danger:id => state.actions.get(id)?.danger
  });
  return el('p', `Web 尚未适配组件：${node.type}${node.id ? `（${node.id}）` : ''}`, 'notice');
}
async function renderPage(page, target, modal = null) {
  if (page.localConsole) { renderConsole(target); return; }
  const context = {owner:page.owner, pageId:page.id, tables:new Map(), panels:[], disposers:[], inputs:new Map(), refreshers:[], followers:[], initializers:[], generation:state.generation, modal};
  state.contexts.add(context);
  const content = await render(page.content, context);
  await Promise.all(context.initializers.map(load => load()));
  if (context.generation === state.generation) target.replaceChildren(content);
}
function renderConsole(target) {
  const form = el('form'), input = el('input'), output = el('pre', '', 'output');
  input.placeholder = '输入 OneHistory 指令'; input.setAttribute('aria-label','指令');
  const run = button('执行', async () => {
    const result = await execute(input.value); output.textContent = result.message || JSON.stringify(result.data, null, 2);
  });
  form.onsubmit = e => { e.preventDefault(); if (!run.disabled) run.click(); };
  form.append(input, run); target.replaceChildren(form, output);
}
async function connect() {
  $('reload').disabled = true;
  report('正在读取模块页面…');
  try {
    const catalog = rowsOf(await invoke('vulcan.command.list'));
    const names = catalog.map(c => c.commandName ?? c.name);
    const pages = [], actions = new Map(), errors = [];
    for (const name of names.filter(n => n?.endsWith('.ui.describe') && !n.startsWith('aurora.'))) {
      try {
        const set = validateEnvelope(await invoke(name), 'pages');
        const actionName = name.replace(/describe$/, 'actions');
        const declared = names.includes(actionName) ? validateEnvelope(await invoke(actionName), 'actions', set.owner).actions : [];
        for (const a of declared) { if (actions.has(a.id)) throw new Error(`动作 id 冲突：${a.id}`); }
        for (const p of set.pages) { if (pages.some(existing => existing.id === p.id)) throw new Error(`页面 id 冲突：${p.id}`); }
        pages.push(...set.pages.map(p => ({...p, owner:set.owner})));
        for (const a of declared) actions.set(a.id, a);
      } catch (error) { errors.push(`${name}：${error.message}`); }
    }
    pages.push({id:'console',title:'控制台',owner:'HistoryAurora',scene:'HistoryAurora',
      localConsole:true,placement:{side:'bottom',ratio:.3,visible:true}});
    state.pages = pages; state.actions = actions; workspace.setPages(pages);
    const scenes = workspace.scenes;
    const floatingId = new URL(location.href).searchParams.get('page');
    if (floatingId) {
      const page = pages.find(p => p.id === floatingId);
      if (!page) throw new Error('弹出页面已不存在');
      document.body.classList.add('popout'); document.title = `${page.title} · OneHistory`;
      state.generation++; clearContexts();
      const body = el('div'); $('main').replaceChildren(el('h1',page.title),body);
      await renderPage(page,body);
    } else await workspace.open(scenes.includes(state.scene) ? state.scene
      : scenes.includes(workspace.saved.lastScene) ? workspace.saved.lastScene : scenes[0], true);
    report(`已连接 · ${catalog.length} 条指令 · ${pages.length} 个页面${errors.length ? ' · ' + errors.join('；') : ''}`);
  } catch (error) { report(error); $('main').replaceChildren(el('h1','连接失败'), el('p',error.message,'notice')); }
  finally { $('reload').disabled = false; }
}
$('reload').onclick = connect;
$('theme').onclick = () => {
  const theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = theme; localStorage.setItem('aurora.web.theme', theme);
};
document.documentElement.dataset.theme = localStorage.getItem('aurora.web.theme') || 'light';
connect();
