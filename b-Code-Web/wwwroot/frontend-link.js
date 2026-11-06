// REQ-WEB-040: host-originated UI commands arrive over SSE and are answered by this page.
// The server picks the most recently active page, so this page reports focus and visibility.
const headers={'Content-Type':'application/json','X-Aurora-Web':'1'};

export function clientId(storage=globalThis.sessionStorage) {
  try {
    const saved=storage?.getItem('aurora.web.client');
    if(saved&&/^[A-Za-z0-9]{8,64}$/.test(saved))return saved;
    const id=[...crypto.getRandomValues(new Uint8Array(16))].map(byte=>byte.toString(16).padStart(2,'0')).join('');
    storage?.setItem('aurora.web.client',id);
    return id;
  } catch {
    return [...crypto.getRandomValues(new Uint8Array(16))].map(byte=>byte.toString(16).padStart(2,'0')).join('');
  }
}

// Shape whatever the local command produced into the host CommandResult form.
export function pushedResult(command,value) {
  if(value===false||value===undefined||value===null)return {success:false,message:`网页不处理 ${command}`};
  if(value===true)return {success:true,message:'操作完成'};
  if(typeof value!=='object')return {success:true,message:String(value)};
  const {browser,...rest}=value;
  return {success:rest.success!==false,message:rest.message??(rest.success===false?'执行失败':'操作完成'),...(rest.data===undefined?{}:{data:rest.data})};
}

export function connectFrontendLink({run,report=()=>{},win=globalThis,doc=globalThis.document,kind}) {
  const id=clientId(win.sessionStorage);
  // REQ-WEB-045: inside the app shell the page reports the shell's own path, so the host can start the shell next time.
  const launch=typeof win.oneHistoryShell?.launch==='string'?win.oneHistoryShell.launch:'';
  const mode=kind??(launch?'shell':win.matchMedia?.('(max-width: 767px)').matches?'mobile':'desktop');
  const source=new win.EventSource(`/api/events?client=${id}&kind=${mode}${launch?`&launch=${encodeURIComponent(launch)}`:''}`);
  const post=(path,body)=>win.fetch(path,{method:'POST',headers,body:JSON.stringify(body)}).catch(()=>undefined);
  const active=()=>post('/api/events/active',{client:id,visible:doc.visibilityState!=='hidden'});
  source.addEventListener('open',active);
  source.addEventListener('invoke',async event=>{
    let request;
    try {request=JSON.parse(event.data);}catch {return;}
    let result;
    try {result=pushedResult(request.command,await run(request.command,request.args??{}));}
    catch(error){result={success:false,message:error?.message??String(error)};}
    if(request.id)await post('/api/events/reply',{id:request.id,result});
    else if(result.success===false)report(new Error(result.message));
  });
  win.addEventListener('focus',active);
  doc.addEventListener('visibilitychange',active);
  return {id,close:()=>source.close()};
}
