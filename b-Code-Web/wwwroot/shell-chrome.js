// REQ-WEB-044: inside the OneHistory shell the window has no system title bar. The page draws the three window
// buttons and asks the shell to act through its generic window bridge (REQ-SHELL-005). Outside the shell nothing changes.
const verbs=[['minimize','—','最小化'],['maximize','☐','最大化或还原'],['close','✕','关闭']];

export function inShell(win=globalThis) {
  return win?.oneHistoryShell?.version===1&&typeof win.chrome?.webview?.postMessage==='function';
}

export function windowCommand(win,verb,extra={}) {
  if(inShell(win))win.chrome.webview.postMessage({oneHistoryWindow:verb,...extra});
}

// The web view's own window covers the frame, so edge resizing is asked of the shell from thin handles on the page edges.
const edges=['n','s','e','w','ne','nw','se','sw'];
export function addResizeEdges(doc=globalThis.document) {
  const win=doc?.defaultView;
  if(!inShell(win)||doc.querySelector('.shell-resize'))return false;
  for(const edge of edges){
    const handle=doc.createElement('div');handle.className=`shell-resize shell-resize-${edge}`;handle.setAttribute('aria-hidden','true');
    handle.addEventListener('mousedown',event=>{if(event.button===0){event.preventDefault();windowCommand(win,'resize',{edge});}});
    doc.body.append(handle);
  }
  return true;
}

// Turns `row` into the window's title row: its text goes away, the three buttons sit at its end,
// pressing elsewhere on the row drags the window and a double click maximizes or restores it.
export function adoptTitleRow(row,{keep=[]}={}) {
  const doc=row?.ownerDocument,win=doc?.defaultView;
  if(!inShell(win)||row.dataset.shellChrome)return false;
  row.dataset.shellChrome='1';
  for(const child of [...row.children])if(!keep.includes(child))child.remove();
  const controls=doc.createElement('div');controls.className='shell-controls';
  for(const [verb,glyph,label] of verbs){
    const button=doc.createElement('button');button.type='button';button.textContent=glyph;
    button.title=label;button.setAttribute('aria-label',label);button.dataset.verb=verb;
    button.addEventListener('click',()=>windowCommand(win,verb));
    controls.append(button);
  }
  row.append(controls);row.classList.add('shell-title-row');
  const interactive='button,input,select,textarea,a,[contenteditable="true"]';
  row.addEventListener('mousedown',event=>{if(event.button===0&&!event.target.closest(interactive)&&event.detail===1)windowCommand(win,'drag');});
  row.addEventListener('dblclick',event=>{if(!event.target.closest(interactive))windowCommand(win,'maximize');});
  return true;
}
