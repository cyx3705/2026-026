import {adoptTitleRow} from './shell-chrome.js';
import {el,button} from './dom.js';
import {floatStorageKey,floatRecords,floatBounds} from './float-model.js';

// REQ-WEB-024: move the existing page, never create a second renderer or data subscription.
export function createFloatHost({mobile,getPage,openDialog,closeDialogs=()=>{},moved=()=>{},report}) {
  const sessions=new Map(),pending=new Map(),identity=crypto.randomUUID();let ledger,exiting=false,currentPages=[],timer;
  try {ledger=floatRecords(JSON.parse(localStorage.getItem(floatStorageKey)));}catch {ledger=floatRecords();}
  const save=()=>{try{localStorage.setItem(floatStorageKey,JSON.stringify(ledger));}catch{report('浏览器无法保存浮窗状态');}};
  const notify=record=>{moved(record.page);};
  function transfer(element,move) {
    const positions=[element,...element.querySelectorAll('*')].filter(node=>node.scrollTop||node.scrollLeft)
      .map(node=>({node,top:node.scrollTop,left:node.scrollLeft}));
    move();for(const {node,top,left}of positions){node.scrollTop=top;node.scrollLeft=left;}
  }
  function placeholder(page,deferred=false) {
    const box=el('div',undefined,'page-content float-placeholder');
    box.append(el('p',deferred?`${page.title} · 待恢复浮窗`:`${page.title} · 已弹出`));
    if(deferred)box.append(button('恢复浮窗',()=>set(page,true),report));
    box.append(button('还原',()=>set(page,false),report));return box;
  }
  function mount(record,destination) {
    const view=getPage(record.page);
    record.element=view.element;record.placeholder=placeholder(record.page);
    transfer(record.element,()=>{record.element.replaceWith(record.placeholder);destination.append(record.element);});
    return view.ready;
  }
  function restore(record,{remember=true,close=true}={}) {
    if(sessions.get(record.page.id)!==record)return false;
    sessions.delete(record.page.id);
    record.cancelOpen?.();
    if(record.child){
      rememberGeometry(record);closeDialogs(record.element?.ownerDocument);
    }
    if(record.modal?.open)record.modal.close();
    if(record.element&&record.placeholder?.parentNode){transfer(record.element,()=>record.placeholder.replaceWith(record.element));notify(record);}
    if(record.child&&close&&!record.child.closed)record.child.close();
    if(remember){ledger[record.page.id]={...(ledger[record.page.id]??floatBounds(null,screen)),floating:false};save();}
    return false;
  }
  function rememberGeometry(record) {
    if(!record.child||record.child.closed)return;
    try {
      const child=record.child;
      ledger[record.page.id]={left:child.screenX,top:child.screenY,width:child.outerWidth,height:child.outerHeight,floating:true};
    }catch { /* A navigated or closing child must still allow the page to return. */ }
  }
  function decorate(record) {
    const child=record.child,doc=child.document,heading=doc.getElementById('float-heading'),body=doc.getElementById('float-body');
    if(!heading||!body)throw new Error('浮窗已离开页面入口');
    doc.title=`${record.page.title} · OneHistory`;doc.documentElement.dataset.theme=document.documentElement.dataset.theme;
    heading.replaceChildren(el('h1',record.page.title),button('还原',()=>set(record.page,false),report));
    // REQ-WEB-044: the shell's float window has no title or address bar; its heading row carries the window buttons.
    delete heading.dataset.shellChrome;adoptTitleRow(heading,{keep:[...heading.children]});
    if(record.element&&record.element.ownerDocument!==document){closeDialogs(record.element.ownerDocument);transfer(record.element,()=>body.append(record.element));}
    else mount(record,body);
    if(record.document!==doc){
      record.document=doc;
      child.addEventListener('beforeunload',()=>{
      if(sessions.get(record.page.id)!==record||record.document!==doc)return;
      rememberGeometry(record);closeDialogs(record.element?.ownerDocument);
      // Keep listeners and cached branches in the owner while the lightweight shell reloads or closes.
      if(record.element&&record.placeholder?.parentNode){transfer(record.element,()=>record.placeholder.replaceWith(record.element));notify(record);}
      },{once:true});
    }
    notify(record);
  }
  async function set(page,on) {
    let record=sessions.get(page.id);
    if(record?.child?.closed){restore(record);record=undefined;}
    const target=on??!record;
    if(!target){
      if(record)return restore(record);
      const waiting=pending.get(page.id);
      if(waiting){waiting.placeholder.replaceWith(waiting.element);pending.delete(page.id);moved(page);}
      if(ledger[page.id]){ledger[page.id].floating=false;save();}return false;
    }
    if(record){if(record.child&&!record.child.closed)record.child.focus();else record.modal?.focus();return true;}
    const waiting=pending.get(page.id);
    if(waiting){waiting.placeholder.replaceWith(waiting.element);pending.delete(page.id);}
    record={page};sessions.set(page.id,record);
    if(mobile.matches) {
      const body=el('div',undefined,'float-mobile-body');record.modal=openDialog(page.title,body);record.modal.classList.add('page-float-dialog');
      record.modal.addEventListener('close',()=>restore(record),{once:true});
      const ready=mount(record,body);notify(record);await ready;return sessions.has(page.id);
    }
    const bounds=floatBounds(ledger[page.id],screen),features=['popup',...Object.entries(bounds).map(([key,value])=>`${key}=${value}`)].join(',');
    const url=new URL('float.html',location.href);url.searchParams.set('page',page.id);
    const child=window.open(url,`aurora-${identity}-${page.id}`,features);
    if(!child){sessions.delete(page.id);if(waiting){waiting.element.replaceWith(waiting.placeholder);pending.set(page.id,waiting);}throw new Error('浏览器阻止了新窗口，请允许本站弹出窗口');}
    record.child=child;child.opener=null;monitor();
    try {
      await new Promise((resolve,reject)=>{
        const timeout=setTimeout(()=>finish(new Error('浮窗加载超时')),5000);
        const finish=error=>{clearTimeout(timeout);child.removeEventListener('load',loaded);error?reject(error):resolve();};
        record.cancelOpen=()=>finish();
        const loaded=()=>{try{if(child.document.getElementById('float-body'))finish();}catch(error){finish(error);}};
        child.addEventListener('load',loaded);loaded();
      });
      if(sessions.get(page.id)!==record||child.closed)return false;
      decorate(record);rememberGeometry(record);save();
      return true;
    }catch(error){restore(record,{remember:false});throw error;}
  }
  function reconcile(pages) {
    currentPages=pages;
    const available=new Map(pages.map(page=>[page.id,page]));
    for(const record of [...sessions.values()]){
      const page=available.get(record.page.id);
      if(!page){restore(record,{remember:false});continue;}
      record.page=page;
      if(record.suspended){record.suspended=false;if(record.child&&!record.child.closed)decorate(record);else if(record.modal?.open){mount(record,record.modal.querySelector('.float-mobile-body'));notify(record);}}
    }
    // Automatic new windows are subject to browser permission. Keep an explicit resume action in the saved slot.
    if(!mobile.matches)for(const [id,memory]of Object.entries(ledger)){
      if(!memory.floating||!available.has(id)||sessions.has(id)||pending.has(id))continue;
      const page=available.get(id),{element}=getPage(page),box=placeholder(page,true);
      element.replaceWith(box);pending.set(id,{page,element,placeholder:box});moved(page);
    }
  }
  function suspend(ids) {
    for(const [id,waiting]of pending)if(!ids||ids.has(id)){waiting.placeholder.replaceWith(waiting.element);pending.delete(id);}
    for(const record of sessions.values()){
      if(ids&&!ids.has(record.page.id))continue;
      if(record.modal&&!ids){restore(record,{remember:false});continue;}
      rememberGeometry(record);closeDialogs(record.element?.ownerDocument);
      if(record.element&&record.placeholder?.parentNode)transfer(record.element,()=>record.placeholder.replaceWith(record.element));
      record.element=undefined;record.suspended=true;
    }
  }
  function poll(){
    let changed=false;
    for(const record of [...sessions.values()])if(record.child){
      if(record.child.closed){restore(record,{remember:!exiting});continue;}
      try{if(!record.suspended&&record.child.document!==record.document&&record.child.document.readyState==='complete')decorate(record);}
      catch{restore(record,{close:false});continue;}
      const before=JSON.stringify(ledger[record.page.id]);rememberGeometry(record);changed ||= before!==JSON.stringify(ledger[record.page.id]);
    }
    if(changed)save();
  }
  function monitor(){
    if(timer||exiting||![...sessions.values()].some(record=>record.child))return;
    timer=setTimeout(()=>{timer=undefined;poll();monitor();},500);
  }
  window.addEventListener('pagehide',()=>{
    exiting=true;
    for(const record of [...sessions.values()])restore(record,{remember:false});save();clearTimeout(timer);timer=undefined;
  });
  window.addEventListener('pageshow',event=>{if(event.persisted){exiting=false;reconcile(currentPages);}});
  return {set,reconcile,suspend,isFloating(id){const record=sessions.get(id);return !!record&&(record.child?!record.child.closed:!!record.modal?.open);},
    setTheme(theme){for(const {child}of sessions.values())try{if(child&&!child.closed)child.document.documentElement.dataset.theme=theme;}catch{}}
  };
}
