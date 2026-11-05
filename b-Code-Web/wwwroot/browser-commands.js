import {el,isVisible} from './dom.js';
import {pageIds} from './layout.js';
import {consoleSetting} from './console-model.js';
import {showCommandDialog} from './command-dialog.js';
import {selectSource} from './source-picker.js';
import {strictRatio} from './workspace-state.js';
import {commandName} from './catalog-model.js';
import {actionInvocation} from './protocol.js';
import {selectionRegistry,selectionReport} from './selection.js';
import {validatePanel} from './panel-model.js';
import {missingReport,walkPageNodes} from './page-tree.js';

const sameId=(left,right)=>String(left??'').toLowerCase()===String(right??'').toLowerCase();
function panelReport(state,floats) {
  const live=[...state.contexts].flatMap(context=>context.panels??[]),data=[];
  const pending=(state.panelDefinitions??[]).filter(panel=>!state.pages.some(page=>page.standalonePanel&&sameId(page.id,panel.id)));
  const pages=[...state.pages.filter(page=>!page.standalonePanel||(state.panelDefinitions??[]).some(panel=>sameId(panel.id,page.id))),...pending];
  for(const page of pages)walkPageNodes(page.content,node=>{
    const type=node.type?.toLowerCase();
    if(['panel','popup'].includes(type)){
      try {validatePanel(node);}catch{return;}
      const scope=live.find(scope=>scope.panelSpec===node&&scope.pageId===page.id);
      data.push({id:node.id??'page-panel',title:node.text??'面板',owner:page.owner,page:page.id,
        rows:node.rows.length,widgets:node.rows.reduce((count,row)=>count+row.widgets.length,0),
        state:pending.includes(page)?'未注册（需重新连接）':!scope?(page.standalonePanel?'隐藏':'未挂载'):!isVisible(scope.panelElement)?'隐藏':floats?.isFloating(page.id)?'浮出':'停靠'});
    }
  });
  return {success:true,message:data.length?`共 ${data.length} 个页面面板：\n${data.map(panel=>`${panel.id} · ${panel.title} · ${panel.rows} 行 ${panel.widgets} 个控件 · ${panel.state}（${panel.owner}/${panel.page}）`).join('\n')}`:'(没有页面面板)',data};
}

