import {connectFrontendLink} from './frontend-link.js';
import {adoptTitleRow,addResizeEdges} from './shell-chrome.js';
import {payload, commandText, bindArgs, rowsOf,parseCommand,resolveAction} from './protocol.js';
import {Workspace} from './workspace.js';
import {renderTable} from './table.js';
import {renderPanel} from './panel.js';
import {renderConversations,renderChat} from './chat.js';
import {renderGraph} from './graph.js';
import {createBrowserCommands} from './browser-commands.js';
import {requestCommand,invoke} from './bridge.js';
import {renderConsole} from './console.js';
import {el,button as domButton,isVisible} from './dom.js';
import {selectSource} from './source-picker.js';
import {createDialogHost} from './dialog-host.js';
import {allCommands,normalizeCatalogFilter,setCatalogFilter} from './catalog-model.js';
import {createFloatHost} from './float-host.js';
import {expectedOwner,moduleDomain,contractDomains,loadModuleActions,loadModulePages,readPageDescription,replaceOwnerPages} from './registration.js';
import {renderContainer,renderText} from './node-layout.js';
import {renderPopup,closePopup} from './popup.js';
import {selectionRegistry} from './selection.js';
import {nodeIssue} from './page-tree.js';
import {loadPanelFiles} from './panel-files.js';

