// Pick a server path in the requesting browser; never open a native dialog on the host.
export async function selectSource(command, scope, initial, hooks) {
  const values=Object.fromEntries([...scope.inputs].map(([id,input]) => [id,input.value]));
  const expanded=String(command).replace(/\{([^{}]+)\}/g,(_,ref) => {
    let value;
    if(ref.startsWith('selection.')) { const path=ref.slice(10), split=path.lastIndexOf('.'); value=hooks.selection()[path.slice(0,split)]?.[path.slice(split+1)]; }
    else value=values[ref];
    if(value==null) throw new Error(`无法取得参数：${ref}`);
    return JSON.stringify(String(value));
  });
  const name=expanded.split(/\s+/)[0];
  if(!['aurora.ui.selectfile','aurora.ui.selectdirectory'].includes(name)) {
    if(name.startsWith('aurora.')) throw new Error(`Web 来源选择尚不支持：${name}`);
    const value=await hooks.execute(expanded); return typeof value==='string' ? value : null;
  }
  const directory=name.endsWith('selectdirectory');
  const element=(tag,text) => { const e=document.createElement(tag); if(text!==undefined) e.textContent=text; return e; };
  const body=element('div'); body.className='source-browser';
  const hint=element('p','选择运行 OneHistory 服务的电脑上的路径');
  const bar=element('div'), path=element('input'), go=element('button','转到'), up=element('button','上级'), roots=element('button','磁盘');
  bar.className='source-path'; path.setAttribute('aria-label','文件或目录路径'); path.value=initial || '';
  for(const b of [go,up,roots]) b.type='button'; bar.append(path,go,up,roots);
  const status=element('p'), list=element('div'), choose=element('button',directory ? '选择此目录' : '选择文件');
  list.className='source-entries'; list.setAttribute('role','group'); list.setAttribute('aria-label','文件和目录'); choose.type='button';
  body.append(hint,bar,status,list,choose);
  let parent='', current='', picked='', ticket=0, resolve;
  const result=new Promise(done => resolve=done), modal=hooks.dialog(directory ? '选择目录' : '选择文件',body);
  modal.addEventListener('close',() => { ++ticket; resolve(null); },{once:true});
  async function request(data) {
    const response=await fetch('/api/files',{method:'POST',headers:{'X-Aurora-Web':'1','Content-Type':'application/json'},body:JSON.stringify({...data,directory})});
    const result=await response.json(); if(!response.ok || result.success===false) throw new Error(result.message || '无法读取目录'); return result;
  }
  async function load(value) {
    const id=++ticket; status.textContent='读取中…';
    try {
      const data=await request({path:value}); if(id!==ticket || !modal.open) return;
      current=data.path; parent=data.parent; path.value=current; picked=''; choose.disabled=!directory || !current;
      list.replaceChildren(...data.entries.map(entry => {
        const b=element('button',`${entry.directory ? '目录' : '文件'}　${entry.name}`); b.type='button'; b.title=entry.path;
        b.onclick=() => {
          if(entry.directory) void load(entry.path);
          else { picked=entry.path; path.value=picked; choose.disabled=false; list.querySelectorAll('button').forEach(n=>n.classList.toggle('selected',n===b)); }
        };
        b.ondblclick=() => { if(!entry.directory) void commit(entry.path); }; return b;
      }));
      status.textContent=data.truncated ? '目录项目过多，仅显示前 2000 项；可输入完整路径。' : '';
    } catch(error) { if(id===ticket && modal.open) status.textContent=error.message; }
  }
  async function commit(value) {
    choose.disabled=true;
    try { const data=await request({path:value,select:true}); if(modal.open) { resolve(data.path); modal.close(); } }
    catch(error) { status.textContent=error.message; choose.disabled=false; }
  }
  go.onclick=() => void load(path.value); up.onclick=() => void load(parent); roots.onclick=() => void load('');
  path.oninput=() => { picked=''; choose.disabled=!path.value.trim(); };
  path.onkeydown=e => { if(e.key==='Enter' && !e.isComposing) { e.preventDefault(); void load(path.value); } };
  choose.onclick=() => void commit(picked || path.value || current);
  await load(initial || ''); return result;
}
