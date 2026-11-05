import {selectionRow} from './selection.js';

// Pick a server path in the requesting browser; never open a native dialog on the host.
export async function selectSource(command, scope, initial, hooks) {
  const values=Object.fromEntries([...scope.inputs].map(([id,input]) => [id,input.value]));
  const expanded=String(command).replace(/\{([^{}]+)\}/g,(_,ref) => {
    let value;
    if(/^selection\./i.test(ref)) { const path=ref.slice(10), split=path.lastIndexOf('.'); value=selectionRow(hooks.selection(),path.slice(0,split))?.[path.slice(split+1)]; }
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
  let parent='', current='', picked='', ticket=0, resolve,abort,committing=false,selectedResult=null;
  const result=new Promise(done => resolve=done), modal=hooks.dialog(directory ? '选择目录' : '选择文件',body);
  modal.addEventListener('close',() => { ++ticket;abort?.abort();resolve(selectedResult); },{once:true});
  async function request(data,signal) {
    const response=await fetch('/api/files',{method:'POST',headers:{'X-Aurora-Web':'1','Content-Type':'application/json'},body:JSON.stringify({...data,directory}),signal});
    const result=await response.json(); if(!response.ok || result.success===false) throw new Error(result.message || '无法读取目录'); return result;
  }
  async function load(value) {
    if(committing)return;
    abort?.abort();abort=new AbortController();
    const id=++ticket;status.textContent='读取中…';choose.disabled=true;list.inert=true;list.setAttribute('aria-busy','true');
    try {
      const data=await request({path:value},abort.signal); if(id!==ticket || !modal.open) return;
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
    } catch(error) { if(id===ticket && modal.open && error.name!=='AbortError') status.textContent=error.message; }
    finally {if(id===ticket&&modal.open){list.inert=false;list.removeAttribute('aria-busy');}}
  }
  async function commit(value) {
    if(committing||!modal.open)return;
    committing=true;abort?.abort();abort=new AbortController();const id=++ticket;
    choose.disabled=true;list.inert=true;for(const control of [path,go,up,roots])control.disabled=true;
    try { const data=await request({path:value,select:true},abort.signal); if(id===ticket&&modal.open) { selectedResult=data.path;modal.close(); } }
    catch(error) { if(id===ticket&&modal.open&&error.name!=='AbortError')status.textContent=error.message; }
    finally {committing=false;if(id===ticket&&modal.open){choose.disabled=false;list.inert=false;for(const control of [path,go,up,roots])control.disabled=false;}}
  }
  go.onclick=() => void load(path.value); up.onclick=() => void load(parent); roots.onclick=() => void load('');
  path.oninput=() => { ++ticket;abort?.abort();picked='';status.textContent='';choose.disabled=!path.value.trim();list.inert=false;list.removeAttribute('aria-busy'); };
  path.onkeydown=e => { if(e.key==='Enter' && !e.isComposing) { e.preventDefault(); void load(path.value); } };
  choose.onclick=() => void commit(picked || path.value || current);
  await load(initial || ''); return result;
}