const $ = id => document.getElementById(id);
const state = {pages: [], panelDefinitions:[], actions: new Map(), moduleActions:new Map(),localActions:new Map(),selection: {},channelOwners:new Map(), generation: 0, contexts: new Set(), scene: null, activePage: null, catalog: [], consoleModel: {}, consoleViews: new Set()};
state.channels=selectionRegistry([]);
const channelCandidates=new WeakMap();
const catalogChannels={'aurora.mcp.domain':'domain','aurora.mcp.class':'commandClass'};
let syncingCatalog=false;
const mobile = matchMedia('(max-width:700px)');
const dialogs=createDialogHost({mobile,onClose(modal){
  for(const current of state.contexts)if(current.modal===modal){for(const dispose of current.disposers)dispose();state.contexts.delete(current);}
}});
const dialogHosts=new Map([[document,dialogs]]);
function dialogFor(doc=document,context) {
  if(!doc?.defaultView)doc=document;
  if(!dialogHosts.has(doc))dialogHosts.set(doc,createDialogHost({document:doc,mobile:doc.defaultView.matchMedia('(max-width:700px)')}));
  const open=dialogHosts.get(doc).open;
  if(!context)return open;
  return (...args)=>{
    if(!state.contexts.has(context))throw new Error('页面已关闭或重载');
    const modal=open(...args);(context.dialogs??=new Set()).add(modal);
    modal.addEventListener('close',()=>context.dialogs.delete(modal),{once:true});return modal;
  };
}
const pageDialog=context=>(...args)=>dialogFor(context.element?.ownerDocument,context)(...args);
function closeDialogs(doc) {
  if(!doc||doc===document)return;
  dialogHosts.get(doc)?.dispose();dialogHosts.delete(doc);
}
const dialog=dialogs.open;
const workspace = new Workspace({renderPage, floatPage, report, onSwitch(scene) {
  state.scene=scene;
  closePopup();
  dialogs.closeAll();
},onReveal(){
  for(const context of state.contexts) {
    if(context.initialized){for(const reveal of context.revealers??[])reveal();}
    else void context.initialize?.().catch(pageReport(context));
  }
},onPagesChanged(ids){
  floats.suspend(ids);
  if(!ids){state.generation++;state.selection={};state.channelOwners.clear();dialogs.closeAll();}
  clearContexts(ids);syncCatalog();
  if(ids)for(const current of state.contexts)for(const follow of current.followers)follow();
}});
const floats=createFloatHost({mobile,report,openDialog:dialog,closeDialogs,
  getPage(page){const pane=workspace.pane(page.id);return {element:pane.querySelector('.page-content'),ready:pane.rendered};},
  moved(page){for(const context of state.contexts)if(context.pageId===page.id)for(const relocate of context.relocators??[])relocate();}
});
function clearContexts(ids) {
  for(const context of [...state.contexts])if(!ids||ids.has(context.pageId)) {
    state.contexts.delete(context);context.disposed=true;
    for(const modal of context.dialogs??[])if(modal.open)modal.close();
    for(const dispose of context.disposers)dispose();
    for(const [channel,owner]of state.channelOwners)if(owner===context){state.channelOwners.delete(channel);delete state.selection[channel];}
  }
}
function publish(channel, value, context,source) {
  if(!state.contexts.has(context))return;
  if(source)channelCandidates.set(source,{channel,value,context});
  if(!state.channels.accepts(channel,source))return;
  channel=channel.toLowerCase();
  if(Object.hasOwn(catalogChannels,channel)) {
    if(syncingCatalog)return;
    setCatalogFilter(state.consoleModel,catalogChannels[channel],value?.value||allCommands,state.catalog);
    syncCatalog();return;
  }
  state.channelOwners.set(channel,context);
  if (JSON.stringify(state.selection[channel]) === JSON.stringify(value)) return;
  if(value==null)delete state.selection[channel];else Object.defineProperty(state.selection,channel,{value,writable:true,enumerable:true,configurable:true});
  for (const current of state.contexts) for (const update of current.followers) update();
}
function rebuildChannels() {
  const previous=state.channels;state.channels=selectionRegistry(state.pages,state.actions);
  for(const [channel,entry]of previous.declarations)if(state.channels.declarations.get(channel)?.source!==entry.source){
    delete state.selection[channel];state.channelOwners.delete(channel);
  }
  for(const context of state.contexts)paintChannelNotices(context);
}
function replayChannels() {
  for(const {source}of state.channels.declarations.values()) {
    const cached=channelCandidates.get(source);
    if(cached)publish(cached.channel,cached.value,cached.context,source);
  }
  for(const context of state.contexts)for(const update of context.followers)update();
}
function paintChannelNotices(context) {
  if(!context.element||!context.channelNotices)return;
  const messages=state.channels.conflicts.filter(entry=>entry.pageId===context.pageId).map(entry=>el('p',entry.message,'notice'));
  context.channelNotices.replaceChildren(...messages);context.channelNotices.hidden=!messages.length;
}
// Commit the pair before notifying dependents: commands must never read a new domain with the old class.
function syncCatalog() {
  normalizeCatalogFilter(state.consoleModel,state.catalog);
  let changed=false;
  for(const [channel,field]of Object.entries(catalogChannels)) {
    const value=state.consoleModel[field];
    if(state.selection[channel]?.value!==value){state.selection[channel]={value};changed=true;}
  }
  syncingCatalog=true;
  try {
    for(const context of state.contexts)for(const panel of context.panels)for(const input of panel.inputs.values()) {
      const field=Object.hasOwn(catalogChannels,input.dataset.channel)?catalogChannels[input.dataset.channel]:undefined;
      if(field&&input.value!==state.consoleModel[field])input.syncValue(state.consoleModel[field]);
    }
  }finally{syncingCatalog=false;}
  if(changed)for(const context of state.contexts)for(const update of context.followers)update();
  for(const view of state.consoleViews)view.paint();
}
function report(error,doc=document) {
  const message=error.message??String(error),status=doc?.getElementById('float-status');
  if(status)status.textContent=message;
  state.consoleModel.commandResult={browser:true,error:error instanceof Error,message};
  for(const view of state.consoleViews)view.paint();
}
const pageReport=context=>error=>report(error,context.element?.ownerDocument);
const button=(text,handler,reporter=report)=>domButton(text,handler,reporter);
const browserCommand=createBrowserCommands({state,workspace,dialog,dialogFor,floatPage,floats,execute,invoke,reloadPages:connect,invalidateOwner,reloadActions,reloadPanels,report,button,setTheme,syncCatalog});
async function execute(text,ownerDocument=document,trace=[],context) {
  const name=text.trim().split(/\s/)[0].toLowerCase();
  if(/^aurora\.(ui|app|scene|log|command|module|nav)\./.test(name)) {
    const {args}=parseCommand(text);
    const result=await browserCommand(name,args,ownerDocument,trace,context);
    if(result)return {...(result===true?{success:true,message:'操作完成'}:result),browser:true};
  }
  return requestCommand(text);
}