// UI actions are owned by the requesting browser; data commands use the host bridge.
export function createBrowserCommands({state,workspace,dialog:openDialog,dialogFor,floatPage,floats,execute,invoke,reloadPages,invalidateOwner,reloadActions,reloadPanels,report:reportError,button:makeButton,setTheme,syncCatalog=()=>{}}) {
  return async function browserCommand(command,args={},ownerDocument,trace=[],context) {
    const dialog=dialogFor?dialogFor(ownerDocument,context):openDialog;
    const report=error=>reportError(error,ownerDocument),button=(text,handler)=>makeButton(text,handler,report);
    if(command==='aurora.ui.invoke'){
      const invocation=actionInvocation(state.actions,args,trace);
      return execute(invocation.text,ownerDocument,invocation.trace,context);
    }
    if(command==='aurora.ui.reloadpages')return reloadPages();
    if(command==='aurora.ui.invalidate'){
      const owner=args.owner??args[0];if(!String(owner??'').trim())throw new Error('缺少 owner');
      return invalidateOwner(owner);
    }
    if(command==='aurora.ui.reloadactions')return reloadActions();
    if(command==='aurora.ui.panelreload')return reloadPanels();
    if(command==='aurora.ui.missing')return missingReport(state.pages);
    if(command==='aurora.ui.actions'){
      const actions=[...state.actions.values()].sort((a,b)=>a.id.localeCompare(b.id));
      const names=new Set(state.catalog.map(item=>commandName(item).toLowerCase()));
      const broken=actions.filter(action=>!names.has(action.command.toLowerCase()));
      return {success:true,message:`共 ${actions.length} 条动作：\n${actions.map(action=>`${action.id} → ${action.command}（${action.owner}）`).join('\n')}`
        +(broken.length?`\n\n${broken.length} 条断链：\n${broken.map(action=>`${action.id} → ${action.command}`).join('\n')}`:''),data:actions};
    }
    if(command==='aurora.ui.dialog')return showCommandDialog(args,dialog);
    if(['aurora.ui.selectfile','aurora.ui.selectdirectory'].includes(command)){
      const value=await selectSource(command,{inputs:new Map()},'',{dialog,selection:()=>state.selection});
      return {success:true,message:value?`已选择 ${value}`:'已取消选择',...(value?{data:value}:{})};
    }
    const refresh=()=>Promise.all([...state.contexts].flatMap(c=>c.refreshers.map(load=>load())));
    const prefill=async text=>{
      if(typeof text!=='string'||!text.trim())throw new Error('text 不能为空');
      state.consoleModel.draft=text;
      workspace.show('console');
      for(const view of state.consoleViews)view.prefill(text);
    };
    if(command==='aurora.log.prefill') { await prefill(args.text??args[0]); return true; }
    if(command.startsWith('aurora.log.')) {
      const setting=consoleSetting(state.consoleModel,command,args,state.catalog);
      if(setting){syncCatalog();for(const view of state.consoleViews)view.paint();return setting;}
      if(['aurora.log.focus','aurora.log.copy','aurora.log.export'].includes(command)){
        if(command==='aurora.log.focus'){
          const errors=args.errors??args[0];
          if(errors!==undefined){
            if(!['true','false'].includes(String(errors).toLowerCase()))throw new Error('errors 必须是 true 或 false');
            if(String(errors).toLowerCase()==='true')Object.assign(state.consoleModel,{level:'error',domain:'全部',keyword:'',muteLayout:false});
          }
          syncCatalog();
          workspace.show('console');
        }
        const view=[...state.consoleViews][0];if(!view)throw new Error('请先打开控制台');
        if(command==='aurora.log.copy')await view.copy();
        else if(command==='aurora.log.export')view.exportText(args.path??args[0]);
        else {view.paint();view.focus();}
        return true;
      }
    }
    if(['aurora.command.copyexample','aurora.command.runreadonly'].includes(command)) {
      const requested=String(args.name??args[0]??'').trim();if(!requested)throw new Error('缺少 name');
      const descriptor=state.catalog.find(c=>commandName(c).toLowerCase()===requested.toLowerCase());
      const name=descriptor?commandName(descriptor):requested;
      if(command==='aurora.command.copyexample') {
        if(!descriptor)throw new Error(`未找到指令：${name}`);
        const text=descriptor.example;if(!text?.trim())throw new Error(`${name} 没有示例`);
        try { await navigator.clipboard.writeText(text); }
        catch { const input=el('textarea'); input.value=text; dialog('复制示例',input); input.select();return {success:true,message:'浏览器无法直接复制，已打开示例供手动复制'}; }
        return {success:true,message:`已复制 ${name} 的示例`};
      }
      if(descriptor?.readonly!==true) {
        await prefill(name);return {success:true,message:`${name} 不是只读指令，已填入控制台待确认，未执行`};
      }
      const result=await execute(name,ownerDocument,trace,context);if(result.success!==false)await refresh();return result;
    }
    if(command==='aurora.module.reloadall') { await invoke('vulcan.node.devices'); await refresh(); return true; }
    if(command==='aurora.module.refreshdevices') { await invoke('vulcan.node.refresh'); await refresh(); return true; }
    if(command==='aurora.module.place') {
      const data=await invoke('vulcan.node.devices'), body=el('div',undefined,'stack');
      const modal=dialog(`装载设备 · ${args.name}`,body);
      for(const device of [...data.devices,{id:'none',label:'不装载'}]) body.append(button(device.label,async()=>{
        await invoke('vulcan.node.place',{module:args.name,target:device.self?'local':device.id});
        modal.close(); await refresh();
      }));
      return true;
    }
    if(command==='vulcan.module.open') {
      const response=await fetch('/api/module-root',{headers:{'X-Aurora-Web':'1'}});
      if(!response.ok)throw new Error('无法读取模块目录');
      const root=await response.json(),body=el('div',undefined,'stack'); dialog('模块发现根',body);
      const browse=async path=>{
        const response=await fetch('/api/files',{method:'POST',headers:{'Content-Type':'application/json','X-Aurora-Web':'1'},body:JSON.stringify({path})});
        const data=await response.json(); if(!response.ok)throw new Error(data.message);
        body.replaceChildren(el('p',data.path));
        if(data.parent)body.append(button('上级目录',()=>browse(data.parent)));
        for(const entry of data.entries)body.append(entry.directory?button(entry.name,()=>browse(entry.path)):el('p',entry.name));
      };
      await browse(root.path); return true;
    }
    const id=args.name??args.page??args.id??args[0];
    if(['aurora.ui.layoutsave','aurora.ui.layoutload'].includes(command)){
      const name=command==='aurora.ui.layoutsave'?workspace.saveLayout(id):workspace.loadLayout(id);
      return {success:true,message:`布局方案 [${name}] 已${command.endsWith('save')?'保存':'加载'}`};
    }
    if(command==='aurora.ui.layouts'){const names=workspace.listLayouts();return {success:true,message:names.join('\n')||'暂无命名布局方案',data:names};}
    if(command==='aurora.ui.layoutreset'){workspace.resetLayout();return true;}
    if(command==='aurora.ui.ratio'){
      const page=state.pages.find(page=>page.id.toLowerCase()===String(id).toLowerCase());if(!page)throw new Error(`目标网页尚未注册：${id}`);
      workspace.setRatio(page.id,strictRatio(args.value??args[1]));return true;
    }
    if(['aurora.ui.show','aurora.ui.panelshow','aurora.scene.open','aurora.ui.float','aurora.ui.hide','aurora.ui.dock','aurora.ui.reset'].includes(command)) {
      const page=state.pages.find(p=>p.id.toLowerCase()===String(id).toLowerCase())??state.pages.find(p=>p.title===id);if(!page)throw new Error(`目标网页尚未注册：${id}`);
      if(command==='aurora.ui.panelshow'&&!page.standalonePanel)throw new Error(`没有名为 ${id} 的独立面板`);
      const pageId=page.id;
      if(command==='aurora.ui.float'){
        const value=args.on;
        if(value!==undefined&&!['true','false'].includes(String(value).toLowerCase()))throw new Error('on 必须是 true 或 false');
        return floatPage(page,value===undefined?undefined:String(value).toLowerCase()==='true');
      }
      else if(command==='aurora.ui.hide')workspace.hide(pageId);
      else if(command==='aurora.ui.dock'){
        if(!['left','right','top','bottom','center'].includes(args.pos))throw new Error('pos 必须是 left、right、top、bottom 或 center');
        workspace.place(pageId,null,args.pos,args.ratio===undefined?page.placement?.ratio??.3:strictRatio(args.ratio,'ratio'));
      }
      else if(command==='aurora.ui.reset')workspace.place(pageId,null,page.placement?.side??'right',page.placement?.ratio??.3);
      else workspace.show(pageId);
      return true;
    }
    if(command==='aurora.ui.panelset') {
      const panel=args.panel??args[0],control=args.control??args[1],value=args.value??args[2];
      if(!String(panel??'').trim())throw new Error('缺少 panel');
      if(!String(control??'').trim())throw new Error('缺少 control');
      if(value===undefined||value===null)throw new Error('缺少 value');
      const standalone=state.pages.find(page=>page.standalonePanel&&sameId(page.id,panel));
      if(standalone){
        if(!floats?.isFloating(standalone.id))workspace.show(standalone.id);
        await workspace.pane(standalone.id).rendered;
      }
      const scopes=[...state.contexts].flatMap(c=>c.panels??[]).filter(scope=>sameId(scope.panelId,panel)&&(!standalone||scope.pageId===standalone.id));
      if(!scopes.length)throw new Error(`找不到已挂载面板：${panel}`);
      const inputs=scopes.flatMap(scope=>[...scope.inputs].filter(([id,input])=>sameId(id,control)&&typeof input.setValue==='function').map(([,input])=>input));
      if(!inputs.length)throw new Error(`面板 ${panel} 没有可写控件 ${control}。可用：${[...new Set(scopes.flatMap(scope=>[...scope.inputs.keys()]))].join(' / ')||'(无)'}`);
      for(const input of inputs)input.setValue(value);
      return {success:true,message:`已回写 ${inputs.length} 处面板控件`,data:{written:inputs.length}};
    }
    if(command==='aurora.ui.refreshdata') {
      const page=args.page??args[0],node=args.node??args[1];
      const known=[...state.contexts].flatMap(context=>(context.dataBindings??[]).map(binding=>({...binding,page:context.pageId,owner:context.owner})));
      const targets=known.filter(binding=>(!String(page??'').trim()||sameId(binding.page,page))&&(!String(node??'').trim()||sameId(binding.node,node)));
      if(!targets.length)return {success:false,message:`没有匹配的取数节点。当前共 ${known.length} 处${known.length?': '+known.map(binding=>`${binding.page}#${binding.node}`).join(' / '):''}`};
      await Promise.all(targets.map(binding=>binding.refresh()));
      return {success:true,message:`已触发 ${targets.length} 处页面数据刷新`,data:{count:targets.length}};
    }
    if(command==='aurora.scene.go') {
      await workspace.open(id);return true;
    }
    if(command==='aurora.scene.save')return {success:true,message:`已另存为场景 [${workspace.sceneManager.save(id,args.title)}]`};
    if(command==='aurora.scene.reset')return {success:true,message:workspace.sceneManager.reset(args.scene??args.id??args[0])};
    if(command==='aurora.scene.delete')return {success:true,message:`场景 [${workspace.sceneManager.delete(id)}] 已删除`};
    if(command==='aurora.scene.move'){workspace.sceneManager.move(id,args.before??args[1]);return true;}
    if(command==='aurora.scene.hide'){
      const hidden=String(args.hidden??args[1]??'true').toLowerCase();
      if(!['true','false'].includes(hidden))throw new Error('hidden 必须是 true 或 false');
      workspace.sceneManager.hide(id,hidden==='true');return true;
    }
    if(command==='aurora.app.theme') {setTheme(args.mode);return true;}
    if(command==='aurora.ui.channels')return selectionReport(state.channels??selectionRegistry(state.pages,state.actions),state.selection);
    if(command==='aurora.ui.panels')return panelReport(state,floats);
    if(command==='aurora.command.history'){
      const value=String(args.count??args[0]??20).trim(),count=Number(value);
      if(!/^[+-]?\d+$/.test(value)||!Number.isInteger(count)||count< -2147483648||count>2147483647)throw new Error('count 必须是整数');
      const history=state.consoleModel.history??[],start=Math.max(0,history.length-Math.max(1,count)),items=history.slice(start);
      return {success:true,message:items.length?`最近 ${items.length} 条(共 ${history.length} 条):\n${items.map((item,index)=>`${String(start+index+1).padStart(4)}  ${item}`).join('\n')}`:'(历史为空)',data:items};
    }
    if(command==='aurora.ui.windows')return {success:true,message:state.pages.map(p=>p.id).join('\n'),data:state.pages.map(p=>({id:p.id,title:p.title,visible:pageIds(workspace.tree).includes(p.id)||!!floats?.isFloating(p.id),floating:!!floats?.isFloating(p.id)}))};
    if(command==='aurora.scene.list'){
      const rows=workspace.sceneManager.list();
      return {success:true,message:rows.map(scene=>`${scene.active?'*':' '} ${scene.title} [${scene.id}] ${scene.source}${scene.hidden?' · 已收起':''} · 用过 ${scene.uses} 次`).join('\n'),data:rows};
    }
    if(['aurora.ui.data','aurora.log.snapshot'].includes(command))return false;
    if(command.startsWith('aurora.ui.')||command.startsWith('aurora.app.')||command.startsWith('aurora.scene.')||command.startsWith('aurora.log.'))
      throw new Error(`此桌面界面操作尚未适配 Web：${command}`);
    return false;
  };
}