async function floatPage(page,on) {
  const floating=await floats.set(page,on);
  return {success:true,message:`${page.title} 已${floating?'弹出':'还原'}`};
}
async function action(id, context, row = {}, additions = {}, selection = state.selection) {
  const declaration = resolveAction(state.actions,id);
  const bindingRevision=context.bindingRevision;
  const readValues = () => ({...Object.fromEntries([...context.inputs].map(([key,input]) => [key,
    input.type === 'checkbox' ? String(input.checked) : input.value])), ...additions});
  const values = readValues();
  const args = bindArgs(declaration.args, values, row, selection);
  const clear = (declaration.clearOnSuccess ?? []).map(id => {
    const input = context.inputs.get(id)??[...context.inputs].find(([key])=>key.toLowerCase()===id.toLowerCase())?.[1];
    if (!input) throw new Error(`clearOnSuccess 引用了不存在的控件：${id}`);
    return {input,value:input.value};
  });
  const text = commandText(declaration.command,args);
  const local=await browserCommand(declaration.command.toLowerCase(),args,context.element?.ownerDocument,[],context.pageContext??context);
  const result=local?(local===true?{success:true}:local):await execute(text,context.element?.ownerDocument,[],context.pageContext??context);
  if(result.success===false)throw new Error(result.message||'指令执行失败');
  if((local||state.contexts.has(context.pageContext??context))&&(result.message||!local))report(result.message||'操作完成',context.element?.ownerDocument);
  // An old completion may clear only the original unchanged submission and target.
  let unchanged=false;
  try { unchanged=commandText(declaration.command,bindArgs(declaration.args,readValues(),row,state.selection)) === text; } catch { /* Target no longer exists. */ }
  if (context.generation === state.generation && context.bindingRevision===bindingRevision && state.actions.get(declaration.id)===declaration
    && state.contexts.has(context.pageContext??context) && unchanged && clear.every(({input,value}) => input.isConnected && input.value === value))
    for (const {input} of clear) input.setValue ? input.setValue('') : input.value = '';
  if(!local&&state.contexts.has(context.pageContext??context))await Promise.all([...state.contexts].flatMap(current => current.refreshers.map(refresh => refresh())));
}
function panel(node, context) {
  return renderPanel(node,context,{
    invoke,report:pageReport(context),selection:() => state.selection,
    active:() => context.generation === state.generation && state.contexts.has(context),
    publish:(channel,row,spec) => publish(channel,row,context,spec),
    initialValue:channel=>Object.hasOwn(catalogChannels,channel)?state.consoleModel[catalogChannels[channel]]:undefined,
    declaration:id => state.actions.get(id?.toLowerCase()),
    action:(id,scope,additions) => action(id,scope,{},additions),
    selectSource:(command,scope,initial) => selectSource(command,scope,initial,{
      dialog:pageDialog(context),selection:() => state.selection,execute:async text => payload(await execute(text,context.element?.ownerDocument))
    })
  });
}
async function render(node, context) {
  if(!state.contexts.has(context))return el('span');
  if (!node) return el('p', '此页面没有内容');
  const type = node.type?.toLowerCase();
  const issue=nodeIssue(node);
  if(issue)return el('p',issue.message,'notice');
  if (type === 'text') return renderText(node);
  if (type === 'panel') return panel(node, context);
  if (type === 'popup') {
    return renderPopup(node,context,{panel,dialog:pageDialog(context),report:pageReport(context),active:()=>state.contexts.has(context)});
  }
  if (['stack','grid'].includes(type)) {
    const children=[];
    for(const child of node.children??[])children.push(await render(child,context));
    if(!state.contexts.has(context))return el('span');
    return renderContainer(node,children,context);
  }
  if (type === 'switch') {
    const box = el('section',undefined,'page-switch'), tabs = el('div', undefined, 'tabs'), content = el('div',undefined,'switch-content');
    tabs.setAttribute('role','tablist');
    const children = await Promise.all((node.children ?? []).map(n => render(n, context)));
    (context.relocators??=[]).push(()=>{for(const child of children)if(!child.isConnected)context.element.ownerDocument.adoptNode(child);});
    let shown=-1;
    const show=index=>{
      if(index===shown)return;shown=index;
      content.replaceChildren(...(children[index]?[children[index]]:[]));
      [...tabs.children].forEach((tab,i)=>{tab.setAttribute('aria-selected',String(index===i));tab.tabIndex=index===i?0:-1;});
    };
    (node.children??[]).forEach((child,index)=>{
      const tab=button(child.case||`第 ${index+1} 页`,()=>show(index));tab.setAttribute('role','tab');
      tab.onkeydown=e=>{
        if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;
        e.preventDefault();const next=e.key==='Home'?0:e.key==='End'?children.length-1:(shown+(e.key==='ArrowRight'?1:-1)+children.length)%children.length;
        show(next);tabs.children[next].focus();
      };tabs.append(tab);
    });
    const update=() => {
      let index=0;
      if(node.source) {
        try {
          const source=node.source.trim(),reference=source.startsWith('{')?source:`{${source}}`;
          const value=bindArgs({value:reference},{},{},state.selection).value;
          index=Math.max(0,node.children.findIndex(child=>String(child.case??'').toLowerCase()===value.toLowerCase()));
        } catch { index=0; }
      }
      show(index);
    };
    if(node.source) context.followers.push(update); else box.append(tabs);
    update(); box.append(content); return box;
  }
  if(['conversationlist','chattimeline'].includes(type))return (type==='conversationlist'?renderConversations:renderChat)(node,context,{
    invoke,report:pageReport(context),dialog:pageDialog(context),catalog:()=>state.catalog,selection:()=>state.selection,
    publish:(channel,row)=>publish(channel,row,context,node),
    action:(id,row,additions,selection)=>action(id,context,row,additions,selection)
  });
  if (type==='swimlane') return renderGraph(node,context,{
    invoke,report:pageReport(context),selection:()=>state.selection,
    action:(id,row)=>action(id,context,row)
  });
  if (type === 'table') return renderTable(node, context, {
    invoke, report:pageReport(context), selection:() => state.selection,
    active:() => context.generation === state.generation && state.contexts.has(context),
    publish:(channel,row) => publish(channel,row,context,node),
    action:(id,row,additions) => action(id,context,row,additions),
    declaration:id => state.actions.get(id?.toLowerCase())
  });
  throw new Error(`组件 ${node.type} 已登记支持，但缺少 Web 渲染入口`);
}
async function renderPage(page, target, modal = null) {
  if (page.localConsole) {
    const context={owner:page.owner,pageId:page.id,element:target,modal,tables:new Map(),disposers:[],followers:[],refreshers:[],panels:[]};
    const view=renderConsole(target,state.consoleModel,{catalog:()=>state.catalog,execute:text=>execute(text,target.ownerDocument,[],context),invoke,report:pageReport(context),context,
      onResult:()=>{for(const view of state.consoleViews)view.paint();},
      setDomain:value=>{setCatalogFilter(state.consoleModel,'domain',value,state.catalog);syncCatalog();},
      logsAvailable:()=>state.catalog.some(c=>(c.commandName??c.name)==='aurora.log.snapshot')});
    state.consoleViews.add(view);
    context.disposers.push(()=>{view.dispose();state.consoleViews.delete(view);});context.refreshers.push(()=>view.refresh());state.contexts.add(context);
    return;
  }
  const context = {owner:page.owner, pageId:page.id,element:target, tables:new Map(), panels:[], disposers:[], inputs:new Map(), refreshers:[], followers:[], initializers:[], revealers:[], generation:state.generation, modal};
  state.contexts.add(context);
  context.initialize=async()=>{
    if(!context.mounted||context.initialized||context.generation!==state.generation||!state.contexts.has(context)||!isVisible(target))return;
    context.initialized=true;
    await Promise.all(context.initializers.map(load=>load()));
  };
  const content = await render(page.content, context);
  if (context.generation !== state.generation||!state.contexts.has(context))return;
  context.channelNotices=el('div',undefined,'channel-notices');paintChannelNotices(context);
  target.replaceChildren(context.channelNotices,content);
  context.mounted=true;
  await context.initialize();
}
let connection=Promise.resolve(),pendingConnections=0;
function enqueueRegistration(work) {
  pendingConnections++;$('reload').disabled=true;
  const current=connection.then(work);
  connection=current.catch(()=>{});
  return current.finally(()=>{if(!--pendingConnections)$('reload').disabled=false;});
}
function connect() {return enqueueRegistration(connectCore);}
async function connectCore() {
  report('正在读取模块页面…');
  try {
    const catalog = rowsOf(await invoke('vulcan.command.list'));
    const names = catalog.map(c => c.commandName ?? c.name);
    const registry=await loadModuleActions(names,invoke),actions=new Map(registry.actions),localActions=new Map(),errors=[...registry.errors];
    let pages=[];
    for(const domain of contractDomains(names,'.ui.describe')) {
      try {
        pages=replaceOwnerPages(pages,await loadModulePages(domain,invoke),expectedOwner(domain));
      }catch(error){errors.push(`${domain}.ui.describe：${error.message}`);}
    }
    if(names.includes('aurora.ui.data')) {
      try {
        const response=await fetch('/api/frontend',{headers:{'X-Aurora-Web':'1'}});
        if(!response.ok) throw new Error('无法读取 Aurora 原生页面描述');
        const set=await readPageDescription(await response.json(),'HistoryAurora');
        const required=new Set();
        const collect=node=>{ if(!node||typeof node!=='object')return; for(const [key,value] of Object.entries(node)) {
          if(['action','cellAction','cellEditAction'].includes(key)&&typeof value==='string')required.add(value);
          else if(value&&typeof value==='object') { if(Array.isArray(value))value.forEach(collect);else collect(value); }
        }};
        set.pages.forEach(collect); required.add('preview.node');
        // 2.1.1: the module hands out the native pages' declarations; aurora.ui.actions now lists this page's own state.
        const registry=rowsOf(await invoke('aurora.ui.hostedactions'));
        const ownActions=[...required].map(id=>{const a=registry.find(a=>a.id===id); if(!a)throw new Error(`Aurora 缺少动作：${id}`); return a;});
        for(const p of set.pages) if(pages.some(page=>page.id===p.id)) throw new Error(`页面 id 冲突：${p.id}`);
        pages.push(...set.pages.map(p=>({...p,owner:set.owner})));
        for(const a of ownActions){const own={...a,owner:'HistoryAurora'};localActions.set(a.id,own);if(!actions.has(a.id))actions.set(a.id,own);}
      } catch(error) { errors.push(`Aurora 原生页面：${error.message}`); }
    }
    pages.push({id:'console',title:'控制台',owner:'HistoryAurora',scene:'HistoryAurora',
      localConsole:true,placement:{side:'bottom',ratio:.3,visible:true}});
    state.panelDefinitions=[];
    try {const panels=await loadPanelFiles(pages);pages.push(...panels.pages);state.panelDefinitions=panels.pages;errors.push(...panels.errors);}
    catch(error){errors.push(error.message);}
    Object.assign(state,{catalog,pages,actions,moduleActions:registry.actions,localActions});rebuildChannels();workspace.setPages(pages);
    errors.push(...state.channels.conflicts.map(entry=>entry.message));
    if(names.includes('aurora.ui.data')) {
      try {
        const response=await fetch('/api/layout-history',{headers:{'X-Aurora-Web':'1'}});
        if(!response.ok)throw new Error('无法读取桌面布局历史');
        const history=await response.json();
        errors.push(...workspace.restoreDesktopHistory(history));
        errors.push(...(history.errors??[]));
      } catch(error) { errors.push(error.message); }
    }
    const scenes = workspace.scenes;
    const initialScene=pages.some(page=>page.owner==='HistoryAurora'&&!page.localConsole)?'HistoryAurora':pages[0]?.scene||pages[0]?.owner;
    const floatingId = new URL(location.href).searchParams.get('page');
    if (floatingId) {
      const page = pages.find(p => p.id === floatingId);
      if (!page) throw new Error('弹出页面已不存在');
      document.body.classList.add('popout'); document.title = `${page.title} · OneHistory`;
      state.generation++; clearContexts();
      const body = el('div'); $('main').replaceChildren(el('h1',page.title),body);
      await renderPage(page,body);
    } else {
      const recoveryUrl=new URL(location.href),desktopScene=recoveryUrl.searchParams.get('desktopLayout');
      if(desktopScene) {
        try {
          workspace.restoreDesktopLayout(desktopScene);
          recoveryUrl.searchParams.delete('desktopLayout');
          history.replaceState(history.state,'',recoveryUrl);
        } catch(error) {errors.push(error.message);}
      }
      await workspace.open(scenes.includes(workspace.saved.lastScene) ? workspace.saved.lastScene
        : scenes.includes(state.scene) ? state.scene : scenes.includes(initialScene)?initialScene:scenes[0], true, false);
      floats.reconcile(pages);
    }
    const message=`已连接 · ${catalog.length} 条指令 · ${pages.length} 个页面${errors.length ? ' · ' + errors.join('；') : ''}`;
    report(message);return {success:true,message,data:{pages:pages.length,errors}};
  } catch (error) {
    report(error);
    if(!workspace.pages.length)$('main').replaceChildren(el('h1','连接失败'), el('p',error.message,'notice'));
    return {success:false,message:error.message};
  }
}
function reloadPanels() {
  return enqueueRegistration(async()=>{
    const loaded=await loadPanelFiles(state.pages),definitions=loaded.pages,ids=new Set();
    const pages=state.pages.map(page=>{
      if(!page.standalonePanel)return page;
      const replacement=definitions.find(panel=>panel.id.toLowerCase()===page.id.toLowerCase());
      if(!replacement)return page;
      ids.add(page.id);return {...replacement,id:page.id};
    });
    const pending=definitions.filter(panel=>!pages.some(page=>page.standalonePanel&&page.id.toLowerCase()===panel.id.toLowerCase())).map(panel=>panel.id);
    state.pages=pages;state.panelDefinitions=definitions;rebuildChannels();
    workspace.setPages(pages,{replaceIds:ids});workspace.setLayout(workspace.fullTree,workspace.layoutActive);workspace.draw();
    floats.reconcile(pages);replayChannels();syncCatalog();
    return {success:true,message:`已重载 ${ids.size} 个独立面板${pending.length?'；新增面板需重新连接：'+pending.join(' / '):''}${loaded.errors.length?'；'+loaded.errors.join('；'):''}`,
      data:{rebuilt:ids.size,pending,errors:loaded.errors}};
  });
}
function rebindActions(changed) {
  let panels=0,tables=0;
  for(const context of state.contexts){
    for(const scope of context.panels)if(scope.rebindActions?.(changed))panels++;
    for(const bind of context.actionBindings??[])if(bind(changed))tables++;
  }
  return {panels,tables};
}
function reloadActions() {
  return enqueueRegistration(async()=>{
    const catalog=rowsOf(await invoke('vulcan.command.list'));
    const registry=await loadModuleActions(catalog.map(c=>c.commandName??c.name),invoke);
    state.catalog=catalog;state.moduleActions=registry.actions;state.actions=new Map([...state.localActions,...registry.actions]);
    rebuildChannels();
    const rebuilt=rebindActions();syncCatalog();
    return {success:true,message:`问了 ${registry.asked} 个模块，收下 ${registry.actions.size} 条动作；已更新 ${rebuilt.panels} 个面板、${rebuilt.tables} 个表格${registry.errors.length?'；'+registry.errors.join('；'):''}`,
      data:{actions:registry.actions.size,...rebuilt,errors:registry.errors}};
  });
}
function invalidateOwner(value) {
  const domain=moduleDomain(value),owner=expectedOwner(domain);
  return enqueueRegistration(async()=>{
    const catalog=rowsOf(await invoke('vulcan.command.list')),names=catalog.map(c=>c.commandName??c.name);
    const registry=await loadModuleActions(names,invoke,{domain,existing:state.moduleActions}),errors=[...registry.errors];
    let pages;
    try {pages=replaceOwnerPages(state.pages,await loadModulePages(domain,invoke),owner);}
    catch(error){errors.push(`${domain}.ui.describe：${error.message}`);}
    const changed=new Set([...state.moduleActions.values(),...registry.actions.values()].filter(action=>action.owner.toLowerCase()===owner.toLowerCase()).map(action=>action.id));
    state.catalog=catalog;state.moduleActions=registry.actions;state.actions=new Map([...state.localActions,...registry.actions]);
    if(pages){
      const ids=new Set([...state.pages,...pages].filter(page=>page.owner.toLowerCase()===owner.toLowerCase()).map(page=>page.id));
      state.pages=pages;rebuildChannels();workspace.setPages(pages,{replaceIds:ids});
      if(workspace.scenes.includes(workspace.scene)){workspace.setLayout(workspace.fullTree,workspace.layoutActive);workspace.draw();}
      else await workspace.open(workspace.scenes.includes(workspace.saved.lastScene)?workspace.saved.lastScene:workspace.scenes[0],true,false);
      floats.reconcile(pages);
    }
    if(!pages)rebuildChannels();
    replayChannels();rebindActions(changed);syncCatalog();
    const count=pages?pages.filter(page=>page.owner.toLowerCase()===owner.toLowerCase()).length:0;
    return {success:true,message:`已刷新 ${owner} · ${count} 个页面${errors.length?'；'+errors.join('；'):''}`,data:{owner,pages:count,errors}};
  });
}
$('reload').onclick = connect;
function setTheme(mode) {
  const theme=mode==='toggle'||!mode ? (document.documentElement.dataset.theme==='dark'?'light':'dark') : mode;
  if(!['light','dark'].includes(theme))throw new Error('主题只能是 light、dark 或 toggle');
  document.documentElement.dataset.theme=theme;
  floats.setTheme(theme);
  try {localStorage.setItem('aurora.web.theme',theme);}catch {report('浏览器无法保存主题');}
}
$('theme').onclick=()=>setTheme('toggle');
try {setTheme(localStorage.getItem('aurora.web.theme')||'light');}catch {setTheme('light');}
// REQ-WEB-044: in the shell the rail's text row becomes the window title row with the three window buttons.
adoptTitleRow(document.querySelector('.rail-brand'),{keep:[document.querySelector('.rail-tools')]});addResizeEdges(document);
connect();
// REQ-WEB-040: the host reaches this page through the Aurora module; window verbs map onto the page.
connectFrontendLink({report,run:async(command,args)=>{
  const name=String(command).toLowerCase();
  if(name==='vulcan.app.show'){window.focus();return {success:true,message:'Aurora 网页已打开'};}
  if(name==='vulcan.app.focusconsole'){window.focus();workspace.show('console');return {success:true,message:'已聚焦控制台'};}
  if(name==='vulcan.app.hide'||name==='vulcan.app.close')return {success:true,message:'网页由浏览器管理，未关闭'};
  return browserCommand(name,args,document,[],undefined);
}});
